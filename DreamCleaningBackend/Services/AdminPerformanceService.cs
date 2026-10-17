using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services.Interfaces;
using DreamCleaningBackend.Services.Reporting;
using Microsoft.EntityFrameworkCore;

namespace DreamCleaningBackend.Services
{
    /// <summary>
    /// Outgoing Payments → Admins (2026-10, SuperAdmin): per admin, the orders they booked that were
    /// completed in a period, what those orders came to without tips, and the net the company kept
    /// after sales tax, the cleaners' wages and the Stripe fees on those orders. The staff bonuses
    /// on them are reported BESIDE the net, never taken off it (owner's rule, 2026-10-16).
    /// Also the Shifts tab's per-admin "Orders" view (<see cref="GetAdminOrdersAsync"/>).
    ///
    /// <b>Read-only and display-only.</b> It reports costs that are already reported elsewhere — the
    /// wages through <c>Order.CleanerTotalSalary</c>, the Stripe fees through
    /// <see cref="IStripeFeeEstimator"/>, the bonuses through
    /// <see cref="IAdminBonusService.GetOrderBonusCostsGelAsync"/> — and adds none of them anywhere.
    /// </summary>
    public class AdminPerformanceService : IAdminPerformanceService
    {
        private readonly ApplicationDbContext _context;
        private readonly IAdminBonusService _bonuses;
        private readonly IFinancialRateService _rates;
        private readonly IStripeFeeEstimator _stripeFees;

        public AdminPerformanceService(
            ApplicationDbContext context, IAdminBonusService bonuses, IFinancialRateService rates,
            IStripeFeeEstimator? stripeFees = null)
        {
            _context = context;
            _bonuses = bonuses;
            _rates = rates;
            // Tests leave it out and get Stripe's standard rates.
            _stripeFees = stripeFees ?? new StripeFeeEstimator(context, null);
        }

        public async Task<AdminPerformanceListDto> GetAsync(DateTime? fromInclusive, DateTime? toInclusive)
        {
            var (from, admins, orderRows) = await LoadAsync(fromInclusive, toInclusive);
            var adminIds = admins.Select(a => a.Id).ToList();

            var byAdmin = adminIds.ToDictionary(id => id, _ => new AdminPerformanceRowDto());
            foreach (var o in orderRows)
            {
                var row = byAdmin[o.BookerId];
                row.CompletedOrders++;
                row.OrdersTotal += o.OrderTotal;
                row.SalesTax += o.SalesTax;
                row.CleanerWages += o.CleanerWages;
                row.StripeFees += o.StripeFee;
                row.StaffBonuses += o.StaffBonusesUsd;
                row.NetIncome += o.Net;
                // The two halves always re-add to the totals above: each order lands in exactly one.
                if (o.IsNewCustomerOrder)
                {
                    row.NewCustomerOrders++;
                    row.OrdersTotalNew += o.OrderTotal;
                    row.NetIncomeNew += o.Net;
                    row.StaffBonusesNew += o.StaffBonusesUsd;
                }
                else
                {
                    row.ReturningCustomerOrders++;
                    row.OrdersTotalReturning += o.OrderTotal;
                    row.NetIncomeReturning += o.Net;
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
                row.StaffBonusesNew = decimal.Round(row.StaffBonusesNew, 2);
                row.StaffBonusesReturning = row.StaffBonuses - row.StaffBonusesNew;
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

        public async Task<List<AdminPerformanceOrderRow>> GetOrderRowsAsync(DateTime? fromInclusive, DateTime? toInclusive)
            => (await LoadAsync(fromInclusive, toInclusive)).Orders;

        public async Task<AdminOrdersViewDto?> GetAdminOrdersAsync(int adminId, DateTime? fromInclusive, DateTime? toInclusive)
        {
            var (from, admins, rows) = await LoadAsync(fromInclusive, toInclusive, adminId);
            var admin = admins.FirstOrDefault(a => a.Id == adminId);
            if (admin == null) return null;

            var ids = rows.Select(r => r.OrderId).ToList();
            // Only what describes the JOB — never the customer (no name, email, phone, address, id).
            var details = (await _context.Orders
                    .Where(o => ids.Contains(o.Id))
                    .Select(o => new
                    {
                        o.Id, TypeName = o.ServiceType.Name, TypeKey = o.ServiceType.ServiceKey,
                        IsCustom = o.ServiceType.IsCustom, o.CustomServiceDisplayName, o.PaymentMethod,
                        o.SubTotal, Discounts = o.DiscountAmount + o.SubscriptionDiscountAmount + o.LoyaltyDiscountAmount,
                        o.TotalRefundedAmount,
                        Services = o.OrderServices
                            .OrderBy(s => s.Id)
                            .Select(s => new AdminOrderItemDto { Name = s.Service.Name, Key = s.Service.ServiceKey, Quantity = s.Quantity, Cost = s.Cost })
                            .ToList(),
                        Extras = o.OrderExtraServices
                            .OrderBy(e => e.Id)
                            .Select(e => new AdminOrderItemDto { Name = e.ExtraService.Name, Key = e.ExtraService.ExtraServiceKey, Quantity = e.Quantity, Hours = e.Hours, Cost = e.Cost })
                            .ToList(),
                        // Deep vs Regular, by the admin Orders list rule (ResidentialCleaningVariant).
                        DeepExtras = o.OrderExtraServices
                            .Select(e => new ResidentialCleaningVariant.Line
                            {
                                IsExtra = true,
                                Key = e.ExtraService.ExtraServiceKey,
                                Name = e.ExtraService.Name,
                                IsDeepCleaning = e.ExtraService.IsDeepCleaning,
                                IsSuperDeepCleaning = e.ExtraService.IsSuperDeepCleaning
                            })
                            .ToList()
                    })
                    .AsSplitQuery()
                    .ToListAsync())
                .ToDictionary(o => o.Id);

            var lines = rows
                .Where(r => details.ContainsKey(r.OrderId))
                .OrderByDescending(r => r.ServiceDate)
                .ThenByDescending(r => r.OrderId)
                .Select(r =>
                {
                    var d = details[r.OrderId];
                    var custom = d.CustomServiceDisplayName?.Trim();
                    return new AdminOrderLineDto
                    {
                        OrderId = r.OrderId,
                        ServiceDate = r.ServiceDate,
                        ServiceType = string.IsNullOrEmpty(custom) || custom == d.TypeName ? d.TypeName : $"{d.TypeName} — {custom}",
                        RawServiceTypeName = d.TypeName,
                        ServiceTypeKey = d.IsCustom || string.IsNullOrWhiteSpace(d.TypeKey) ? null : d.TypeKey.Trim(),
                        IsCustomServiceType = d.IsCustom,
                        CustomServiceDisplayName = d.IsCustom && !string.IsNullOrEmpty(custom) ? custom : null,
                        IsDeepCleaning = d.DeepExtras.Any(ResidentialCleaningVariant.LineMakesDeep)
                            || d.Services.Any(s => ResidentialCleaningVariant.ServiceLineMakesDeep(s.Key, s.Name)),
                        IsNewCustomerOrder = r.IsNewCustomerOrder,
                        PaymentMethod = PaymentMethodLabel(d.PaymentMethod),
                        Services = d.Services,
                        ExtraServices = d.Extras,
                        SubTotal = d.SubTotal,
                        Discounts = d.Discounts,
                        SalesTax = r.SalesTax,
                        RefundedAmount = d.TotalRefundedAmount,
                        OrderTotal = r.OrderTotal,
                        CleanerWages = r.CleanerWages,
                        StripeFee = r.StripeFee,
                        Net = r.Net
                    };
                })
                .ToList();

            return new AdminOrdersViewDto
            {
                AdminId = admin.Id,
                FirstName = admin.FirstName,
                LastName = admin.LastName,
                From = from,
                To = toInclusive?.Date,
                Summary = Summarise(lines),
                Orders = lines
            };
        }

        /// <summary>Totals and per-order averages, split new vs returning. Pure.</summary>
        public static AdminOrdersSummaryDto Summarise(IReadOnlyCollection<AdminOrderLineDto> lines)
        {
            static decimal? Avg(decimal total, int count) =>
                count > 0 ? decimal.Round(total / count, 2, MidpointRounding.AwayFromZero) : null;

            var fresh = lines.Where(l => l.IsNewCustomerOrder).ToList();
            var back = lines.Where(l => !l.IsNewCustomerOrder).ToList();
            var s = new AdminOrdersSummaryDto
            {
                Orders = lines.Count,
                NewCustomerOrders = fresh.Count,
                ReturningCustomerOrders = back.Count,
                SubTotal = lines.Sum(l => l.SubTotal),
                Discounts = lines.Sum(l => l.Discounts),
                SalesTax = lines.Sum(l => l.SalesTax),
                OrdersTotal = lines.Sum(l => l.OrderTotal),
                OrdersTotalNew = fresh.Sum(l => l.OrderTotal),
                OrdersTotalReturning = back.Sum(l => l.OrderTotal),
                CleanerWages = lines.Sum(l => l.CleanerWages),
                StripeFees = lines.Sum(l => l.StripeFee),
                NetIncome = lines.Sum(l => l.Net),
                NetIncomeNew = fresh.Sum(l => l.Net),
                NetIncomeReturning = back.Sum(l => l.Net)
            };
            s.AveragePrice = Avg(s.OrdersTotal, s.Orders);
            s.AveragePriceNew = Avg(s.OrdersTotalNew, s.NewCustomerOrders);
            s.AveragePriceReturning = Avg(s.OrdersTotalReturning, s.ReturningCustomerOrders);
            s.AverageNet = Avg(s.NetIncome, s.Orders);
            s.AverageNetNew = Avg(s.NetIncomeNew, s.NewCustomerOrders);
            s.AverageNetReturning = Avg(s.NetIncomeReturning, s.ReturningCustomerOrders);
            return s;
        }

        public static string PaymentMethodLabel(PaymentMethod method) => method switch
        {
            PaymentMethod.Normal => "Card",
            PaymentMethod.BankTransfer => "Bank transfer",
            _ => method.ToString()
        };

        /// <summary>
        /// Every order the tab counts, one row each, carrying the per-order figures the totals are
        /// summed from. Shared by <see cref="GetAsync"/> and the analytics agent (2026-10), so a
        /// per-week or per-service breakdown of one admin re-adds to exactly what the tab shows.
        /// </summary>
        private async Task<(DateTime? From, List<AdminRef> Admins, List<AdminPerformanceOrderRow> Orders)> LoadAsync(
            DateTime? fromInclusive, DateTime? toInclusive, int? onlyAdminId = null)
        {
            var from = fromInclusive?.Date;
            var toExclusive = toInclusive?.Date.AddDays(1);
            if (from.HasValue && toExclusive.HasValue && toExclusive <= from)
                throw new InvalidOperationException("The end date cannot be before the start date.");

            // Admin role only — a SuperAdmin earns no bonus and is not measured here (owner's call).
            // Blocked admins stay: what they booked in the period still happened.
            var adminQuery = _context.Users.Where(u => !u.IsDeleted && u.Role == UserRole.Admin);
            if (onlyAdminId.HasValue) adminQuery = adminQuery.Where(u => u.Id == onlyAdminId.Value);
            var admins = (await adminQuery
                .Select(u => new { u.Id, u.FirstName, u.LastName, u.Email, u.AdminPosition, u.IsActive })
                .ToListAsync())
                .Select(u => new AdminRef(u.Id, u.FirstName, u.LastName, u.Email, u.AdminPosition, u.IsActive))
                .ToList();
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

            // The Stripe fee each card-paid order cost — Statistics' own estimate, same order set.
            var stripeFees = await _stripeFees.ByOrderAsync(query);

            // Every staff bonus each order costs (both slots + percentage), GEL — the same figures
            // Statistics converts. Same window, so every order above is covered.
            var bonusGel = await _bonuses.GetOrderBonusCostsGelAsync(from, toExclusive);
            var fx = new Dictionary<int, decimal>();

            var rows = new List<AdminPerformanceOrderRow>(orders.Count);
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
                var fee = stripeFees.TryGetValue(o.Id, out var f) ? f : 0m;

                rows.Add(new AdminPerformanceOrderRow
                {
                    OrderId = o.Id,
                    BookerId = o.BookerId,
                    ServiceDate = o.ServiceDate.Date,
                    IsNewCustomerOrder = o.IsNewCustomerOrder,
                    OrderTotal = figures.OrderTotal,
                    SalesTax = figures.Tax,
                    Revenue = figures.Revenue,
                    CleanerWages = o.CleanerTotalSalary,
                    StripeFee = fee,
                    StaffBonusesUsd = bonusUsd,
                    Net = AdminOrderEarnings.Net(figures.Revenue, o.CleanerTotalSalary, fee)
                });
            }

            return (from, admins, rows);
        }

        private sealed record AdminRef(
            int Id, string FirstName, string LastName, string Email, AdminPosition AdminPosition, bool IsActive);
    }

    public interface IAdminPerformanceService
    {
        /// <summary>Both dates inclusive service dates; null on either side = open-ended.</summary>
        Task<AdminPerformanceListDto> GetAsync(DateTime? fromInclusive, DateTime? toInclusive);

        /// <summary>The same orders, one row each — what the analytics agent groups for breakdowns.</summary>
        Task<List<AdminPerformanceOrderRow>> GetOrderRowsAsync(DateTime? fromInclusive, DateTime? toInclusive);

        /// <summary>Shifts → one admin's orders (no customer details). Null when the id is not an
        /// Admin-role staff member.</summary>
        Task<AdminOrdersViewDto?> GetAdminOrdersAsync(int adminId, DateTime? fromInclusive, DateTime? toInclusive);
    }

    /// <summary>One order as Outgoing Payments → Admins counts it. Bonuses are unrounded USD.</summary>
    public class AdminPerformanceOrderRow
    {
        public int OrderId { get; set; }

        /// <summary>The booking admin: <c>Order.BonusBookerId</c>.</summary>
        public int BookerId { get; set; }

        public DateTime ServiceDate { get; set; }
        public bool IsNewCustomerOrder { get; set; }

        /// <summary>Price without tips, sales tax included, net of refunds.</summary>
        public decimal OrderTotal { get; set; }

        public decimal SalesTax { get; set; }

        /// <summary>OrderTotal − SalesTax.</summary>
        public decimal Revenue { get; set; }

        public decimal CleanerWages { get; set; }

        /// <summary>Estimated Stripe fee (card-paid orders only; 0 otherwise).</summary>
        public decimal StripeFee { get; set; }

        /// <summary>Reported beside the net, never taken off it.</summary>
        public decimal StaffBonusesUsd { get; set; }

        /// <summary>Revenue − wages − Stripe fee (<see cref="AdminOrderEarnings.Net"/>).</summary>
        public decimal Net { get; set; }
    }
}
