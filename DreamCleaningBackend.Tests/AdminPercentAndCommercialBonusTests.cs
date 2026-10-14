using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// 2026-10 staff-pay additions: SuperAdmins earn no bonus, the dated percentage bonus set in the
    /// shifts panel, the Admins tab on Outgoing Payments, one-time commercial bonuses on the
    /// salary, and the cleaners-dashboard full-name search.
    ///
    /// Worked order used throughout (USD): subtotal 200, tax 17.75, tips 30, cleaners' wages 100,
    /// returning customer booked by an administrator (fixed bonus 10 GEL at 0.37 = $3.70).
    ///   order total (no tips) = 217.75;  net before the % bonus = 200 − 100 − 3.70 = 96.30.
    /// </summary>
    public class AdminPercentAndCommercialBonusTests : IDisposable
    {
        private const decimal Rate = 0.37m;   // USD per GEL
        private const int Ana = 501;          // Admin, administrator
        private const int Owner = 502;        // SuperAdmin
        private readonly ApplicationDbContext _context;

        public AdminPercentAndCommercialBonusTests()
        {
            _context = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase($"admin-percent-bonus-{Guid.NewGuid()}", b => b.EnableNullChecks(false))
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options);
            _context.Database.EnsureCreated();
            _context.Users.AddRange(
                new User { Id = Ana, FirstName = "Ana", LastName = "Beridze", Email = "ana@example.invalid", Role = UserRole.Admin, IsActive = true },
                new User { Id = Owner, FirstName = "Owner", LastName = "Boss", Email = "owner@example.invalid", Role = UserRole.SuperAdmin, IsActive = true });
            _context.SaveChanges();
        }

        public void Dispose() => _context.Dispose();

        private AdminBonusService Bonuses() => new(_context, (_, _) => Task.FromResult(Rate));

        private Order AddOrder(int id, DateTime serviceDate, int? booker = Ana, string status = OrderStatuses.Done)
        {
            var o = new Order
            {
                Id = id, UserId = Ana, ServiceDate = serviceDate, Status = status, IsPaid = true,
                SubTotal = 200m, Tax = 17.75m, Tips = 30m, Total = 247.75m, CleanerTotalSalary = 100m,
                BonusBookerId = booker, BonusBookerPosition = AdminPosition.Administrator,
                IsNewCustomerOrder = false
            };
            _context.Orders.Add(o);
            _context.SaveChanges();
            return o;
        }

        // ── Pure rules ─────────────────────────────────────────────────────────────

        [Fact]
        public void OrderTotalIsThePriceWithoutTips_AndNetTakesOffTaxWagesAndBonuses()
        {
            var f = AdminOrderEarnings.Figures(200m, 0m, 0m, 0m, 17.75m, 30m, 0m, 0m);
            Assert.Equal(217.75m, f.OrderTotal);
            Assert.Equal(200m, f.Revenue);
            Assert.Equal(17.75m, f.Tax);
            Assert.Equal(96.30m, AdminOrderEarnings.Net(f.Revenue, 100m, 3.70m));
        }

        [Fact]
        public void APartRefundComesOffTheOrderTotalProRata()
        {
            // Half of everything billed (247.75) refunded → half of the price without tips remains.
            var f = AdminOrderEarnings.Figures(200m, 0m, 0m, 0m, 17.75m, 30m, 0m, 123.875m);
            Assert.Equal(108.88m, f.OrderTotal);
        }

        [Fact]
        public void TheRuleInForceIsTheLatestOneThatHasStarted()
        {
            var rules = new List<AdminPercentBonusRule>
            {
                new() { Id = 1, NewCustomerPercent = 5m, ExistingCustomerPercent = 5m, StartDate = new DateTime(2026, 9, 1) },
                new() { Id = 2, NewCustomerPercent = 8m, ExistingCustomerPercent = 8m, StartDate = new DateTime(2026, 10, 10) },
            };
            Assert.Null(AdminOrderEarnings.RuleOn(rules, new DateTime(2026, 8, 31)));
            Assert.Equal(1, AdminOrderEarnings.RuleOn(rules, new DateTime(2026, 10, 9))!.Id);
            Assert.Equal(2, AdminOrderEarnings.RuleOn(rules, new DateTime(2026, 10, 10, 15, 0, 0))!.Id);
        }

        [Fact]
        public void PercentOfFullOrderOrOfNet_AndANegativeNetEarnsNothing()
        {
            var full = new AdminPercentBonusRule { NewCustomerPercent = 10m, ExistingCustomerPercent = 10m, Basis = AdminPercentBonusBasis.FullOrder };
            var net = new AdminPercentBonusRule { NewCustomerPercent = 10m, ExistingCustomerPercent = 10m, Basis = AdminPercentBonusBasis.NetIncome };
            Assert.Equal(21.78m, AdminOrderEarnings.PercentBonusUsd(full, false, 217.75m, 96.30m));
            Assert.Equal(9.63m, AdminOrderEarnings.PercentBonusUsd(net, false, 217.75m, 96.30m));
            Assert.Equal(0m, AdminOrderEarnings.PercentBonusUsd(net, false, 217.75m, -12m));
            Assert.Equal(0m, AdminOrderEarnings.PercentBonusUsd(new AdminPercentBonusRule(), false, 217.75m, 96.30m));
            Assert.Equal(0m, AdminOrderEarnings.PercentBonusUsd(null, false, 217.75m, 96.30m));
        }

        [Fact]
        public void NewAndReturningCustomersEachTakeTheirOwnPercent()
        {
            var rule = new AdminPercentBonusRule { NewCustomerPercent = 10m, ExistingCustomerPercent = 4m };
            Assert.Equal(21.78m, AdminOrderEarnings.PercentBonusUsd(rule, true, 217.75m, 96.30m));
            Assert.Equal(8.71m, AdminOrderEarnings.PercentBonusUsd(rule, false, 217.75m, 96.30m));
            // A rule may pay only one kind of customer.
            var newOnly = new AdminPercentBonusRule { NewCustomerPercent = 5m };
            Assert.True(newOnly.PaysAnything);
            Assert.Equal(0m, AdminOrderEarnings.PercentBonusUsd(newOnly, false, 217.75m, 96.30m));
        }

        // ── SuperAdmins earn no bonus ───────────────────────────────────────────────

        [Fact]
        public async Task ASuperAdminAssignedToAnOrderIsNotSnapshottedAsItsBonusEarner()
        {
            var order = AddOrder(1, new DateTime(2026, 10, 3), booker: null);

            await Bonuses().AssignAdminAsync(order.Id, Owner, Owner);

            var saved = await _context.Orders.SingleAsync(o => o.Id == order.Id);
            Assert.Equal(Owner, saved.AssignedAdminId);      // still responsible for it
            Assert.Null(saved.BonusBookerId);                // but nobody earns on it
            Assert.Null(saved.BonusManagerId);
            Assert.Empty(await Bonuses().GetOrderBonusCostsGelAsync(null, null));
        }

        [Fact]
        public async Task AnAdminAssignedToAnOrderStillEarns()
        {
            var order = AddOrder(1, new DateTime(2026, 10, 3), booker: null);
            await Bonuses().AssignAdminAsync(order.Id, Ana, Owner);
            Assert.Equal(Ana, (await _context.Orders.SingleAsync()).BonusBookerId);
        }

        // ── Percentage bonus ────────────────────────────────────────────────────────

        [Fact]
        public async Task APercentageBonusCountsOnlyFromItsStartDate_OnTopOfTheFixedBonus()
        {
            AddOrder(1, new DateTime(2026, 10, 5));    // before the start date
            AddOrder(2, new DateTime(2026, 10, 12));   // after

            var service = Bonuses();
            await service.AddPercentRuleAsync(Ana,
                new SetAdminPercentBonusDto { Basis = "FullOrder", NewCustomerPercent = 10m, ExistingCustomerPercent = 10m, StartDate = new DateTime(2026, 10, 10) },
                Owner);

            var costs = await service.GetOrderBonusCostsGelAsync(new DateTime(2026, 10, 1), new DateTime(2026, 11, 1));
            Assert.Equal(10m, costs[1]);                                    // fixed only
            Assert.Equal(10m + 21.78m / Rate, costs[2]);                    // fixed + 10% of 217.75 in GEL

            var row = (await service.GetBonusesAsync(new DateTime(2026, 10, 1), new DateTime(2026, 11, 1), Owner, true)).Single();
            Assert.Equal(20m, row.FixedBonusAmount);
            Assert.Equal(decimal.Round(21.78m / Rate, 2), row.PercentBonusAmount);
            Assert.Equal(21.78m, row.PercentBonusUsd);
            Assert.Equal(1, row.PercentBonusOrderCount);
            Assert.Equal(row.FixedBonusAmount + row.PercentBonusAmount, row.BonusAmount);
        }

        [Fact]
        public async Task ThePercentageOfNetTakesOffTaxWagesAndTheFixedBonus()
        {
            AddOrder(1, new DateTime(2026, 10, 12));
            var service = Bonuses();
            await service.AddPercentRuleAsync(Ana,
                new SetAdminPercentBonusDto { Basis = "NetIncome", NewCustomerPercent = 10m, ExistingCustomerPercent = 10m, StartDate = new DateTime(2026, 10, 1) },
                Owner);

            var row = (await service.GetBonusesAsync(new DateTime(2026, 10, 1), new DateTime(2026, 11, 1), Owner, true)).Single();
            Assert.Equal(9.63m, row.PercentBonusUsd);   // 10% of (200 − 100 − 3.70)
        }

        [Fact]
        public async Task StoppingAPercentageBonusLeavesEarlierOrdersPaid()
        {
            AddOrder(1, new DateTime(2026, 10, 5));
            AddOrder(2, new DateTime(2026, 10, 20));
            var service = Bonuses();
            await service.AddPercentRuleAsync(Ana, new SetAdminPercentBonusDto { NewCustomerPercent = 10m, ExistingCustomerPercent = 10m, StartDate = new DateTime(2026, 10, 1) }, Owner);
            await service.AddPercentRuleAsync(Ana, new SetAdminPercentBonusDto { StartDate = new DateTime(2026, 10, 15) }, Owner);

            var row = (await service.GetBonusesAsync(new DateTime(2026, 10, 1), new DateTime(2026, 11, 1), Owner, true)).Single();
            Assert.Equal(1, row.PercentBonusOrderCount);
            Assert.Equal(2, row.PercentRules.Count);
            Assert.True(row.PercentRules.Single(r => r.NewCustomerPercent == 0m && r.ExistingCustomerPercent == 0m).StartDate < DateTime.Today.AddYears(1));
        }

        [Fact]
        public async Task OnlyAdminsCanBeGivenAPercentageBonus()
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Bonuses().AddPercentRuleAsync(Owner, new SetAdminPercentBonusDto { NewCustomerPercent = 5m }, Owner));
            Assert.Contains("admins", ex.Message);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Bonuses().AddPercentRuleAsync(Ana, new SetAdminPercentBonusDto { ExistingCustomerPercent = 101m }, Owner));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Bonuses().AddPercentRuleAsync(Ana, new SetAdminPercentBonusDto { Basis = "Tips", NewCustomerPercent = 5m }, Owner));
        }

        // ── Admins tab ──────────────────────────────────────────────────────────────

        [Fact]
        public async Task TheAdminsTabCountsCompletedOrders_TotalsWithoutTips_AndTheNet()
        {
            AddOrder(1, new DateTime(2026, 10, 5));
            AddOrder(2, new DateTime(2026, 10, 6), status: OrderStatuses.Active);   // not completed
            AddOrder(3, new DateTime(2026, 9, 30));                                  // outside the period
            var newCustomer = AddOrder(4, new DateTime(2026, 10, 8));
            newCustomer.IsNewCustomerOrder = true;
            _context.SaveChanges();

            var service = new AdminPerformanceService(_context, Bonuses(), new FixedRateService(Rate));
            var list = await service.GetAsync(new DateTime(2026, 10, 1), new DateTime(2026, 10, 31));

            Assert.DoesNotContain(list.Admins, a => a.AdminId == Owner);   // SuperAdmins are not listed
            var ana = list.Admins.Single(a => a.AdminId == Ana);
            Assert.Equal(2, ana.CompletedOrders);
            Assert.Equal(435.50m, ana.OrdersTotal);
            Assert.Equal(35.50m, ana.SalesTax);
            Assert.Equal(200m, ana.CleanerWages);
            Assert.Equal(7.40m, ana.StaffBonuses);     // 2 × 10 GEL (the new-customer rate is 10 too)
            Assert.Equal(192.60m, ana.NetIncome);

            // Split by whether the customer was new — the halves re-add to the totals.
            Assert.Equal(1, ana.NewCustomerOrders);
            Assert.Equal(1, ana.ReturningCustomerOrders);
            Assert.Equal(217.75m, ana.OrdersTotalNew);
            Assert.Equal(217.75m, ana.OrdersTotalReturning);
            Assert.Equal(96.30m, ana.NetIncomeNew);
            Assert.Equal(96.30m, ana.NetIncomeReturning);

            var allTime = await service.GetAsync(null, null);
            Assert.Equal(3, allTime.Admins.Single(a => a.AdminId == Ana).CompletedOrders);
        }

        // ── Commercial bonus ────────────────────────────────────────────────────────

        [Fact]
        public async Task ACommercialBonusMustBeOneTimeOnAStaffSalary()
        {
            var expenses = new ExpenseService(_context, new RecordingAuditService(), new FixedRateService(Rate));
            var dto = new CreateExpenseDto
            {
                CategoryId = SalaryExpenseRules.SalariesCategoryId, StaffUserId = Ana, Amount = 500m,
                Currency = "GEL", StartDate = new DateTime(2026, 10, 12), BonusLabel = "Onyx commercial bonus",
                IsRecurring = true, FrequencyMonths = 1
            };
            await Assert.ThrowsAsync<InvalidOperationException>(() => expenses.CreateAsync(dto, Owner));

            dto.IsRecurring = false;
            dto.FrequencyMonths = null;
            var saved = await expenses.CreateAsync(dto, Owner);
            Assert.Equal("Onyx commercial bonus", saved.BonusLabel);
            Assert.Equal("Ana Beridze", saved.Name);   // the person, not the bonus
        }

        [Fact]
        public async Task ACommercialBonusIsPaidWholeOnTheSecondPayment_AndListedByName()
        {
            var rates = new FixedRateService(Rate);
            var expenses = new ExpenseService(_context, new RecordingAuditService(), rates);
            await expenses.CreateAsync(new CreateExpenseDto
            {
                CategoryId = SalaryExpenseRules.SalariesCategoryId, StaffUserId = Ana, Amount = 2000m,
                Currency = "GEL", StartDate = new DateTime(2026, 10, 1)
            }, Owner);
            await expenses.CreateAsync(new CreateExpenseDto
            {
                CategoryId = SalaryExpenseRules.SalariesCategoryId, StaffUserId = Ana, Amount = 500m,
                Currency = "GEL", StartDate = new DateTime(2026, 10, 12), BonusLabel = "Onyx commercial bonus"
            }, Owner);
            await expenses.CreateAsync(new CreateExpenseDto
            {
                CategoryId = SalaryExpenseRules.SalariesCategoryId, StaffUserId = Ana, Amount = 37m,
                Currency = "USD", StartDate = new DateTime(2026, 10, 20), BonusLabel = "Referral bonus"
            }, Owner);

            var payouts = new AdminSalaryPayoutService(_context, expenses, new RecordingAuditService(), Bonuses(), rates);
            var payee = (await payouts.GetAsync(2026, 10)).Payees.Single();

            Assert.Equal("GEL", payee.Currency);
            Assert.Equal(2000m, payee.SalaryTotal);
            Assert.Equal(600m, payee.CommercialBonusTotal);           // 500 GEL + $37 = 100 GEL
            Assert.Equal(new[] { "Onyx commercial bonus", "Referral bonus" }, payee.CommercialBonuses.Select(c => c.Label));
            Assert.Equal(2600m, payee.MonthTotal);

            var first = payee.Instalments.Single(i => i.Half == 1);
            var second = payee.Instalments.Single(i => i.Half == 2);
            Assert.Equal(1000m, first.Amount);                        // salary half only
            Assert.Equal(1600m, second.Amount);                       // salary half + every bonus
            Assert.Equal(600m, second.BonusAmount);

            // It is an ordinary salary cost for the reports: 2,000 + 500 GEL at 0.37 plus $37.
            var reported = await expenses.GetTotalInRangeAsync(new DateTime(2026, 10, 1), new DateTime(2026, 11, 1));
            Assert.Equal(740m + 185m + 37m, reported);
        }

        // ── Cleaners dashboard search ───────────────────────────────────────────────

        [Fact]
        public async Task ACleanerIsFoundByFirstAndLastNameTypedWithASpace()
        {
            _context.Cleaners.AddRange(
                new Cleaner { Id = 1, FirstName = "Nino", LastName = "Kapanadze", IsActive = true },
                new Cleaner { Id = 2, FirstName = "Nino", LastName = "Gelashvili", IsActive = true });
            _context.SaveChanges();

            var service = new CleanerManagementService(_context, new ConfigurationBuilder().Build(), new RecordingAuditService());

            Assert.Equal(new[] { 1 }, (await service.GetAllAsync(search: "Nino Kapa")).Select(c => c.Id));
            Assert.Equal(new[] { 1 }, (await service.GetAllAsync(search: "  Nino   Kapanadze ")).Select(c => c.Id));
            Assert.Equal(2, (await service.GetAllAsync(search: "Nino")).Count);
        }

        /// <summary>One fixed exchange rate for every month.</summary>
        private sealed class FixedRateService : IFinancialRateService
        {
            private readonly decimal _rate;
            public FixedRateService(decimal rate) => _rate = rate;

            public Task<MonthlyFinancialSnapshot> GetOrCreateAsync(int year, int month)
                => Task.FromResult(new MonthlyFinancialSnapshot { Year = year, Month = month, UsdPerGel = _rate, FxSource = "manual" });

            public async Task<Dictionary<int, MonthlyFinancialSnapshot>> GetOrCreateForRangeAsync(DateTime fromInclusive, DateTime toExclusive)
            {
                var result = new Dictionary<int, MonthlyFinancialSnapshot>();
                if (toExclusive <= fromInclusive) return result;
                var start = fromInclusive < new DateTime(2000, 1, 1) ? new DateTime(2000, 1, 1) : fromInclusive;
                for (var c = new DateTime(start.Year, start.Month, 1); c < toExclusive; c = c.AddMonths(1))
                    result[c.Year * 100 + c.Month] = await GetOrCreateAsync(c.Year, c.Month);
                return result;
            }

            public Task<MonthlyFinancialRateDto> SetManualFxAsync(int year, int month, decimal usdPerGel, int byUserId) => throw new NotSupportedException();
            public Task<MonthlyFinancialRateDto> RefetchAsync(int year, int month, int byUserId) => throw new NotSupportedException();
            public Task<List<MonthlyFinancialRateDto>> ListAsync(DateTime fromInclusive, DateTime toExclusive) => throw new NotSupportedException();
            public Task<decimal> BonusTotalGelForMonthAsync(int year, int month) => throw new NotSupportedException();
            public MonthlyFinancialRateDto ToDto(MonthlyFinancialSnapshot snap, string? updatedByUserName = null, decimal adminBonusTotalGel = 0m) => throw new NotSupportedException();
        }
    }
}
