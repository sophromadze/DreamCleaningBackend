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
        public string PaymentIntentId { get; set; }
        public string SessionId { get; set; } // Optional: for new bookings, use sessionId instead of orderId
    }
}
