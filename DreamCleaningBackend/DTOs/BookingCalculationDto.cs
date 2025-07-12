namespace DreamCleaningBackend.DTOs
{
    public class BookingCalculationDto
    {
        public decimal SubTotal { get; set; }
        public decimal Tax { get; set; }
        public decimal DiscountAmount { get; set; }
        public decimal Tips { get; set; }
        public decimal CompanyDevelopmentTips { get; set; }
        public decimal Total { get; set; }
        public decimal TotalDuration { get; set; }
    }
}
