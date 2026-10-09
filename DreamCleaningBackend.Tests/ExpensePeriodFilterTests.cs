using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// The Expenses page is a view of ONE month. It used to list every row of a name since the
    /// beginning — Google Ads writes one row a day, so October showed every day back to February —
    /// while its totals were already month-scoped. These pin the month as the only thing that
    /// decides what is listed, and pin the page's totals to the same breakdown Statistics and
    /// Finances read, so the two screens can never report a different month.
    /// </summary>
    public class ExpensePeriodFilterTests : IDisposable
    {
        private const int AdsCategoryId = 10;
        private const int SoftwareCategoryId = 11;
        private const int OfficeCategoryId = 12;

        private static readonly DateTime Oct9 = new(2026, 10, 9);

        private readonly ApplicationDbContext _context;
        private readonly ExpenseService _service;
        private int _nextId = 1;

        public ExpensePeriodFilterTests()
        {
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase($"expense-period-{Guid.NewGuid()}")
                .Options;
            _context = new ApplicationDbContext(options);
            _context.ExpenseCategories.AddRange(
                new ExpenseCategory { Id = AdsCategoryId, Name = "Google Ads", DisplayOrder = 0 },
                new ExpenseCategory { Id = SoftwareCategoryId, Name = "Software", DisplayOrder = 1 },
                new ExpenseCategory { Id = OfficeCategoryId, Name = "Office", DisplayOrder = 2 });
            _context.SaveChanges();

            // Every row here is USD, so the FX service must never be reached.
            _service = new ExpenseService(
                _context, new RecordingAuditService(), RecurringRefinementTests.Stub<IFinancialRateService>(
                    (m, _) => throw new InvalidOperationException("No FX lookup expected: " + m.Name)));
        }

        public void Dispose() => _context.Dispose();

        private Expense Add(int categoryId, string name, decimal amount, DateTime start,
            int? everyMonths = null, DateTime? end = null, bool prorate = false)
        {
            var row = new Expense
            {
                Id = _nextId++,
                Name = name,
                Amount = amount,
                CategoryId = categoryId,
                StartDate = start,
                IsRecurring = everyMonths.HasValue,
                FrequencyMonths = everyMonths,
                EndDate = end,
                ProrateByDay = prorate,
                CreatedByUserId = 1
            };
            _context.Expenses.Add(row);
            return row;
        }

        /// <summary>One ad-spend row a day, exactly as GoogleAdsCostService writes them.</summary>
        private void AddDailyAds(DateTime from, DateTime toInclusive, decimal perDay)
        {
            for (var d = from; d <= toInclusive; d = d.AddDays(1))
                Add(AdsCategoryId, "Google Ads Spend", perDay, d);
        }

        private static GroupedCategoryDto Category(GroupedExpensesDto view, int id) =>
            view.Categories.Single(c => c.CategoryId == id);

        // ── What the month lists ────────────────────────────────────────────────

        [Fact]
        public async Task DailyAdSpend_ListsOnlyTheSelectedMonthsDays()
        {
            AddDailyAds(new DateTime(2026, 2, 1), Oct9, 10m);
            await _context.SaveChangesAsync(TestContext.Current.CancellationToken);

            var view = await _service.GetGroupedAsync(2026, 10, Oct9);

            var ads = Assert.Single(Category(view, AdsCategoryId).Names);
            Assert.Equal(9, ads.Entries.Count);
            Assert.All(ads.Entries, e => Assert.Equal(10, e.StartDate.Month));
            Assert.Equal(90m, ads.MonthTotal);
            Assert.Equal(90m, Category(view, AdsCategoryId).MonthTotal);
            Assert.Equal(90m, view.MonthTotal);

            // The all-time figure is still there for the secondary line: Feb 1 → Oct 9 is 251 days.
            Assert.Equal(2510m, ads.AllTimeTotal);
        }

        [Fact]
        public async Task SwitchingMonth_ListsThatMonthInstead()
        {
            AddDailyAds(new DateTime(2026, 2, 1), Oct9, 10m);
            await _context.SaveChangesAsync(TestContext.Current.CancellationToken);

            var march = await _service.GetGroupedAsync(2026, 3, Oct9);

            var ads = Assert.Single(Category(march, AdsCategoryId).Names);
            Assert.Equal(31, ads.Entries.Count);
            Assert.All(ads.Entries, e => Assert.Equal(3, e.StartDate.Month));
            Assert.Equal(310m, march.MonthTotal);
            // Feb (28 days) + March — never anything after the month being viewed.
            Assert.Equal(590m, ads.AllTimeTotal);
        }

        [Fact]
        public async Task ANameWithNothingInTheMonth_IsNotListed_InAnyCategory()
        {
            Add(OfficeCategoryId, "Printer", 300m, new DateTime(2026, 3, 14));
            Add(SoftwareCategoryId, "Old CRM", 40m, new DateTime(2026, 1, 1), everyMonths: 1,
                end: new DateTime(2026, 8, 31));
            Add(SoftwareCategoryId, "Claude", 20m, new DateTime(2026, 10, 3));
            await _context.SaveChangesAsync(TestContext.Current.CancellationToken);

            var view = await _service.GetGroupedAsync(2026, 10, Oct9);

            Assert.Empty(Category(view, OfficeCategoryId).Names);
            Assert.Equal(0m, Category(view, OfficeCategoryId).MonthTotal);
            var software = Assert.Single(Category(view, SoftwareCategoryId).Names);
            Assert.Equal("Claude", software.Name);
            Assert.Equal(20m, view.MonthTotal);
        }

        [Fact]
        public async Task ARecurringEntry_IsListedOnlyInMonthsItCharges()
        {
            // Monthly since January: charges in October.
            var monthly = Add(SoftwareCategoryId, "RingCentral", 50m, new DateTime(2026, 1, 1), everyMonths: 1);
            // Quarterly from July: July, October, January — charges in October.
            var quarterlyOn = Add(SoftwareCategoryId, "Domain", 30m, new DateTime(2026, 7, 15), everyMonths: 3);
            // Quarterly from August: August, November — nothing in October, so not listed.
            Add(SoftwareCategoryId, "Backup", 25m, new DateTime(2026, 8, 20), everyMonths: 3);
            await _context.SaveChangesAsync(TestContext.Current.CancellationToken);

            var view = await _service.GetGroupedAsync(2026, 10, Oct9);

            var names = Category(view, SoftwareCategoryId).Names;
            Assert.Equal(new[] { "RingCentral", "Domain" }, names.Select(n => n.Name));
            Assert.Equal(monthly.Id, Assert.Single(names[0].Entries).Id);
            Assert.Equal(quarterlyOn.Id, Assert.Single(names[1].Entries).Id);
            Assert.Equal(80m, view.MonthTotal);
        }

        [Fact]
        public async Task AnAdjustedAmount_ListsOnlyTheRowInForceThatMonth_ButKeepsTheWholeAllTime()
        {
            // "Raise from a date" leaves two rows under one name: $100 Jan–Aug, $120 from September.
            Add(SoftwareCategoryId, "Claude", 100m, new DateTime(2026, 1, 1), everyMonths: 1,
                end: new DateTime(2026, 8, 31));
            var raised = Add(SoftwareCategoryId, "Claude", 120m, new DateTime(2026, 9, 1), everyMonths: 1);
            await _context.SaveChangesAsync(TestContext.Current.CancellationToken);

            var view = await _service.GetGroupedAsync(2026, 10, Oct9);

            var line = Assert.Single(Category(view, SoftwareCategoryId).Names);
            Assert.Equal(raised.Id, Assert.Single(line.Entries).Id);
            Assert.Equal(120m, line.MonthTotal);
            Assert.Equal(8 * 100m + 2 * 120m, line.AllTimeTotal);
        }

        [Fact]
        public async Task ACreditThatCancelsTheMonth_IsStillListed()
        {
            // A promotional credit nets the month to zero. Both rows happened in it.
            Add(AdsCategoryId, "Google Ads Spend", 50m, new DateTime(2026, 10, 2));
            Add(AdsCategoryId, "Google Ads Spend", -50m, new DateTime(2026, 10, 3));
            await _context.SaveChangesAsync(TestContext.Current.CancellationToken);

            var view = await _service.GetGroupedAsync(2026, 10, Oct9);

            var ads = Assert.Single(Category(view, AdsCategoryId).Names);
            Assert.Equal(2, ads.Entries.Count);
            Assert.Equal(0m, ads.MonthTotal);
        }

        // ── The running month's charged-so-far figure ───────────────────────────

        [Fact]
        public async Task TheRunningMonth_ReportsWhatIsChargedThroughToday()
        {
            Add(SoftwareCategoryId, "Claude", 20m, new DateTime(2026, 10, 9));          // today: counts
            Add(SoftwareCategoryId, "Insurance", 300m, new DateTime(2026, 10, 25));     // later this month
            await _context.SaveChangesAsync(TestContext.Current.CancellationToken);

            var view = await _service.GetGroupedAsync(2026, 10, Oct9);

            Assert.Equal(320m, view.MonthTotal);
            Assert.Equal(20m, view.ChargedToDate);
            Assert.Equal(Oct9, view.ToDate);
            // Both are listed — a charge later in the month still belongs to the month.
            Assert.Equal(2, Category(view, SoftwareCategoryId).Names.Count);
        }

        [Fact]
        public async Task AnyOtherMonth_HasNoChargedSoFarFigure()
        {
            Add(SoftwareCategoryId, "Claude", 20m, new DateTime(2026, 9, 9));
            await _context.SaveChangesAsync(TestContext.Current.CancellationToken);

            var past = await _service.GetGroupedAsync(2026, 9, Oct9);
            var future = await _service.GetGroupedAsync(2026, 11, Oct9);

            Assert.Null(past.ChargedToDate);
            Assert.Null(past.ToDate);
            Assert.Null(future.ChargedToDate);
        }

        // ── One month, one answer: the page agrees with Statistics / Finances ───

        [Fact]
        public async Task EveryTotal_MatchesTheBreakdownStatisticsAndFinancesRead()
        {
            AddDailyAds(new DateTime(2026, 8, 25), Oct9, 7.35m);
            Add(SoftwareCategoryId, "RingCentral", 50m, new DateTime(2026, 1, 1), everyMonths: 1);
            Add(SoftwareCategoryId, "Ads agency", 900m, new DateTime(2026, 9, 12), everyMonths: 1, prorate: true);
            Add(SoftwareCategoryId, "Domain", 30m, new DateTime(2026, 7, 15), everyMonths: 3);
            Add(OfficeCategoryId, "Desk", 410m, new DateTime(2026, 10, 30));
            Add(OfficeCategoryId, "Refund", -35m, new DateTime(2026, 10, 1));
            await _context.SaveChangesAsync(TestContext.Current.CancellationToken);

            foreach (var month in new[] { 8, 9, 10 })
            {
                var start = new DateTime(2026, month, 1);
                var view = await _service.GetGroupedAsync(2026, month, Oct9);
                // Finances with the projection on asks for the whole month: [1st, next 1st).
                var breakdown = await _service.GetBreakdownAsync(start, start.AddMonths(1));

                Assert.Equal(breakdown.Total, view.MonthTotal);
                foreach (var cat in view.Categories)
                {
                    var expected = breakdown.ByCategory.FirstOrDefault(c => c.CategoryId == cat.CategoryId)?.Total ?? 0m;
                    Assert.Equal(expected, cat.MonthTotal);
                    Assert.Equal(cat.MonthTotal, cat.Names.Sum(n => n.MonthTotal));
                }

                // Every listed entry is one the breakdown counted in this month, and vice versa.
                var listed = view.Categories.SelectMany(c => c.Names).SelectMany(n => n.Entries)
                    .Select(e => e.Id).OrderBy(i => i);
                var counted = breakdown.ByCategory.SelectMany(c => c.Items)
                    .Select(i => i.ExpenseId).Distinct().OrderBy(i => i);
                Assert.Equal(counted, listed);
            }

            // Finances' "This Month" with the projection off runs the 1st → today, inclusive, which
            // the statistics endpoint turns into [1st, today + 1 day).
            var running = await _service.GetGroupedAsync(2026, 10, Oct9);
            var toDate = await _service.GetBreakdownAsync(new DateTime(2026, 10, 1), Oct9.AddDays(1));
            Assert.Equal(toDate.Total, running.ChargedToDate);
        }

        [Theory]
        [InlineData(2026, 0)]
        [InlineData(2026, 13)]
        [InlineData(0, 10)]
        [InlineData(10000, 10)]
        public async Task AnImpossibleMonth_IsRefusedWithAMessage(int year, int month)
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _service.GetGroupedAsync(year, month, Oct9));
            Assert.False(string.IsNullOrWhiteSpace(ex.Message));
        }
    }
}
