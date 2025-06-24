namespace DreamCleaningBackend.DTOs
{
    public class SubscriptionDto
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string? Description { get; set; }
        public decimal DiscountPercentage { get; set; }
        public int SubscriptionDays { get; set; }
        public bool IsActive { get; set; }
        public int DisplayOrder { get; set; }
    }
}
