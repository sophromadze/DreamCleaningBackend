namespace DreamCleaningBackend.DTOs
{
    public class ProfileDto
    {
        public int Id { get; set; }
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public string Email { get; set; }
        public string? ProfilePictureUrl { get; set; }
        public string? Phone { get; set; }
        public string Role { get; set; }
        public bool FirstTimeOrder { get; set; }
        public int? SubscriptionId { get; set; }
        public string? SubscriptionName { get; set; }
        public decimal? SubscriptionDiscountPercentage { get; set; }
        public DateTime? SubscriptionExpiryDate { get; set; }
        public List<ApartmentDto> Apartments { get; set; } = new List<ApartmentDto>();
        /// <summary>When true, user can receive emails and (in future) SMS. Used for both email and RingCentral messaging.</summary>
        public bool CanReceiveCommunications { get; set; }

        /// <summary>When true, user can receive emails from the company.</summary>
        public bool CanReceiveEmails { get; set; }

        /// <summary>When true, user can receive SMS/messages from the company.</summary>
        public bool CanReceiveMessages { get; set; }
        // Placeholder for future orders
        // public List<OrderDto> Orders { get; set; } = new List<OrderDto>();
    }
}
