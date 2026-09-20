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

        // Set by prepare-payment when the intent for THIS attempt has already been charged on
        // Stripe but no order came out of it — a confirm-payment that fell over after the
        // charge. The frontend must skip the card step and confirm against THIS intent id
        // instead; asking for a new one charges the customer twice for one cleaning.
        //
        // Deliberately its own field rather than RequiresPayment=false: that flag already means
        // "a gift card covers everything", and the frontend answers it by confirming with an
        // EMPTY intent id — which on this path would ask the server to create an order nobody
        // paid for. The two outcomes look alike and mean opposite things.
        public string? AlreadyPaidPaymentIntentId { get; set; }

        /// <summary>
        /// True when the payer MAY save the card they are about to type (2026-09): saved cards
        /// are on, the payer is the account owner, and this intent carries that owner's Stripe
        /// Customer. The browser then applies the customer's choice from the pre-payment "Save
        /// your card?" modal as setup_future_usage=off_session at CONFIRMATION — on this same
        /// intent, before any money moves. False: nothing can be saved, so nothing is offered.
        /// </summary>
        public bool CanSaveCard { get; set; }

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
        // Optional: secret payment-link token (/order/{id}/pay?t=...) — lets a logged-out payer
        // confirm an existing order's payment when there is no Stripe intent to resolve them from.
        [JsonPropertyName("guestToken")]
        public string? GuestToken { get; set; }
    }
}
