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

    // One row per staff member for the shifts bonus panel and the user-profile stat.
    //
    // A person earns on up to two slots (see AdminBonusAttribution): the OWN counts are bookings
    // they took themselves, the TEAM counts are bookings one of their administrators took that
    // they earn a manager's share of. They are reported separately because they pay DIFFERENT
    // rates, not merely because they are different questions — collapsing them would make the
    // bonus impossible to check by hand.
    public class AdminBonusSummaryDto
    {
        public int AdminId { get; set; }
        public string FirstName { get; set; } = string.Empty;
        public string LastName { get; set; } = string.Empty;
        public string? ShiftColor { get; set; }

        /// <summary>"Administrator" or "Manager" — the position they hold TODAY.</summary>
        public string Position { get; set; } = nameof(Models.AdminPosition.Administrator);

        /// <summary>The manager this administrator reports to; null for managers and unattached admins.</summary>
        public int? ManagerId { get; set; }
        public string? ManagerName { get; set; }

        /// <summary>Number of administrators reporting to this person. Only meaningful for a Manager.</summary>
        public int TeamSize { get; set; }

        // Orders they earn on in the period, any status — the "(N assigned)" hint measures the
        // eligible count against this.
        public int AssignedCount { get; set; }
        // Orders eligible for bonus payout in the period, both slots combined.
        public int EligibleCount { get; set; }

        /// <summary>Eligible orders this person booked themselves, for a first-time customer.</summary>
        public int OwnNewCustomerCount { get; set; }
        /// <summary>Eligible orders this person booked themselves, for a returning customer.</summary>
        public int OwnExistingCustomerCount { get; set; }
        /// <summary>Eligible orders one of their administrators booked, for a first-time customer.</summary>
        public int TeamNewCustomerCount { get; set; }
        /// <summary>Eligible orders one of their administrators booked, for a returning customer.</summary>
        public int TeamExistingCustomerCount { get; set; }

        /// <summary>
        /// Rates for orders this person books themselves, at the position they hold TODAY. The
        /// bonus amount is computed per order from the position snapshotted on it, so for somebody
        /// promoted mid-period these rates describe what they earn from now on rather than every
        /// order in the count.
        /// </summary>
        public decimal OwnNewCustomerRate { get; set; }
        public decimal OwnExistingCustomerRate { get; set; }
        public bool OwnNewCustomerRateIsCustom { get; set; }
        public bool OwnExistingCustomerRateIsCustom { get; set; }

        /// <summary>Rates for a manager's share of their administrators' bookings.</summary>
        public decimal TeamNewCustomerRate { get; set; }
        public decimal TeamExistingCustomerRate { get; set; }
        public bool TeamNewCustomerRateIsCustom { get; set; }
        public bool TeamExistingCustomerRateIsCustom { get; set; }

        public decimal BonusAmount { get; set; }
        public string Currency { get; set; } = "GEL";
    }

    /// <summary>
    /// The company-wide defaults: three slots x whether the customer was new. Slot 2 (a manager
    /// booking an order themselves) is its own pair, NOT slot 1 plus slot 3 — see AdminBonusSetting.
    /// </summary>
    public class AdminBonusRatesDto
    {
        public decimal AdministratorNewCustomerRate { get; set; }
        public decimal AdministratorExistingCustomerRate { get; set; }
        public decimal ManagerOwnBookingNewCustomerRate { get; set; }
        public decimal ManagerOwnBookingExistingCustomerRate { get; set; }
        public decimal ManagerTeamNewCustomerRate { get; set; }
        public decimal ManagerTeamExistingCustomerRate { get; set; }
        public string Currency { get; set; } = "GEL";
        public DateTime UpdatedAt { get; set; }
        public int? UpdatedByUserId { get; set; }
        public string? UpdatedByUserName { get; set; }
    }

    public class SetAdminBonusRatesDto
    {
        public decimal AdministratorNewCustomerRate { get; set; }
        public decimal AdministratorExistingCustomerRate { get; set; }
        public decimal ManagerOwnBookingNewCustomerRate { get; set; }
        public decimal ManagerOwnBookingExistingCustomerRate { get; set; }
        public decimal ManagerTeamNewCustomerRate { get; set; }
        public decimal ManagerTeamExistingCustomerRate { get; set; }
    }

    /// <summary>
    /// Per-person rates: one pair for orders they book themselves, one for a manager's share of
    /// their administrators' bookings. NULL on a field means "follow the company default" — that is
    /// how an override is cleared, and it is deliberately different from sending the default's
    /// current value, which would pin the person to today's figure forever.
    /// </summary>
    public class SetAdminBonusOverrideDto
    {
        public decimal? OwnBookingNewCustomerRate { get; set; }
        public decimal? OwnBookingExistingCustomerRate { get; set; }
        public decimal? TeamBookingNewCustomerRate { get; set; }
        public decimal? TeamBookingExistingCustomerRate { get; set; }
    }
}
