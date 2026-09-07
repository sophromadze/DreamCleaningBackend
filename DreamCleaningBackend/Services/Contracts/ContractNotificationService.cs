using System.Net;
using DreamCleaningBackend.Helpers.Contracts;
using DreamCleaningBackend.Services.Interfaces;

namespace DreamCleaningBackend.Services.Contracts
{
    /// <summary>
    /// Every email the contract system sends. Kept out of the general EmailService because these
    /// are paperwork notices with their own vocabulary - nothing here mentions payment, cards or
    /// checkout, and none of it touches the booking flow.
    ///
    /// Nothing is sent on preview: generating a document is an internal act, and the first mail a
    /// counterparty ever receives is the review invitation an admin explicitly approves.
    /// </summary>
    public class ContractNotificationService
    {
        private readonly IEmailService _email;
        private readonly IConfiguration _configuration;
        private readonly ILogger<ContractNotificationService> _logger;

        public ContractNotificationService(
            IEmailService email,
            IConfiguration configuration,
            ILogger<ContractNotificationService> logger)
        {
            _email = email;
            _configuration = configuration;
            _logger = logger;
        }

        public string FrontendUrl =>
            (_configuration["Frontend:Url"] ?? "https://dreamcleaningnyc.com").TrimEnd('/');

        public string BuildReviewUrl(string token) => $"{FrontendUrl}/contract/review/{token}";
        public string BuildSigningUrl(string token) => $"{FrontendUrl}/contract/sign/{token}";

        public async Task SendClientReviewInvitationAsync(
            string toEmail, string recipientName, string contractNumber,
            string contractorName, string reviewUrl)
        {
            if (string.IsNullOrWhiteSpace(toEmail))
            {
                _logger.LogWarning("No client email on contract {Number}; review invitation not sent.", contractNumber);
                return;
            }

            var subject = $"Please review your service agreement ({contractNumber})";
            var body = Wrap($@"
                <h2 style='margin:0 0 16px;font-size:20px;color:#0f172a;'>Your service agreement is ready to review</h2>
                <p>Hello {E(recipientName)},</p>
                <p>{E(contractorName)} has prepared the Master Service Agreement for your commercial cleaning
                   service. Please read it in full, check that your company details are correct, and continue to
                   signature when you are ready.</p>
                <p style='margin:28px 0;'>{Button(reviewUrl, "Review the agreement")}</p>
                <p style='color:#475569;font-size:13px;'>If anything in your own details needs correcting, you can
                   edit it directly on that page before signing. If a change is needed to the price, term, scope or
                   your company's legal name or address, we will prepare a revised version for you.</p>
                <p style='color:#64748b;font-size:12px;'>Reference: {E(contractNumber)}</p>");

            await _email.SendEmailAsync(toEmail, subject, body);
        }

        public async Task SendSignatureInvitationAsync(
            string toEmail, string recipientName, string contractNumber,
            string counterpartyName, string signingUrl, DateTime expiresAt)
        {
            if (string.IsNullOrWhiteSpace(toEmail))
            {
                _logger.LogWarning("No email for a signer on contract {Number}; signing link not sent.", contractNumber);
                return;
            }

            var subject = $"Signature requested: {contractNumber}";
            var body = Wrap($@"
                <h2 style='margin:0 0 16px;font-size:20px;color:#0f172a;'>Your signature is requested</h2>
                <p>Hello {E(recipientName)},</p>
                <p>The Master Service Agreement between {E(counterpartyName)} is ready for signature. Opening the
                   link below shows you the complete agreement and lets you sign it electronically.</p>
                <p style='margin:28px 0;'>{Button(signingUrl, "Review and sign")}</p>
                <p style='color:#475569;font-size:13px;'>This link is personal to you and signs on your behalf only.
                   Please do not forward it. It expires on {expiresAt:MMMM d, yyyy}.</p>
                <p style='color:#64748b;font-size:12px;'>Reference: {E(contractNumber)}</p>");

            await _email.SendEmailAsync(toEmail, subject, body);
        }

        public async Task SendFullyExecutedAsync(
            string toEmail, string recipientName, string contractNumber,
            string viewUrl, byte[]? pdfBytes, string pdfFileName)
        {
            if (string.IsNullOrWhiteSpace(toEmail)) return;

            var subject = $"Contract fully executed: {contractNumber}";
            var body = Wrap($@"
                <h2 style='margin:0 0 16px;font-size:20px;color:#0f172a;'>Your agreement is fully executed</h2>
                <p>Hello {E(recipientName)},</p>
                <p>Both parties have now signed the Master Service Agreement. The executed copy is attached to this
                   message, and includes the Electronic Signature Certificate recording who signed, when, and the
                   document hash each signature was applied to.</p>
                <p style='margin:28px 0;'>{Button(viewUrl, "View contract")}</p>
                <p style='color:#64748b;font-size:12px;'>Reference: {E(contractNumber)}</p>");

            if (pdfBytes is { Length: > 0 })
            {
                await _email.SendEmailWithAttachmentAsync(toEmail, subject, body, pdfBytes, pdfFileName, "application/pdf");
            }
            else
            {
                await _email.SendEmailAsync(toEmail, subject, body);
            }
        }

        /// <summary>
        /// Tells the admin team a client changed something that needs re-approval. Sent to the
        /// contractor notice address, because a revision blocks signing until somebody looks.
        /// </summary>
        public async Task SendRevisionRequestedAsync(
            string toEmail, string contractNumber, string clientName,
            IEnumerable<string> changedFields, string adminUrl)
        {
            if (string.IsNullOrWhiteSpace(toEmail)) return;

            var list = string.Join("</li><li>", changedFields.Select(E));
            var subject = $"Contract revision requested: {contractNumber}";
            var body = Wrap($@"
                <h2 style='margin:0 0 16px;font-size:20px;color:#0f172a;'>A client edit created a new version</h2>
                <p>{E(clientName)} changed the following on {E(contractNumber)}:</p>
                <ul><li>{list}</li></ul>
                <p>A new version has been generated and the contract is back in <strong>Needs revision</strong>.
                   It cannot be signed until it is approved again.</p>
                <p style='margin:28px 0;'>{Button(adminUrl, "Open the contract")}</p>");

            await _email.SendEmailAsync(toEmail, subject, body);
        }

        // ── shared chrome ──────────────────────────────────────────────────────

        private static string Wrap(string inner) => $@"
<!DOCTYPE html>
<html><body style='margin:0;padding:0;background:#f1f5f9;'>
  <div style=""max-width:600px;margin:0 auto;padding:32px 24px;font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,Helvetica,Arial,sans-serif;font-size:15px;line-height:1.6;color:#0f172a;"">
    <div style='background:#ffffff;border-radius:12px;padding:32px;border:1px solid #e2e8f0;'>
      {inner}
    </div>
    <p style='text-align:center;color:#94a3b8;font-size:12px;margin-top:24px;'>Dream Cleaning NYC</p>
  </div>
</body></html>";

        private static string Button(string url, string label) =>
            $@"<a href='{WebUtility.HtmlEncode(url)}' style='display:inline-block;background:#2563eb;color:#ffffff !important;
               text-decoration:none;padding:13px 26px;border-radius:8px;font-weight:600;'>{WebUtility.HtmlEncode(label)}</a>";

        private static string E(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

        /// <summary>Executed-document filename: DC-2026-0001-chick-tastic-llc-Executed.pdf</summary>
        public static string ExecutedFileName(string contractNumber, string clientLegalName) =>
            $"{contractNumber}-{ContractTextFormat.Slug(clientLegalName)}-Executed.pdf";
    }
}
