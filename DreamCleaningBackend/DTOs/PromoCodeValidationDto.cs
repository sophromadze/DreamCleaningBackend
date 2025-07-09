namespace DreamCleaningBackend.DTOs
{
    public class PromoCodeValidationDto
    {
        public bool IsValid { get; set; }
        public decimal DiscountValue { get; set; }
        public bool IsPercentage { get; set; }
        public string? Message { get; set; }
        public bool IsGiftCard { get; set; } = false;
        public decimal AvailableBalance { get; set; } = 0;
    }
}
