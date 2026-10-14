using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace DreamCleaningBackend.Services
{
    /// <summary>
    /// Outgoing Payments → Admins (2026-10, SuperAdmin): per admin, the orders they booked that were
    /// completed in a period, what those orders came to without tips, and the net the company kept
    /// after sales tax, the cleaners' wages and the staff bonuses on those orders.
    ///
    /// <b>Read-only and display-only.</b> It reports costs that are already reported elsewhere — the
    /// wages through <c>Order.CleanerTotalSalary</c>, the bonuses through
    /// <see cref="IAdminBonusService.GetOrderBonusCostsGelAsync"/> — and adds none of them anywhere.
    /// </summary>
    public class AdminPerformanceService : IAdminPerformanceService
    {
        private readonly ApplicationDbContext _context;
        private readonly IAdminBonusService _bonuses;
        private readonly IFinancialRateService _rates;

        public AdminPerformanceService(
            ApplicationDbContext context, IAdminBonusService bonuses, IFinancialRateService rates)
        {
            _context = context;
            _bonuses = bonuses;
            _rates = rates;
        }

        public async Task<AdminPerformanceListDto> GetAsync(DateTime? fromInclusive, DateTime? toInclusive)
        {
            var from = fromInclusive?.Date;
            var toExclusive = toInclusive?.Date.AddDays(1);
            if (from.HasValue && toExclusive.HasValue && toExclusive <= from)
                throw new InvalidOperationException("The end date cannot be before the start date.");

            // Admin role only — a SuperAdmin earns no bonus and is not measured here (owner's call).
            // Blocked admins stay: what they booked in the period still happened.
            var admins = await _context.Users
                .Where(u => !u.IsDeleted && u.Role == UserRole.Admin)
                .Select(u => new { u.Id, u.FirstName, u.LastName, u.Email, u.AdminPosition, u.IsActive })
                .ToListAsync();
            var adminIds = admins.Select(a => a.Id).ToList();

            // "Completed" is the bonus rule — Done AND paid — so an order counts here exactly when
            // it pays its booker a bonus. It belongs to its BOOKER (the snapshotted assignment).
            var query = _context.Orders
                .Where(o => o.BonusBookerId != null && adminIds.Contains(o.BonusBookerId.Value))
                .Where(AdminBonusAttribution.BonusEligible);
            if (from.HasValue) query = query.Where(o => o.ServiceDate >= from.Value);
            if (toExclusive.HasValue) query = query.Where(o => o.ServiceDate < toExclusive.Value);

            var orders = await query
                .Select(o => new
                {
                    o.Id, BookerId = o.BonusBookerId!.Value, o.ServiceDate,
                    o.SubTotal, o.DiscountAmount, o.SubscriptionDiscountAmount, o.LoyaltyDiscountAmount,
                    o.Tax, o.Tips, o.CompanyDevelopmentTips, o.TotalRefundedAmount, o.CleanerTotalSalary,
                    o.IsNewCustomerOrder
                })
                .ToListAsync();

            // Every staff bonus each order costs (both slots + percentage), GEL — the same figures
            // Statistics converts. Same window, so every order above is covered.
            var bonusGel = await _bonuses.GetOrderBonusCostsGelAsync(from, toExclusive);
            var fx = new Dictionary<int, decimal>();

            var byAdmin = adminIds.ToDictionary(id => id, _ => new AdminPerformanceRowDto());
            foreach (var o in orders)
            {
                var figures = AdminOrderEarnings.Figures(
                    o.SubTotal, o.DiscountAmount, o.SubscriptionDiscountAmount, o.LoyaltyDiscountAmount,
                    o.Tax, o.Tips, o.CompanyDevelopmentTips, o.TotalRefundedAmount);

                decimal bonusUsd = 0m;
                if (bonusGel.TryGetValue(o.Id, out var gel) && gel != 0m)
                {
                    var key = o.ServiceDate.Year * 100 + o.ServiceDate.Month;
                    if (!fx.TryGetValue(key, out var usdPerGel))
                        fx[key] = usdPerGel = (await _rates.GetOrCreateAsync(o.ServiceDate.Year, o.ServiceDate.Month)).UsdPerGel;
                    bonusUsd = gel * usdPerGel;
                }

                var row = byAdmin[o.BookerId];
                var net = AdminOrderEarnings.Net(figures.Revenue, o.CleanerTotalSalary, bonusUsd);
                row.CompletedOrders++;
                row.OrdersTotal += figures.OrderTotal;
                row.SalesTax += figures.Tax;
                row.CleanerWages += o.CleanerTotalSalary;
                row.StaffBonuses += bonusUsd;
                row.NetIncome += net;
                // The two halves always re-add to the totals above: each order lands in exactly one.
                if (o.IsNewCustomerOrder)
                {
                    row.NewCustomerOrders++;
                    row.OrdersTotalNew += figures.OrderTotal;
                    row.NetIncomeNew += net;
                }
                else
                {
                    row.ReturningCustomerOrders++;
                    row.OrdersTotalReturning += figures.OrderTotal;
                    row.NetIncomeReturning += net;
                }
            }

            var rows = admins.Select(a =>
            {
                var row = byAdmin[a.Id];
                row.AdminId = a.Id;
                row.FirstName = a.FirstName;
                row.LastName = a.LastName;
                row.Email = a.Email;
                row.Position = a.AdminPosition.ToString();
                row.IsActive = a.IsActive;
                row.StaffBonuses = decimal.Round(row.StaffBonuses, 2);
                row.NetIncome = decimal.Round(row.NetIncome, 2);
                row.NetIncomeNew = decimal.Round(row.NetIncomeNew, 2);
                row.NetIncomeReturning = row.NetIncome - row.NetIncomeNew;
                return row;
            })
            // Busiest first; then by name so the list does not reshuffle between periods.
            .OrderByDescending(r => r.CompletedOrders)
            .ThenByDescending(r => r.OrdersTotal)
            .ThenBy(r => r.FirstName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.LastName, StringComparer.OrdinalIgnoreCase)
            .ToList();

            return new AdminPerformanceListDto
            {
                From = from,
                To = toInclusive?.Date,
                Admins = rows
            };
        }
    }

    public interface IAdminPerformanceService
    {
        /// <summary>Both dates inclusive service dates; null on either side = open-ended.</summary>
        Task<AdminPerformanceListDto> GetAsync(DateTime? fromInclusive, DateTime? toInclusive);
    }
}
