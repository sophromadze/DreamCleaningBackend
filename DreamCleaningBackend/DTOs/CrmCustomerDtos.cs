using System.ComponentModel.DataAnnotations;

namespace DreamCleaningBackend.DTOs
{
    // ── Customer list / 360 ──

    public class CrmCustomerListItemDto
    {
        public int Id { get; set; }
        public string FirstName { get; set; } = string.Empty;
        public string LastName { get; set; } = string.Empty;
        public string FullName { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string? Phone { get; set; }
        public decimal LifetimeValue { get; set; }
        public int OrderCount { get; set; }
        public DateTime? LastOrderDate { get; set; }
        public DateTime CreatedAt { get; set; }
        public bool IsSubscribed { get; set; }
        public string? SubscriptionName { get; set; }
        public int BubblePoints { get; set; }
        /// <summary>Single primary funnel stage: New / Active / AtRisk / Churned / Prospect.</summary>
        public string LifecycleStage { get; set; } = string.Empty;
        /// <summary>All computed segment keys this customer belongs to (overlapping).</summary>
        public List<string> Segments { get; set; } = new();
        /// <summary>Manual admin-applied tags.</summary>
        public List<CustomerTagDto> Tags { get; set; } = new();
    }

    public class CrmCustomerDetailDto : CrmCustomerListItemDto
    {
        public decimal AverageOrderValue { get; set; }
        public DateTime? FirstOrderDate { get; set; }
        public int ConsecutiveOrderCount { get; set; }
        public decimal LoyaltyDiscountPercentage { get; set; }
        public decimal BubbleCredits { get; set; }
        public bool CanReceiveEmails { get; set; }
        public bool CanReceiveMessages { get; set; }
        public List<CrmCustomerOrderDto> RecentOrders { get; set; } = new();
    }

    public class CrmCustomerOrderDto
    {
        public int Id { get; set; }
        public DateTime ServiceDate { get; set; }
        public decimal Total { get; set; }
        public string Status { get; set; } = string.Empty;
        public string ServiceTypeName { get; set; } = string.Empty;
        public string? ServiceAddress { get; set; }
    }

    // ── Segments ──

    public class CrmSegmentDto
    {
        public string Key { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public int Count { get; set; }
        public decimal TotalValue { get; set; }
    }

    // ── Tags ──

    public class CustomerTagDto
    {
        public int Id { get; set; }
        public int UserId { get; set; }
        public string Label { get; set; } = string.Empty;
        public string? Color { get; set; }
        public string? CreatedByAdminName { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    public class CreateCustomerTagDto
    {
        [Required]
        [StringLength(50)]
        public string Label { get; set; } = string.Empty;

        [StringLength(10)]
        public string? Color { get; set; }
    }
}
