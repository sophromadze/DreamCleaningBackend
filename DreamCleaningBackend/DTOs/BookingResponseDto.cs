namespace DreamCleaningBackend.DTOs
{
    public class BookingResponseDto
    {
        public int OrderId { get; set; }
        public string Status { get; set; }
        public decimal Total { get; set; }
        public string PaymentIntentId { get; set; }
        public string PaymentClientSecret { get; set; }
    }

    public class ConfirmPaymentDto
    {
        public string PaymentIntentId { get; set; }
    }
}
