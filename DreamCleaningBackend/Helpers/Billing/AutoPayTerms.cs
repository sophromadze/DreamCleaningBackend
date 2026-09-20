using System.Security.Cryptography;
using System.Text;
using DreamCleaningBackend.Models.Billing;

namespace DreamCleaningBackend.Helpers.Billing
{
    /// <summary>What the arrangement-specific paragraphs need to say.</summary>
    public class AutoPayTermsContext
    {
        public bool AllowBackupFallback { get; set; }

        /// <summary>"every 2 weeks at 123 Main St, Apt 4" — recurring scope only.</summary>
        public string? SeriesDescription { get; set; }

        /// <summary>The business client's name — commercial scope only.</summary>
        public string? ClientName { get; set; }
    }

    /// <summary>
    /// THE WORDING OF EVERY AUTOMATIC-PAYMENT AUTHORISATION, and the single source of it.
    ///
    /// The Billing tab renders exactly the text this class returns, and the text (plus its SHA-256)
    /// is frozen onto the <see cref="PaymentAuthorization"/> row the customer's click creates. So
    /// what was shown, what was agreed and what is on record cannot drift apart.
    ///
    /// EVERY SENTENCE HERE DESCRIBES WHAT THE CODE ACTUALLY DOES — the timing rules, the "one card
    /// once, never twice" Backup rule, the balance-due amount. If the charging behaviour changes,
    /// this wording changes in the same edit and <see cref="Version"/> moves, which invalidates the
    /// version any open browser tab is holding (the accept endpoints refuse a stale version).
    ///
    /// The office-booked-orders consents are the booking form's three consents verbatim
    /// (<c>shared/booking/consent-texts.ts</c> on the frontend). Keep them identical.
    ///
    /// NOTE FOR THE OWNER: this is operational wording drafted by engineering. Have it reviewed
    /// before AutoPay is switched on in production (<c>Billing:AutoPayEnabled</c>).
    /// </summary>
    public static class AutoPayTerms
    {
        public const string Version = "2026-09.1";

        public const string SmsConsentText =
            "I consent to receive Customer care text messages from Dream Cleaning. Reply STOP to opt-out; " +
            "Reply HELP for support; Message and data rates apply; Messaging frequency may vary. " +
            "Visit our Privacy Policy and Terms and Conditions for more information.";

        public const string CancellationConsentText =
            "I understand that I will be charged a $70 cancellation fee if I cancel or reschedule my " +
            "appointment less than 48 hours before the scheduled service. I also confirm that the booking " +
            "details accurately reflect the cleaning requirements. If the actual condition differs from the " +
            "described details, Dream Cleaning Team reserves the right to either leave or adjust the " +
            "services to match the actual condition. I understand, that cleaning lady may arrive within a " +
            "30-60 minute window of the scheduled cleaning time.";

        public const string TermsConsentText =
            "I have read and agree to Dream Cleaning's Privacy Policy and Terms and Conditions.";

        private const string CardStorage =
            "Your cards are stored by our payment processor, Stripe. Dream Cleaning NYC never stores your " +
            "full card number or security code.";

        private const string Amounts =
            "Every automatic charge is for the balance then owed on that specific order or invoice, calculated " +
            "by us from the order or invoice itself — including sales tax, tips, discounts, gift cards and any " +
            "amount you have already paid. Nothing that is already paid, cancelled or covered by another payment " +
            "is charged.";

        private const string Cancelling =
            "You can turn Automatic Payments off, change your Primary or Backup card, or revoke this " +
            "authorization at any time from the Billing tab of your profile. That stops future automatic " +
            "charges; it does not cancel a payment that has already been made, and it does not erase a balance " +
            "you still owe.";

        private static string BackupSentence(bool allowBackup) => allowBackup
            ? "Backup card: if your Primary card cannot be charged, we will try your Backup card once for that " +
              "same payment. You will never be charged twice for the same payment, and we will tell you that your " +
              "Primary card failed even when the Backup card succeeds."
            : "Backup card: not used for this arrangement. If your Primary card cannot be charged, we will not " +
              "try another card — we will notify you by email, text and in your account so you can pay.";

        public static string Render(PaymentAuthorizationScope scope, AutoPayTermsContext context)
        {
            var sb = new StringBuilder();

            switch (scope)
            {
                case PaymentAuthorizationScope.General:
                    sb.AppendLine("Automatic Payments — general agreement");
                    sb.AppendLine();
                    sb.AppendLine("By turning on Automatic Payments you allow Dream Cleaning NYC to charge the card marked " +
                                  "as your Primary card, but ONLY for the specific arrangements you authorize separately " +
                                  "(a recurring cleaning, orders our office books for you, or a business client's " +
                                  "invoices). Turning this on does not, by itself, authorize any charge.");
                    sb.AppendLine();
                    sb.AppendLine(Amounts);
                    sb.AppendLine();
                    sb.AppendLine("If an automatic payment fails, we will notify you by email, text message and in your " +
                                  "account, and you can pay yourself from your account. We never retry the same card " +
                                  "automatically.");
                    sb.AppendLine();
                    sb.AppendLine("Turning Automatic Payments off pauses every arrangement you have authorized; your saved " +
                                  "cards and your payment history are kept.");
                    break;

                case PaymentAuthorizationScope.RecurringSeries:
                    sb.AppendLine("Automatic payments for your recurring cleaning"
                                  + (string.IsNullOrWhiteSpace(context.SeriesDescription) ? "" : $" ({context.SeriesDescription})"));
                    sb.AppendLine();
                    sb.AppendLine("We will charge your Primary card for ONE cleaning at a time: the next unpaid cleaning in " +
                                  "this series. The charge is made when our system would otherwise send you a payment " +
                                  "request for it — no earlier than 24 hours after the previous cleaning in this series has " +
                                  "finished. We never charge several upcoming cleanings at once, and we never charge a " +
                                  "cleaning before the one ahead of it has been paid.");
                    sb.AppendLine();
                    sb.AppendLine("If the price of a cleaning changes before it is charged, the updated balance is charged. " +
                                  "A cancelled cleaning is not charged.");
                    sb.AppendLine();
                    sb.AppendLine(Amounts);
                    sb.AppendLine();
                    sb.AppendLine(BackupSentence(context.AllowBackupFallback));
                    break;

                case PaymentAuthorizationScope.OfficeBookedOrders:
                    sb.AppendLine("Card charges for orders our office books for you");
                    sb.AppendLine();
                    sb.AppendLine("You authorize Dream Cleaning NYC staff to charge your Primary card for an order they book " +
                                  "on your behalf (for example over the phone) when you ask them to. Nothing is charged " +
                                  "automatically under this authorization: a staff member charges one specific order, and " +
                                  "without this authorization we send you a payment link instead.");
                    sb.AppendLine();
                    sb.AppendLine(Amounts);
                    sb.AppendLine();
                    sb.AppendLine(BackupSentence(context.AllowBackupFallback));
                    sb.AppendLine();
                    sb.AppendLine("Because you will not see our booking form for these orders, you also agree, for every " +
                                  "order booked for you by our office:");
                    sb.AppendLine("• " + SmsConsentText);
                    sb.AppendLine("• " + CancellationConsentText);
                    sb.AppendLine("• " + TermsConsentText);
                    break;

                case PaymentAuthorizationScope.CommercialClient:
                    sb.AppendLine("Automatic payment of invoices"
                                  + (string.IsNullOrWhiteSpace(context.ClientName) ? "" : $" for {context.ClientName}"));
                    sb.AppendLine();
                    sb.AppendLine("On each invoice's due date, if a balance remains, we will charge your Primary card for " +
                                  "that invoice's remaining balance — no card fee is added. Your payment terms (for example " +
                                  "Net 15) do not change: nothing is charged before the due date.");
                    sb.AppendLine();
                    sb.AppendLine("An invoice that is already paid — by bank transfer, ACH, card or any other method — is not " +
                                  "charged, nor is one while a bank payment for it is still settling, and cleanings covered by " +
                                  "a paid invoice are never charged again individually. Each invoice is charged " +
                                  "automatically at most once; if that charge fails we will notify you and you can pay it from " +
                                  "your account. We never automatically debit a bank account.");
                    sb.AppendLine();
                    sb.AppendLine(BackupSentence(context.AllowBackupFallback));
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(scope));
            }

            sb.AppendLine();
            sb.AppendLine(Cancelling);
            sb.AppendLine();
            sb.Append(CardStorage);

            return sb.ToString().Replace("\r\n", "\n");
        }

        public static string Hash(string text) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }
}
