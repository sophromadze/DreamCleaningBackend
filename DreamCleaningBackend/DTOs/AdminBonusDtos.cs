using System;
using System.Collections.Generic;

namespace DreamCleaningBackend.DTOs
{
    // Body of PATCH /api/order/{id}/assigned-admin — adminId == null clears the assignment.
    public class AssignAdminDto
    {
        public int? AdminId { get; set; }
    }

    // Returned by the assign endpoint and embedded inside OrderDto/OrderListDto so the
    // frontend can render the "By: F. LastName" pill without a second fetch.
    public class OrderAssignedAdminDto
    {
        public int? AdminId { get; set; }
        public string? FirstName { get; set; }
        public string? LastName { get; set; }
        // Display form used by the order-details pill: "F. LastName" (e.g. "J. Smith").
        // Null when no admin is assigned.
        public string? DisplayName { get; set; }
    }

    // One row per admin for the shifts bonus panel and the user-profile stat.
    public class AdminBonusSummaryDto
    {
        public int AdminId { get; set; }
        public string FirstName { get; set; } = string.Empty;
        public string LastName { get; set; } = string.Empty;
        public string? ShiftColor { get; set; }
        // Total assigned orders in the period (any status).
        public int AssignedCount { get; set; }
        // Orders eligible for bonus payout in the period.
        public int EligibleCount { get; set; }
        // EligibleCount * RatePerOrder, in the configured currency (GEL).
        public decimal BonusAmount { get; set; }
        public decimal RatePerOrder { get; set; }
        public string Currency { get; set; } = "GEL";
    }

    public class AdminBonusRateDto
    {
        public decimal RatePerOrder { get; set; }
        public string Currency { get; set; } = "GEL";
        public DateTime UpdatedAt { get; set; }
        public int? UpdatedByUserId { get; set; }
        public string? UpdatedByUserName { get; set; }
    }

    public class SetAdminBonusRateDto
    {
        public decimal RatePerOrder { get; set; }
    }
}
