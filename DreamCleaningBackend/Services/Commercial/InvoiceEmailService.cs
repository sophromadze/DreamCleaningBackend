using System.Net;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs.Commercial;
using DreamCleaningBackend.Helpers.Commercial;
using DreamCleaningBackend.Models.Commercial;
using DreamCleaningBackend.Services.Interfaces;
using DreamCleaningBackend.Controllers;
using Microsoft.EntityFrameworkCore;

namespace DreamCleaningBackend.Services.Commercial
{
    /// <summary>
    /// Every mail the commercial invoicing system sends: the invoice itself, payment reminders and
    /// payment receipts.
    ///
    /// Kept out of the general EmailService for the same reason ContractNotificationService is -
    /// these are business-to-business billing notices with their own vocabulary. Nothing here
    /// mentions the booking flow, and no residential customer ever receives one.
    ///
    /// EVERY SEND IS LOGGED, INCLUDING A FAILED ONE. A send that quietly did nothing is the worst
    /// outcome for an invoice, because the admin believes the client has it and the client is
    /// waiting: the failure row is what makes that visible on the detail page instead.
    ///
    /// The invoice is only marked Sent AFTER the transport accepts it, so a bounce never leaves an
    /// invoice claiming it reached anyone.
    /// </summary>
    public class InvoiceEmailService
    {
        private readonly ApplicationDbContext _context;
        private readonly IEmailService _email;
        private readonly InvoiceService _invoices;
        private readonly InvoicePdfService _pdf;
        private readonly ILogger<InvoiceEmailService> _logger;

        public InvoiceEmailService(
            ApplicationDbContext context,
            IEmailService email,
            InvoiceService invoices,
            InvoicePdfService pdf,
            ILogger<InvoiceEmailService> logger)
        {
            _context = context;
            _email = email;
            _invoices = invoices;
            _pdf = pdf;
            _logger = logger;
        }

        /// <summary>
        /// Sends (or resends) the invoice to the client's billing contact.
        ///
        /// The PDF is attached when the caller asks for it, but the LINK is what the mail leads
        /// with: the public page always shows the current balance, whereas an attachment is a
        /// snapshot that a later partial payment silently invalidates.
        /// </summary>
        public async Task<CommercialInvoiceEmailLog> SendInvoiceAsync(
            int invoiceId, SendInvoiceDto dto, int userId)
        {
            var invoice = await _context.CommercialInvoices
                .Include(i => i.Items)
                .Include(i => i.Client)
                .Include(i => i.Contract)
                .FirstOrDefaultAsync(i => i.Id == invoiceId)
                ?? throw new InvoiceWorkflowException("Invoice not found.");

            if (!InvoiceStatusPolicy.CanSend(invoice.Status))
                throw new InvoiceWorkflowException("A voided invoice cannot be sent.");

            var recipient = await ResolveRecipientAsync(invoice, dto.RecipientEmail);
            var isResend = invoice.FirstSentAt.HasValue;
            var subject = $"Invoice {invoice.InvoiceNumber} from Dream Cleaning NYC";

            var publicDto = await _invoices.ToPublicDtoAsync(invoice);
            var body = BuildInvoiceEmailBody(publicDto, _invoices.BuildPublicUrl(invoice.PublicToken), dto.Message);

            var log = await DeliverAsync(
                invoice, recipient, subject, body,
                isResend ? InvoiceEmailType.InvoiceResent : InvoiceEmailType.InvoiceSent,
                dto.AttachPdf ? publicDto : null);

            if (log.Status == InvoiceEmailStatus.Sent)
                await _invoices.MarkSentAsync(invoice, userId, recipient);

            return log;
        }

        /// <summary>
        /// A payment-chasing reminder.
        ///
        /// Guarded against accidental repetition: the same reminder key cannot go out twice on the
        /// same day unless the admin forces it. Duplicate chasers are how a billing system starts
        /// reading as harassment to the client on the other end.
        /// </summary>
        public async Task<CommercialInvoiceEmailLog> SendReminderAsync(
            int invoiceId, SendInvoiceReminderDto dto, int userId, string reminderKey = "manual")
        {
            var invoice = await _context.CommercialInvoices
                .Include(i => i.Items)
                .Include(i => i.Client)
                .Include(i => i.Contract)
                .FirstOrDefaultAsync(i => i.Id == invoiceId)
                ?? throw new InvoiceWorkflowException("Invoice not found.");

            if (!InvoiceStatusPolicy.CanSendReminder(invoice.Status))
                throw new InvoiceWorkflowException(
                    "A reminder only applies to an issued invoice with a balance outstanding.");

            // ACH takes days to settle, and the invoice reads unpaid the whole time. Chasing a
            // customer whose money is already on its way is the single most annoying thing this
            // system could do, so an in-flight payment covering the balance suppresses reminders
            // — including the automated ones, which pass Force = false.
            if (!dto.Force && await HasPaymentInFlightAsync(invoiceId, invoice.BalanceDue))
                throw new InvoiceWorkflowException(
                    "A bank payment for this invoice is already processing. No reminder was sent.");

            if (!dto.Force && await AlreadyRemindedTodayAsync(invoiceId, reminderKey))
                throw new InvoiceWorkflowException(
                    "A reminder for this invoice has already gone out today.");

            var recipient = await ResolveRecipientAsync(invoice, dto.RecipientEmail);
            var subject = invoice.Status == InvoiceStatus.Overdue
                ? $"Overdue: invoice {invoice.InvoiceNumber} from Dream Cleaning NYC"
                : $"Reminder: invoice {invoice.InvoiceNumber} from Dream Cleaning NYC";

            var publicDto = await _invoices.ToPublicDtoAsync(invoice);
            var body = BuildReminderEmailBody(publicDto, _invoices.BuildPublicUrl(invoice.PublicToken));

            var log = await DeliverAsync(invoice, recipient, subject, body, InvoiceEmailType.Reminder, null);

            if (log.Status == InvoiceEmailStatus.Sent)
            {
                _context.CommercialInvoiceReminders.Add(new CommercialInvoiceReminder
                {
                    CommercialInvoiceId = invoice.Id,
                    ReminderKey = reminderKey,
                    Recipient = recipient,
                    SentAt = DateTime.UtcNow,
                    SentByUserId = userId
                });
                await _context.SaveChangesAsync();

                await _invoices.LogActivityAsync(invoice.Id, "reminder_sent",
                    $"Payment reminder sent to {recipient}.", userId);
            }

            return log;
        }

        /// <summary>Confirms a fully-settled invoice. Sent on request, never automatically.</summary>
        public async Task<CommercialInvoiceEmailLog> SendPaymentReceiptAsync(int invoiceId, int userId)
        {
            var invoice = await _context.CommercialInvoices
                .Include(i => i.Items)
                .Include(i => i.Client)
                .Include(i => i.Contract)
                .FirstOrDefaultAsync(i => i.Id == invoiceId)
                ?? throw new InvoiceWorkflowException("Invoice not found.");

            if (invoice.Status != InvoiceStatus.Paid)
                throw new InvoiceWorkflowException(
                    "A receipt is only sent once the invoice is paid in full.");

            var recipient = await ResolveRecipientAsync(invoice, null);
            var subject = $"Payment received - invoice {invoice.InvoiceNumber}";

            var publicDto = await _invoices.ToPublicDtoAsync(invoice);
            var body = BuildReceiptEmailBody(publicDto, _invoices.BuildPublicUrl(invoice.PublicToken));

            var log = await DeliverAsync(invoice, recipient, subject, body,
                InvoiceEmailType.PaymentReceipt, publicDto);

            if (log.Status == InvoiceEmailStatus.Sent)
            {
                // userId 0 means the Stripe webhook sent this, not a person — the activity trail
                // resolves that to "System" rather than naming a non-existent admin.
                await _invoices.LogActivityAsync(invoice.Id, "payment_confirmation_sent",
                    $"Payment confirmation sent to {recipient}.",
                    userId > 0 ? userId : null,
                    userId > 0 ? null : "System");
            }

            return log;
        }

        // ── Delivery ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Hands the message to the transport and records the outcome either way.
        ///
        /// A throwing send is caught, not propagated: the caller's job (recording that a send was
        /// attempted, and leaving the invoice honest about whether it went) matters more than the
        /// request failing, and the failure reaches the admin through the email history instead.
        /// </summary>
        private async Task<CommercialInvoiceEmailLog> DeliverAsync(
            CommercialInvoice invoice, string recipient, string subject, string body,
            InvoiceEmailType type, PublicInvoiceDto? attachAs)
        {
            var log = new CommercialInvoiceEmailLog
            {
                CommercialInvoiceId = invoice.Id,
                EmailType = type,
                Recipient = recipient,
                Subject = subject,
                SentAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow
            };

            try
            {
                if (attachAs != null)
                {
                    var bytes = _pdf.Render(attachAs, _invoices.BuildPublicUrl(invoice.PublicToken));
                    await _email.SendEmailWithAttachmentAsync(
                        recipient, subject, body, bytes,
                        InvoicePdfService.BuildFileName(invoice.InvoiceNumber), "application/pdf");
                }
                else
                {
                    await _email.SendEmailAsync(recipient, subject, body);
                }

                log.Status = InvoiceEmailStatus.Sent;
            }
            catch (Exception ex)
            {
                log.Status = InvoiceEmailStatus.Failed;
                log.FailureReason = Truncate(ex.Message, 500);
                _logger.LogError(ex,
                    "Failed to send {Type} for invoice {Number} to {Recipient}.",
                    type, invoice.InvoiceNumber, recipient);
            }

            _context.CommercialInvoiceEmailLogs.Add(log);
            await _context.SaveChangesAsync();
            return log;
        }

        /// <summary>
        /// Who the invoice goes to: an explicit override, then the client's billing contact, then
        /// the company notice address on the client record.
        ///
        /// Refuses rather than guesses when there is nothing. An invoice with no deliverable
        /// address must fail visibly at the moment the admin presses Send - silently skipping the
        /// mail would leave them believing the client had been billed.
        /// </summary>
        private async Task<string> ResolveRecipientAsync(CommercialInvoice invoice, string? overrideEmail)
        {
            var candidate = Trim(overrideEmail);

            if (candidate == null)
            {
                var contact = await _invoices.ResolveBillingContactAsync(invoice.ContractClientId);
                candidate = Trim(contact?.Email) ?? Trim(invoice.Client?.NoticeEmail);
            }

            if (string.IsNullOrWhiteSpace(candidate))
                throw new InvoiceWorkflowException(
                    "This client has no billing email address. Add one to the client record, or "
                    + "enter an address to send this invoice to.");

            return candidate;
        }

        /// <summary>
        /// True when an online payment covering the whole outstanding balance is authorized or
        /// settling.
        ///
        /// Covering the FULL balance is the test, not merely "some payment exists": a part-payment
        /// in flight still leaves money genuinely owed and worth a reminder about.
        /// </summary>
        private async Task<bool> HasPaymentInFlightAsync(int invoiceId, decimal balanceDue)
        {
            // SETTLING ONLY, matching the customer's view and the checkout guard. An abandoned
            // Checkout Session is not a payment on its way, and suppressing a reminder over one
            // would let a genuinely unpaid invoice go quiet for a day.
            var attempts = await _context.CommercialInvoicePaymentAttempts
                .Where(a => a.CommercialInvoiceId == invoiceId
                            && a.Status == InvoicePaymentAttemptStatus.Processing)
                .ToListAsync();

            return attempts.Any(a => a.Amount >= balanceDue);
        }

        private async Task<bool> AlreadyRemindedTodayAsync(int invoiceId, string reminderKey)
        {
            var since = DateTime.UtcNow.Date;
            return await _context.CommercialInvoiceReminders.AnyAsync(r =>
                r.CommercialInvoiceId == invoiceId &&
                r.ReminderKey == reminderKey &&
                r.SentAt >= since);
        }

        // ── Bodies ───────────────────────────────────────────────────────────────────────────

        private string BuildInvoiceEmailBody(
            PublicInvoiceDto invoice, string publicUrl, string? adminMessage)
        {
            return Wrap(invoice, $@"
                <p style=""margin:0 0 4px;font-size:12px;letter-spacing:0.12em;text-transform:uppercase;color:#64748b;"">Invoice</p>
                <h2 style='margin:0 0 20px;font-size:22px;color:#0f172a;font-weight:700;'>{E(invoice.InvoiceNumber)}</h2>
                <p style='margin:0 0 12px;'>Hello{(string.IsNullOrWhiteSpace(invoice.BillingContactName) ? "" : " " + E(invoice.BillingContactName!))},</p>
                <p style='margin:0 0 8px;'>Your invoice for cleaning services at
                   <strong>{E(invoice.ServiceAddress ?? invoice.ClientName)}</strong> is ready.</p>
                {(string.IsNullOrWhiteSpace(adminMessage) ? "" : $"<p style='background:#f8fafc;border-left:3px solid #2563eb;padding:10px 14px;margin:16px 0;'>{E(adminMessage!)}</p>")}
                {DetailsTable(invoice)}
                {AmountsTable(invoice)}
                <p style='margin:28px 0;text-align:center;'>{Button(publicUrl, "View &amp; Pay Invoice")}</p>
                {PaymentBlurb(invoice)}
                {NoteBlock(invoice.CustomerNote)}");
        }

        private string BuildReminderEmailBody(PublicInvoiceDto invoice, string publicUrl)
        {
            var overdue = invoice.Status == InvoiceStatus.Overdue;

            return Wrap(invoice, $@"
                <h2 style='margin:0 0 20px;font-size:22px;color:#0f172a;font-weight:700;'>
                    {(overdue ? "Invoice past due" : "A friendly payment reminder")}</h2>
                <p style='margin:0 0 12px;'>Hello{(string.IsNullOrWhiteSpace(invoice.BillingContactName) ? "" : " " + E(invoice.BillingContactName!))},</p>
                <p style='margin:0 0 8px;'>{(overdue
                    ? $"Invoice <strong>{E(invoice.InvoiceNumber)}</strong> was due on {invoice.DueDate:MMMM d, yyyy} and has an outstanding balance."
                    : $"This is a reminder that invoice <strong>{E(invoice.InvoiceNumber)}</strong> is due on {invoice.DueDate:MMMM d, yyyy}.")}</p>
                {DetailsTable(invoice)}
                {AmountsTable(invoice)}
                <p style='margin:28px 0;text-align:center;'>{Button(publicUrl, "View &amp; Pay Invoice")}</p>
                <p style='color:#475569;font-size:13px;'>If this payment has already been sent, please
                   ignore this message - bank transfers can take a few days to appear. Do let us know
                   if anything about the invoice needs correcting.</p>");
        }

        private string BuildReceiptEmailBody(PublicInvoiceDto invoice, string publicUrl)
        {
            return Wrap(invoice, $@"
                <h2 style='margin:0 0 20px;font-size:22px;color:#15803d;font-weight:700;'>Payment received</h2>
                <p style='margin:0 0 12px;'>Hello{(string.IsNullOrWhiteSpace(invoice.BillingContactName) ? "" : " " + E(invoice.BillingContactName!))},</p>
                <p style='margin:0 0 8px;'>Thank you - we have received payment in full for invoice
                   <strong>{E(invoice.InvoiceNumber)}</strong>.</p>
                {AmountsTable(invoice)}
                <table style='width:100%;border-collapse:collapse;margin:0 0 18px;font-size:14px;'>
                    <tr><td style='padding:6px 0;color:#475569;'>Payment date</td>
                        <td style='padding:6px 0;text-align:right;'>{(invoice.PaidAt?.ToString("MMMM d, yyyy") ?? "-")}</td></tr>
                </table>
                <p style='margin:28px 0;text-align:center;'>{Button(publicUrl, "View receipt")}</p>
                <p style='color:#475569;font-size:13px;'>We appreciate your business.</p>");
        }

        /// <summary>
        /// Who, what and when - the reference fields, with no money in them.
        ///
        /// Split from <see cref="AmountsTable"/> because they answer different questions and a
        /// client scanning for "how much" should not have to read past six reference rows to find
        /// it. The SERVICE LINE uses the label the server resolved - "Service date" for one
        /// cleaning, "Service dates" for a list, "Service period" for a range - and is omitted
        /// entirely when the invoice records none, rather than printing an invented range.
        /// </summary>
        private static string DetailsTable(PublicInvoiceDto invoice) => $@"
            <table style='width:100%;border-collapse:collapse;margin:18px 0;font-size:14px;'>
                <tr><td style='padding:6px 0;color:#475569;'>Invoice number</td>
                    <td style='padding:6px 0;text-align:right;'>{E(invoice.InvoiceNumber)}</td></tr>
                <tr><td style='padding:6px 0;color:#475569;'>Invoice date</td>
                    <td style='padding:6px 0;text-align:right;'>{invoice.InvoiceDate:MMMM d, yyyy}</td></tr>
                <tr><td style='padding:6px 0;color:#475569;'>Due date</td>
                    <td style='padding:6px 0;text-align:right;'>{invoice.DueDate:MMMM d, yyyy}</td></tr>
                {(string.IsNullOrWhiteSpace(invoice.ServiceDateText) ? "" : $@"<tr><td style='padding:6px 0;color:#475569;'>{E(invoice.ServiceDateLabel ?? "Service period")}</td>
                    <td style='padding:6px 0;text-align:right;'>{E(invoice.ServiceDateText!)}</td></tr>")}
                {(string.IsNullOrWhiteSpace(invoice.ServiceAddress) ? "" : $@"<tr><td style='padding:6px 0;color:#475569;'>Service location</td>
                    <td style='padding:6px 0;text-align:right;'>{E(invoice.ServiceAddress!)}</td></tr>")}
                {(string.IsNullOrWhiteSpace(invoice.ContractNumber) ? "" : $@"<tr><td style='padding:6px 0;color:#475569;'>Contract</td>
                    <td style='padding:6px 0;text-align:right;'>{E(invoice.ContractNumber!)}</td></tr>")}
            </table>";

        /// <summary>
        /// Subtotal, tax, total, balance.
        ///
        /// THE TAX IS A DOLLAR AMOUNT, ALWAYS - including on a tax-inclusive invoice, where this
        /// used to say "Included" and therefore stated the tax nowhere at all. An amount the client
        /// cannot see is one they cannot check against their own books or a sales-tax return, and
        /// "Subtotal $925.43 / Sales tax: Included / Total $925.43" additionally makes the subtotal
        /// wrong. The "(included)" suffix keeps the one thing the old wording got right: saying
        /// plainly that the figure is already inside the total rather than added to it.
        /// </summary>
        private static string AmountsTable(PublicInvoiceDto invoice)
        {
            var rate = invoice.TaxRate is > 0m
                ? $" ({invoice.TaxRate!.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}%)"
                : string.Empty;

            var taxRow = invoice.TaxType == InvoiceTaxType.Exempt
                ? string.Empty
                : $@"<tr><td style='padding:6px 16px;color:#475569;'>Sales tax{rate}</td>
                     <td style='padding:6px 16px;text-align:right;'>{Money(invoice.TaxAmount)}{(invoice.TaxType == InvoiceTaxType.Included ? " <span style='color:#64748b;font-size:12px;'>(included)</span>" : "")}</td></tr>";

            var discountRow = invoice.DiscountAmount > 0m
                ? $@"<tr><td style='padding:6px 16px;color:#475569;'>Discount</td>
                     <td style='padding:6px 16px;text-align:right;'>-{Money(invoice.DiscountAmount)}</td></tr>"
                : string.Empty;

            // Money received is shown POSITIVE. A payment row is stored positive and only a
            // reversal is negative, so a hand-written minus here would print a real settlement as
            // "-$925.43" on the customer's own receipt. Discount keeps its sign: that IS a
            // reduction of what is billed, whereas money received is the bill being met.
            var paidRow = invoice.AmountPaid != 0m
                ? $@"<tr><td style='padding:6px 16px;color:#475569;'>Amount paid</td>
                     <td style='padding:6px 16px;text-align:right;'>{Money(invoice.AmountPaid)}</td></tr>"
                : string.Empty;

            // One flat table inside a bordered card. Nested tables would survive Outlook better in
            // a complex layout, but this is four to six rows of label/value - the simplest markup
            // that renders identically everywhere is the right one.
            return $@"
            <table style=""width:100%;border-collapse:collapse;margin:18px 0;font-size:14px;
                          background:#f8fafc;border:1px solid #e2e8f0;"">
                <tr><td style='padding:14px 16px 6px;color:#475569;'>Subtotal</td>
                    <td style='padding:14px 16px 6px;text-align:right;'>{Money(invoice.SubTotal)}</td></tr>
                {discountRow}
                {taxRow}
                <tr><td style='padding:8px 16px 6px;border-top:1px solid #e2e8f0;font-weight:700;'>Total</td>
                    <td style='padding:8px 16px 6px;border-top:1px solid #e2e8f0;text-align:right;font-weight:700;'>
                        {Money(invoice.Total)}</td></tr>
                {paidRow}
                <tr><td style=""padding:10px 16px 14px;border-top:2px solid #2563eb;font-weight:700;"">Balance due</td>
                    <td style=""padding:10px 16px 14px;border-top:2px solid #2563eb;text-align:right;
                               font-weight:700;font-size:18px;color:#2563eb;"">
                        {Money(invoice.BalanceDue)}</td></tr>
            </table>";
        }

        /// <summary>
        /// How to pay, worded for whichever routes are actually on.
        ///
        /// NO BANK DETAILS AND NO STRIPE ANYTHING IN THE EMAIL ITSELF — the account number lives
        /// on the invoice page and the PDF, behind the token. An email is forwarded, quoted and
        /// archived far more casually than a link is opened, and it is the channel invoice-fraud
        /// attempts actually travel on.
        /// </summary>
        private static string PaymentBlurb(PublicInvoiceDto invoice)
        {
            var online = invoice.PaymentOptions.StripeAchAvailable;
            var card = invoice.PaymentOptions.StripeCardAvailable;
            var manual = invoice.PaymentOptions.ManualAchAvailable;

            var lines = new List<string>();

            if (online)
            {
                // The fee is NAMED here as well as on the payment page. A customer who only reads
                // the email should not be surprised by an extra charge when they get to Stripe -
                // the exact figure is on the page before they authorize anything, but they are
                // told it exists before they choose a method.
                var fee = invoice.PaymentOptions.AchProcessingFee > 0m
                    ? $" An {E(invoice.PaymentOptions.AchProcessingFeeLabel)} of "
                      + $"{Money(invoice.PaymentOptions.AchProcessingFee)} applies to bank payments made "
                      + "this way, and is shown before you confirm."
                    : string.Empty;

                lines.Add(
                    "You can pay securely straight from your US bank account by opening the invoice "
                    + "above and choosing <strong>Pay from Bank</strong>." + fee);
            }

            if (card)
                lines.Add("Card payment is also available on the invoice page.");

            if (manual)
            {
                lines.Add(
                    "Prefer to send the transfer yourself? Full bank transfer instructions are on the "
                    + $"invoice page, with {E(invoice.PaymentOptions.ManualAchFeeNote).ToLowerInvariant()}. "
                    + $"Please include invoice number <strong>{E(invoice.InvoiceNumber)}</strong> "
                    + "in your payment memo or reference so we can match it to your account.");
            }

            // Every route is off — say nothing rather than instruct the customer to do something
            // impossible. The admin warning on the invoice page is where that gets caught.
            if (lines.Count == 0) return string.Empty;

            return "<p style='color:#475569;font-size:13px;'>" + string.Join(" ", lines) + "</p>";
        }

        private static string NoteBlock(string? note) =>
            string.IsNullOrWhiteSpace(note)
                ? string.Empty
                : $"<p style='color:#475569;font-size:13px;white-space:pre-line;'>{E(note!)}</p>";

        /// <summary>
        /// The shared shell: masthead, body, company block, footer.
        ///
        /// THE LOGO IS AN ABSOLUTE PUBLIC HTTPS URL, served by <c>PublicBrandController</c>. An
        /// email client will not read a local path and will not follow a development URL, so the
        /// one thing that reliably works is an address any inbox in the world can fetch
        /// anonymously. It carries real alt text, because a great many clients block images by
        /// default and the mail has to still say who it is from.
        ///
        /// THE TRADING NAME LEADS, matching the PDF and the web invoice: "DBA Dream Cleaning NYC"
        /// large and in brand blue, the registered entity small and grey underneath. The hierarchy
        /// is resolved on the DTO precisely so the three surfaces cannot each decide it
        /// differently.
        ///
        /// NO BANK DETAILS ANYWHERE IN THE MAIL. The account number lives on the token-addressed
        /// invoice page and in the PDF. An email is forwarded, quoted and archived far more
        /// casually than a link is opened, and it is the channel invoice-fraud attempts actually
        /// travel on.
        /// </summary>
        private string Wrap(PublicInvoiceDto invoice, string inner)
        {
            var company = invoice.Company;
            var logoUrl = PublicBrandController.BuildLogoUrl(_invoices.FrontendUrl);

            var contactLine = string.Join(" &nbsp;·&nbsp; ", new[]
            {
                company.Address,
                company.CityStateZip,
                company.Phone,
                company.Email
            }.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => E(x!)));

            return $@"
            <div style=""background:#f1f5f9;padding:24px 12px;"">
            <div style=""font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,Arial,sans-serif;
                        max-width:640px;margin:0 auto;background:#ffffff;border:1px solid #e2e8f0;
                        color:#0f172a;line-height:1.6;"">

                <div style=""padding:28px 28px 20px;border-bottom:3px solid #2563eb;"">
                    <img src=""{logoUrl}"" alt=""{E(PublicBrandController.LogoAltText)}"" height=""40""
                         style=""display:block;height:40px;width:auto;border:0;margin:0 0 14px;"">
                    <div style=""font-size:18px;font-weight:700;color:#2563eb;line-height:1.25;"">
                        {E(company.PrimaryName)}</div>
                    {(string.IsNullOrWhiteSpace(company.SecondaryName) ? "" : $@"<div style=""font-size:13px;color:#64748b;"">{E(company.SecondaryName!)}</div>")}
                    {(string.IsNullOrWhiteSpace(contactLine) ? "" : $@"<div style=""font-size:12px;color:#94a3b8;margin-top:8px;"">{contactLine}</div>")}
                </div>

                <div style=""padding:28px;"">
                    {inner}
                </div>

                <div style=""padding:16px 28px 24px;border-top:1px solid #e2e8f0;background:#f8fafc;"">
                    <p style='color:#94a3b8;font-size:12px;margin:0;'>
                        This message was sent by {E(company.PrimaryName)} regarding a commercial
                        cleaning account. Please do not share the invoice link with anyone outside
                        your organization.
                    </p>
                    {(string.IsNullOrWhiteSpace(company.FooterText) ? "" : $@"<p style='color:#94a3b8;font-size:12px;margin:8px 0 0;'>{E(company.FooterText!)}</p>")}
                </div>
            </div>
            </div>";
        }

        private static string Button(string url, string label) => $@"
            <a href='{url}' style=""display:inline-block;background:#2563eb;color:#ffffff;
               text-decoration:none;padding:14px 30px;font-weight:600;font-size:15px;"">
               {label}</a>";

        /// <summary>
        /// HTML-encodes an interpolated value. Client names, addresses and admin-typed messages
        /// all reach these templates, and an unescaped apostrophe or angle bracket would break the
        /// markup at best.
        /// </summary>
        private static string E(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

        private static string Money(decimal value) =>
            value.ToString("C2", System.Globalization.CultureInfo.GetCultureInfo("en-US"));

        private static string Truncate(string value, int max) =>
            value.Length <= max ? value : value[..max];

        private static string? Trim(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
