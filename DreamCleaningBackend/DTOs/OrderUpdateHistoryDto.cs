namespace DreamCleaningBackend.DTOs
{
    public class OrderUpdateHistoryDto
    {
        public int Id { get; set; }
        public DateTime UpdatedAt { get; set; }
        public string UpdatedBy { get; set; }
        public string UpdatedByEmail { get; set; }
        public decimal OriginalSubTotal { get; set; }
        public decimal OriginalTax { get; set; }
        public decimal OriginalTips { get; set; }
        public decimal OriginalCompanyDevelopmentTips { get; set; }
        public decimal OriginalTotal { get; set; }
        public decimal NewSubTotal { get; set; }
        public decimal NewTax { get; set; }
        public decimal NewTips { get; set; }
        public decimal NewCompanyDevelopmentTips { get; set; }
        public decimal NewTotal { get; set; }
        public decimal AdditionalAmount { get; set; }
        public string? PaymentIntentId { get; set; }
        public bool IsPaid { get; set; }
        public DateTime? PaidAt { get; set; }
        public string? UpdateNotes { get; set; }
    }
}
