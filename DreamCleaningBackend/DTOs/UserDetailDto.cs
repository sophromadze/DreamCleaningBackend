namespace DreamCleaningBackend.DTOs
{
    public class UserDetailDto
    {
        public int Id { get; set; }
        public string FirstName { get; set; } = "";
        public string LastName { get; set; } = "";
        public string Email { get; set; } = "";
        public string? Phone { get; set; }
        public string Role { get; set; } = "";
        public string? AuthProvider { get; set; }
        public bool IsActive { get; set; }
        public bool FirstTimeOrder { get; set; }
        public int? SubscriptionId { get; set; }
        public string? SubscriptionName { get; set; }
        public DateTime? SubscriptionExpiryDate { get; set; }
        public DateTime CreatedAt { get; set; }

        // Statistics
        public int TotalOrders { get; set; }
        public decimal TotalSpent { get; set; }
        public DateTime? LastOrderDate { get; set; }
        public int ApartmentCount { get; set; }
    }
}
