using System.ComponentModel.DataAnnotations;

namespace DreamCleaningBackend.DTOs
{
    /// <summary>One repeating tier as the customer's Plan tab shows it.</summary>
    public class PlanOptionDto
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public decimal DiscountPercentage { get; set; }
        public int SubscriptionDays { get; set; }
        public int DisplayOrder { get; set; }

        /// <summary>The customer picked this tier on their profile. A preference only.</summary>
        public bool IsPreferred { get; set; }

        /// <summary>
        /// The customer currently HOLDS this tier, so their next cleaning on it is discounted.
        /// Only ever true after a cleaning has been booked on the plan — see PlanSelectionPolicy.
        /// </summary>
        public bool IsActive { get; set; }
    }

    /// <summary>Everything the Plan tab renders, in one read.</summary>
    public class PlanOverviewDto
    {
        public List<PlanOptionDto> Plans { get; set; } = new();

        /// <summary>The tier the customer chose on their profile. Null = none chosen.</summary>
        public int? PreferredSubscriptionId { get; set; }

        /// <summary>The live plan, if any — the one that actually discounts the next cleaning.</summary>
        public int? ActiveSubscriptionId { get; set; }
        public string? ActiveSubscriptionName { get; set; }
        public decimal? ActiveDiscountPercentage { get; set; }
        public DateTime? ActiveExpiresAt { get; set; }

        /// <summary>
        /// True when the customer has never had a cleaning, i.e. the next one is their first and
        /// carries no plan discount whichever tier they pick. Drives the tab's wording only.
        /// </summary>
        public bool NextCleaningIsFirstOnPlan { get; set; }
    }

    public class SelectPlanDto
    {
        /// <summary>The tier to prefer. Null clears the choice.</summary>
        public int? SubscriptionId { get; set; }
    }
}
