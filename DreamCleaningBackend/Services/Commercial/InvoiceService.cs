using System.Text.Json;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs.Commercial;
using DreamCleaningBackend.Helpers.Commercial;
using DreamCleaningBackend.Models.Commercial;
using DreamCleaningBackend.Models.Contracts;
using DreamCleaningBackend.Services.Contracts;
using Microsoft.EntityFrameworkCore;

namespace DreamCleaningBackend.Services.Commercial
{
    /// <summary>Thrown for a rule violation an admin can act on; the filter maps it to 400.</summary>
    public class InvoiceWorkflowException : Exception
    {
        public InvoiceWorkflowException(string message) : base(message) { }
    }

    /// <summary>
    /// Creating, editing, duplicating and voiding commercial invoices, plus the list and detail
    /// projections.
    ///
    /// Money is never taken on trust. Every write path funnels through
    /// <see cref="ApplyFinancials"/>, which recomputes the line amounts, subtotal, discount, tax
    /// and total from <see cref="InvoiceCalculator"/> and overwrites whatever arrived. Status is
    /// likewise never assigned from a request - <see cref="InvoiceStatusPolicy.Resolve"/> derives
    /// it from what has been sent, seen and paid.
    ///
    /// Payments live next door in <c>InvoicePaymentService</c>, which owns the transaction that
    /// keeps a payment row, the invoice totals, the status and the activity trail consistent.
    /// </summary>
    public class InvoiceService
    {
        private readonly ApplicationDbContext _context;
        private readonly InvoiceNumberService _numbers;
        private readonly BillingSettingsService _billing;
        private readonly IConfiguration _configuration;
        private readonly ILogger<InvoiceService> _logger;

        public InvoiceService(
            ApplicationDbContext context,
            InvoiceNumberService numbers,
            BillingSettingsService billing,
            IConfiguration configuration,
            ILogger<InvoiceService> logger)
        {
            _context = context;
            _numbers = numbers;
            _billing = billing;
            _configuration = configuration;
            _logger = logger;
        }

        public string FrontendUrl =>
            (_configuration["Frontend:Url"] ?? "https://dreamcleaningnyc.com").TrimEnd('/');

        public string BuildPublicUrl(string token) => $"{FrontendUrl}/invoice/{token}";

        // ── Create ────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Creates a Draft.
        ///
        /// Retries the SAVE on a unique-index violation, not just the number pick: the pre-check
        /// in InvoiceNumberService and the insert are not atomic, so the index is the last word
        /// and the retry is what turns its verdict back into a successful create.
        /// </summary>
        public async Task<CommercialInvoice> CreateAsync(SaveInvoiceDto dto, int userId)
        {
            var client = await _context.ContractClients.FindAsync(dto.ContractClientId)
                ?? throw new InvoiceWorkflowException("The selected client no longer exists.");

            await ValidateContractBelongsToClientAsync(dto.ContractId, client.Id);

            var settings = await _billing.GetOrCreateAsync();
            var invoiceDate = (dto.InvoiceDate ?? DateTime.UtcNow).Date;

            for (var attempt = 0; attempt < 3; attempt++)
            {
                var invoice = new CommercialInvoice
                {
                    InvoiceNumber = await _numbers.NextInvoiceNumberAsync(),
                    PublicToken = InvoiceNumberService.NewPublicToken(),
                    ContractClientId = client.Id,
                    ContractId = dto.ContractId,
                    ContractServiceLocationId = dto.ContractServiceLocationId,
                    Status = InvoiceStatus.Draft,
                    InvoiceDate = invoiceDate,
                    DueTerms = dto.DueTerms,
                    DueDate = InvoiceCalculator.ResolveDueDate(invoiceDate, dto.DueTerms, dto.CustomDueDate),
                    ServiceStartDate = dto.ServiceStartDate?.Date,
                    ServiceEndDate = dto.ServiceEndDate?.Date,
                    ServiceAddress = await ResolveServiceAddressAsync(dto),
                    PoNumber = Trim(dto.PoNumber),
                    ClientReference = Trim(dto.ClientReference),
                    PaymentMethod = dto.PaymentMethod,
                    CustomerNote = Trim(dto.CustomerNote) ?? settings.DefaultCustomerNote,
                    InternalNote = Trim(dto.InternalNote),
                    Currency = "USD",
                    CreatedByUserId = userId,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };

                ApplyItems(invoice, dto.Items);
                ApplyFinancials(invoice, dto);

                _context.CommercialInvoices.Add(invoice);

                try
                {
                    await _context.SaveChangesAsync();
                }
                catch (DbUpdateException ex) when (IsUniqueViolation(ex) && attempt < 2)
                {
                    // Another request took this number between the check and the insert. Detach
                    // and go round again with a fresh one.
                    _logger.LogWarning(ex,
                        "Invoice number {Number} collided on insert; retrying.", invoice.InvoiceNumber);
                    _context.Entry(invoice).State = EntityState.Detached;
                    foreach (var item in invoice.Items) _context.Entry(item).State = EntityState.Detached;
                    continue;
                }

                await LogActivityAsync(invoice.Id, "invoice_created",
                    $"Invoice {invoice.InvoiceNumber} created for {client.LegalEntityName}.", userId);

                return invoice;
            }

            throw new InvoiceWorkflowException("Could not allocate an invoice number. Please try again.");
        }

        // ── Update ────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Applies an edit.
        ///
        /// Two things are deliberately immutable here and are not read off the DTO at all: the
        /// invoice NUMBER and the public TOKEN. The number is in the client's payment memo and the
        /// token is in their inbox; regenerating either would strand a payment or a link that is
        /// already in the world.
        ///
        /// Monetary fields are frozen once the invoice is Paid - notes and references stay
        /// editable, because correcting a PO number on a paid invoice is ordinary bookkeeping.
        /// </summary>
        public async Task<CommercialInvoice> UpdateAsync(int invoiceId, SaveInvoiceDto dto, int userId)
        {
            var invoice = await LoadForWriteAsync(invoiceId);

            if (!InvoiceStatusPolicy.CanEdit(invoice.Status))
                throw new InvoiceWorkflowException(
                    "A voided invoice is read-only. Duplicate it if you need to issue a corrected one.");

            await ValidateContractBelongsToClientAsync(dto.ContractId, dto.ContractClientId);

            var monetaryEditable = InvoiceStatusPolicy.CanEditMonetaryValues(invoice.Status);
            FinancialSnapshot? before = monetaryEditable ? SnapshotFinancials(invoice) : null;

            // Always editable.
            invoice.PoNumber = Trim(dto.PoNumber);
            invoice.ClientReference = Trim(dto.ClientReference);
            invoice.CustomerNote = Trim(dto.CustomerNote);
            invoice.InternalNote = Trim(dto.InternalNote);
            invoice.ServiceStartDate = dto.ServiceStartDate?.Date;
            invoice.ServiceEndDate = dto.ServiceEndDate?.Date;

            if (monetaryEditable)
            {
                invoice.ContractClientId = dto.ContractClientId;
                invoice.ContractId = dto.ContractId;
                invoice.ContractServiceLocationId = dto.ContractServiceLocationId;
                invoice.ServiceAddress = await ResolveServiceAddressAsync(dto);
                invoice.PaymentMethod = dto.PaymentMethod;

                var invoiceDate = (dto.InvoiceDate ?? invoice.InvoiceDate).Date;
                invoice.InvoiceDate = invoiceDate;
                invoice.DueTerms = dto.DueTerms;
                invoice.DueDate = InvoiceCalculator.ResolveDueDate(invoiceDate, dto.DueTerms, dto.CustomDueDate);

                ApplyItems(invoice, dto.Items);
                ApplyFinancials(invoice, dto);
            }

            RecalculateStatus(invoice);
            invoice.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            // A figures change AFTER the client has their copy is the one edit worth spelling out
            // in the trail - the invoice in their inbox no longer matches this one.
            if (before != null && invoice.HasBeenSent() && before != SnapshotFinancials(invoice))
            {
                await LogActivityAsync(invoice.Id, "invoice_amounts_changed",
                    $"Invoice amounts changed after sending: total is now {invoice.Total:C} "
                    + $"(was {before.Value.Total:C}). The client's copy is out of date until it is resent.",
                    userId);
            }
            else
            {
                await LogActivityAsync(invoice.Id, "invoice_updated", "Invoice updated.", userId);
            }

            return invoice;
        }

        private readonly record struct FinancialSnapshot(
            decimal SubTotal, decimal DiscountAmount, decimal TaxAmount, decimal Total);

        private static FinancialSnapshot SnapshotFinancials(CommercialInvoice i) =>
            new(i.SubTotal, i.DiscountAmount, i.TaxAmount, i.Total);

        // ── The money, always server-side ─────────────────────────────────────────────────────

        /// <summary>
        /// Replaces the line collection from the DTO, recomputing every line amount.
        /// Rows are matched by id so an untouched line keeps its identity; anything missing from
        /// the DTO is removed.
        /// </summary>
        private void ApplyItems(CommercialInvoice invoice, List<SaveInvoiceItemDto> items)
        {
            var keptIds = items.Where(i => i.Id is > 0).Select(i => i.Id!.Value).ToHashSet();

            foreach (var existing in invoice.Items.Where(i => !keptIds.Contains(i.Id)).ToList())
            {
                invoice.Items.Remove(existing);
                _context.CommercialInvoiceItems.Remove(existing);
            }

            var order = 0;
            foreach (var dto in items)
            {
                var description = Trim(dto.Description);
                if (string.IsNullOrWhiteSpace(description)) continue;

                var quantity = Math.Max(0m, dto.Quantity);
                var unitPrice = Math.Max(0m, dto.UnitPrice);

                var row = dto.Id is > 0
                    ? invoice.Items.FirstOrDefault(i => i.Id == dto.Id!.Value)
                    : null;

                if (row == null)
                {
                    row = new CommercialInvoiceItem { CreatedAt = DateTime.UtcNow };
                    invoice.Items.Add(row);
                }

                row.Description = description;
                row.Quantity = quantity;
                row.UnitPrice = unitPrice;
                // Never dto-supplied: the browser has no Amount field to send.
                row.Amount = InvoiceCalculator.LineAmount(quantity, unitPrice);
                row.SortOrder = order++;
                row.UpdatedAt = DateTime.UtcNow;
            }
        }

        /// <summary>
        /// Recomputes every derived monetary column from the lines and the discount/tax settings.
        /// This is the only assignment to SubTotal, DiscountAmount, TaxAmount and Total anywhere.
        /// </summary>
        private static void ApplyFinancials(CommercialInvoice invoice, SaveInvoiceDto dto)
        {
            invoice.DiscountType = dto.DiscountType;
            invoice.DiscountValue = dto.DiscountType == InvoiceDiscountType.None ? null : dto.DiscountValue;
            invoice.TaxType = dto.TaxType;
            invoice.TaxRate = dto.TaxType == InvoiceTaxType.Exempt ? null : dto.TaxRate;

            var totals = InvoiceCalculator.Calculate(new InvoiceTotalsInput
            {
                Lines = invoice.Items
                    .OrderBy(i => i.SortOrder)
                    .Select(i => new InvoiceLineInput { Quantity = i.Quantity, UnitPrice = i.UnitPrice })
                    .ToList(),
                DiscountType = invoice.DiscountType,
                DiscountValue = invoice.DiscountValue,
                TaxType = invoice.TaxType,
                TaxRate = invoice.TaxRate
            });

            invoice.SubTotal = totals.SubTotal;
            invoice.DiscountAmount = totals.DiscountAmount;
            invoice.TaxAmount = totals.TaxAmount;
            invoice.Total = totals.Total;
            invoice.BalanceDue = InvoiceCalculator.ResolveBalance(invoice.Total, invoice.AmountPaid);
        }

        /// <summary>
        /// Re-derives status from the invoice's own facts. Called after any change that could move
        /// it. Draft and Void are left alone by the policy, so this is safe to call anywhere.
        /// </summary>
        public static void RecalculateStatus(CommercialInvoice invoice)
        {
            var resolved = InvoiceStatusPolicy.Resolve(
                invoice.Status,
                invoice.Total,
                invoice.AmountPaid,
                invoice.DueDate,
                invoice.FirstSentAt.HasValue,
                invoice.FirstViewedAt.HasValue,
                DateTime.UtcNow);

            invoice.Status = resolved;
            invoice.BalanceDue = InvoiceCalculator.ResolveBalance(invoice.Total, invoice.AmountPaid);

            // PaidAt tracks the transition, not the status: it is stamped when the invoice first
            // becomes Paid and cleared if a reversal takes it back out of Paid, so it can never
            // claim a payment date for an invoice that is once again owed.
            if (resolved == InvoiceStatus.Paid) invoice.PaidAt ??= DateTime.UtcNow;
            else if (resolved != InvoiceStatus.Void) invoice.PaidAt = null;
        }

        // ── Lifecycle actions ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Voids an invoice. The row is KEPT and the number stays permanently reserved - an
        /// issued invoice is never deleted, because "invoice DCI-2026-74521863 does not exist" is
        /// not an answer anyone can reconcile against.
        /// </summary>
        public async Task<CommercialInvoice> VoidAsync(int invoiceId, string reason, int userId)
        {
            var invoice = await LoadForWriteAsync(invoiceId);

            if (!InvoiceStatusPolicy.CanVoid(invoice.Status))
                throw new InvoiceWorkflowException(invoice.Status == InvoiceStatus.Void
                    ? "This invoice is already void."
                    : "A paid invoice cannot be voided. Record a reversing payment instead.");

            if (string.IsNullOrWhiteSpace(reason))
                throw new InvoiceWorkflowException("A reason is required to void an invoice.");

            invoice.Status = InvoiceStatus.Void;
            invoice.VoidedAt = DateTime.UtcNow;
            invoice.VoidReason = reason.Trim();
            invoice.VoidedByUserId = userId;
            invoice.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();
            await LogActivityAsync(invoice.Id, "invoice_voided",
                $"Invoice voided. Reason: {reason.Trim()}", userId);

            return invoice;
        }

        /// <summary>Only an unsent Draft may be removed outright.</summary>
        public async Task DeleteDraftAsync(int invoiceId, int userId)
        {
            var invoice = await LoadForWriteAsync(invoiceId);

            if (!InvoiceStatusPolicy.CanDelete(invoice.Status))
                throw new InvoiceWorkflowException(
                    "Only a draft can be deleted. Void this invoice instead so its number stays on record.");

            _context.CommercialInvoiceItems.RemoveRange(invoice.Items);
            _context.CommercialInvoiceActivityLogs.RemoveRange(
                _context.CommercialInvoiceActivityLogs.Where(a => a.CommercialInvoiceId == invoice.Id));
            _context.CommercialInvoices.Remove(invoice);
            await _context.SaveChangesAsync();

            _logger.LogInformation(
                "Draft invoice {Number} deleted by user {UserId}.", invoice.InvoiceNumber, userId);
        }

        /// <summary>
        /// Copies an invoice into a fresh Draft - the recurring-billing workhorse until automatic
        /// generation exists.
        ///
        /// WHAT IS COPIED: client, contract, location, address, line items, payment method, tax
        /// and discount settings, customer note.
        /// WHAT IS NEW: id, invoice number, public token.
        /// WHAT IS RESET: status to Draft, amount paid to zero, and the payment, email, view and
        /// activity histories to empty - those belong to the invoice that earned them, and
        /// carrying them over would claim this new one had already been paid.
        /// </summary>
        public async Task<CommercialInvoice> DuplicateAsync(int invoiceId, int userId)
        {
            var source = await _context.CommercialInvoices
                .Include(i => i.Items)
                .FirstOrDefaultAsync(i => i.Id == invoiceId)
                ?? throw new InvoiceWorkflowException("Invoice not found.");

            var invoiceDate = DateTime.UtcNow.Date;

            var copy = new CommercialInvoice
            {
                InvoiceNumber = await _numbers.NextInvoiceNumberAsync(),
                PublicToken = InvoiceNumberService.NewPublicToken(),
                ContractClientId = source.ContractClientId,
                ContractId = source.ContractId,
                ContractServiceLocationId = source.ContractServiceLocationId,
                Status = InvoiceStatus.Draft,
                InvoiceDate = invoiceDate,
                DueTerms = source.DueTerms,
                // Recalculated from today rather than copied: the source's due date belongs to the
                // source's billing period.
                DueDate = InvoiceCalculator.ResolveDueDate(invoiceDate, source.DueTerms, source.DueDate),
                ServiceAddress = source.ServiceAddress,
                PoNumber = source.PoNumber,
                ClientReference = source.ClientReference,
                DiscountType = source.DiscountType,
                DiscountValue = source.DiscountValue,
                TaxType = source.TaxType,
                TaxRate = source.TaxRate,
                PaymentMethod = source.PaymentMethod,
                CustomerNote = source.CustomerNote,
                // The internal note is a working comment about THAT invoice - usually a chase or a
                // correction - and is not carried into a new billing period.
                InternalNote = null,
                Currency = source.Currency,
                CreatedByUserId = userId,
                DuplicatedFromInvoiceId = source.Id,
                AmountPaid = 0m,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            foreach (var item in source.Items.OrderBy(i => i.SortOrder))
            {
                copy.Items.Add(new CommercialInvoiceItem
                {
                    Description = item.Description,
                    Quantity = item.Quantity,
                    UnitPrice = item.UnitPrice,
                    Amount = InvoiceCalculator.LineAmount(item.Quantity, item.UnitPrice),
                    SortOrder = item.SortOrder,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                });
            }

            RecomputeTotalsFromRows(copy);

            _context.CommercialInvoices.Add(copy);
            await _context.SaveChangesAsync();

            await LogActivityAsync(copy.Id, "invoice_created",
                $"Invoice {copy.InvoiceNumber} created by duplicating {source.InvoiceNumber}.", userId);

            return copy;
        }

        /// <summary>Recomputes totals from the rows already on the entity (no DTO involved).</summary>
        public static void RecomputeTotalsFromRows(CommercialInvoice invoice)
        {
            var totals = InvoiceCalculator.Calculate(new InvoiceTotalsInput
            {
                Lines = invoice.Items.OrderBy(i => i.SortOrder)
                    .Select(i => new InvoiceLineInput { Quantity = i.Quantity, UnitPrice = i.UnitPrice })
                    .ToList(),
                DiscountType = invoice.DiscountType,
                DiscountValue = invoice.DiscountValue,
                TaxType = invoice.TaxType,
                TaxRate = invoice.TaxRate
            });

            invoice.SubTotal = totals.SubTotal;
            invoice.DiscountAmount = totals.DiscountAmount;
            invoice.TaxAmount = totals.TaxAmount;
            invoice.Total = totals.Total;
            invoice.BalanceDue = InvoiceCalculator.ResolveBalance(invoice.Total, invoice.AmountPaid);
        }

        // ── Sending and viewing ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Stamps the send timestamps and moves a Draft to Sent. Called by InvoiceEmailService
        /// AFTER the mail actually goes out, so a failed send never leaves an invoice claiming the
        /// client has it.
        /// </summary>
        public async Task MarkSentAsync(CommercialInvoice invoice, int userId, string recipient)
        {
            var isFirstSend = invoice.FirstSentAt == null;

            invoice.FirstSentAt ??= DateTime.UtcNow;
            invoice.LastSentAt = DateTime.UtcNow;

            // Draft is absorbing for the automatic rules, so the send is what promotes it - the
            // one legitimate manual transition out of Draft.
            if (invoice.Status == InvoiceStatus.Draft) invoice.Status = InvoiceStatus.Sent;

            RecalculateStatus(invoice);
            invoice.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            await LogActivityAsync(invoice.Id,
                isFirstSend ? "invoice_sent" : "invoice_resent",
                isFirstSend
                    ? $"Invoice sent to {recipient}."
                    : $"Invoice resent to {recipient}.",
                userId);
        }

        /// <summary>
        /// Records that the client opened the public page.
        ///
        /// The view COUNT and timestamps are recorded for every open; the STATUS moves only from
        /// Sent, so a Paid, Void, Overdue or Partially Paid invoice never regresses to Viewed.
        /// </summary>
        public async Task RegisterViewAsync(CommercialInvoice invoice, string? ipAddress)
        {
            var isFirstView = invoice.FirstViewedAt == null;

            invoice.FirstViewedAt ??= DateTime.UtcNow;
            invoice.LastViewedAt = DateTime.UtcNow;
            invoice.ViewCount++;

            if (InvoiceStatusPolicy.ShouldMarkViewed(invoice.Status))
                invoice.Status = InvoiceStatus.Viewed;

            await _context.SaveChangesAsync();

            // Only the FIRST open is worth a timeline entry; a client refreshing the page twenty
            // times would otherwise bury everything else in the activity log.
            if (isFirstView)
            {
                await LogActivityAsync(invoice.Id, "invoice_viewed",
                    "Customer opened the invoice.", null, "Customer", ipAddress);
            }
        }

        // ── Reads ─────────────────────────────────────────────────────────────────────────────

        public Task<CommercialInvoice?> FindByTokenAsync(string token) =>
            _context.CommercialInvoices
                .Include(i => i.Items)
                .Include(i => i.Client)
                .Include(i => i.Contract)
                .Include(i => i.ServiceLocation)
                .FirstOrDefaultAsync(i => i.PublicToken == token);

        public async Task<CommercialInvoice> LoadForWriteAsync(int invoiceId) =>
            await _context.CommercialInvoices
                .Include(i => i.Items)
                .Include(i => i.Client)
                .FirstOrDefaultAsync(i => i.Id == invoiceId)
                ?? throw new InvoiceWorkflowException("Invoice not found.");

        /// <summary>
        /// The full admin detail projection, including the internal note and both histories.
        /// Never hand this to an unauthenticated caller - the public page has its own DTO.
        /// </summary>
        public async Task<InvoiceDetailDto?> GetDetailAsync(int invoiceId)
        {
            var invoice = await _context.CommercialInvoices
                .Include(i => i.Items)
                .Include(i => i.Client)
                .Include(i => i.Contract)
                .Include(i => i.ServiceLocation)
                .Include(i => i.CreatedByUser)
                .FirstOrDefaultAsync(i => i.Id == invoiceId);

            if (invoice == null) return null;

            var payments = await _context.CommercialInvoicePayments
                .Include(p => p.RecordedByUser)
                .Where(p => p.CommercialInvoiceId == invoiceId)
                .OrderByDescending(p => p.PaymentDate).ThenByDescending(p => p.Id)
                .ToListAsync();

            var emails = await _context.CommercialInvoiceEmailLogs
                .Where(e => e.CommercialInvoiceId == invoiceId)
                .OrderByDescending(e => e.SentAt)
                .ToListAsync();

            var activity = await _context.CommercialInvoiceActivityLogs
                .Where(a => a.CommercialInvoiceId == invoiceId)
                .OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.Id)
                .Take(200)
                .ToListAsync();

            var billingContact = await ResolveBillingContactAsync(invoice.ContractClientId);

            var attempts = await _context.CommercialInvoicePaymentAttempts
                .Where(a => a.CommercialInvoiceId == invoiceId)
                .OrderByDescending(a => a.Id)
                .Take(50)
                .ToListAsync();

            return new InvoiceDetailDto
            {
                Id = invoice.Id,
                InvoiceNumber = invoice.InvoiceNumber,
                PublicUrl = BuildPublicUrl(invoice.PublicToken),
                Status = invoice.Status,
                StatusLabel = InvoiceStatusPolicy.Label(invoice.Status),

                ContractClientId = invoice.ContractClientId,
                ClientName = invoice.Client?.LegalEntityName ?? string.Empty,
                BillingContactName = billingContact?.FullName,
                BillingEmail = billingContact?.Email ?? invoice.Client?.NoticeEmail,
                BillingPhone = billingContact?.Phone ?? invoice.Client?.Phone,
                BillingAddress = BuildClientAddress(invoice.Client),

                ContractId = invoice.ContractId,
                ContractNumber = invoice.Contract?.ContractNumber,

                ContractServiceLocationId = invoice.ContractServiceLocationId,
                ServiceAddress = invoice.ServiceAddress,

                InvoiceDate = invoice.InvoiceDate,
                DueDate = invoice.DueDate,
                DueTerms = invoice.DueTerms,
                ServiceStartDate = invoice.ServiceStartDate,
                ServiceEndDate = invoice.ServiceEndDate,

                PoNumber = invoice.PoNumber,
                ClientReference = invoice.ClientReference,

                SubTotal = invoice.SubTotal,
                DiscountType = invoice.DiscountType,
                DiscountValue = invoice.DiscountValue,
                DiscountAmount = invoice.DiscountAmount,
                TaxType = invoice.TaxType,
                TaxRate = invoice.TaxRate,
                TaxAmount = invoice.TaxAmount,
                Total = invoice.Total,
                AmountPaid = invoice.AmountPaid,
                BalanceDue = invoice.BalanceDue,
                Overpayment = InvoiceCalculator.ResolveOverpayment(invoice.Total, invoice.AmountPaid),

                Currency = invoice.Currency,
                PaymentMethod = invoice.PaymentMethod,

                CustomerNote = invoice.CustomerNote,
                InternalNote = invoice.InternalNote,

                FirstSentAt = invoice.FirstSentAt,
                LastSentAt = invoice.LastSentAt,
                FirstViewedAt = invoice.FirstViewedAt,
                LastViewedAt = invoice.LastViewedAt,
                ViewCount = invoice.ViewCount,
                PaidAt = invoice.PaidAt,
                VoidedAt = invoice.VoidedAt,
                VoidReason = invoice.VoidReason,

                CreatedByName = BuildUserName(invoice.CreatedByUser),
                CreatedAt = invoice.CreatedAt,
                UpdatedAt = invoice.UpdatedAt,
                DuplicatedFromInvoiceId = invoice.DuplicatedFromInvoiceId,

                Items = invoice.Items.OrderBy(i => i.SortOrder).Select(ToItemDto).ToList(),
                Payments = payments.Select(p => new InvoicePaymentDto
                {
                    Id = p.Id,
                    Amount = p.Amount,
                    PaymentDate = p.PaymentDate,
                    PaymentMethod = p.PaymentMethod,
                    PaymentMethodLabel = PaymentMethodLabel(p.PaymentMethod),
                    TransactionReference = p.TransactionReference,
                    InternalNote = p.InternalNote,
                    IsReversal = p.IsReversal,
                    RecordedByName = BuildUserName(p.RecordedByUser),
                    CreatedAt = p.CreatedAt
                }).ToList(),
                EmailHistory = emails.Select(e => new InvoiceEmailLogDto
                {
                    Id = e.Id,
                    EmailType = e.EmailType,
                    EmailTypeLabel = EmailTypeLabel(e.EmailType),
                    Recipient = e.Recipient,
                    Subject = e.Subject,
                    Status = e.Status,
                    FailureReason = e.FailureReason,
                    SentAt = e.SentAt
                }).ToList(),
                Activity = activity.Select(a => new InvoiceActivityLogDto
                {
                    Id = a.Id,
                    Action = a.Action,
                    Description = a.Description,
                    ActorName = a.ActorName,
                    CreatedAt = a.CreatedAt
                }).ToList(),

                PaymentAttempts = attempts.Select(a => new InvoicePaymentAttemptDto
                {
                    Id = a.Id,
                    Provider = a.Provider,
                    PaymentMethod = a.PaymentMethod,
                    PaymentMethodLabel = PaymentMethodLabel(a.PaymentMethod),
                    Amount = a.Amount,
                    Currency = a.Currency,
                    Status = a.Status,
                    StatusLabel = AttemptStatusLabel(a.Status),
                    StripePaymentIntentId = a.StripePaymentIntentId,
                    PaymentSourceLabel = a.PaymentSourceLabel,
                    FailureCode = a.FailureCode,
                    FailureMessage = a.FailureMessage,
                    CreatedAt = a.CreatedAt,
                    CompletedAt = a.CompletedAt,
                    IsInFlight = a.IsInFlight
                }).ToList(),

                HasPaymentInProgress = attempts.Any(a =>
                    a.Status == InvoicePaymentAttemptStatus.Processing
                    || (a.Status == InvoicePaymentAttemptStatus.CheckoutOpen
                        && a.CreatedAt >= DateTime.UtcNow.AddHours(-24))),

                CanEdit = InvoiceStatusPolicy.CanEdit(invoice.Status),
                CanEditMonetaryValues = InvoiceStatusPolicy.CanEditMonetaryValues(invoice.Status),
                EditRequiresWarning = InvoiceStatusPolicy.EditRequiresWarning(invoice.Status),
                CanSend = InvoiceStatusPolicy.CanSend(invoice.Status),
                CanRecordPayment = InvoiceStatusPolicy.CanRecordPayment(invoice.Status),
                CanVoid = InvoiceStatusPolicy.CanVoid(invoice.Status),
                CanDelete = InvoiceStatusPolicy.CanDelete(invoice.Status),
                CanSendReminder = InvoiceStatusPolicy.CanSendReminder(invoice.Status)
            };
        }

        public static InvoiceItemDto ToItemDto(CommercialInvoiceItem i) => new()
        {
            Id = i.Id,
            Description = i.Description,
            Quantity = i.Quantity,
            UnitPrice = i.UnitPrice,
            Amount = i.Amount,
            SortOrder = i.SortOrder
        };

        /// <summary>
        /// Builds the public projection. A deliberately separate assembly from the admin DTO -
        /// see PublicInvoiceDto for why filtering the admin one would be the wrong default.
        /// </summary>
        public async Task<PublicInvoiceDto> ToPublicDtoAsync(CommercialInvoice invoice)
        {
            var settings = await _billing.GetOrCreateAsync();
            var contact = await ResolveBillingContactAsync(invoice.ContractClientId);
            var options = await BuildPaymentOptionsAsync(invoice, settings);

            return new PublicInvoiceDto
            {
                InvoiceNumber = invoice.InvoiceNumber,
                Status = invoice.Status,
                StatusLabel = InvoiceStatusPolicy.Label(invoice.Status),

                InvoiceDate = invoice.InvoiceDate,
                DueDate = invoice.DueDate,
                ServiceStartDate = invoice.ServiceStartDate,
                ServiceEndDate = invoice.ServiceEndDate,

                ClientName = invoice.Client?.LegalEntityName ?? string.Empty,
                BillingContactName = contact?.FullName,
                BillingAddress = BuildClientAddress(invoice.Client),
                ServiceAddress = invoice.ServiceAddress,
                ContractNumber = invoice.Contract?.ContractNumber,
                PoNumber = invoice.PoNumber,
                ClientReference = invoice.ClientReference,

                Items = invoice.Items.OrderBy(i => i.SortOrder).Select(ToItemDto).ToList(),

                SubTotal = invoice.SubTotal,
                DiscountAmount = invoice.DiscountAmount,
                TaxType = invoice.TaxType,
                TaxRate = invoice.TaxRate,
                TaxAmount = invoice.TaxAmount,
                Total = invoice.Total,
                AmountPaid = invoice.AmountPaid,
                BalanceDue = invoice.BalanceDue,
                Currency = invoice.Currency,
                PaidAt = invoice.PaidAt,
                CustomerNote = invoice.CustomerNote,

                PaymentMethod = invoice.PaymentMethod,
                // Bank details appear only when manual ACH is switched on AND complete. A void
                // invoice is not collectable, so it prints none at all — inviting a payment
                // nobody can apply is worse than saying nothing.
                PaymentInstructions = options.ManualAchAvailable
                    ? BillingSettingsService.BuildPaymentInstructions(
                        settings, invoice.InvoiceNumber, invoice.PaymentMethod)
                    : null,
                PaymentOptions = options,
                Company = BillingSettingsService.BuildCompany(settings)
            };
        }

        /// <summary>
        /// What the customer may do right now.
        ///
        /// A settled, void or draft invoice offers NOTHING — every route is switched off at once
        /// rather than each surface remembering to check. Everything else is the intersection of
        /// "the owner enabled it" and "it is currently usable".
        /// </summary>
        public async Task<PublicPaymentOptionsDto> BuildPaymentOptionsAsync(
            CommercialInvoice invoice, BillingSettings settings)
        {
            var options = new PublicPaymentOptionsDto();

            var collectable = invoice.Status != InvoiceStatus.Void
                              && invoice.Status != InvoiceStatus.Draft
                              && invoice.BalanceDue > 0m;

            if (!collectable) return options;

            options.ManualAchAvailable = BillingSettingsService.CanOfferManualAch(settings);

            // Attempts are only consulted for an invoice that can still take money, so a paid
            // invoice never shows a stale "processing" banner.
            var attempts = await _context.CommercialInvoicePaymentAttempts
                .Where(a => a.CommercialInvoiceId == invoice.Id)
                .OrderByDescending(a => a.Id)
                .Take(20)
                .ToListAsync();

            var cutoff = DateTime.UtcNow.AddHours(-24);
            var inFlight = attempts.FirstOrDefault(a =>
                a.Status == InvoicePaymentAttemptStatus.Processing
                || (a.Status == InvoicePaymentAttemptStatus.CheckoutOpen && a.CreatedAt >= cutoff));

            if (inFlight != null)
            {
                options.PaymentInProgress = true;
                options.ProcessingAmount = inFlight.Amount;
                options.ProcessingStartedAt = inFlight.CreatedAt;

                // Both buttons stay OFF while money is in flight. This is the duplicate-payment
                // guard's visible half: ACH takes days to settle and the invoice reads unpaid the
                // whole time, so without it a customer would quite reasonably pay again.
                return options;
            }

            options.StripeAchAvailable = settings.StripeAchEnabled;
            options.StripeCardAvailable = settings.StripeCardEnabled;

            // A failed attempt with nothing in flight: say so plainly and let them retry. The
            // wording is FIXED here rather than taken from Stripe — its message is for admins.
            var lastTerminal = attempts.FirstOrDefault(a =>
                a.Status is InvoicePaymentAttemptStatus.Failed
                         or InvoicePaymentAttemptStatus.Succeeded);

            if (lastTerminal?.Status == InvoicePaymentAttemptStatus.Failed)
            {
                options.LastAttemptFailed = true;
                options.LastFailureMessage =
                    "Bank payment was unsuccessful. Please try again or use another payment method.";
            }

            return options;
        }

        // ── Activity trail ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Appends a sentence to the invoice's timeline. The actor name is resolved once and
        /// stored, so a later rename or a deleted admin account does not blank out history.
        /// </summary>
        public async Task LogActivityAsync(
            int invoiceId, string action, string description, int? userId,
            string? actorNameOverride = null, string? ipAddress = null, object? metadata = null)
        {
            var actorName = actorNameOverride;

            if (actorName == null && userId.HasValue)
            {
                var user = await _context.Users
                    .Where(u => u.Id == userId.Value)
                    .Select(u => new { u.FirstName, u.LastName })
                    .FirstOrDefaultAsync();
                actorName = user == null ? null : $"{user.FirstName} {user.LastName}".Trim();
            }

            _context.CommercialInvoiceActivityLogs.Add(new CommercialInvoiceActivityLog
            {
                CommercialInvoiceId = invoiceId,
                Action = action,
                Description = description,
                UserId = userId,
                ActorName = actorName ?? "System",
                IpAddress = ipAddress,
                MetadataJson = metadata == null ? null : JsonSerializer.Serialize(metadata),
                CreatedAt = DateTime.UtcNow
            });

            await _context.SaveChangesAsync();
        }

        // ── Helpers ───────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Refuses a contract that belongs to a different client. Without this an admin could
        /// attach one client's agreement to another client's invoice, and the contract number
        /// printed on it would disclose a third party's paperwork reference.
        /// </summary>
        private async Task ValidateContractBelongsToClientAsync(int? contractId, int clientId)
        {
            if (contractId is not > 0) return;

            var owner = await _context.Contracts
                .Where(c => c.Id == contractId.Value)
                .Select(c => (int?)c.ContractClientId)
                .FirstOrDefaultAsync();

            if (owner == null)
                throw new InvoiceWorkflowException("The selected contract no longer exists.");

            if (owner.Value != clientId)
                throw new InvoiceWorkflowException(
                    "That contract belongs to a different client. Choose a contract for this client.");
        }

        /// <summary>
        /// The address as billed. Taken from the chosen location when the admin did not type one,
        /// and stored flat on the invoice rather than joined at read time - an invoice is a record
        /// of what was billed, and re-addressing a location later must not rewrite it.
        /// </summary>
        private async Task<string?> ResolveServiceAddressAsync(SaveInvoiceDto dto)
        {
            var typed = Trim(dto.ServiceAddress);
            if (!string.IsNullOrWhiteSpace(typed)) return typed;

            if (dto.ContractServiceLocationId is not > 0) return null;

            var location = await _context.ContractServiceLocations
                .FirstOrDefaultAsync(l => l.Id == dto.ContractServiceLocationId.Value);

            return location == null ? null : FormatLocation(location);
        }

        public static string FormatLocation(ContractServiceLocation l) =>
            string.Join(", ", new[]
            {
                l.Address,
                l.City,
                string.Join(" ", new[] { l.State, l.Zip }.Where(x => !string.IsNullOrWhiteSpace(x)))
            }.Where(x => !string.IsNullOrWhiteSpace(x)));

        public static string? BuildClientAddress(ContractClient? c)
        {
            if (c == null) return null;
            return string.Join(", ", new[]
            {
                c.PrincipalAddress,
                c.City,
                string.Join(" ", new[] { c.State, c.Zip }.Where(x => !string.IsNullOrWhiteSpace(x)))
            }.Where(x => !string.IsNullOrWhiteSpace(x)));
        }

        /// <summary>
        /// The person an invoice is addressed to: the client's active billing/signer contact.
        /// Falls back to the company's notice email, which is the address the contract itself
        /// serves notice on.
        /// </summary>
        public async Task<ContractContact?> ResolveBillingContactAsync(int clientId) =>
            await _context.ContractContacts
                .Where(c => c.ContractClientId == clientId && c.IsActive)
                .OrderBy(c => c.Role == ContractContactRole.ClientSigner ? 0 : 1)
                .ThenBy(c => c.Id)
                .FirstOrDefaultAsync();

        public static string? BuildUserName(Models.User? user) =>
            user == null ? null : $"{user.FirstName} {user.LastName}".Trim();

        public static string PaymentMethodLabel(InvoicePaymentRecordMethod method) => method switch
        {
            InvoicePaymentRecordMethod.AchBankTransfer => "ACH Bank Transfer",
            InvoicePaymentRecordMethod.WireTransfer => "Wire Transfer",
            InvoicePaymentRecordMethod.Check => "Check",
            InvoicePaymentRecordMethod.Card => "Card",
            InvoicePaymentRecordMethod.Cash => "Cash",
            _ => "Other"
        };

        /// <summary>
        /// Human label for an attempt. "Processing" is worded for the reader: ACH genuinely takes
        /// days, and an admin seeing it needs to know the money is on its way rather than stuck.
        /// </summary>
        public static string AttemptStatusLabel(InvoicePaymentAttemptStatus status) => status switch
        {
            InvoicePaymentAttemptStatus.Created => "Started",
            InvoicePaymentAttemptStatus.CheckoutOpen => "Awaiting customer",
            InvoicePaymentAttemptStatus.Processing => "Processing",
            InvoicePaymentAttemptStatus.Succeeded => "Succeeded",
            InvoicePaymentAttemptStatus.Failed => "Failed",
            InvoicePaymentAttemptStatus.Expired => "Expired",
            InvoicePaymentAttemptStatus.Canceled => "Canceled",
            _ => status.ToString()
        };

        public static string EmailTypeLabel(InvoiceEmailType type) => type switch
        {
            InvoiceEmailType.InvoiceSent => "Invoice Sent",
            InvoiceEmailType.InvoiceResent => "Invoice Resent",
            InvoiceEmailType.PaymentReceipt => "Payment Receipt",
            InvoiceEmailType.Reminder => "Payment Reminder",
            _ => type.ToString()
        };

        private static bool IsUniqueViolation(DbUpdateException ex) =>
            ex.InnerException?.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) == true
            || ex.InnerException?.Message.Contains("unique", StringComparison.OrdinalIgnoreCase) == true;

        private static string? Trim(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>Small readability helpers used across the invoice services.</summary>
    public static class CommercialInvoiceExtensions
    {
        public static bool HasBeenSent(this CommercialInvoice invoice) => invoice.FirstSentAt.HasValue;
    }
}
