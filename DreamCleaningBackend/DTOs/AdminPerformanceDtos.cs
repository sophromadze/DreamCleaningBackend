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

        /// <summary>Orders total − sales tax − cleaners' wages − Stripe fees, USD. Staff bonuses are
        /// NOT taken off (owner's rule, 2026-10-16) — they are reported beside it.</summary>
        public decimal NetIncome { get; set; }
        public decimal NetIncomeNew { get; set; }
        public decimal NetIncomeReturning { get; set; }

        // The three things taken off, so the net can be checked by hand.
        public decimal SalesTax { get; set; }
        public decimal CleanerWages { get; set; }
        public decimal StripeFees { get; set; }

        /// <summary>Every staff bonus those orders cost, USD — shown under the net, never taken off it.</summary>
        public decimal StaffBonuses { get; set; }
        public decimal StaffBonusesNew { get; set; }
        public decimal StaffBonusesReturning { get; set; }
    }

    public class AdminPerformanceListDto
    {
        /// <summary>Inclusive service-date range; null = open-ended (all time).</summary>
        public DateTime? From { get; set; }
        public DateTime? To { get; set; }
        public List<AdminPerformanceRowDto> Admins { get; set; } = new();
    }

    /// <summary>
    /// Shifts → one admin's "Orders" (2026-10): the orders that admin booked and completed in the
    /// period, with no customer details at all — no name, email, phone, address or customer id.
    /// Same order set and money as Outgoing Payments → Admins, staff bonuses left out (an admin
    /// may open their own, and an order's bonuses include their manager's share).
    /// </summary>
    public class AdminOrdersViewDto
    {
        public int AdminId { get; set; }
        public string FirstName { get; set; } = string.Empty;
        public string LastName { get; set; } = string.Empty;
        public DateTime? From { get; set; }
        public DateTime? To { get; set; }
        public AdminOrdersSummaryDto Summary { get; set; } = new();
        public List<AdminOrderLineDto> Orders { get; set; } = new();
    }

    public class AdminOrdersSummaryDto
    {
        public int Orders { get; set; }
        public int NewCustomerOrders { get; set; }
        public int ReturningCustomerOrders { get; set; }

        public decimal SubTotal { get; set; }
        public decimal Discounts { get; set; }
        public decimal SalesTax { get; set; }
        /// <summary>Price without tips, sales tax included, net of refunds.</summary>
        public decimal OrdersTotal { get; set; }
        public decimal OrdersTotalNew { get; set; }
        public decimal OrdersTotalReturning { get; set; }
        public decimal CleanerWages { get; set; }
        public decimal StripeFees { get; set; }
        /// <summary>Orders total − sales tax − cleaners' wages − Stripe fees.</summary>
        public decimal NetIncome { get; set; }
        public decimal NetIncomeNew { get; set; }
        public decimal NetIncomeReturning { get; set; }

        // Per-order averages; null when there is no order of that kind to average.
        public decimal? AveragePrice { get; set; }
        public decimal? AveragePriceNew { get; set; }
        public decimal? AveragePriceReturning { get; set; }
        public decimal? AverageNet { get; set; }
        public decimal? AverageNetNew { get; set; }
        public decimal? AverageNetReturning { get; set; }
    }

    public class AdminOrderLineDto
    {
        public int OrderId { get; set; }
        public DateTime ServiceDate { get; set; }
        public string ServiceType { get; set; } = string.Empty;

        // The ingredients of the SHORT label ("Regular", "Deep", "Move In/Out", the label of a
        // Pre-arranged order). The rule lives once, in the frontend's
        // shared/admin/service-type-short-label.ts, so this view names a type exactly as the admin
        // Orders tab does — same arrangement as OutgoingPaymentOrderDto.

        /// <summary>The RAW ServiceType.Name, before a custom label is folded in.</summary>
        public string RawServiceTypeName { get; set; } = string.Empty;
        /// <summary>ServiceType.ServiceKey of a non-custom type; null for custom / unkeyed.</summary>
        public string? ServiceTypeKey { get; set; }
        public bool IsCustomServiceType { get; set; }
        /// <summary>The per-order label an admin chose for a custom ("Pre-Arranged") order.</summary>
        public string? CustomServiceDisplayName { get; set; }
        /// <summary>Residential only: whether the deep-cleaning extra is on this order.</summary>
        public bool IsDeepCleaning { get; set; }

        public bool IsNewCustomerOrder { get; set; }
        /// <summary>"Card", "Cash", "Zelle", "Check", "Invoice", "Bank transfer", "Other".</summary>
        public string PaymentMethod { get; set; } = string.Empty;
        public List<AdminOrderItemDto> Services { get; set; } = new();
        public List<AdminOrderItemDto> ExtraServices { get; set; } = new();

        /// <summary>The order's subtotal before discounts and tax.</summary>
        public decimal SubTotal { get; set; }
        /// <summary>Promo + subscription + loyalty discounts.</summary>
        public decimal Discounts { get; set; }
        public decimal SalesTax { get; set; }
        public decimal RefundedAmount { get; set; }
        /// <summary>Price without tips, sales tax included, net of refunds.</summary>
        public decimal OrderTotal { get; set; }
        public decimal CleanerWages { get; set; }
        public decimal StripeFee { get; set; }
        /// <summary>OrderTotal − SalesTax − CleanerWages − StripeFee.</summary>
        public decimal Net { get; set; }
    }

    public class AdminOrderItemDto
    {
        public string Name { get; set; } = string.Empty;
        /// <summary>Service.ServiceKey ("bedrooms", "bathrooms", "sqft", …) or the extra's key; null when unkeyed.</summary>
        public string? Key { get; set; }
        public int Quantity { get; set; }
        /// <summary>Hours on an hourly extra; 0 otherwise.</summary>
        public decimal Hours { get; set; }
        public decimal Cost { get; set; }
    }
}
