using System;
using System.Text.Json.Serialization;

namespace DreamCleaningBackend.DTOs
{
    /// <summary>
    /// The consents the payment page collects before an admin-created order's first payment
    /// (same wording as the /booking form). Cancellation and terms must be true — they are the
    /// customer's own agreement, not the admin's. SMS is OPTIONAL (2026-10, never a condition of
    /// paying): it is recorded through Helpers/SmsConsentRules and null changes nothing.
    /// </summary>
    public class AcceptPaymentConsentDto
    {
        [JsonPropertyName("smsConsent")]
        public bool? SmsConsent { get; set; }

        /// <summary>Whether the page SHOWED the SMS box ticked (see SmsConsentRules).</summary>
        [JsonPropertyName("smsConsentShown")]
        public bool? SmsConsentShown { get; set; }

        [JsonPropertyName("cancellationConsent")]
        public bool CancellationConsent { get; set; }

        [JsonPropertyName("termsConsent")]
        public bool TermsConsent { get; set; }
    }

    public class PaymentConsentResultDto
    {
        public int OrderId { get; set; }
        public DateTime AcceptedAt { get; set; }
    }
}
