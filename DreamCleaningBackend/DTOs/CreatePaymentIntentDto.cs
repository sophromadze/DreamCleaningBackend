namespace DreamCleaningBackend.DTOs
{
    public class CreatePaymentIntentDto
    {
        public long Amount { get; set; } // Amount in cents
        public string Currency { get; set; }
        public Dictionary<string, string> Metadata { get; set; }
    }
}
