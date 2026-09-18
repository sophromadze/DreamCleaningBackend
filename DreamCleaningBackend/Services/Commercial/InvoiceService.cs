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
                    ServiceDatesJson = SerializeServiceDates(dto.ServiceDates),
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
                if (dto.OrderIds?.Count > 0)
                    await InvoiceOrderLinkService.ApplySelectionAsync(_context, invoice,
                        new SaveInvoiceOrdersDto { OrderIds = dto.OrderIds, NegotiatedGroupTotal = dto.NegotiatedGroupTotal });
                ApplyTaxDefaultIfRequested(settings, dto, userId);

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
                    foreach (var link in invoice.CoveredOrders) _context.Entry(link).State = EntityState.Detached;
                    continue;
                }

                await LogActivityAsync(invoice.Id, "invoice_created",
                    $"Invoice {invoice.InvoiceNumber} created for {client.LegalEntityName}.", userId);

                return invoice;
            }

            throw new InvoiceWorkflowException("Could not allocate an invoice number. Please try again.");
        }

        public async Task<InvoiceOrdersResultDto> PreviewLinkedOrdersAsync(SaveInvoiceDto dto, int? invoiceId)
        {
            await ValidateContractBelongsToClientAsync(dto.ContractId, dto.ContractClientId);
            if (invoiceId.HasValue)
            {
                var saved = await _context.CommercialInvoices.AsNoTracking().FirstOrDefaultAsync(i => i.Id == invoiceId.Value)
                    ?? throw new InvoiceWorkflowException("Invoice not found.");
                if (saved.Status != InvoiceStatus.Draft)
                    throw new InvoiceWorkflowException("Only Draft invoices can preview a new cleaning selection.");
            }
            var preview = new CommercialInvoice { Id = invoiceId ?? 0, ContractClientId = dto.ContractClientId,
                ContractId = dto.ContractId, Status = InvoiceStatus.Draft };
            ApplyFinancials(preview, dto);
            return await InvoiceOrderLinkService.ApplySelectionAsync(_context, preview,
                new SaveInvoiceOrdersDto { OrderIds = dto.OrderIds ?? new(), NegotiatedGroupTotal = dto.NegotiatedGroupTotal }, updateLinks: false);
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

            if (invoice.Status != InvoiceStatus.Draft && dto.OrderIds != null)
                throw new InvoiceWorkflowException("Linked cleaning selections can only be changed on Draft invoices.");

            var driftChoices = dto.DraftDriftChoices ?? new List<string>();
            if (driftChoices.Any(c => c is not ("KeepPrice" or "CurrentPrice" or "KeepTax" or "CurrentTax"))
                || (driftChoices.Count > 0 && invoice.Status != InvoiceStatus.Draft))
                throw new InvoiceWorkflowException("Drift choices apply only to a Draft invoice.");

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
            // Which cleanings the invoice covers is a factual correction, not a monetary change -
            // an admin fixing a service date on a paid invoice is ordinary bookkeeping, the same
            // reasoning that keeps the PO number editable.
            invoice.ServiceDatesJson = SerializeServiceDates(dto.ServiceDates);

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

                if (invoice.Status == InvoiceStatus.Draft && (dto.OrderIds != null || invoice.CoveredOrders.Count > 0))
                {
                    await InvoiceOrderLinkService.ApplySelectionAsync(_context, invoice, new SaveInvoiceOrdersDto {
                        OrderIds = dto.OrderIds ?? invoice.CoveredOrders.Select(l => l.OrderId).ToList(),
                        NegotiatedGroupTotal = dto.OrderIds == null ? invoice.NegotiatedOrderGroupTotal : dto.NegotiatedGroupTotal
                    });
                    if (dto.OrderIds?.Count == 0) { ApplyItems(invoice, dto.Items); ApplyFinancials(invoice, dto); }
                }

                var settings = await _billing.GetOrCreateAsync();
                ApplyTaxDefaultIfRequested(settings, dto, userId);
            }

            // Saving unrelated fields does not acknowledge price/tax drift. Only an explicit
            // review choice dismisses its warning; the other warnings remain visible.
            var remainingWarnings = ParseDraftWarnings(invoice.DraftWarningsJson).Where(w =>
                !(w.StartsWith("Current contract pricing") && driftChoices.Any(c => c.EndsWith("Price")))
                && !(w.StartsWith("Current contract tax") && driftChoices.Any(c => c.EndsWith("Tax")))).ToList();
            invoice.DraftWarningsJson = remainingWarnings.Count == 0 ? null : JsonSerializer.Serialize(remainingWarnings);

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

            foreach (var choice in driftChoices)
                await LogActivityAsync(invoice.Id, "invoice_drift_choice", $"Draft review: {choice}. Total {invoice.Total:C}; tax {invoice.TaxAmount:C} at {invoice.TaxRate}%.", userId);
            return invoice;
        }

        // ── Service dates ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The individual visit dates an invoice covers, read back off the row. Empty when it
        /// records none - which is legitimate and common, and is why every reader falls back to
        /// the period bounds rather than treating this as required.
        /// </summary>
        /// <summary>
        /// The generation warnings frozen onto a draft. An unreadable column returns nothing
        /// rather than taking the invoice page down with it — the same tolerance
        /// <see cref="ParseServiceDates"/> applies, and for the same reason: a warning is
        /// advisory, and losing it must never cost an admin the invoice itself.
        /// </summary>
        public static List<string> ParseDraftWarnings(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new List<string>();
            try
            {
                return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
            }
            catch (JsonException)
            {
                return new List<string>();
            }
        }

        public static List<DateTime> ParseServiceDates(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new List<DateTime>();
            try
            {
                return JsonSerializer.Deserialize<List<DateTime>>(json)?
                           .Select(d => d.Date)
                           .Distinct()
                           .OrderBy(d => d)
                           .ToList()
                       ?? new List<DateTime>();
            }
            catch (JsonException)
            {
                // An unreadable column must not take a whole invoice page down with it; the period
                // bounds beside it still describe the work.
                return new List<DateTime>();
            }
        }

        /// <summary>Null rather than "[]" for an empty list, so "records none" is one state.</summary>
        private static string? SerializeServiceDates(IEnumerable<DateTime>? dates)
        {
            var normalised = (dates ?? Enumerable.Empty<DateTime>())
                .Select(d => d.Date)
                .Distinct()
                .OrderBy(d => d)
                .ToList();

            return normalised.Count == 0 ? null : JsonSerializer.Serialize(normalised);
        }

        /// <summary>The label and text every surface prints for "what does this invoice cover".</summary>
        public static ServiceDateDisplay DescribeServiceDates(CommercialInvoice invoice) =>
            ServiceDateFormatter.Describe(
                ParseServiceDates(invoice.ServiceDatesJson),
                invoice.ServiceStartDate,
                invoice.ServiceEndDate);

        /// <summary>
        /// Writes a rate typed on the invoice form back as the default for FUTURE documents, when
        /// the admin asked for that.
        ///
        /// It touches <c>BillingSettings</c> and nothing else. A finalized invoice and a signed
        /// contract each carry their own rate snapshot and are never recomputed from this row -
        /// which is precisely why the rate is snapshotted per document.
        /// </summary>
        private void ApplyTaxDefaultIfRequested(BillingSettings settings, SaveInvoiceDto dto, int userId)
        {
            if (!dto.SaveTaxRateAsDefault) return;
            _billing.ApplyTaxDefaults(settings, dto.TaxType, dto.TaxRate, null, userId);
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

        /// <summary>
        /// Only an unsent Draft may be removed outright.
        ///
        /// KEPT EXACTLY AS IT WAS. The newer <see cref="PermanentlyDeleteAsync"/> is a superset -
        /// it accepts a Draft too - but this is what the long-standing
        /// <c>DELETE api/admin/commercial/invoices/{id}</c> route calls, and a route other code and
        /// other admins already rely on does not change meaning underneath them.
        /// </summary>
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

        // ── Archive ───────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Takes an invoice off the default list. CHANGES NOTHING ELSE: not the status, not a
        /// figure, not a payment row, and not the public token - a client who was sent this
        /// invoice keeps being able to open it, because archiving is our filing decision and not
        /// a statement to them.
        ///
        /// Idempotent, so a double-click or a retried request is not an error.
        /// </summary>
        public async Task<CommercialInvoice> ArchiveAsync(int invoiceId, int userId)
        {
            var invoice = await LoadForWriteAsync(invoiceId);
            if (invoice.IsArchived) return invoice;

            invoice.IsArchived = true;
            invoice.ArchivedAt = DateTime.UtcNow;
            invoice.ArchivedByUserId = userId;
            invoice.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();
            await LogActivityAsync(invoice.Id, "invoice_archived",
                "Invoice archived. Figures, payments and history are unchanged.", userId);

            return invoice;
        }

        /// <summary>Puts an archived invoice back on the active list.</summary>
        public async Task<CommercialInvoice> UnarchiveAsync(int invoiceId, int userId)
        {
            var invoice = await LoadForWriteAsync(invoiceId);
            if (!invoice.IsArchived) return invoice;

            invoice.IsArchived = false;
            invoice.ArchivedAt = null;
            invoice.ArchivedByUserId = null;
            invoice.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();
            await LogActivityAsync(invoice.Id, "invoice_unarchived", "Invoice unarchived.", userId);

            return invoice;
        }

        // ── Permanent delete ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Everything <see cref="InvoiceHardDeletePolicy"/> needs, counted in the database.
        /// Separate from the delete because the detail endpoint asks the same question to decide
        /// whether to offer the option at all.
        /// </summary>
        public async Task<InvoiceDeletionFacts> GatherDeletionFactsAsync(int invoiceId)
        {
            var invoice = await _context.CommercialInvoices
                .AsNoTracking()
                .FirstOrDefaultAsync(i => i.Id == invoiceId)
                ?? throw new InvoiceWorkflowException("Invoice not found.");

            var paymentCount = await _context.CommercialInvoicePayments
                .CountAsync(p => p.CommercialInvoiceId == invoiceId);

            // Only attempts that actually reached Stripe. A row created and abandoned before the
            // API call carries neither id and is ours alone, so it does not protect anything.
            var attemptCount = await _context.CommercialInvoicePaymentAttempts
                .CountAsync(a => a.CommercialInvoiceId == invoiceId
                                 && (a.StripeCheckoutSessionId != null || a.StripePaymentIntentId != null));

            var committedAllocations = await _context.CommercialInvoiceOrders
                .CountAsync(o => o.CommercialInvoiceId == invoiceId && o.CommittedAt != null);

            return new InvoiceDeletionFacts
            {
                Status = invoice.Status,
                AmountPaid = invoice.AmountPaid,
                PaymentCount = paymentCount,
                ExternalPaymentAttemptCount = attemptCount,
                CommittedOrderAllocationCount = committedAllocations
            };
        }

        /// <summary>The blocker sentence for this invoice, or null when it may be destroyed.</summary>
        public async Task<string?> DescribeHardDeleteBlockerAsync(int invoiceId) =>
            InvoiceHardDeletePolicy.DescribeBlocker(await GatherDeletionFactsAsync(invoiceId));

        /// <summary>
        /// PERMANENTLY destroys an invoice that never touched money - a test or a mistake.
        ///
        /// Everything invoice-owned cascades from the row itself (items, payment attempts, email
        /// logs, activity logs, reminders and any uncommitted order allocations), so the delete is
        /// one Remove inside one transaction. There is deliberately no manual RemoveRange of those
        /// children: duplicating a cascade in code is how one of them gets forgotten when a new
        /// child table is added.
        ///
        /// WHAT IT CANNOT REACH, by policy rather than by luck: payment rows and Stripe activity
        /// make the invoice undeletable in the first place, so the cascade on
        /// <c>CommercialInvoicePayments</c> never fires here. The client, the contract, the service
        /// location and the orders are all Restrict- or SetNull-mapped and are untouched.
        /// </summary>
        public async Task<CommercialInvoice> PermanentlyDeleteAsync(int invoiceId, int userId)
        {
            var invoice = await LoadForWriteAsync(invoiceId);

            var blocker = InvoiceHardDeletePolicy.DescribeBlocker(
                await GatherDeletionFactsAsync(invoiceId));
            if (blocker != null) throw new InvoiceWorkflowException(blocker);

            await using var transaction = await _context.Database.BeginTransactionAsync();

            _context.CommercialInvoices.Remove(invoice);
            await _context.SaveChangesAsync();
            await transaction.CommitAsync();

            _logger.LogInformation(
                "Invoice {Number} permanently deleted by user {UserId}.", invoice.InvoiceNumber, userId);

            // Returned so the caller can write the surviving audit row from it - after this the
            // invoice's own activity log is gone with the invoice.
            return invoice;
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

            // The draft warnings described something to check BEFORE sending. It has been sent.
            invoice.DraftWarningsJson = null;

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
                .Include(i => i.CoveredOrders)
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


            // Null when this invoice may be permanently destroyed; otherwise the sentence naming
            // the financial activity that protects it. Resolved here so the action dialog can
            // explain itself before the admin types a confirmation.
            var hardDeleteBlocker = InvoiceHardDeletePolicy.DescribeBlocker(
                await GatherDeletionFactsAsync(invoiceId));
            var serviceDates = DescribeServiceDates(invoice);
            var currentContract = invoice.Status == InvoiceStatus.Draft && invoice.Contract != null
                ? await RecurringInvoiceService.LoadSnapshotAsync(_context, invoice.Contract) : null;

            // A Stripe payment AND a manual one AND more money in than was billed. Nothing is
            // corrected automatically - the money really did arrive twice and only a person can
            // decide which half to refund - but it is surfaced, because the customer will notice a
            // duplicate debit long before a month-end reconciliation does.
            var hasStripePayment = payments.Any(p => p.Provider == InvoicePaymentProvider.Stripe && !p.IsReversal);
            var hasManualPayment = payments.Any(p => p.Provider == InvoicePaymentProvider.Manual && !p.IsReversal);
            var overpaid = InvoiceCalculator.ResolveOverpayment(invoice.Total, invoice.AmountPaid);

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
                ServiceDates = ParseServiceDates(invoice.ServiceDatesJson),
                ServiceDateLabel = serviceDates.HasValue ? serviceDates.Label : null,
                ServiceDateText = serviceDates.HasValue ? serviceDates.Text : null,

                PoNumber = invoice.PoNumber,
                ClientReference = invoice.ClientReference,

                // Warnings raised when this draft was GENERATED, stored on the row rather than
                // returned once — so they are visible in the editor whichever route the admin took
                // to get here. Cleared when the invoice leaves Draft or its warning is explicitly acknowledged.
                DraftWarnings = ParseDraftWarnings(invoice.DraftWarningsJson),
                CurrentContractUnitPrice = currentContract == null ? null
                    : currentContract.Pricing.PriceMode == Models.Contracts.ContractPriceMode.TaxInclusive
                        ? currentContract.Pricing.TotalPrice : currentContract.Pricing.PreTaxPrice,
                CurrentContractTaxType = currentContract == null ? null
                    : currentContract.Pricing.PriceMode == Models.Contracts.ContractPriceMode.TaxInclusive
                        ? InvoiceTaxType.Included : InvoiceTaxType.Added,
                CurrentContractTaxRate = currentContract?.Pricing.SalesTaxRatePercent,
                CleaningsCovered = await _context.CommercialInvoiceOrders
                    .Where(l => l.CommercialInvoiceId == invoiceId)
                    .OrderBy(l => l.Order!.ServiceDate).ThenBy(l => l.OrderId)
                    .Select(l => new InvoiceOrderAllocationDto {
                        OrderId = l.OrderId, ServiceDate = l.Order!.ServiceDate,
                        ServiceAddress = l.Order.ServiceAddress + (l.Order.AptSuite == null ? "" : ", " + l.Order.AptSuite),
                        AllocatedAmount = l.AllocatedAmount, OriginalOrderTotal = l.OriginalOrderTotal,
                        OrderStatus = l.Order.Status, IsProposal = l.CommittedAt == null,
                        CommittedAt = l.CommittedAt, ActivatedOrderAt = l.ActivatedOrderAt
                    }).ToListAsync(),
                NegotiatedOrderGroupTotal = invoice.NegotiatedOrderGroupTotal,

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
                Payments = payments.Select(ToPaymentDto).ToList(),
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

                // Only a SETTLING payment counts, matching the customer's view. An open Checkout
                // Session the customer may have abandoned is not a payment in progress.
                HasPaymentInProgress = attempts.Any(a => a.IsAwaitingSettlement),

                CanEdit = InvoiceStatusPolicy.CanEdit(invoice.Status),
                CanEditMonetaryValues = InvoiceStatusPolicy.CanEditMonetaryValues(invoice.Status),
                EditRequiresWarning = InvoiceStatusPolicy.EditRequiresWarning(invoice.Status),
                CanSend = InvoiceStatusPolicy.CanSend(invoice.Status),
                CanRecordPayment = InvoiceStatusPolicy.CanRecordPayment(invoice.Status, invoice.BalanceDue),
                CanVoid = InvoiceStatusPolicy.CanVoid(invoice.Status),
                CanDelete = InvoiceStatusPolicy.CanDelete(invoice.Status),
                CanSendReminder = InvoiceStatusPolicy.CanSendReminder(invoice.Status),

                IsArchived = invoice.IsArchived,
                ArchivedAt = invoice.ArchivedAt,
                CanHardDelete = hardDeleteBlocker == null,
                CannotHardDeleteReason = hardDeleteBlocker,

                PotentialDuplicatePayment = hasStripePayment && hasManualPayment && overpaid > 0m,
                HasProcessingStripePayment =
                    attempts.Any(a => a.Status == InvoicePaymentAttemptStatus.Processing)
            };
        }

        /// <summary>
        /// One payment row for the admin ledger.
        ///
        /// MONEY RECEIVED IS POSITIVE and only a reversal is negative, so nothing here paints a
        /// sign onto <c>Amount</c> - a real settlement printed as "-$925.43" is the regression
        /// InvoicePaymentSignTests exists for. The processing fee is shown BESIDE the amount, never
        /// folded into it: the invoice was met by <c>Amount</c>, and the extra is what the payment
        /// method cost the customer.
        /// </summary>
        public static InvoicePaymentDto ToPaymentDto(CommercialInvoicePayment p) => new()
        {
            Id = p.Id,
            Amount = p.Amount,
            ProcessingFee = p.ProcessingFee,
            TotalCharged = InvoiceCalculator.Round2(p.Amount + p.ProcessingFee),
            PaymentDate = p.PaymentDate,
            PaymentMethod = p.PaymentMethod,
            PaymentMethodLabel = PaymentMethodLabel(p.PaymentMethod),
            Provider = p.Provider,
            ProviderLabel = ProviderLabel(p.Provider),
            TransactionReference = p.TransactionReference,
            InternalNote = p.InternalNote,
            IsReversal = p.IsReversal,
            // NULL when a webhook wrote the row, and deliberately not filled in from
            // RecordedByLabel: "recorded by" means a PERSON, and an audit trail that names an
            // admin where there was none is worse than one that says nothing. Which system
            // produced the row is answered by ProviderLabel beside it.
            RecordedByName = BuildUserName(p.RecordedByUser),
            CreatedAt = p.CreatedAt
        };

        /// <summary>
        /// "Manual" or "Stripe (online)". Stated plainly on the ledger because a bank transfer an
        /// admin typed in from a statement must never look like it came through the processor -
        /// the two reconcile against completely different records.
        /// </summary>
        public static string ProviderLabel(InvoicePaymentProvider provider) => provider switch
        {
            InvoicePaymentProvider.Stripe => "Stripe (online)",
            _ => "Manual"
        };

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
            var publicServiceDates = DescribeServiceDates(invoice);

            return new PublicInvoiceDto
            {
                InvoiceNumber = invoice.InvoiceNumber,
                Status = invoice.Status,
                StatusLabel = InvoiceStatusPolicy.Label(invoice.Status),

                InvoiceDate = invoice.InvoiceDate,
                DueDate = invoice.DueDate,
                ServiceStartDate = invoice.ServiceStartDate,
                ServiceEndDate = invoice.ServiceEndDate,
                ServiceDates = ParseServiceDates(invoice.ServiceDatesJson),
                ServiceDateLabel = publicServiceDates.HasValue ? publicServiceDates.Label : null,
                ServiceDateText = publicServiceDates.HasValue ? publicServiceDates.Text : null,

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

            // THE FEE IS QUOTED BEFORE THE CUSTOMER COMMITS TO ANYTHING. Computed here from the
            // invoice's own live balance so a part-payment recorded this morning is reflected, and
            // recomputed identically by the checkout endpoint - the page can only ever DISPLAY it.
            options.AchProcessingFee = AchProcessingFeeCalculator.Resolve(invoice.BalanceDue, settings);
            options.AchTotalWithFee =
                AchProcessingFeeCalculator.ResolveTotalCharge(invoice.BalanceDue, options.AchProcessingFee);
            options.AchProcessingFeeLabel = AchProcessingFeeCalculator.CustomerFacingLabel;
            options.ManualAchFeeNote = AchProcessingFeeCalculator.ManualAchNoFeeNote;

            // Attempts are only consulted for an invoice that can still take money, so a paid
            // invoice never shows a stale "processing" banner.
            var attempts = await _context.CommercialInvoicePaymentAttempts
                .Where(a => a.CommercialInvoiceId == invoice.Id)
                .OrderByDescending(a => a.Id)
                .Take(20)
                .ToListAsync();

            // ONLY A SETTLING PAYMENT BLOCKS THE CUSTOMER — see IsAwaitingSettlement.
            //
            // This used to also count CheckoutOpen, which is set the moment a Session is created.
            // The result was that opening Stripe and closing the tab without paying locked the
            // invoice as "processing" for 24 hours: pay button disabled, no retry, and a banner
            // claiming a payment was under way when nothing had been submitted. Creating a
            // Checkout Session is not evidence of a debit.
            var settling = attempts.FirstOrDefault(a => a.IsAwaitingSettlement);

            if (settling != null)
            {
                options.PaymentInProgress = true;
                options.ProcessingAmount = settling.Amount;
                options.ProcessingFeeAmount = settling.ProcessingFee;
                options.ProcessingTotalCharged = settling.TotalCharged > 0m
                    ? settling.TotalCharged
                    : AchProcessingFeeCalculator.ResolveTotalCharge(settling.Amount, settling.ProcessingFee);
                options.ProcessingStartedAt = settling.CreatedAt;

                // Both buttons stay OFF while money is genuinely in flight. This is the
                // duplicate-payment guard's visible half: ACH takes days to settle and the invoice
                // reads unpaid the whole time, so without it a customer would reasonably pay again.
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
        /// The person an invoice is addressed to: the client's primary billing contact when one is
        /// flagged, otherwise their oldest active client-side contact. Falls back to the company's
        /// notice email, which is the address the contract itself serves notice on.
        ///
        /// The explicit flag is checked FIRST so a client whose accounts-payable contact is not
        /// their signer is billed correctly; the ordering below it is exactly what this resolver
        /// did before the flag existed, so no client's invoices change recipient on deployment.
        /// </summary>
        public async Task<ContractContact?> ResolveBillingContactAsync(int clientId) =>
            await _context.ContractContacts
                .Where(c => c.ContractClientId == clientId && c.IsActive)
                .OrderBy(c => c.IsPrimaryBillingContact ? 0 : 1)
                .ThenBy(c => c.Role == ContractContactRole.ClientSigner ? 0 : 1)
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
