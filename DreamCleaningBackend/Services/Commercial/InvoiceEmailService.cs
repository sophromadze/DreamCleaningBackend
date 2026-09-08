using System.Net;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs.Commercial;
using DreamCleaningBackend.Helpers.Commercial;
using DreamCleaningBackend.Models.Commercial;
using DreamCleaningBackend.Services.Interfaces;
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
            var cutoff = DateTime.UtcNow.AddHours(-24);

            var attempts = await _context.CommercialInvoicePaymentAttempts
                .Where(a => a.CommercialInvoiceId == invoiceId
                            && (a.Status == InvoicePaymentAttemptStatus.Processing
                                || a.Status == InvoicePaymentAttemptStatus.CheckoutOpen))
                .ToListAsync();

            return attempts.Any(a =>
                (a.Status == InvoicePaymentAttemptStatus.Processing || a.CreatedAt >= cutoff)
                && a.Amount >= balanceDue);
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

        private static string BuildInvoiceEmailBody(
            PublicInvoiceDto invoice, string publicUrl, string? adminMessage)
        {
            var period = InvoicePdfService.FormatServicePeriod(invoice);

            return Wrap($@"
                <h2 style='margin:0 0 16px;font-size:20px;color:#0f172a;'>Invoice {E(invoice.InvoiceNumber)}</h2>
                <p>Hello{(string.IsNullOrWhiteSpace(invoice.BillingContactName) ? "" : " " + E(invoice.BillingContactName!))},</p>
                <p>Please find your invoice from {E(invoice.Company.LegalName)}
                   {(string.IsNullOrWhiteSpace(invoice.Company.DbaName) ? "" : "(DBA " + E(invoice.Company.DbaName!) + ")")}
                   for cleaning services at {E(invoice.ServiceAddress ?? invoice.ClientName)}.</p>
                {(string.IsNullOrWhiteSpace(adminMessage) ? "" : $"<p style='background:#f8fafc;border-left:3px solid #2563eb;padding:10px 14px;'>{E(adminMessage!)}</p>")}
                {SummaryTable(invoice, period)}
                <p style='margin:28px 0;'>{Button(publicUrl, "View &amp; Pay Invoice")}</p>
                {PaymentBlurb(invoice)}
                {NoteBlock(invoice.CustomerNote)}");
        }

        private static string BuildReminderEmailBody(PublicInvoiceDto invoice, string publicUrl)
        {
            var overdue = invoice.Status == InvoiceStatus.Overdue;

            return Wrap($@"
                <h2 style='margin:0 0 16px;font-size:20px;color:#0f172a;'>
                    {(overdue ? "Invoice past due" : "A friendly payment reminder")}</h2>
                <p>Hello{(string.IsNullOrWhiteSpace(invoice.BillingContactName) ? "" : " " + E(invoice.BillingContactName!))},</p>
                <p>{(overdue
                    ? $"Invoice <strong>{E(invoice.InvoiceNumber)}</strong> was due on {invoice.DueDate:MMMM d, yyyy} and has an outstanding balance."
                    : $"This is a reminder that invoice <strong>{E(invoice.InvoiceNumber)}</strong> is due on {invoice.DueDate:MMMM d, yyyy}.")}</p>
                {SummaryTable(invoice, null)}
                <p style='margin:28px 0;'>{Button(publicUrl, "View &amp; Pay Invoice")}</p>
                <p style='color:#475569;font-size:13px;'>If this payment has already been sent, please
                   ignore this message - bank transfers can take a few days to appear. Do let us know
                   if anything about the invoice needs correcting.</p>");
        }

        private static string BuildReceiptEmailBody(PublicInvoiceDto invoice, string publicUrl)
        {
            return Wrap($@"
                <h2 style='margin:0 0 16px;font-size:20px;color:#15803d;'>Payment received</h2>
                <p>Hello{(string.IsNullOrWhiteSpace(invoice.BillingContactName) ? "" : " " + E(invoice.BillingContactName!))},</p>
                <p>Thank you - we have received payment in full for invoice
                   <strong>{E(invoice.InvoiceNumber)}</strong>.</p>
                <table style='width:100%;border-collapse:collapse;margin:18px 0;font-size:14px;'>
                    <tr><td style='padding:6px 0;color:#475569;'>Invoice</td>
                        <td style='padding:6px 0;text-align:right;'>{E(invoice.InvoiceNumber)}</td></tr>
                    <tr><td style='padding:6px 0;color:#475569;'>Amount paid</td>
                        <td style='padding:6px 0;text-align:right;'>{Money(invoice.AmountPaid)}</td></tr>
                    <tr><td style='padding:6px 0;color:#475569;'>Payment date</td>
                        <td style='padding:6px 0;text-align:right;'>{(invoice.PaidAt?.ToString("MMMM d, yyyy") ?? "-")}</td></tr>
                    <tr><td style='padding:10px 0;border-top:2px solid #15803d;font-weight:700;'>Balance due</td>
                        <td style='padding:10px 0;border-top:2px solid #15803d;text-align:right;font-weight:700;color:#15803d;'>
                            {Money(invoice.BalanceDue)}</td></tr>
                </table>
                <p style='margin:28px 0;'>{Button(publicUrl, "View receipt")}</p>
                <p style='color:#475569;font-size:13px;'>We appreciate your business.</p>");
        }

        private static string SummaryTable(PublicInvoiceDto invoice, string? period) => $@"
            <table style='width:100%;border-collapse:collapse;margin:18px 0;font-size:14px;'>
                <tr><td style='padding:6px 0;color:#475569;'>Invoice number</td>
                    <td style='padding:6px 0;text-align:right;'>{E(invoice.InvoiceNumber)}</td></tr>
                <tr><td style='padding:6px 0;color:#475569;'>Invoice date</td>
                    <td style='padding:6px 0;text-align:right;'>{invoice.InvoiceDate:MMMM d, yyyy}</td></tr>
                <tr><td style='padding:6px 0;color:#475569;'>Due date</td>
                    <td style='padding:6px 0;text-align:right;'>{invoice.DueDate:MMMM d, yyyy}</td></tr>
                {(period == null ? "" : $@"<tr><td style='padding:6px 0;color:#475569;'>Service period</td>
                    <td style='padding:6px 0;text-align:right;'>{E(period)}</td></tr>")}
                {(string.IsNullOrWhiteSpace(invoice.ServiceAddress) ? "" : $@"<tr><td style='padding:6px 0;color:#475569;'>Service location</td>
                    <td style='padding:6px 0;text-align:right;'>{E(invoice.ServiceAddress!)}</td></tr>")}
                {(string.IsNullOrWhiteSpace(invoice.ContractNumber) ? "" : $@"<tr><td style='padding:6px 0;color:#475569;'>Contract</td>
                    <td style='padding:6px 0;text-align:right;'>{E(invoice.ContractNumber!)}</td></tr>")}
                <tr><td style='padding:10px 0;border-top:2px solid #2563eb;font-weight:700;'>Amount due</td>
                    <td style='padding:10px 0;border-top:2px solid #2563eb;text-align:right;font-weight:700;font-size:17px;color:#2563eb;'>
                        {Money(invoice.BalanceDue)}</td></tr>
            </table>";

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
                lines.Add(
                    "You can pay securely straight from your US bank account by opening the invoice "
                    + "above and choosing <strong>Pay from Bank</strong>.");
            }

            if (card)
                lines.Add("Card payment is also available on the invoice page.");

            if (manual)
            {
                lines.Add(
                    "Prefer to send the transfer yourself? Full bank transfer instructions are on the "
                    + $"invoice page. Please include invoice number <strong>{E(invoice.InvoiceNumber)}</strong> "
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

        /// <summary>The shared shell, matching the contract notification mails.</summary>
        private static string Wrap(string inner) => $@"
            <div style=""font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,Arial,sans-serif;
                        max-width:640px;margin:0 auto;padding:32px 24px;color:#0f172a;line-height:1.6;"">
                {inner}
                <hr style='border:none;border-top:1px solid #e2e8f0;margin:32px 0 16px;'>
                <p style='color:#94a3b8;font-size:12px;margin:0;'>
                    This message was sent by Dream Cleaning NYC regarding a commercial cleaning
                    account. Please do not share the invoice link with anyone outside your
                    organization.
                </p>
            </div>";

        private static string Button(string url, string label) => $@"
            <a href='{url}' style=""display:inline-block;background:#2563eb;color:#ffffff;
               text-decoration:none;padding:13px 26px;border-radius:8px;font-weight:600;font-size:15px;"">
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
