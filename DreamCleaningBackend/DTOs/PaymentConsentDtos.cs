using System;
using System.Text.Json.Serialization;

namespace DreamCleaningBackend.DTOs
{
    /// <summary>
    /// The three consents the payment page collects before an admin-created order's first
    /// payment (same wording as the /booking form). All three must be true — they are the
    /// customer's own agreement, not the admin's, so a partial tick is a rejected request.
    /// </summary>
    public class AcceptPaymentConsentDto
    {
        [JsonPropertyName("smsConsent")]
        public bool SmsConsent { get; set; }

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
