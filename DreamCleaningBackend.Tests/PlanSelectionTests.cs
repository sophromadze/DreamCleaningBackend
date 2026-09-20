using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DreamCleaningBackend.Tests;

/// <summary>
/// Choosing a plan from the customer's profile (2026-09).
///
/// The rule under test is the one in <see cref="PlanSelectionPolicy"/>: a plan chosen on the
/// profile is a PREFERENCE. The money side of a plan - who gets the discount and when - is
/// decided entirely by <c>BookingCreationService.ResolveDiscountsAsync</c> reading
/// <c>User.SubscriptionId</c>, and this feature must leave that untouched. Every test below is
/// pointed at that: if a future change quietly turns the preference into an activation, the
/// customer's FIRST cleaning starts arriving discounted and these fail.
/// </summary>
public class PlanSelectionTests
{
    private static ApplicationDbContext Db() => new(new DbContextOptionsBuilder<ApplicationDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString(), b => b.EnableNullChecks(false))
        .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options);

    private static void SeedTiers(ApplicationDbContext db)
    {
        db.Subscriptions.AddRange(
            new Subscription { Id = 1, Name = "One Time", SubscriptionDays = 0, DiscountPercentage = 0, IsActive = true, DisplayOrder = 1 },
            new Subscription { Id = 2, Name = "Weekly", SubscriptionDays = 7, DiscountPercentage = 15, IsActive = true, DisplayOrder = 2 },
            new Subscription { Id = 3, Name = "Bi-Weekly", SubscriptionDays = 14, DiscountPercentage = 10, IsActive = true, DisplayOrder = 3 },
            new Subscription { Id = 4, Name = "Monthly", SubscriptionDays = 30, DiscountPercentage = 5, IsActive = true, DisplayOrder = 4 },
            new Subscription { Id = 5, Name = "Retired", SubscriptionDays = 7, DiscountPercentage = 20, IsActive = false, DisplayOrder = 5 });
    }

    // -- The policy itself ---------------------------------------------------------------

    [Fact]
    public void OneTime_IsNotSelectableAsAPlan()
    {
        // "One Time" is the absence of a plan. Offering it as something to opt into leaves the
        // customer wondering what they just bought; clearing the preference is the way off a plan.
        Assert.False(PlanSelectionPolicy.IsSelectable(
            new Subscription { Name = "One Time", SubscriptionDays = 0, IsActive = true }));
    }

    [Fact]
    public void ADeactivatedTier_IsNotSelectable()
    {
        Assert.False(PlanSelectionPolicy.IsSelectable(
            new Subscription { Name = "Weekly", SubscriptionDays = 7, IsActive = false }));
        Assert.False(PlanSelectionPolicy.IsSelectable(null));
    }

    [Fact]
    public void ARepeatingActiveTier_IsSelectable()
    {
        Assert.True(PlanSelectionPolicy.IsSelectable(
            new Subscription { Name = "Weekly", SubscriptionDays = 7, IsActive = true }));
    }

    [Fact]
    public void APreferenceAloneNeverMakesTheNextCleaningDiscounted()
    {
        var now = DateTime.UtcNow;
        var user = new User { Id = 1, PreferredSubscriptionId = 2, SubscriptionId = null, Subscription = null };

        Assert.False(PlanSelectionPolicy.NextCleaningWouldBeDiscounted(user, tierDays: 7, now));
        Assert.False(RecurringPlanRule.IsActiveUserSubscription(user, now));
    }

    [Fact]
    public void AnExpiredPlanDoesNotDiscountTheNextCleaning()
    {
        var now = DateTime.UtcNow;
        var user = new User
        {
            Id = 1,
            SubscriptionId = 2,
            Subscription = new Subscription { Id = 2, Name = "Weekly", SubscriptionDays = 7, DiscountPercentage = 15 },
            SubscriptionExpiryDate = now.AddDays(-1)
        };

        Assert.False(PlanSelectionPolicy.NextCleaningWouldBeDiscounted(user, tierDays: 7, now));
    }

    [Fact]
    public void ALivePlanDiscountsOnlyItsOwnTier()
    {
        var now = DateTime.UtcNow;
        var user = new User
        {
            Id = 1,
            SubscriptionId = 2,
            Subscription = new Subscription { Id = 2, Name = "Weekly", SubscriptionDays = 7, DiscountPercentage = 15 },
            SubscriptionExpiryDate = now.AddDays(5)
        };

        Assert.True(PlanSelectionPolicy.NextCleaningWouldBeDiscounted(user, tierDays: 7, now));
        Assert.False(PlanSelectionPolicy.NextCleaningWouldBeDiscounted(user, tierDays: 14, now));
        Assert.False(PlanSelectionPolicy.NextCleaningWouldBeDiscounted(user, tierDays: 30, now));
    }

    // -- The pricing chain is untouched ---------------------------------------------------

    /// <summary>
    /// THE test this file exists for. A customer picks Weekly on their profile and books their
    /// first cleaning on Weekly: the order must still be full price, because the discount begins
    /// on the second cleaning in a row. Runs the real
    /// <c>BookingCreationService.ResolveDiscountsAsync</c>.
    /// </summary>
    [Fact]
    public async Task ChoosingAPlanOnTheProfile_DoesNotDiscountTheFirstCleaning()
    {
        using var db = Db();
        SeedTiers(db);
        db.Users.Add(new User
        {
            Id = 1, FirstName = "Pat", LastName = "Customer", Email = "pat@example.com", IsActive = true,
            // The profile choice - and nothing else. No SubscriptionId, no expiry.
            PreferredSubscriptionId = 2, PreferredSubscriptionSelectedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var svc = new BookingCreationService(db, null!, null!, NullLogger<BookingCreationService>.Instance);

        var (_, subscriptionDiscount) = await svc.ResolveDiscountsAsync(
            new CreateBookingDto { SubscriptionId = 2 }, orderUserId: 1, subTotal: 400m);

        Assert.Equal(0m, subscriptionDiscount);
    }

    /// <summary>
    /// The second cleaning in a row IS discounted - i.e. the feature changed nothing about the
    /// real activation path, which is still a booked cleaning writing User.SubscriptionId.
    /// </summary>
    [Fact]
    public async Task TheSecondCleaningInARow_IsStillDiscounted()
    {
        using var db = Db();
        SeedTiers(db);
        db.Users.Add(new User
        {
            Id = 1, FirstName = "Pat", LastName = "Customer", Email = "pat@example.com", IsActive = true,
            PreferredSubscriptionId = 2,
            SubscriptionId = 2,                                   // written by ISubscriptionService
            SubscriptionExpiryDate = DateTime.UtcNow.AddDays(5)
        });
        await db.SaveChangesAsync();

        var svc = new BookingCreationService(db, null!, null!, NullLogger<BookingCreationService>.Instance);

        var (_, subscriptionDiscount) = await svc.ResolveDiscountsAsync(
            new CreateBookingDto { SubscriptionId = 2 }, orderUserId: 1, subTotal: 400m);

        Assert.Equal(60m, subscriptionDiscount);   // 15% of 400
    }

    /// <summary>
    /// A preference for a DIFFERENT tier than the one being booked changes nothing either way -
    /// the tier on the order is what is priced, exactly as before.
    /// </summary>
    [Fact]
    public async Task APreferenceForAnotherTier_DoesNotLeakIntoThePrice()
    {
        using var db = Db();
        SeedTiers(db);
        db.Users.Add(new User
        {
            Id = 1, FirstName = "Pat", LastName = "Customer", Email = "pat@example.com", IsActive = true,
            PreferredSubscriptionId = 2,                          // prefers Weekly
            SubscriptionId = 3,                                   // actually holds Bi-Weekly
            SubscriptionExpiryDate = DateTime.UtcNow.AddDays(5)
        });
        await db.SaveChangesAsync();

        var svc = new BookingCreationService(db, null!, null!, NullLogger<BookingCreationService>.Instance);

        // Booking the tier they hold: discounted.
        var (_, onHeldTier) = await svc.ResolveDiscountsAsync(
            new CreateBookingDto { SubscriptionId = 3 }, orderUserId: 1, subTotal: 400m);
        Assert.Equal(40m, onHeldTier);            // 10% of 400

        // Booking the tier they merely PREFER: not discounted.
        var (_, onPreferredTier) = await svc.ResolveDiscountsAsync(
            new CreateBookingDto { SubscriptionId = 2 }, orderUserId: 1, subTotal: 400m);
        Assert.Equal(0m, onPreferredTier);
    }

    /// <summary>
    /// Nothing on the money side may read the preference column. This is a source-level check
    /// because the damage would not show up as a failing assertion - it would show up as a
    /// discount on somebody's first order months later.
    /// </summary>
    [Fact]
    public void NothingInThePricingOrPaymentChainReadsThePreference()
    {
        var root = FindRepoDirectory("DreamCleaningBackend/DreamCleaningBackend");
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (rel.StartsWith("Migrations/") || rel.StartsWith("obj/") || rel.StartsWith("bin/")) continue;

            // The places that legitimately know about it: the model, the policy that explains it,
            // the Plan tab's wire shape, the endpoints that serve it, and the read the booking
            // page pre-selects from.
            if (rel is "Models/User.cs" or "Helpers/PlanSelectionPolicy.cs" or "DTOs/PlanDtos.cs"
                or "Controllers/ProfileController.cs" or "Controllers/BookingController.cs") continue;

            if (File.ReadAllText(file).Contains("PreferredSubscription"))
                offenders.Add(rel);
        }

        Assert.True(offenders.Count == 0,
            "The plan PREFERENCE must never reach pricing, booking creation or payment. " +
            "Unexpected readers: " + string.Join(", ", offenders));
    }

    private static string FindRepoDirectory(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(relative);
    }
}
