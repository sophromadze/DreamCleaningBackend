using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Helpers.Recurring;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Models.Contracts;
using DreamCleaningBackend.Services;
using DreamCleaningBackend.Services.Contracts;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DreamCleaningBackend.Tests;

/// <summary>
/// Weekday / month-day recurrence, the upcoming-cleanings COUNT that replaced the fixed 30-day
/// window, and contract-linked plans (2026-10). See the recurring-orders skill.
/// </summary>
public class RecurringServiceDaysAndCountTests
{
    private static readonly DayOfWeek[] SundayToFriday =
    {
        DayOfWeek.Sunday, DayOfWeek.Monday, DayOfWeek.Tuesday,
        DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday
    };

    private static RecurrenceRule Weekly(DateTime anchor, int every, IEnumerable<DayOfWeek> days, DateTime? end = null) =>
        new(anchor, RecurrenceIntervalUnit.Weeks, every, days.ToList(), new List<int>(), DayOfWeek.Sunday, end);

    private static RecurrenceRule Monthly(DateTime anchor, int every, IEnumerable<int> days, DateTime? end = null) =>
        new(anchor, RecurrenceIntervalUnit.Months, every, new List<DayOfWeek>(), days.ToList(), DayOfWeek.Sunday, end);

    // ── Weekly ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SixSelectedWeekdays_GiveSixOccurrencesInTheWeek()
    {
        var sunday = new DateTime(2026, 10, 4);
        var dates = RecurrenceCalculator.Occurrences(Weekly(sunday, 1, SundayToFriday), sunday).Take(6).ToList();

        Assert.Equal(Enumerable.Range(0, 6).Select(i => sunday.AddDays(i)), dates);
    }

    [Fact]
    public void Saturday_IsGeneratedWhenSelected_AndNothingIsHardCodedAway()
    {
        var anchor = new DateTime(2026, 10, 4);
        Assert.Equal(new[] { new DateTime(2026, 10, 10), new DateTime(2026, 10, 17) },
            RecurrenceCalculator.Occurrences(Weekly(anchor, 1, new[] { DayOfWeek.Saturday }), anchor).Take(2));

        var everyDay = RecurrenceCalculator.Occurrences(
            Weekly(anchor, 1, Enum.GetValues<DayOfWeek>()), anchor).Take(14).ToList();
        Assert.Equal(14, everyDay.Distinct().Count());
        Assert.Equal(2, everyDay.Count(d => d.DayOfWeek == DayOfWeek.Saturday));
    }

    [Fact]
    public void UnselectedWeekdays_NeverGenerate()
    {
        var anchor = new DateTime(2026, 10, 4);
        var dates = RecurrenceCalculator.Occurrences(Weekly(anchor, 1, SundayToFriday), anchor).Take(60).ToList();

        Assert.Equal(60, dates.Count);
        Assert.DoesNotContain(dates, d => d.DayOfWeek == DayOfWeek.Saturday);

        var tueSat = RecurrenceCalculator.Occurrences(
            Weekly(anchor, 1, new[] { DayOfWeek.Tuesday, DayOfWeek.Saturday }), anchor).Take(20).ToList();
        Assert.All(tueSat, d => Assert.Contains(d.DayOfWeek, new[] { DayOfWeek.Tuesday, DayOfWeek.Saturday }));
    }

    [Fact]
    public void EveryTwoWeeks_MondayAndWednesday_FollowTheTwoWeekCadence()
    {
        var monday = new DateTime(2026, 10, 5);
        var dates = RecurrenceCalculator.Occurrences(
            Weekly(monday, 2, new[] { DayOfWeek.Monday, DayOfWeek.Wednesday }), monday).Take(6).ToList();

        Assert.Equal(new[]
        {
            new DateTime(2026, 10, 5), new DateTime(2026, 10, 7),
            new DateTime(2026, 10, 19), new DateTime(2026, 10, 21),
            new DateTime(2026, 11, 2), new DateTime(2026, 11, 4)
        }, dates);
    }

    [Fact]
    public void FirstCleaning_StartsOnTheFirstSelectedDayOnOrAfterIt_NeverBefore()
    {
        var wednesday = new DateTime(2026, 10, 7);
        var dates = RecurrenceCalculator.Occurrences(
            Weekly(wednesday, 1, new[] { DayOfWeek.Monday, DayOfWeek.Friday }), wednesday.AddDays(-10)).Take(3).ToList();

        Assert.Equal(new[] { new DateTime(2026, 10, 9), new DateTime(2026, 10, 12), new DateTime(2026, 10, 16) }, dates);
    }

    [Fact]
    public void NextMissingOccurrences_SkipsDatesThePlanAlreadyHolds()
    {
        var sunday = new DateTime(2026, 10, 4);
        var rule = Weekly(sunday, 1, SundayToFriday);
        var taken = new[] { sunday, sunday.AddDays(1), sunday.AddDays(3) };

        var next = RecurrenceCalculator.NextMissingOccurrences(rule, sunday, taken, 4);

        Assert.Equal(new[] { sunday.AddDays(2), sunday.AddDays(4), sunday.AddDays(5), sunday.AddDays(7) }, next);
        Assert.Empty(next.Intersect(taken));
    }

    // ── Monthly ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void MultipleMonthDays_GenerateEachSelectedDay()
    {
        var anchor = new DateTime(2027, 1, 1);
        var dates = RecurrenceCalculator.Occurrences(Monthly(anchor, 1, new[] { 30, 1, 15 }), anchor).Take(6).ToList();

        // February has no 30th — that occurrence is skipped, never moved to the 28th or March 1.
        Assert.Equal(new[]
        {
            new DateTime(2027, 1, 1), new DateTime(2027, 1, 15), new DateTime(2027, 1, 30),
            new DateTime(2027, 2, 1), new DateTime(2027, 2, 15), new DateTime(2027, 3, 1)
        }, dates);
    }

    [Fact]
    public void Day31_IsSkippedInMonthsThatDoNotHaveIt()
    {
        var anchor = new DateTime(2027, 1, 1);
        var dates = RecurrenceCalculator.Occurrences(Monthly(anchor, 1, new[] { 31 }), anchor).Take(5).ToList();

        Assert.Equal(new[]
        {
            new DateTime(2027, 1, 31), new DateTime(2027, 3, 31), new DateTime(2027, 5, 31),
            new DateTime(2027, 7, 31), new DateTime(2027, 8, 31)
        }, dates);
    }

    [Fact]
    public void EveryTwoMonths_FollowsTheCadence()
    {
        var anchor = new DateTime(2027, 1, 5);
        var dates = RecurrenceCalculator.Occurrences(Monthly(anchor, 2, new[] { 5, 20 }), anchor).Take(5).ToList();

        Assert.Equal(new[]
        {
            new DateTime(2027, 1, 5), new DateTime(2027, 1, 20), new DateTime(2027, 3, 5),
            new DateTime(2027, 3, 20), new DateTime(2027, 5, 5)
        }, dates);
    }

    [Fact]
    public void EndDate_IsNeverExceeded_WeeklyOrMonthly()
    {
        var sunday = new DateTime(2026, 10, 4);
        Assert.Equal(Enumerable.Range(0, 4).Select(i => sunday.AddDays(i)),
            RecurrenceCalculator.Occurrences(Weekly(sunday, 1, SundayToFriday, end: sunday.AddDays(3)), sunday).Take(50));

        var january = new DateTime(2027, 1, 1);
        var monthly = RecurrenceCalculator.Occurrences(
            Monthly(january, 1, new[] { 1, 15 }, end: new DateTime(2027, 2, 10)), january).Take(50).ToList();
        Assert.Equal(new[] { new DateTime(2027, 1, 1), new DateTime(2027, 1, 15), new DateTime(2027, 2, 1) }, monthly);
    }

    [Fact]
    public void APatternThatCanNeverLand_EndsInsteadOfLoopingForever()
    {
        // The 30th and 31st, every 12 months, from February: no February has either.
        var anchor = new DateTime(2027, 2, 1);
        Assert.Empty(RecurrenceCalculator.Occurrences(Monthly(anchor, 12, new[] { 30, 31 }), anchor).Take(1));
    }

    // ── Backward compatibility ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(RecurrenceIntervalUnit.Weeks, 1)]
    [InlineData(RecurrenceIntervalUnit.Weeks, 2)]
    [InlineData(RecurrenceIntervalUnit.Months, 1)]
    [InlineData(RecurrenceIntervalUnit.Days, 3)]
    public void APlanWithNoSelectedDays_KeepsItsOriginalDates(RecurrenceIntervalUnit unit, int every)
    {
        var anchor = new DateTime(2026, 1, 31);
        var today = new DateTime(2026, 10, 2);
        var legacy = RecurrenceCalculator.OccurrencesWithinHorizon(anchor, unit, every, today);
        var series = new RecurringOrderSeries { AnchorDate = anchor, IntervalUnit = unit, IntervalValue = every };

        Assert.Equal(legacy, RecurrenceCalculator.OccurrencesWithinHorizon(RecurrenceRule.From(series), today));
        Assert.NotEmpty(legacy);
    }

    [Theory]
    [InlineData(RecurrenceIntervalUnit.Weeks, "[]", null, null, "at least one service day")]
    [InlineData(RecurrenceIntervalUnit.Months, "[1]", null, null, "weekly schedule")]
    [InlineData(RecurrenceIntervalUnit.Weeks, null, "[1]", null, "monthly schedule")]
    [InlineData(RecurrenceIntervalUnit.Months, null, "[0]", null, "between 1 and 31")]
    [InlineData(RecurrenceIntervalUnit.Weeks, null, null, 0, "between 1 and 60")]
    [InlineData(RecurrenceIntervalUnit.Weeks, null, null, 61, "between 1 and 60")]
    public void InvalidPatternsAndCountsAreRefused(RecurrenceIntervalUnit unit, string? weekdays, string? monthDays,
        int? target, string expected)
    {
        var error = RecurrenceCalculator.ValidatePattern(unit, Parse(weekdays), Parse(monthDays), target);
        Assert.NotNull(error);
        Assert.Contains(expected, error, StringComparison.OrdinalIgnoreCase);
        Assert.Null(RecurrenceCalculator.ValidatePattern(RecurrenceIntervalUnit.Weeks, new[] { 0, 6 }, null, 60));
    }

    private static List<int>? Parse(string? json) =>
        json == null ? null : System.Text.Json.JsonSerializer.Deserialize<List<int>>(json);

    // ── Generation through the real service ───────────────────────────────────────────────────

    private sealed class Fixture : IDisposable
    {
        public readonly ApplicationDbContext Db = RecurringDiscountRegressionTests.Db();
        public readonly RecurringOrderSeriesService Series;
        public readonly DateTime Today = NyTimeHelper.NowNy.Date;

        public Fixture(PaymentMethod method = PaymentMethod.Normal)
        {
            Db.ServiceTypes.Add(new ServiceType { Id = 1, Name = "Commercial", BasePrice = 100, TimeDuration = 120 });
            Db.Services.Add(new Service { Id = 1, ServiceTypeId = 1, Name = "Bathrooms", ServiceKey = "bathrooms", Cost = 30, TimeDuration = 45, IsActive = true });
            Db.Users.Add(new User { Id = 1, FirstName = "Test", LastName = "Customer", IsActive = true });
            Db.Users.Add(new User { Id = 5, FirstName = "Test", LastName = "Admin" });
            // The template is in the PAST, so only generated cleanings count as upcoming.
            Db.Orders.Add(new Order { Id = 1, UserId = 1, ServiceTypeId = 1, ServiceDate = Today.AddDays(-14),
                ServiceTime = TimeSpan.FromHours(9), Status = OrderStatuses.Done, PaymentMethod = method,
                SubTotal = 100, Total = 128.88m, Tips = 20, ServiceAddress = "Test address", MaidsCount = 1 });
            // A priced service line and a tip on the template — what a weekly-flat-fee visit must NOT inherit.
            Db.OrderServices.Add(new Models.OrderService { Id = 1, OrderId = 1, ServiceId = 1, Quantity = 2, Cost = 60, Duration = 90 });
            Db.SaveChanges();

            var audit = new AuditService(Db, new HttpContextAccessor(), NullLogger<AuditService>.Instance);
            var loyalty = new LoyaltyDiscountService(Db, audit, NullLogger<LoyaltyDiscountService>.Instance);
            var booking = new BookingCreationService(Db, loyalty,
                RecurringDiscountRegressionTests.Stub<IGiftCardService>(), NullLogger<BookingCreationService>.Instance);
            Series = new RecurringOrderSeriesService(Db, booking, audit, NullLogger<RecurringOrderSeriesService>.Instance,
                new RecurringCustomerPaymentService(Db, RecurringDiscountRegressionTests.Stub<IStripeService>(), audit,
                    NullLogger<RecurringCustomerPaymentService>.Instance));
        }

        public SaveRecurringSeriesDto Plan(int target, params DayOfWeek[] days) => new()
        {
            IntervalValue = 1,
            IntervalUnit = RecurrenceIntervalUnit.Weeks,
            AnchorDate = Today.AddDays(1),
            ServiceDaysOfWeek = (days.Length == 0 ? SundayToFriday : days).Select(d => (int)d).ToList(),
            UpcomingOccurrenceTarget = target,
            AutoRequestPayment = true,
            IsActive = true
        };

        public Task<List<Order>> Generated() =>
            Db.Orders.Where(o => o.IsGeneratedByRecurringSeries).OrderBy(o => o.ServiceDate).ToListAsync();

        /// <summary>A weekly-flat-fee contract held by a client linked to the customer account.</summary>
        public Contract AddContract(ContractPricingBasis basis, int id = 40, int clientId = 1, int? sourceUserId = 1)
        {
            if (!Db.ContractClients.Any(c => c.Id == clientId))
                Db.ContractClients.Add(new ContractClient { Id = clientId, LegalEntityName = "Onyx LLC", SourceUserId = sourceUserId, IsActive = true });
            if (!Db.ContractServiceLocations.Any(l => l.Id == clientId))
                Db.ContractServiceLocations.Add(new ContractServiceLocation { Id = clientId, ContractClientId = clientId,
                    Address = "1 Commerce St", City = "New York", State = "NY", Zip = "10001" });
            var contract = new Contract
            {
                Id = id, ContractNumber = $"DCC-2026-0000{id}", ContractClientId = clientId,
                ContractServiceLocationId = clientId, ContractorProfileId = 1, ContractTemplateId = 1,
                Status = ContractStatus.FullySigned,
                DraftSnapshotJson = Snapshot(basis, "Sunday through Saturday").ToJson()
            };
            Db.Contracts.Add(contract);
            Db.SaveChanges();
            return contract;
        }

        public void Dispose() => Db.Dispose();
    }

    internal static ContractSnapshot Snapshot(ContractPricingBasis basis, string weekDefinition)
    {
        var snapshot = new ContractSnapshot();
        snapshot.Pricing.PricingBasis = basis;
        snapshot.Pricing.PriceMode = ContractPriceMode.PreTax;
        snapshot.Pricing.PreTaxPrice = 875m;
        snapshot.Pricing.SalesTaxAmount = 77.66m;
        snapshot.Pricing.TotalPrice = 952.66m;
        snapshot.Pricing.SalesTaxRatePercent = 8.875m;
        snapshot.Pricing.ScheduledVisitsPerFeePeriod = 6;
        snapshot.Schedule.VisitsPerPeriod = 6;
        snapshot.Schedule.WeekDefinition = weekDefinition;
        return snapshot;
    }

    [Theory]
    [InlineData(6)]
    [InlineData(12)]
    public async Task TheCount_CreatesExactlyThatManyUpcomingCleanings(int target)
    {
        using var f = new Fixture();
        var series = await f.Series.CreateAsync(1, f.Plan(target), 5);

        var generated = await f.Generated();
        Assert.Equal(target, generated.Count);
        Assert.Equal(target, series.UpcomingCount);
        Assert.DoesNotContain(generated, o => o.ServiceDate.DayOfWeek == DayOfWeek.Saturday);
        Assert.Equal(generated.Count, generated.Select(o => o.RecurrenceOccurrenceDate).Distinct().Count());
        Assert.Empty(series.PendingDates);
    }

    [Fact]
    public async Task RunningTheGeneratorAgain_CreatesNoDuplicates()
    {
        using var f = new Fixture();
        var series = await f.Series.CreateAsync(1, f.Plan(6), 5);

        var again = await f.Series.GenerateAsync(series.Id, 5);
        var third = await f.Series.GenerateAsync(series.Id, 5);

        Assert.Equal(0, again.CreatedCount);
        Assert.Equal(0, third.CreatedCount);
        Assert.Equal(6, (await f.Generated()).Count);
    }

    [Fact]
    public async Task ExistingUpcomingCleanings_CountTowardTheTarget()
    {
        using var f = new Fixture();
        var series = await f.Series.CreateAsync(1, f.Plan(6), 5);

        var dto = f.Plan(8);
        await f.Series.UpdateAsync(series.Id, dto, 5);

        // Raising 6 → 8 adds two, it does not add eight.
        Assert.Equal(8, (await f.Generated()).Count);
    }

    [Fact]
    public async Task RollingGeneration_ReplenishesTheTargetAfterACleaningPasses()
    {
        using var f = new Fixture();
        var series = await f.Series.CreateAsync(1, f.Plan(6), 5);
        var first = (await f.Generated()).First();
        var lastBefore = (await f.Generated()).Max(o => o.ServiceDate);

        // The earliest visit has now happened.
        first.ServiceDate = f.Today.AddDays(-1);
        first.Status = OrderStatuses.Done;
        await f.Db.SaveChangesAsync();

        var pass = await f.Series.GenerateAsync(series.Id, 5);

        Assert.Equal(1, pass.CreatedCount);
        var generated = await f.Generated();
        Assert.Equal(7, generated.Count);
        Assert.Equal(6, generated.Count(o => o.ServiceDate >= f.Today));
        Assert.True(generated.Max(o => o.ServiceDate) > lastBefore);
    }

    [Fact]
    public async Task APausedPlan_GeneratesNothingAndKeepsWhatExists()
    {
        using var f = new Fixture();
        var series = await f.Series.CreateAsync(1, f.Plan(6), 5);
        await f.Series.SetStateAsync(series.Id, "pause", 5);

        var first = (await f.Generated()).First();
        first.ServiceDate = f.Today.AddDays(-1);
        await f.Db.SaveChangesAsync();

        var pass = await f.Series.GenerateAsync(series.Id, 5);

        Assert.Equal(0, pass.CreatedCount);
        Assert.Equal(6, (await f.Generated()).Count);
        Assert.Contains(pass.Warnings, w => w.Contains("paused"));
    }

    [Fact]
    public async Task AnOldPlan_WithNoDaysAndNoCount_KeepsTheThirtyDayWindow()
    {
        using var f = new Fixture();
        var series = await f.Series.CreateAsync(1, new SaveRecurringSeriesDto
        {
            IntervalValue = 1, IntervalUnit = RecurrenceIntervalUnit.Weeks, AnchorDate = f.Today.AddDays(1), IsActive = true
        }, 5);

        var expected = RecurrenceCalculator.OccurrencesWithinHorizon(
            f.Today.AddDays(1), RecurrenceIntervalUnit.Weeks, 1, f.Today);

        Assert.Null(series.UpcomingOccurrenceTarget);
        Assert.Empty(series.ServiceDaysOfWeek);
        Assert.Equal(expected, (await f.Generated()).Select(o => o.RecurrenceOccurrenceDate!.Value));

        // Editing it without choosing a count keeps it legacy; once a count is set, it cannot be
        // blanked back to the window by accident.
        var dto = new SaveRecurringSeriesDto { IntervalValue = 1, IntervalUnit = RecurrenceIntervalUnit.Weeks,
            AnchorDate = f.Today.AddDays(1), IsActive = true, Notes = "kept" };
        Assert.Null((await f.Series.UpdateAsync(series.Id, dto, 5)).UpcomingOccurrenceTarget);
        dto.UpcomingOccurrenceTarget = 4;
        await f.Series.UpdateAsync(series.Id, dto, 5);
        dto.UpcomingOccurrenceTarget = null;
        await Assert.ThrowsAsync<RecurringSeriesException>(() => f.Series.UpdateAsync(series.Id, dto, 5));
    }

    [Fact]
    public async Task ChangingTheServiceDays_IsARuleChangeThatAsksKeepOrRegenerate()
    {
        using var f = new Fixture();
        var series = await f.Series.CreateAsync(1, f.Plan(6), 5);

        var dto = f.Plan(6, DayOfWeek.Monday, DayOfWeek.Wednesday);
        await Assert.ThrowsAsync<RecurringSeriesException>(() => f.Series.UpdateAsync(series.Id, dto, 5));

        dto.FutureOrdersAction = "Keep";
        var kept = await f.Series.UpdateAsync(series.Id, dto, 5);

        // Nothing already generated was touched; new dates start after the last retained one.
        var generated = await f.Generated();
        Assert.Equal(6, generated.Count(o => o.Status == OrderStatuses.Pending));
        Assert.Equal(6, kept.UpcomingCount);
    }

    // ── Commercial contracts ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AWeeklyFlatFeePlan_StampsTheContract_BillsByInvoice_AndRequestsNothingPerVisit()
    {
        using var f = new Fixture();
        var contract = f.AddContract(ContractPricingBasis.WeeklyFlatFee);

        var options = await f.Series.GetContractOptionsAsync(1);
        Assert.Equal(contract.Id, options.SuggestedContractId);

        var dto = f.Plan(6);
        dto.ContractId = contract.Id;
        dto.AutoRequestPayment = true; // asked for, and refused for a weekly flat fee
        var series = await f.Series.CreateAsync(1, dto, 5);

        Assert.False(series.AutoRequestPayment);
        Assert.True(series.BillingControlledByContract);
        Assert.Equal(contract.Id, series.Contract!.Id);

        var generated = await f.Generated();
        Assert.Equal(6, generated.Count);
        Assert.All(generated, o =>
        {
            Assert.Equal(contract.Id, o.ContractId);
            Assert.Equal(contract.ContractClientId, o.ContractClientId);
            Assert.Equal(series.Id, o.RecurringSeriesId);
            Assert.NotNull(o.RecurrenceOccurrenceDate);
            // Invoice method: the automatic request, Pay All and AutoPay only ever act on Normal.
            Assert.Equal(PaymentMethod.Invoice, o.PaymentMethod);
        });

        // The worker's independent guard sees the same contract as weekly flat.
        Assert.Contains(contract.Id, await RecurringOrderGenerationService.LoadWeeklyFlatFeeContractIdsAsync(f.Db, new[] { contract.Id }));
    }

    [Fact]
    public async Task APerVisitContractPlan_KeepsTheTemplatesBillingAndItsAutomaticRequests()
    {
        using var f = new Fixture();
        var contract = f.AddContract(ContractPricingBasis.PerVisit);

        var dto = f.Plan(3);
        dto.ContractId = contract.Id;
        var series = await f.Series.CreateAsync(1, dto, 5);

        Assert.True(series.AutoRequestPayment);
        Assert.False(series.BillingControlledByContract);
        Assert.All(await f.Generated(), o =>
        {
            Assert.Equal(contract.Id, o.ContractId);
            Assert.Equal(PaymentMethod.Normal, o.PaymentMethod);
        });
        Assert.Empty(await RecurringOrderGenerationService.LoadWeeklyFlatFeeContractIdsAsync(f.Db, new[] { contract.Id }));
    }

    [Fact]
    public async Task AContractOfAnotherClient_CannotBeLinked()
    {
        using var f = new Fixture();
        var foreign = f.AddContract(ContractPricingBasis.WeeklyFlatFee, id: 41, clientId: 2, sourceUserId: 99);

        var dto = f.Plan(6);
        dto.ContractId = foreign.Id;

        await Assert.ThrowsAsync<RecurringSeriesException>(() => f.Series.CreateAsync(1, dto, 5));
        Assert.Empty(await f.Db.RecurringOrderSeries.ToListAsync());
    }

    // ── Weekly-flat-fee visits are $0 operational records (2026-10) ──────────────────────────

    [Fact]
    public async Task AWeeklyFlatFeeVisit_StartsAtZero_WithNoTip_AndKeepsAllTheWork()
    {
        using var weekly = new Fixture();
        var contract = weekly.AddContract(ContractPricingBasis.WeeklyFlatFee);
        var dto = weekly.Plan(2);
        dto.ContractId = contract.Id;
        var series = await weekly.Series.CreateAsync(1, dto, 5);

        // The same plan with no contract, for what the WORK looks like when priced normally.
        using var priced = new Fixture();
        await priced.Series.CreateAsync(1, priced.Plan(2), 5);

        var visits = await weekly.Db.Orders.Include(o => o.OrderServices)
            .Where(o => o.IsGeneratedByRecurringSeries).OrderBy(o => o.ServiceDate).ToListAsync();
        var reference = await priced.Db.Orders.Include(o => o.OrderServices)
            .Where(o => o.IsGeneratedByRecurringSeries).OrderBy(o => o.ServiceDate).ToListAsync();

        Assert.Equal(2, visits.Count);
        for (var i = 0; i < visits.Count; i++)
        {
            var o = visits[i];
            Assert.Equal(0m, o.SubTotal); Assert.Equal(0m, o.Tax); Assert.Equal(0m, o.Total);
            Assert.Equal(0m, o.Tips); Assert.Equal(0m, o.CompanyDevelopmentTips);
            Assert.All(o.OrderServices, l => Assert.Equal(0m, l.Cost));

            // The work is identical to a priced visit.
            Assert.Equal(reference[i].TotalDuration, o.TotalDuration);
            Assert.Equal(reference[i].MaidsCount, o.MaidsCount);
            Assert.Equal(reference[i].ServiceTime, o.ServiceTime);
            Assert.Equal(reference[i].RecurrenceOccurrenceDate, o.RecurrenceOccurrenceDate);
            Assert.Equal(reference[i].OrderServices.Select(l => (l.ServiceId, l.Quantity, l.Duration)),
                o.OrderServices.Select(l => (l.ServiceId, l.Quantity, l.Duration)));
            Assert.NotEmpty(o.OrderServices);

            Assert.Equal(contract.Id, o.ContractId);
            Assert.Equal(contract.ContractClientId, o.ContractClientId);
            Assert.Equal(series.Id, o.RecurringSeriesId);
            Assert.Equal(OrderStatuses.Pending, o.Status);
        }
    }

    [Fact]
    public async Task ResidentialPlans_StillCopyTheirNormalPriceAndTip()
    {
        using var f = new Fixture();
        await f.Series.CreateAsync(1, f.Plan(2), 5);

        Assert.All(await f.Db.Orders.Include(o => o.OrderServices).Where(o => o.IsGeneratedByRecurringSeries).ToListAsync(), o =>
        {
            Assert.True(o.Total > 0m);
            Assert.Equal(20m, o.Tips);
            Assert.All(o.OrderServices, l => Assert.True(l.Cost > 0m));
            Assert.Null(o.ContractId);
        });
    }

    [Fact]
    public async Task PerVisitContractPlans_KeepTheirPricingAndTip()
    {
        using var f = new Fixture();
        var contract = f.AddContract(ContractPricingBasis.PerVisit);
        var dto = f.Plan(2);
        dto.ContractId = contract.Id;
        await f.Series.CreateAsync(1, dto, 5);

        Assert.All(await f.Generated(), o =>
        {
            Assert.True(o.Total > 0m);
            Assert.Equal(20m, o.Tips);
        });
        Assert.Empty(await ContractBilledOrders.LoadLabelsAsync(f.Db, await f.Generated()));
    }

    [Fact]
    public async Task WeeklyFlatFeeVisits_CarryTheBilledByContractLabel_OnEveryListAndDetail()
    {
        using var f = new Fixture();
        var contract = f.AddContract(ContractPricingBasis.WeeklyFlatFee);
        var dto = f.Plan(2);
        dto.ContractId = contract.Id;
        await f.Series.CreateAsync(1, dto, 5);
        var visit = (await f.Generated()).First();
        var expected = $"Billed weekly by contract {contract.ContractNumber}";

        var labels = await ContractBilledOrders.LoadLabelsAsync(f.Db, await f.Db.Orders.ToListAsync());
        Assert.Equal(expected, labels[visit.Id]);
        Assert.False(labels.ContainsKey(1)); // the template is not a contract visit

        var orders = new DreamCleaningBackend.Services.OrderService(
            // The customer list reads through the repository; answer from the same test database.
            RecurringDiscountRegressionTests.Stub<DreamCleaningBackend.Repositories.Interfaces.IOrderRepository>((m, args) =>
                m.Name == "GetUserOrdersAsync"
                    ? f.Db.Orders.Include(o => o.ServiceType).Where(o => o.UserId == (int)args![0]!).ToListAsync()
                    : throw new InvalidOperationException(m.Name)), f.Db,
            RecurringDiscountRegressionTests.Stub<IStripeService>(), RecurringDiscountRegressionTests.Stub<IEmailService>(),
            RecurringDiscountRegressionTests.Stub<ISmsService>(), new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
            NullLogger<DreamCleaningBackend.Services.OrderService>.Instance, RecurringDiscountRegressionTests.Stub<ILoyaltyDiscountService>());

        var mine = await orders.GetUserOrders(1);
        Assert.Equal(expected, mine.Single(o => o.Id == visit.Id).BilledByContractLabel);
        Assert.Null(mine.Single(o => o.Id == 1).BilledByContractLabel);
        Assert.Equal(expected, (await orders.GetAllOrdersForAdmin()).Single(o => o.Id == visit.Id).BilledByContractLabel);
        Assert.Equal(expected, (await orders.GetOrderByIdForAdmin(visit.Id)).BilledByContractLabel);

        var audit = new AuditService(f.Db, new HttpContextAccessor(), NullLogger<AuditService>.Instance);
        var upcoming = await new RecurringCustomerPaymentService(f.Db, RecurringDiscountRegressionTests.Stub<IStripeService>(),
            audit, NullLogger<RecurringCustomerPaymentService>.Instance).GetUpcomingAsync(1);
        Assert.Equal(expected, upcoming.Orders.Single(o => o.OrderId == visit.Id).BilledByContractLabel);
        Assert.Equal(0m, upcoming.Orders.Single(o => o.OrderId == visit.Id).AmountDue);
    }

    [Fact]
    public async Task GeneratingWeeklyFlatFeeVisits_LeavesTheCompletedTemplateUntouched()
    {
        using var f = new Fixture();
        var contract = f.AddContract(ContractPricingBasis.WeeklyFlatFee);
        var dto = f.Plan(3);
        dto.ContractId = contract.Id;
        await f.Series.CreateAsync(1, dto, 5);

        f.Db.ChangeTracker.Clear();
        var template = await f.Db.Orders.Include(o => o.OrderServices).SingleAsync(o => o.Id == 1);
        Assert.Equal(OrderStatuses.Done, template.Status);
        Assert.Equal(128.88m, template.Total);
        Assert.Equal(20m, template.Tips);
        Assert.Equal(60m, template.OrderServices.Single().Cost);
    }
}
