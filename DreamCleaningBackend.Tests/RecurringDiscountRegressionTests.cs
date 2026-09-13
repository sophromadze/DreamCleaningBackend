using System.Reflection;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DreamCleaningBackend.Tests;

public class RecurringDiscountRegressionTests
{
    internal static ApplicationDbContext Db(params IInterceptor[] interceptors) => new(new DbContextOptionsBuilder<ApplicationDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString(), b => b.EnableNullChecks(false))
        .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)).AddInterceptors(interceptors).Options);

    internal static T Stub<T>(Func<MethodInfo, object?[]?, object?>? run = null) where T : class
    {
        var proxy = DispatchProxy.Create<T, Proxy>(); ((Proxy)(object)proxy).Run = run; return proxy;
    }
    public class Proxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?>? Run;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Run == null
            ? throw new InvalidOperationException($"Unexpected external call: {method!.Name}") : Run(method!, args);
    }

    private static async Task<RecurringOrderSeriesService> Setup(ApplicationDbContext db, bool lifetime = false,
        decimal accountPercent = 22, PaymentMethod method = PaymentMethod.Normal, bool brokenBooking = false)
    {
        db.ServiceTypes.Add(new ServiceType { Id = 1, Name = "Residential", BasePrice = 100, TimeDuration = 120 });
        db.Users.Add(new User { Id = 1, FirstName = "Test", LastName = "Customer", IsActive = true,
            LoyaltyDiscountPercentage = accountPercent, LoyaltyDiscountIsLifetime = lifetime });
        db.Users.Add(new User { Id = 5, FirstName = "Test", LastName = "Admin" });
        db.Orders.Add(new Order { Id = 1, UserId = 1, ServiceTypeId = 1, ServiceDate = NyTimeHelper.NowNy.Date,
            ServiceTime = TimeSpan.FromHours(9), Status = OrderStatuses.Active, PaymentMethod = method,
            SubTotal = 100, Total = 52.26m, PromoCode = "ORIGINAL20", DiscountAmount = 20,
            PointsRedeemed = 1000, PointsRedeemedDiscount = 10, LoyaltyDiscountPercentage = 22, LoyaltyDiscountAmount = 22,
            GiftCardCode = "original-gift", GiftCardAmountUsed = 5, RewardBalanceUsed = 3,
            ServiceAddress = "Test address", MaidsCount = 1 });
        await db.SaveChangesAsync();
        var audit = new AuditService(db, new HttpContextAccessor(), NullLogger<AuditService>.Instance);
        var loyalty = new LoyaltyDiscountService(db, audit, NullLogger<LoyaltyDiscountService>.Instance);
        IBookingCreationService booking = brokenBooking ? Stub<IBookingCreationService>()
            : new BookingCreationService(db, loyalty, Stub<IGiftCardService>(), NullLogger<BookingCreationService>.Instance);
        return new RecurringOrderSeriesService(db, booking, audit, NullLogger<RecurringOrderSeriesService>.Instance,
            new RecurringCustomerPaymentService(db, Stub<IStripeService>(), audit, NullLogger<RecurringCustomerPaymentService>.Instance));
    }

    [Theory]
    [InlineData(false, 22, null, 0)]
    [InlineData(false, 10, null, 0)]
    [InlineData(false, 15, null, 0)]
    [InlineData(false, 22, 15, 15)]
    [InlineData(true, 20, 15, 20)]
    [InlineData(true, 15, 20, 20)]
    [InlineData(true, 20, null, 20)]
    public async Task RealGenerationAndPreviewUseOnlyHigherLifetimeOrSeriesDiscount(bool lifetime, int account, int? seriesPercent, int expected)
    {
        using var db = Db(); var service = await Setup(db, lifetime, account);
        var dto = new SaveRecurringSeriesDto { RecurringLoyaltyDiscountPercent = seriesPercent };
        var preview = await service.PreviewAsync(1, dto);
        Assert.Equal(expected, preview.LoyaltyPercent);
        Assert.Contains(preview.SourceDiscounts, d => d.Label.Contains("ORIGINAL20") && d.Amount == 20);
        Assert.Contains(preview.SourceDiscounts, d => d.Label == "Bubble Points" && d.Amount == 10);
        Assert.Contains(preview.SourceDiscounts, d => d.Label.Contains("Loyalty", StringComparison.OrdinalIgnoreCase) && d.Percent == 22);
        Assert.Single(await db.Orders.ToListAsync()); Assert.Empty(await db.RecurringOrderSeries.ToListAsync());
        var created = await service.CreateAsync(1, dto, 5);
        Assert.Equal(seriesPercent, created.RecurringLoyaltyDiscountPercent);
        Assert.Empty(created.GenerationWarnings);
        var generated = await db.Orders.Where(o => o.IsGeneratedByRecurringSeries).ToListAsync();
        Assert.NotEmpty(generated);
        Assert.All(generated, o => {
            Assert.Equal(expected, o.LoyaltyDiscountPercentage); Assert.Equal(preview.Total, o.Total);
            Assert.Equal(0, o.DiscountAmount); Assert.Null(o.PromoCode);
            Assert.Equal(0, o.PointsRedeemedDiscount); Assert.Equal(0, o.PointsRedeemed);
            Assert.Equal(0, o.SubscriptionDiscountAmount); Assert.Equal(0, o.GiftCardAmountUsed); Assert.Equal(0, o.RewardBalanceUsed);
        });
        Assert.Equal(22, (await db.Orders.FindAsync(1))!.LoyaltyDiscountPercentage);
        Assert.Equal(account, (await db.Users.FindAsync(1))!.LoyaltyDiscountPercentage);
        Assert.Contains(await db.AuditLogs.ToListAsync(), a => a.Action == "RecurringSeriesCreated");
    }

    /// <summary>
    /// A series discount may be written as a FIXED AMOUNT instead of a percentage. The amount the
    /// admin typed is the amount that comes off every cleaning; the percentage column every other
    /// loyalty surface reads is derived from it, never typed. Lifetime still does not stack — the
    /// comparison is simply made in MONEY, which is the only way a fixed amount can be compared
    /// with a percentage at all.
    /// </summary>
    [Theory]
    [InlineData(false, 22, 15, 15, 15)]      // no lifetime: the fixed amount applies as typed
    [InlineData(false, 22, 7.5, 7.5, 7.5)]   // fractions of a dollar survive
    [InlineData(true, 20, 15, 20, 20)]       // lifetime 20% of $100 beats $15
    [InlineData(true, 10, 25, 25, 25)]       // $25 beats lifetime 10% of $100
    [InlineData(false, 22, 250, 100, 100)]   // clamped to the subtotal, never below zero
    public async Task AFixedRecurringDiscountTakesOffTheAmountTypedAndNeverStacksWithLifetime(
        bool lifetime, int account, double fixedDollars, double expectedDollars, double expectedPercentage)
    {
        decimal fixedAmount = (decimal)fixedDollars, expectedAmount = (decimal)expectedDollars,
            expectedPercent = (decimal)expectedPercentage;
        using var db = Db(); var service = await Setup(db, lifetime, account);
        var dto = new SaveRecurringSeriesDto { RecurringLoyaltyDiscountAmount = fixedAmount };

        var preview = await service.PreviewAsync(1, dto);
        Assert.Equal(100, preview.BaseCleaning);
        Assert.Equal(expectedAmount, preview.LoyaltyAmount);
        Assert.Equal(expectedPercent, preview.LoyaltyPercent);

        var created = await service.CreateAsync(1, dto, 5);
        Assert.Equal(fixedAmount, created.RecurringLoyaltyDiscountAmount);
        Assert.Null(created.RecurringLoyaltyDiscountPercent);

        var generated = await db.Orders.Where(o => o.IsGeneratedByRecurringSeries).ToListAsync();
        Assert.NotEmpty(generated);
        Assert.All(generated, o =>
        {
            Assert.Equal(expectedAmount, o.LoyaltyDiscountAmount);
            Assert.Equal(expectedPercent, o.LoyaltyDiscountPercentage);
            Assert.Equal(preview.Total, o.Total);
        });
    }

    /// <summary>
    /// Percentage and fixed amount are ONE agreement written two ways, so both together is
    /// rejected rather than resolved by preferring one — guessing would bill the wrong figure
    /// every fortnight, silently.
    /// </summary>
    [Fact]
    public async Task APercentageAndAFixedAmountTogetherIsRefusedRatherThanResolved()
    {
        using var db = Db(); var service = await Setup(db);
        var both = new SaveRecurringSeriesDto { RecurringLoyaltyDiscountPercent = 10, RecurringLoyaltyDiscountAmount = 50 };

        await Assert.ThrowsAsync<RecurringSeriesException>(() => service.PreviewAsync(1, both));
        await Assert.ThrowsAsync<RecurringSeriesException>(() => service.CreateAsync(1, both, 5));
        await Assert.ThrowsAsync<RecurringSeriesException>(() => service.CreateAsync(1,
            new SaveRecurringSeriesDto { RecurringLoyaltyDiscountAmount = -1 }, 5));

        Assert.Empty(await db.RecurringOrderSeries.ToListAsync());
        Assert.Null((await db.Orders.FindAsync(1))!.RecurringSeriesId);
    }

    /// <summary>Switching an existing series from one form to the other is a rule change, so it
    /// must ask what to do with future orders exactly as a percentage change does.</summary>
    [Fact]
    public async Task SwitchingBetweenPercentageAndFixedAmountIsARuleChange()
    {
        using var db = Db(); var service = await Setup(db);
        var series = await service.CreateAsync(1, new SaveRecurringSeriesDto { RecurringLoyaltyDiscountPercent = 10 }, 5);

        var toFixed = new SaveRecurringSeriesDto { RecurringLoyaltyDiscountAmount = 10 };
        await Assert.ThrowsAsync<RecurringSeriesException>(() => service.UpdateAsync(series.Id, toFixed, 5));

        toFixed.FutureOrdersAction = "Regenerate";
        var updated = await service.UpdateAsync(series.Id, toFixed, 5);
        Assert.Equal(10, updated.RecurringLoyaltyDiscountAmount);
        Assert.Null(updated.RecurringLoyaltyDiscountPercent);
    }

    [Theory]
    [InlineData("Keep")]
    [InlineData("Regenerate")]
    public async Task DiscountEditRequiresChoiceAndOnlyRepricesEligibleReplacementOrders(string choice)
    {
        using var db = Db(); var service = await Setup(db);
        var series = await service.CreateAsync(1, new SaveRecurringSeriesDto(), 5);
        var old = await db.Orders.Where(o => o.IsGeneratedByRecurringSeries).OrderBy(o => o.ServiceDate).ToListAsync();
        old[0].IsPaid = true; await db.SaveChangesAsync();
        var totals = old.Select(o => o.Total).ToArray();
        var dto = new SaveRecurringSeriesDto { RecurringLoyaltyDiscountPercent = 15 };
        await Assert.ThrowsAsync<RecurringSeriesException>(() => service.UpdateAsync(series.Id, dto, 5));
        dto.FutureOrdersAction = choice; await service.UpdateAsync(series.Id, dto, 5);
        Assert.Equal(totals, old.Select(o => o.Total));
        Assert.All(old, o => Assert.Equal(0, o.LoyaltyDiscountPercentage));
        Assert.NotEqual(OrderStatuses.Cancelled, old[0].Status);
        if (choice == "Regenerate") {
            Assert.All(old.Skip(1), o => Assert.Equal(OrderStatuses.Cancelled, o.Status));
            var newOrders = await db.Orders.Where(o => o.IsGeneratedByRecurringSeries && o.Id > old.Last().Id).ToListAsync();
            Assert.NotEmpty(newOrders); Assert.All(newOrders, o => Assert.Equal(15, o.LoyaltyDiscountPercentage));
        } else Assert.Equal(old.Count + 1, await db.Orders.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvoiceGenerationExcludesBothLoyaltySources(bool legacyLinkedCardOrder)
    {
        using var db = Db(); var service = await Setup(db, true, 20, legacyLinkedCardOrder ? PaymentMethod.Normal : PaymentMethod.Invoice);
        if (legacyLinkedCardOrder) {
            db.CommercialInvoiceOrders.Add(new DreamCleaningBackend.Models.Commercial.CommercialInvoiceOrder { OrderId = 1 });
            await db.SaveChangesAsync();
        }
        var dto = new SaveRecurringSeriesDto { RecurringLoyaltyDiscountPercent = 25 };
        Assert.True((await service.PreviewAsync(1, dto)).CommercialLoyaltyExcluded);
        await service.CreateAsync(1, dto, 5);
        Assert.All(await db.Orders.Where(o => o.IsGeneratedByRecurringSeries).ToListAsync(), o => Assert.Equal(0, o.LoyaltyDiscountAmount));
    }

    [Fact]
    public async Task FailedAuditDoesNotPoisonGenerationAndPostCommitFailureReportsSavedSeries()
    {
        var interceptor = new RejectAuditWrites(); using var db = Db(interceptor);
        var service = await Setup(db); interceptor.Enabled = true;
        var created = await service.CreateAsync(1, new SaveRecurringSeriesDto(), 5);
        Assert.Empty(created.GenerationWarnings);
        Assert.NotEmpty(created.Occurrences.Where(o => o.WasGenerated));
        Assert.Empty(db.ChangeTracker.Entries<AuditLog>().Where(e => e.State == EntityState.Added));
        using var brokenDb = Db(); var broken = await Setup(brokenDb, brokenBooking: true);
        var saved = await broken.CreateAsync(1, new SaveRecurringSeriesDto(), 5);
        Assert.NotEmpty(saved.GenerationWarnings);
        Assert.Equal(saved.Id, (await brokenDb.Orders.FindAsync(1))!.RecurringSeriesId);
        Assert.Single(await brokenDb.RecurringOrderSeries.ToListAsync());
    }

    [Fact]
    public async Task InvalidInitialRequestLeavesNoSeriesOrTemplateLink()
    {
        using var db = Db(); var service = await Setup(db);
        await Assert.ThrowsAsync<RecurringSeriesException>(() => service.CreateAsync(1,
            new SaveRecurringSeriesDto { RecurringLoyaltyDiscountPercent = 101 }, 5));
        Assert.Empty(await db.RecurringOrderSeries.ToListAsync()); Assert.Null((await db.Orders.FindAsync(1))!.RecurringSeriesId);
    }

    [Fact]
    public async Task AdminListReadsFreshRowsAndAmountsInSameProcessAfterGenerationAndRegeneration()
    {
        using var db = Db(); var seriesService = await Setup(db);
        (await db.Orders.FindAsync(1))!.IsPaid = true; await db.SaveChangesAsync();
        var orders = new DreamCleaningBackend.Services.OrderService(Stub<DreamCleaningBackend.Repositories.Interfaces.IOrderRepository>(), db,
            Stub<IStripeService>(), Stub<IEmailService>(), Stub<ISmsService>(), new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
            NullLogger<DreamCleaningBackend.Services.OrderService>.Instance, Stub<ILoyaltyDiscountService>());
        Assert.Single(await orders.GetAllOrdersForAdmin());
        var series = await seriesService.CreateAsync(1, new SaveRecurringSeriesDto { IsActive = false }, 5);
        Assert.Single(await orders.GetAllOrdersForAdmin());
        await seriesService.SetStateAsync(series.Id, "resume", 5);
        var generated = await orders.GetAllOrdersForAdmin(); Assert.True(generated.Count > 1);
        Assert.Equal(await db.Orders.SumAsync(o => o.Total), generated.Sum(o => o.Total));
        await seriesService.UpdateAsync(series.Id, new SaveRecurringSeriesDto { IntervalValue = 2, FutureOrdersAction = "Regenerate" }, 5);
        var replaced = await orders.GetAllOrdersForAdmin();
        Assert.Contains(replaced, o => o.Status == OrderStatuses.Cancelled);
        Assert.Equal(await db.Orders.CountAsync(), replaced.Count);
        var next = await db.Orders.FirstAsync(o => o.IsGeneratedByRecurringSeries && o.Status == OrderStatuses.Pending);
        await seriesService.SkipAsync(series.Id, next.Id, 5);
        Assert.Equal(OrderStatuses.Cancelled, (await orders.GetAllOrdersForAdmin()).Single(o => o.Id == next.Id).Status);
    }

    internal class RejectAuditWrites : SaveChangesInterceptor
    {
        public bool Enabled;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (Enabled && data.Context!.ChangeTracker.Entries<AuditLog>().Any(e => e.State == EntityState.Added))
                throw new DbUpdateException("Simulated audit write failure");
            return ValueTask.FromResult(result);
        }
    }
}
