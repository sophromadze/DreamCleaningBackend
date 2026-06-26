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
        // False when the payable total is below Stripe's minimum charge (e.g. a gift card
        // fully covers the order). In that case no PaymentIntent/ClientSecret is created and
        // the frontend must skip the Stripe card step and confirm the booking directly.
        public bool RequiresPayment { get; set; } = true;
        public string SessionId { get; set; } // For new bookings created via prepare-payment
        // Guest booking: returned when user was auto-created so frontend can authenticate
        public string? GuestToken { get; set; }
        public string? GuestRefreshToken { get; set; }
        public UserDto? GuestUser { get; set; }
    }

    public class ConfirmPaymentDto
    {
        [JsonPropertyName("paymentIntentId")]
        public string? PaymentIntentId { get; set; }
        [JsonPropertyName("sessionId")]
        public string? SessionId { get; set; } // Optional: for new bookings only; not sent for admin-scheduled / profile payments
    }
}
