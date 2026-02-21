using System.Text.Json.Serialization;

namespace DreamCleaningBackend.DTOs
{
    public class BookingResponseDto
    {
        public int OrderId { get; set; }
        public string Status { get; set; }
        public decimal Total { get; set; }
        public string PaymentIntentId { get; set; }
        public string PaymentClientSecret { get; set; }
        public string SessionId { get; set; } // For new bookings created via prepare-payment
    }

    public class ConfirmPaymentDto
    {
        [JsonPropertyName("paymentIntentId")]
        public string? PaymentIntentId { get; set; }
        [JsonPropertyName("sessionId")]
        public string? SessionId { get; set; } // Optional: for new bookings only; not sent for admin-scheduled / profile payments
    }
}
