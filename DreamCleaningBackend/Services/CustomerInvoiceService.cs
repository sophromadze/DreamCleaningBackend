using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Helpers.Commercial;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services.Commercial;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace DreamCleaningBackend.Services
{
    /// <summary>A refused invoice action; the message is admin-facing and says why.</summary>
    public class CustomerInvoiceException : Exception
    {
        public CustomerInvoiceException(string message) : base(message) { }
    }

    public record CustomerInvoiceSendResult(bool EmailSent, bool SmsSent, string Message);

    /// <summary>
    /// Regular customer invoices — Admin → Invoices (2026-09). See Models/CustomerInvoice.cs for
    /// the shape and <see cref="CustomerInvoiceStatusPolicy"/> for how status is decided.
    ///
    /// This service never moves money. Creating an invoice creates the part-payment REQUEST it
    /// collects (through <see cref="IOrderPartialPaymentService"/>, the one writer of those rows);
    /// the card payment settles that request through the existing Stripe path, and a bank
    /// transfer is recorded against it with the existing manual-slice endpoint, whose last slice
    /// completes the order (Active, loyalty, subscription, confirmation) exactly like a card.
    ///
    /// An ADDITIONAL invoice bills the order-edit top-up instead — money owed on top of an order
    /// that was already paid, because an admin raised its price afterwards. It creates no request:
    /// its card button is the order's ordinary payment link, which already collects the top-up
    /// (<see cref="OrderAdditionalCharge"/>), and a bank transfer is recorded on the order's Update
    /// History row like any other manual top-up payment.
    /// </summary>
    public interface ICustomerInvoiceService
    {
        Task<List<CustomerInvoice>> CreateAsync(CreateCustomerInvoiceDto dto, int adminUserId, CancellationToken ct = default);
        Task<CustomerInvoiceSendResult> SendAsync(int invoiceId, bool sendEmail, bool sendSms, int adminUserId, CancellationToken ct = default);
        Task VoidAsync(int invoiceId, string? reason, int adminUserId, CancellationToken ct = default);
        Task<List<CustomerInvoiceDto>> ListAsync(string? search, string? status, int? userId, int? orderId, CancellationToken ct = default);
        Task<CustomerInvoiceDto?> GetAsync(int invoiceId, CancellationToken ct = default);
        Task<List<CustomerInvoiceOrderOptionDto>> GetInvoiceableOrdersAsync(int userId, CancellationToken ct = default);
        Task<PublicCustomerInvoiceDto?> GetPublicAsync(string token, CancellationToken ct = default);
        /// <summary>The signed-in customer's OWN invoices (orders they own), newest first. Drafts
        /// an admin has not sent yet are left out — the customer has not been billed by them.</summary>
        Task<List<MyCustomerInvoiceDto>> ListForCustomerAsync(int userId, CancellationToken ct = default);
        /// <summary>The invoice as a PDF (same content as the public page), or null for an unknown token.</summary>
        Task<(byte[] Bytes, string FileName)?> RenderPdfAsync(string token, CancellationToken ct = default);
    }

    public class CustomerInvoiceService : ICustomerInvoiceService
    {
        /// <summary>DCR = Dream Cleaning Regular invoice. DCI stays the commercial prefix.</summary>
        public const string InvoicePrefix = "DCR";

        private readonly ApplicationDbContext _context;
        private readonly IOrderPartialPaymentService _partialPayments;
        private readonly IEmailService _emailService;
        private readonly ISmsService _smsService;
        private readonly IAuditService _auditService;
        private readonly BillingSettingsService _billingSettings;
        private readonly IConfiguration _configuration;
        private readonly ILogger<CustomerInvoiceService> _logger;
        private readonly CustomerInvoicePdfService _pdf;

        public CustomerInvoiceService(
            ApplicationDbContext context,
            IOrderPartialPaymentService partialPayments,
            IEmailService emailService,
            ISmsService smsService,
            IAuditService auditService,
            BillingSettingsService billingSettings,
            IConfiguration configuration,
            ILogger<CustomerInvoiceService> logger,
            CustomerInvoicePdfService? pdf = null)
        {
            _context = context;
            _partialPayments = partialPayments;
            _emailService = emailService;
            _smsService = smsService;
            _auditService = auditService;
            _billingSettings = billingSettings;
            _configuration = configuration;
            _logger = logger;
            _pdf = pdf ?? new CustomerInvoicePdfService();
        }

        private string FrontendUrl => (_configuration["Frontend:Url"] ?? "https://dreamcleaningnyc.com").TrimEnd('/');

        public string PublicUrl(CustomerInvoice invoice) => $"{FrontendUrl}/pay-invoice/{invoice.PublicToken}";

        // ─── Create ─────────────────────────────────────────────────────────────────────────

        public async Task<List<CustomerInvoice>> CreateAsync(CreateCustomerInvoiceDto dto, int adminUserId, CancellationToken ct = default)
        {
            var order = await _context.Orders.FirstOrDefaultAsync(o => o.Id == dto.OrderId, ct)
                ?? throw new CustomerInvoiceException("Order not found.");

            var note = string.IsNullOrWhiteSpace(dto.Note) ? null : dto.Note.Trim();

            // A PAID order that an edit has since made more expensive owes the difference on top.
            // That is billed by an Additional invoice — there is no balance left to split.
            if (order.IsPaid)
            {
                var topUp = await LoadTopUpStateAsync(order, ct);
                if (topUp.Outstanding >= OrderAdditionalCharge.MinimumCollectableAmount)
                    return new List<CustomerInvoice> { await CreateAdditionalAsync(order, topUp, dto, note, adminUserId, ct) };
            }

            // Same eligibility the request itself enforces, stated up front so the admin reads
            // the real reason rather than a part-payment message.
            if (!OrderBalance.CanRequestPartialPayment(order, out var refusal))
                throw new CustomerInvoiceException(refusal!);

            var openInvoices = await LiveInvoicesQuery(order.Id).ToListAsync(ct);
            var amounts = (dto.SplitAmounts ?? new List<decimal>())
                .Select(OrderPricingCalculator.Round2)
                .ToList();
            var isSplit = amounts.Count > 0;

            if (!isSplit)
            {
                if (openInvoices.Count > 0)
                    throw new CustomerInvoiceException(
                        $"This order already has an open invoice ({string.Join(", ", openInvoices.Select(i => i.InvoiceNumber))}). Void it before issuing a new one.");
            }
            else
            {
                if (openInvoices.Any(i => i.Kind == CustomerInvoiceKind.Full))
                    throw new CustomerInvoiceException(
                        "This order already has an invoice for its whole balance. Void it before splitting the payment.");
                if (amounts.Count > 12)
                    throw new CustomerInvoiceException("An order can be split into at most 12 invoices.");
            }

            // The customer's pay link needs the order's secret payment token — created once here
            // so every invoice for this order points at the same card page.
            if (string.IsNullOrEmpty(order.PaymentAccessToken))
                order.PaymentAccessToken = PaymentLinkHelper.GenerateToken();

            var created = new List<CustomerInvoice>();
            using var transaction = await _context.Database.BeginTransactionAsync(ct);
            try
            {
                if (!isSplit)
                {
                    var due = OrderBalance.AmountDue(order);
                    created.Add(await CreateOneAsync(order, CustomerInvoiceKind.Full, due, note, adminUserId, allowAlongside: false, ct));
                }
                else
                {
                    foreach (var amount in amounts)
                        created.Add(await CreateOneAsync(order, CustomerInvoiceKind.Split, amount, note, adminUserId, allowAlongside: true, ct));
                }

                await transaction.CommitAsync(ct);
            }
            catch (PartialPaymentException ex)
            {
                await transaction.RollbackAsync(ct);
                throw new CustomerInvoiceException(ex.Message);
            }

            foreach (var invoice in created)
            {
                await LogAsync(order.Id, "CustomerInvoiceCreated", new
                {
                    invoice.InvoiceNumber,
                    Kind = invoice.Kind.ToString(),
                    invoice.Amount,
                    OrderTotal = order.Total,
                    invoice.Note
                }, adminUserId);
            }

            return created;
        }

        /// <summary>
        /// Bills the order-edit top-up still outstanding on a paid order. One open Additional
        /// invoice at a time — the card page collects the WHOLE outstanding top-up, so two open
        /// ones would each claim the same money.
        /// </summary>
        private async Task<CustomerInvoice> CreateAdditionalAsync(
            Order order, CustomerInvoiceTopUpState topUp, CreateCustomerInvoiceDto dto, string? note,
            int adminUserId, CancellationToken ct)
        {
            if (dto.SplitAmounts is { Count: > 0 })
                throw new CustomerInvoiceException(
                    "An additional charge added after payment is invoiced as one amount — it can't be split.");
            if (OrderStatuses.IsCancelled(order.Status) || OrderStatuses.IsRefunded(order.Status))
                throw new CustomerInvoiceException("This order is cancelled and cannot take payments.");
            if (topUp.Outstanding < OrderBalance.StripeMinimumChargeAmount)
                throw new CustomerInvoiceException("The additional amount is below the $0.50 minimum that can be paid online.");

            var existing = await _context.CustomerInvoices
                .Where(i => i.OrderId == order.Id && i.Kind == CustomerInvoiceKind.Additional && i.VoidedAt == null)
                .ToListAsync(ct);
            var open = existing.FirstOrDefault(i =>
                CustomerInvoiceStatusPolicy.IsPayable(CustomerInvoiceStatusPolicy.Resolve(i, order, null, topUp)));
            if (open != null)
                throw new CustomerInvoiceException(
                    $"This order already has an open invoice for its additional charge ({open.InvoiceNumber}). Void it before issuing a new one.");

            if (string.IsNullOrEmpty(order.PaymentAccessToken))
                order.PaymentAccessToken = PaymentLinkHelper.GenerateToken();

            var invoice = new CustomerInvoice
            {
                InvoiceNumber = await NewInvoiceNumberAsync(ct),
                PublicToken = PaymentLinkHelper.GenerateToken(),
                OrderId = order.Id,
                OrderPartialPaymentId = null,
                Kind = CustomerInvoiceKind.Additional,
                Amount = topUp.Outstanding,
                TopUpCollectedTarget = OrderPricingCalculator.Round2(topUp.CollectedToDate + topUp.Outstanding),
                Note = note,
                CreatedByUserId = adminUserId,
                CreatedAt = DateTime.UtcNow
            };
            _context.CustomerInvoices.Add(invoice);
            await _context.SaveChangesAsync(ct);

            await LogAsync(order.Id, "CustomerInvoiceCreated", new
            {
                invoice.InvoiceNumber,
                Kind = invoice.Kind.ToString(),
                invoice.Amount,
                OrderTotal = order.Total,
                invoice.Note
            }, adminUserId);

            return invoice;
        }

        private async Task<CustomerInvoice> CreateOneAsync(
            Order order, CustomerInvoiceKind kind, decimal amount, string? note, int adminUserId,
            bool allowAlongside, CancellationToken ct)
        {
            var number = await NewInvoiceNumberAsync(ct);
            var request = await _partialPayments.CreateInvoiceRequestAsync(
                order.Id, amount, $"Invoice {number}", adminUserId, allowAlongside, ct);

            var invoice = new CustomerInvoice
            {
                InvoiceNumber = number,
                PublicToken = PaymentLinkHelper.GenerateToken(),
                OrderId = order.Id,
                OrderPartialPaymentId = request.Id,
                Kind = kind,
                Amount = request.RequestedAmount,
                Note = note,
                CreatedByUserId = adminUserId,
                CreatedAt = DateTime.UtcNow
            };
            _context.CustomerInvoices.Add(invoice);
            await _context.SaveChangesAsync(ct);
            return invoice;
        }

        /// <summary>
        /// DCR-YYYY-XXXXXXXX from the same cryptographic generator the commercial numbers use.
        /// The unique index is the real guard; this check just keeps a collision from surfacing
        /// as an error in the (astronomically unlikely) case it happens.
        /// </summary>
        private async Task<string> NewInvoiceNumberAsync(CancellationToken ct)
        {
            for (var attempt = 0; attempt < 10; attempt++)
            {
                var candidate = ReferenceNumberGenerator.Build(InvoicePrefix, NyTimeHelper.NowNy.Year);
                if (!await _context.CustomerInvoices.AnyAsync(i => i.InvoiceNumber == candidate, ct))
                    return candidate;
            }
            throw new InvalidOperationException("Could not allocate a unique invoice number.");
        }

        /// <summary>Invoices on the order that are still a claim on its balance.</summary>
        private IQueryable<CustomerInvoice> LiveInvoicesQuery(int orderId) =>
            _context.CustomerInvoices
                .Include(i => i.OrderPartialPayment)
                .Where(i => i.OrderId == orderId && i.VoidedAt == null
                            && i.OrderPartialPaymentId != null
                            && i.OrderPartialPayment!.Status == OrderPartialPaymentStatus.Pending);

        // ─── Send ───────────────────────────────────────────────────────────────────────────

        public async Task<CustomerInvoiceSendResult> SendAsync(
            int invoiceId, bool sendEmail, bool sendSms, int adminUserId, CancellationToken ct = default)
        {
            if (!sendEmail && !sendSms)
                throw new CustomerInvoiceException("Select at least one channel (email or text message).");

            var invoice = await LoadAsync(invoiceId, ct) ?? throw new CustomerInvoiceException("Invoice not found.");
            var order = invoice.Order!;
            var request = invoice.OrderPartialPayment;
            var topUp = await TopUpStateForAsync(invoice, ct);

            var status = CustomerInvoiceStatusPolicy.Resolve(invoice, order, request, topUp);
            if (!CustomerInvoiceStatusPolicy.IsPayable(status))
                throw new CustomerInvoiceException($"This invoice is {status} and has nothing to collect, so it can't be sent.");

            var customerName = CustomerName(order);
            var customerEmail = NoEmailHelper.ResolveOrderNotificationEmail(order.ContactEmail, order.User);
            var customerPhone = !string.IsNullOrWhiteSpace(order.ContactPhone) ? order.ContactPhone : order.User?.Phone;
            var amountDue = CustomerInvoiceStatusPolicy.AmountDue(invoice, order, request, status, topUp);
            var url = PublicUrl(invoice);

            bool emailSent = false, smsSent = false, smsInvalid = false;
            string? failure = null;

            if (sendEmail && !string.IsNullOrWhiteSpace(customerEmail))
            {
                try
                {
                    // The same document the customer can download from the invoice page, attached
                    // so the bill is in their inbox even if the link is never opened. A render
                    // failure never costs them the email itself.
                    byte[]? pdf = null;
                    try
                    {
                        pdf = (await RenderPdfAsync(invoice.PublicToken, ct))?.Bytes;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Could not render the PDF for invoice {InvoiceNumber}; sending without it", invoice.InvoiceNumber);
                    }

                    await _emailService.SendCustomerInvoiceEmailAsync(customerEmail, customerName, invoice.InvoiceNumber,
                        amountDue, order.Total, invoice.Kind.ToString(), order.Id, order.ServiceDate, url,
                        pdf, CustomerInvoicePdfService.BuildFileName(invoice.InvoiceNumber));
                    emailSent = true;
                }
                catch (Exception ex)
                {
                    failure = "The email could not be sent: " + ex.Message;
                }
            }

            if (sendSms && !string.IsNullOrWhiteSpace(customerPhone) && _smsService.IsSmsEnabled())
            {
                try
                {
                    var e164 = SmsService.NormalizePhoneToE164(customerPhone);
                    if (string.IsNullOrEmpty(e164))
                        smsInvalid = true;
                    else
                    {
                        await _smsService.SendCustomerInvoiceSmsAsync(e164, customerName, invoice.InvoiceNumber, amountDue, order.Id, url);
                        smsSent = true;
                    }
                }
                catch (InvalidPhoneNumberException)
                {
                    smsInvalid = true;
                }
                catch (Exception ex)
                {
                    failure ??= "The text message could not be sent: " + ex.Message;
                }
            }

            if (emailSent || smsSent)
            {
                var now = DateTime.UtcNow;
                invoice.FirstSentAt ??= now;
                invoice.LastSentAt = now;
                invoice.SendCount++;

                // An Additional invoice IS the "your order was updated, please pay the difference"
                // notice, so it stamps the unpaid top-up rows exactly as send-updated-payment does
                // — otherwise the panel keeps offering the old first-send button for money the
                // customer has already been billed for.
                if (invoice.Kind == CustomerInvoiceKind.Additional)
                {
                    var rows = await _context.OrderUpdateHistories
                        .Where(h => h.OrderId == order.Id && !h.IsPaid && h.UpdatedPaymentNotificationSentAt == null)
                        .ToListAsync(ct);
                    foreach (var row in rows) row.UpdatedPaymentNotificationSentAt = now;
                }

                await _context.SaveChangesAsync(ct);
                if (request != null)
                    await _partialPayments.MarkNotificationSentAsync(request.Id, ct);

                await LogAsync(order.Id, "CustomerInvoiceSent", new
                {
                    invoice.InvoiceNumber,
                    AmountDue = amountDue,
                    Email = emailSent,
                    Sms = smsSent
                }, adminUserId);

                var channels = emailSent && smsSent ? "email and text message"
                    : emailSent ? "email" : "text message";
                var smsNote = smsInvalid ? " The phone number on file is invalid, so no text was sent." : "";
                return new CustomerInvoiceSendResult(emailSent, smsSent, $"Invoice {invoice.InvoiceNumber} sent by {channels}.{smsNote}");
            }

            var reason = failure
                ?? (sendEmail && string.IsNullOrWhiteSpace(customerEmail)
                    ? "there is no email address on this order or the customer's account"
                    : smsInvalid ? "the phone number on file is invalid"
                    : sendSms && !_smsService.IsSmsEnabled() ? "SMS sending is currently disabled"
                    : "there is no email address or phone number to send to");
            return new CustomerInvoiceSendResult(false, false,
                $"Invoice {invoice.InvoiceNumber} was not sent — {reason}. Copy the invoice link to the customer instead.");
        }

        // ─── Void ───────────────────────────────────────────────────────────────────────────

        public async Task VoidAsync(int invoiceId, string? reason, int adminUserId, CancellationToken ct = default)
        {
            var invoice = await LoadAsync(invoiceId, ct) ?? throw new CustomerInvoiceException("Invoice not found.");
            var topUp = await TopUpStateForAsync(invoice, ct);
            var status = CustomerInvoiceStatusPolicy.Resolve(invoice, invoice.Order!, invoice.OrderPartialPayment, topUp);
            if (status == CustomerInvoiceStatusPolicy.Void && invoice.VoidedAt != null) return;
            if (status == CustomerInvoiceStatusPolicy.Paid)
                throw new CustomerInvoiceException("This invoice has been paid. Refund the payment on the order instead of voiding it.");

            // Withdraw the request first, so the card page stops accepting money for it. A charge
            // already in flight still settles (the part-payment service records money that
            // genuinely arrived against a cancelled request) — the order shows it either way.
            // An Additional invoice has no request: the top-up stays owed on the order itself.
            if (invoice.OrderPartialPayment is { Status: OrderPartialPaymentStatus.Pending } request)
            {
                try
                {
                    await _partialPayments.CancelRequestAsync(invoice.OrderId, request.Id, adminUserId, ct);
                }
                catch (PartialPaymentException ex)
                {
                    throw new CustomerInvoiceException(ex.Message);
                }
            }

            invoice.VoidedAt = DateTime.UtcNow;
            invoice.VoidedByUserId = adminUserId;
            invoice.VoidReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
            await _context.SaveChangesAsync(ct);

            await LogAsync(invoice.OrderId, "CustomerInvoiceVoided", new
            {
                invoice.InvoiceNumber,
                Reason = invoice.VoidReason
            }, adminUserId);
        }

        // ─── Reads ──────────────────────────────────────────────────────────────────────────

        public async Task<List<CustomerInvoiceDto>> ListAsync(
            string? search, string? status, int? userId, int? orderId, CancellationToken ct = default)
        {
            var query = BaseQuery();
            if (userId.HasValue) query = query.Where(i => i.Order!.UserId == userId.Value);
            if (orderId.HasValue) query = query.Where(i => i.OrderId == orderId.Value);

            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.Trim().ToLower();
                var asNumber = int.TryParse(term.TrimStart('#'), out var n) ? n : (int?)null;
                query = query.Where(i =>
                    i.InvoiceNumber.ToLower().Contains(term)
                    || (asNumber.HasValue && i.OrderId == asNumber.Value)
                    || (i.Order!.ContactFirstName + " " + i.Order.ContactLastName).ToLower().Contains(term)
                    || (i.Order.ContactEmail != null && i.Order.ContactEmail.ToLower().Contains(term))
                    || (i.Order.User != null && (i.Order.User.FirstName + " " + i.Order.User.LastName).ToLower().Contains(term)));
            }

            var rows = await query.OrderByDescending(i => i.CreatedAt).ThenByDescending(i => i.Id)
                .Take(500).ToListAsync(ct);

            var topUps = await LoadTopUpStatesAsync(
                rows.Where(i => i.Kind == CustomerInvoiceKind.Additional).Select(i => i.Order!), ct);
            var dtos = rows.Select(i => ToDto(i, topUps.TryGetValue(i.OrderId, out var s) ? s : null)).ToList();

            // Status is derived, so it is filtered after the projection rather than in SQL.
            if (!string.IsNullOrWhiteSpace(status) && !status.Equals("All", StringComparison.OrdinalIgnoreCase))
            {
                dtos = status.Equals("Open", StringComparison.OrdinalIgnoreCase)
                    ? dtos.Where(d => CustomerInvoiceStatusPolicy.IsPayable(d.Status)).ToList()
                    : dtos.Where(d => d.Status.Equals(status, StringComparison.OrdinalIgnoreCase)).ToList();
            }
            return dtos;
        }

        public async Task<List<MyCustomerInvoiceDto>> ListForCustomerAsync(int userId, CancellationToken ct = default)
        {
            if (userId <= 0) return new List<MyCustomerInvoiceDto>();
            var all = await ListAsync(null, null, userId, null, ct);
            return all
                .Where(i => i.UserId == userId && i.Status != CustomerInvoiceStatusPolicy.NotSent)
                .Select(i => new MyCustomerInvoiceDto
                {
                    InvoiceNumber = i.InvoiceNumber,
                    Kind = i.Kind,
                    Status = i.Status,
                    OrderId = i.OrderId,
                    ServiceTypeName = i.ServiceTypeName,
                    ServiceDate = i.ServiceDate,
                    IssuedAt = i.FirstSentAt ?? i.CreatedAt,
                    Amount = i.Amount,
                    AmountDue = i.AmountDue,
                    PaidAt = i.Status == CustomerInvoiceStatusPolicy.Paid ? i.PaidAt : null,
                    PaidVia = i.Status == CustomerInvoiceStatusPolicy.Paid ? i.PaidVia : null,
                    PublicUrl = i.PublicUrl
                })
                .ToList();
        }

        public async Task<CustomerInvoiceDto?> GetAsync(int invoiceId, CancellationToken ct = default)
        {
            var invoice = await LoadAsync(invoiceId, ct);
            return invoice == null ? null : ToDto(invoice, await TopUpStateForAsync(invoice, ct));
        }

        public async Task<List<CustomerInvoiceOrderOptionDto>> GetInvoiceableOrdersAsync(int userId, CancellationToken ct = default)
        {
            var orders = await _context.Orders
                .AsNoTracking()
                .Include(o => o.ServiceType)
                .Where(o => o.UserId == userId && !o.IsPaid
                            && o.Status != OrderStatuses.Cancelled && o.Status != OrderStatuses.Refunded)
                .OrderByDescending(o => o.ServiceDate).ThenByDescending(o => o.Id)
                .Take(100)
                .ToListAsync(ct);

            var ids = orders.Select(o => o.Id).ToList();
            var pending = await _context.OrderPartialPayments.AsNoTracking()
                .Where(p => ids.Contains(p.OrderId) && p.Status == OrderPartialPaymentStatus.Pending)
                .ToListAsync(ct);
            var liveInvoices = await _context.CustomerInvoices.AsNoTracking()
                .Include(i => i.OrderPartialPayment)
                .Where(i => ids.Contains(i.OrderId) && i.VoidedAt == null && i.OrderPartialPaymentId != null
                            && i.OrderPartialPayment!.Status == OrderPartialPaymentStatus.Pending)
                .ToListAsync(ct);

            var options = orders.Select(o =>
            {
                var due = OrderBalance.AmountDue(o);
                var open = pending.Where(p => p.OrderId == o.Id).Sum(p => p.RequestedAmount);
                var invoices = liveInvoices.Where(i => i.OrderId == o.Id).ToList();
                var can = OrderBalance.CanRequestPartialPayment(o, out var reason);
                var available = OrderPricingCalculator.Round2(Math.Max(0m, due - open));

                if (can && invoices.Any(i => i.Kind == CustomerInvoiceKind.Full))
                {
                    can = false;
                    reason = "Already invoiced in full.";
                }
                else if (can && available < OrderBalance.StripeMinimumChargeAmount)
                {
                    can = false;
                    reason = "Everything owed is already on an open invoice or payment request.";
                }

                return new CustomerInvoiceOrderOptionDto
                {
                    OrderId = o.Id,
                    ServiceTypeName = o.GetDisplayServiceTypeName(),
                    ServiceDate = o.ServiceDate,
                    Status = o.Status,
                    Total = o.Total,
                    AmountDue = due,
                    AvailableToInvoice = available,
                    CanInvoice = can,
                    CannotInvoiceReason = can ? null : reason,
                    OpenInvoiceNumbers = invoices.Select(i => i.InvoiceNumber).ToList()
                };
            }).ToList();

            // PAID orders an edit has since made more expensive: the extra is invoiceable too.
            var paidWithTopUp = await _context.Orders
                .AsNoTracking()
                .Include(o => o.ServiceType)
                .Where(o => o.UserId == userId && o.IsPaid
                            && o.Status != OrderStatuses.Cancelled && o.Status != OrderStatuses.Refunded
                            && _context.OrderUpdateHistories.Any(h => h.OrderId == o.Id && !h.IsPaid && h.AdditionalAmount > 0m))
                .OrderByDescending(o => o.ServiceDate).ThenByDescending(o => o.Id)
                .Take(50)
                .ToListAsync(ct);

            if (paidWithTopUp.Count > 0)
            {
                var topUps = await LoadTopUpStatesAsync(paidWithTopUp, ct);
                var paidIds = paidWithTopUp.Select(o => o.Id).ToList();
                var openAdditional = await _context.CustomerInvoices.AsNoTracking()
                    .Where(i => paidIds.Contains(i.OrderId) && i.Kind == CustomerInvoiceKind.Additional && i.VoidedAt == null)
                    .ToListAsync(ct);

                foreach (var o in paidWithTopUp)
                {
                    var state = topUps.TryGetValue(o.Id, out var s) ? s : default;
                    if (state.Outstanding < OrderAdditionalCharge.MinimumCollectableAmount) continue;

                    var openHere = openAdditional
                        .Where(i => i.OrderId == o.Id
                                    && CustomerInvoiceStatusPolicy.IsPayable(CustomerInvoiceStatusPolicy.Resolve(i, o, null, state)))
                        .ToList();
                    var can = openHere.Count == 0 && state.Outstanding >= OrderBalance.StripeMinimumChargeAmount;

                    options.Add(new CustomerInvoiceOrderOptionDto
                    {
                        OrderId = o.Id,
                        ServiceTypeName = o.GetDisplayServiceTypeName(),
                        ServiceDate = o.ServiceDate,
                        Status = o.Status,
                        Total = o.Total,
                        AmountDue = state.Outstanding,
                        AvailableToInvoice = can ? state.Outstanding : 0m,
                        CanInvoice = can,
                        IsAdditionalCharge = true,
                        CannotInvoiceReason = can ? null
                            : openHere.Count > 0 ? "The additional charge is already on an open invoice."
                            : "The additional amount is below the $0.50 online minimum.",
                        OpenInvoiceNumbers = openHere.Select(i => i.InvoiceNumber).ToList()
                    });
                }
            }

            return options
                .OrderByDescending(o => o.ServiceDate).ThenByDescending(o => o.OrderId)
                .ToList();
        }

        public async Task<PublicCustomerInvoiceDto?> GetPublicAsync(string token, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(token) || token.Length > 64) return null;

            var invoice = await BaseQuery().FirstOrDefaultAsync(i => i.PublicToken == token, ct);
            if (invoice == null) return null;

            var order = invoice.Order!;
            var request = invoice.OrderPartialPayment;
            var topUp = await TopUpStateForAsync(invoice, ct);
            var status = CustomerInvoiceStatusPolicy.Resolve(invoice, order, request, topUp);
            var payable = CustomerInvoiceStatusPolicy.IsPayable(status);
            var isAdditional = invoice.Kind == CustomerInvoiceKind.Additional;

            var settings = await _billingSettings.GetOrCreateAsync(ct);
            var amountDue = CustomerInvoiceStatusPolicy.AmountDue(invoice, order, request, status, topUp);
            var ach = await CustomerInvoiceAchService.ComputeStateAsync(_context, invoice, amountDue, payable, settings, ct);

            string? cardPath = null;
            // No card button while a bank payment is settling — it would collect the same money twice.
            if (payable && !ach.Processing && !string.IsNullOrEmpty(order.PaymentAccessToken))
            {
                // An Additional invoice pays through the order's own payment link, which collects
                // the outstanding top-up on a paid order.
                cardPath = isAdditional
                    ? $"/order/{order.Id}/pay?t={order.PaymentAccessToken}"
                    : $"/order/{order.Id}/pay?t={order.PaymentAccessToken}&request={request!.Id}"
                      + (invoice.Kind == CustomerInvoiceKind.Full ? "&full=1" : "");
            }

            return new PublicCustomerInvoiceDto
            {
                InvoiceNumber = invoice.InvoiceNumber,
                Status = status,
                Kind = invoice.Kind.ToString(),
                IssuedAt = invoice.FirstSentAt ?? invoice.CreatedAt,
                OrderNumber = order.Id,
                ServiceTypeName = order.GetDisplayServiceTypeName(),
                ServiceDate = order.ServiceDate,
                ServiceTime = order.ServiceTime.ToString(@"hh\:mm"),
                ServiceAddress = string.Join(", ", new[]
                {
                    $"{order.ServiceAddress}{(string.IsNullOrWhiteSpace(order.AptSuite) ? "" : $", {order.AptSuite}")}",
                    BillingSettingsService.BuildCityLine(order.City, order.State, order.ZipCode)
                }.Where(s => !string.IsNullOrWhiteSpace(s))),
                BilledToName = CustomerName(order),
                BilledToEmail = NoEmailHelper.ResolveOrderNotificationEmail(order.ContactEmail, order.User),
                BilledToPhone = !string.IsNullOrWhiteSpace(order.ContactPhone) ? order.ContactPhone : order.User?.Phone,
                SubTotal = order.SubTotal,
                Discounts = order.DiscountAmount + order.SubscriptionDiscountAmount + order.LoyaltyDiscountAmount,
                Credits = order.GiftCardAmountUsed + order.PointsRedeemedDiscount + order.RewardBalanceUsed,
                Tax = order.Tax,
                Tips = order.Tips + order.CompanyDevelopmentTips,
                OrderTotal = order.Total,
                // A paid order that an edit made dearer has received its total LESS the top-up.
                OrderAmountPaid = isAdditional
                    ? OrderPricingCalculator.Round2(Math.Max(0m, order.Total - (topUp?.Outstanding ?? 0m)))
                    : order.IsPaid ? order.Total : order.AmountPaid,
                Amount = invoice.Amount,
                AmountDue = CustomerInvoiceStatusPolicy.AmountDue(invoice, order, request, status, topUp),
                Note = invoice.Note,
                PaidAt = status == CustomerInvoiceStatusPolicy.Paid ? ResolvePaidAt(invoice, order, request, topUp) : null,
                CardPaymentPath = cardPath,
                // Bank details only while there is something to pay, and only when complete —
                // half-configured instructions are instructions nobody can act on.
                AchAvailable = ach.Available,
                AchProcessing = ach.Processing,
                AchProcessingFee = ach.ProcessingFee,
                AchTotalCharge = ach.TotalCharge,
                BankTransfer = payable && !ach.Processing && BillingSettingsService.IsManualAchComplete(settings)
                    ? BillingSettingsService.BuildPaymentInstructions(
                        settings, invoice.InvoiceNumber, Models.Commercial.InvoicePaymentMethod.AchBankTransfer)
                    : null,
                Company = BillingSettingsService.BuildCompany(settings)
            };
        }

        public async Task<(byte[] Bytes, string FileName)?> RenderPdfAsync(string token, CancellationToken ct = default)
        {
            var dto = await GetPublicAsync(token, ct);
            if (dto == null) return null;
            return (_pdf.Render(dto, $"{FrontendUrl}/pay-invoice/{token}"),
                CustomerInvoicePdfService.BuildFileName(dto.InvoiceNumber));
        }

        // ─── Helpers ────────────────────────────────────────────────────────────────────────

        private IQueryable<CustomerInvoice> BaseQuery() =>
            _context.CustomerInvoices
                .Include(i => i.Order).ThenInclude(o => o!.User)
                .Include(i => i.Order).ThenInclude(o => o!.ServiceType)
                .Include(i => i.OrderPartialPayment)
                .Include(i => i.CreatedByUser);

        private Task<CustomerInvoice?> LoadAsync(int invoiceId, CancellationToken ct) =>
            BaseQuery().FirstOrDefaultAsync(i => i.Id == invoiceId, ct);

        private async Task<CustomerInvoiceTopUpState?> TopUpStateForAsync(CustomerInvoice invoice, CancellationToken ct) =>
            invoice.Kind == CustomerInvoiceKind.Additional ? await LoadTopUpStateAsync(invoice.Order!, ct) : null;

        private async Task<CustomerInvoiceTopUpState> LoadTopUpStateAsync(Order order, CancellationToken ct)
        {
            var states = await LoadTopUpStatesAsync(new[] { order }, ct);
            return states.TryGetValue(order.Id, out var s) ? s : default;
        }

        /// <summary>
        /// The order-edit top-up picture per order, from the same rules every other surface uses
        /// (<see cref="OrderAdditionalCharge"/>), loaded in one query for a whole list.
        /// </summary>
        private async Task<Dictionary<int, CustomerInvoiceTopUpState>> LoadTopUpStatesAsync(
            IEnumerable<Order> orders, CancellationToken ct)
        {
            var byId = orders.GroupBy(o => o.Id).ToDictionary(g => g.Key, g => g.First());
            if (byId.Count == 0) return new Dictionary<int, CustomerInvoiceTopUpState>();

            var ids = byId.Keys.ToList();
            var histories = await _context.OrderUpdateHistories.AsNoTracking()
                .Where(h => ids.Contains(h.OrderId))
                .ToListAsync(ct);

            return byId.ToDictionary(kv => kv.Key, kv =>
            {
                var rows = histories.Where(h => h.OrderId == kv.Key).ToList();
                // Same positive-and-paid rule as OrderAdditionalCharge.WasCollected, oldest first.
                var payments = rows.Where(h => h.IsPaid && h.AdditionalAmount > 0m)
                    .OrderBy(h => h.PaidAt ?? h.UpdatedAt).ThenBy(h => h.Id)
                    .Select(h => new CustomerInvoiceTopUpPayment(h.AdditionalAmount, h.PaidAt ?? h.UpdatedAt, h.PaymentMethod, h.PaymentReference))
                    .ToList();
                return new CustomerInvoiceTopUpState(
                    OrderAdditionalCharge.CollectedToDate(rows),
                    OrderAdditionalCharge.Outstanding(kv.Value, rows),
                    payments);
            });
        }

        private static DateTime? ResolvePaidAt(CustomerInvoice invoice, Order order, OrderPartialPayment? request,
            CustomerInvoiceTopUpState? topUp) =>
            invoice.Kind == CustomerInvoiceKind.Additional
                ? topUp?.SettlingPayment(invoice.TopUpCollectedTarget ?? invoice.Amount)?.PaidAt
                : request?.PaidAt ?? order.PaidAt;

        private static string CustomerName(Order order)
        {
            var fromOrder = $"{order.ContactFirstName?.Trim()} {order.ContactLastName?.Trim()}".Trim();
            if (!string.IsNullOrWhiteSpace(fromOrder)) return fromOrder;
            var fromUser = order.User != null ? $"{order.User.FirstName?.Trim()} {order.User.LastName?.Trim()}".Trim() : "";
            return string.IsNullOrWhiteSpace(fromUser) ? "Valued Customer" : fromUser;
        }

        public CustomerInvoiceDto ToDto(CustomerInvoice invoice, CustomerInvoiceTopUpState? topUp = null)
        {
            var order = invoice.Order!;
            var request = invoice.OrderPartialPayment;
            var status = CustomerInvoiceStatusPolicy.Resolve(invoice, order, request, topUp);
            var payable = CustomerInvoiceStatusPolicy.IsPayable(status);
            var createdBy = invoice.CreatedByUser;
            var isAdditional = invoice.Kind == CustomerInvoiceKind.Additional;

            string? paidVia;
            string? paymentReference;
            DateTime? paidAt;
            if (isAdditional)
            {
                var settling = status == CustomerInvoiceStatusPolicy.Paid
                    ? topUp?.SettlingPayment(invoice.TopUpCollectedTarget ?? invoice.Amount)
                    : null;
                paidVia = settling == null ? null
                    : settling.Method == PaymentMethod.Normal ? "Card" : settling.Method.ToString();
                paymentReference = settling?.Reference;
                paidAt = settling?.PaidAt;
            }
            else
            {
                var requestPaid = request?.Status == OrderPartialPaymentStatus.Paid;
                paidVia = requestPaid
                    ? (request!.PaymentMethod == PaymentMethod.Normal ? "Card" : request.PaymentMethod.ToString())
                    : null;
                paymentReference = requestPaid ? request!.PaymentReference : null;
                paidAt = request?.PaidAt;
            }

            return new CustomerInvoiceDto
            {
                Id = invoice.Id,
                InvoiceNumber = invoice.InvoiceNumber,
                Kind = invoice.Kind.ToString(),
                Status = status,
                OrderId = order.Id,
                UserId = order.UserId,
                CustomerName = CustomerName(order),
                CustomerEmail = NoEmailHelper.ResolveOrderNotificationEmail(order.ContactEmail, order.User),
                CustomerPhone = !string.IsNullOrWhiteSpace(order.ContactPhone) ? order.ContactPhone : order.User?.Phone,
                ServiceTypeName = order.GetDisplayServiceTypeName(),
                ServiceDate = order.ServiceDate,
                Amount = invoice.Amount,
                AmountDue = CustomerInvoiceStatusPolicy.AmountDue(invoice, order, request, status, topUp),
                OrderTotal = order.Total,
                OrderAmountDue = isAdditional ? (topUp?.Outstanding ?? 0m) : OrderBalance.AmountDue(order),
                PartialPaymentId = request?.Id,
                PaidVia = paidVia,
                PaymentReference = paymentReference,
                PaidAt = paidAt,
                Note = invoice.Note,
                PublicUrl = PublicUrl(invoice),
                CreatedAt = invoice.CreatedAt,
                CreatedByName = createdBy == null ? null : $"{createdBy.FirstName} {createdBy.LastName}".Trim(),
                FirstSentAt = invoice.FirstSentAt,
                LastSentAt = invoice.LastSentAt,
                SendCount = invoice.SendCount,
                VoidedAt = invoice.VoidedAt,
                VoidReason = invoice.VoidReason,
                CanSend = payable,
                CanVoid = payable,
                // Recorded against the invoice's OWN request, which must still be open. An
                // Additional invoice's bank transfer is recorded on the order's Update History row.
                CanRecordPayment = payable && request != null && request.Status == OrderPartialPaymentStatus.Pending
            };
        }

        private async Task LogAsync(int orderId, string action, object payload, int adminUserId)
        {
            try
            {
                await _auditService.LogActionAsync(
                    AuditEntityTypes.CustomerInvoiceAction, orderId, action, null, payload, null, adminUserId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to audit {Action} on order {OrderId}", action, orderId);
            }
        }
    }
}
