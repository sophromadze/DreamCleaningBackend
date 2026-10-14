using System;
using System.Collections.Generic;

namespace DreamCleaningBackend.DTOs
{
    /// <summary>
    /// One admin on Outgoing Payments → Admins (2026-10): the orders they booked and completed in a
    /// period, what those orders came to, and what the company kept. See Helpers/AdminOrderEarnings.
    /// </summary>
    public class AdminPerformanceRowDto
    {
        public int AdminId { get; set; }
        public string FirstName { get; set; } = string.Empty;
        public string LastName { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        /// <summary>"Administrator" or "Manager".</summary>
        public string Position { get; set; } = "Administrator";
        public bool IsActive { get; set; }

        /// <summary>Completed (Done) and paid orders they booked, by service date.</summary>
        public int CompletedOrders { get; set; }
        // Split by Order.IsNewCustomerOrder — the same snapshot the new/returning bonus rates use.
        public int NewCustomerOrders { get; set; }
        public int ReturningCustomerOrders { get; set; }

        /// <summary>Those orders' price without tips (sales tax included, net of refunds), USD.</summary>
        public decimal OrdersTotal { get; set; }
        public decimal OrdersTotalNew { get; set; }
        public decimal OrdersTotalReturning { get; set; }

        /// <summary>Orders total − sales tax − cleaners' wages − staff bonuses, USD.</summary>
        public decimal NetIncome { get; set; }
        public decimal NetIncomeNew { get; set; }
        public decimal NetIncomeReturning { get; set; }

        // The three things taken off, so the net can be checked by hand.
        public decimal SalesTax { get; set; }
        public decimal CleanerWages { get; set; }
        public decimal StaffBonuses { get; set; }
    }

    public class AdminPerformanceListDto
    {
        /// <summary>Inclusive service-date range; null = open-ended (all time).</summary>
        public DateTime? From { get; set; }
        public DateTime? To { get; set; }
        public List<AdminPerformanceRowDto> Admins { get; set; } = new();
    }
}
