using System.Security.Claims;
using System.Text.Json;
using DreamCleaningBackend.Controllers;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// The first-time offer is recognised by SpecialOffer.OfferKey ("first-time"), never by its name;
    /// the "Most popular" plan is a flag only one plan can hold. Fixtures are shaped like production
    /// (GET api/special-offers/public, 2026-10-04): the first-time offer is a CUSTOM-type row with
    /// RequiresFirstTimeCustomer set - which is exactly why neither Type nor Name could be the identity.
    /// </summary>
    public class OfferAndSubscriptionKeyTests : IDisposable
    {
        private readonly ApplicationDbContext _context;
        private readonly SpecialOfferService _offers;

        public OfferAndSubscriptionKeyTests()
        {
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase($"offer-key-{Guid.NewGuid()}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            _context = new ApplicationDbContext(options);
            _offers = new SpecialOfferService(_context, Audit());
            FirstTimeOfferHelper.ResetWarningsForTests();
        }

        public void Dispose() => _context.Dispose();

        private AuditService Audit() => new(_context, new HttpContextAccessor(), NullLogger<AuditService>.Instance);

        /// <summary>Production's row id 1, as the public endpoint returns it, with the migration's key.</summary>
        private static SpecialOffer ProductionFirstTime(string name = "First Time Customer", string? key = FirstTimeOfferHelper.Key) => new()
        {
            Id = 1, Name = name, Description = "Get 10% off on your first order!", IsPercentage = true,
            DiscountValue = 10m, Type = OfferType.Custom, RequiresFirstTimeCustomer = true, IsActive = true,
            OfferKey = key, CreatedAt = DateTime.UtcNow
        };

        private SpecialOffersAdminController AdminController(string role) => new(_offers, _context)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim(ClaimTypes.NameIdentifier, "1"),
                        new Claim(ClaimTypes.Role, role)
                    }, "test"))
                }
            }
        };

        private static string? Message(object? result) => result switch
        {
            ObjectResult o when o.Value != null => JsonSerializer.SerializeToElement(o.Value).GetProperty("message").GetString(),
            _ => null
        };

        private static UpdateSpecialOfferDto UpdateOf(SpecialOffer o) => new()
        {
            Name = o.Name, Description = o.Description, IsPercentage = o.IsPercentage,
            DiscountValue = o.DiscountValue, IsActive = o.IsActive
        };

        // ── Identity ─────────────────────────────────────────────────────────────────────────

        [Fact]
        public void TheKeyedProductionOfferIsTheFirstTimeOffer_AndRenamingItChangesNothing()
        {
            var production = ProductionFirstTime();
            var renamed = ProductionFirstTime(name: "New Client Deal");

            Assert.True(FirstTimeOfferHelper.IsFirstTimeOffer(production));
            Assert.True(FirstTimeOfferHelper.IsFirstTimeOffer(renamed));
            Assert.Same(renamed, FirstTimeOfferHelper.Find(new[] { renamed }));
            Assert.Equal("10%", FirstTimeOfferHelper.FormatDiscountLabel(FirstTimeOfferHelper.Find(new[] { renamed })));
        }

        [Fact]
        public void AKeyedOfferThatIsNotFirstTime_IsNotMatchedByItsNameOrFlag()
        {
            // A "first-timers only" seasonal sale: flag set, name says first time, but its own key.
            var sale = new SpecialOffer
            {
                Id = 2, Name = "First Time Spring Sale", RequiresFirstTimeCustomer = true,
                Type = OfferType.FirstTime, OfferKey = "spring-sale", IsActive = true, DiscountValue = 20m
            };
            var firstTime = ProductionFirstTime();

            Assert.False(FirstTimeOfferHelper.IsFirstTimeOffer(sale));
            // Listed first, still not picked: the key decides.
            Assert.Same(firstTime, FirstTimeOfferHelper.Find(new[] { sale, firstTime }));
        }

        [Fact]
        public void AnUnkeyedOffer_FallsBackToTheLegacyRuleForOperations_ButIsNotAdvertised()
        {
            var unkeyed = ProductionFirstTime(key: null);

            // Operational question (e.g. "don't list it again as a seasonal offer"): legacy rule.
            Assert.True(FirstTimeOfferHelper.IsFirstTimeOffer(unkeyed));
            // Marketing question: key only - nothing advertised rather than a guess.
            Assert.Null(FirstTimeOfferHelper.Find(new[] { unkeyed }));
            Assert.Null(FirstTimeOfferHelper.FormatDiscountLabel(FirstTimeOfferHelper.Find(new[] { unkeyed })));
        }

        [Fact]
        public async Task TheEmailLabelFollowsTheKey_NotTheName()
        {
            _context.SpecialOffers.Add(ProductionFirstTime(name: "Welcome Discount"));
            await _context.SaveChangesAsync();

            Assert.Equal("10%", await _offers.GetFirstTimeDiscountLabel());
        }

        // ── Delete guard ─────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task TheKeyedFirstTimeOfferCannotBeDeleted_EvenThoughItsTypeIsCustom()
        {
            _context.SpecialOffers.Add(ProductionFirstTime());
            await _context.SaveChangesAsync();

            var ex = await Assert.ThrowsAsync<Exception>(() => _offers.DeleteSpecialOffer(1));
            Assert.Contains("first-time", ex.Message);
            Assert.Single(_context.SpecialOffers);
        }

        [Fact]
        public async Task AFirstTimeTypeOfferStaysProtected_AndAnOrdinaryOfferCanBeDeleted()
        {
            _context.SpecialOffers.AddRange(
                new SpecialOffer { Id = 3, Name = "Legacy", Description = "", Type = OfferType.FirstTime, CreatedAt = DateTime.UtcNow },
                new SpecialOffer { Id = 4, Name = "Holiday", Description = "", Type = OfferType.Holiday, CreatedAt = DateTime.UtcNow });
            await _context.SaveChangesAsync();

            await Assert.ThrowsAsync<Exception>(() => _offers.DeleteSpecialOffer(3));
            Assert.True(await _offers.DeleteSpecialOffer(4));
        }

        // ── Admin key field ──────────────────────────────────────────────────────────────────

        [Fact]
        public async Task AMalformedOrDuplicateKeyIsRefusedWithTheReason()
        {
            _context.SpecialOffers.Add(ProductionFirstTime());
            await _context.SaveChangesAsync();
            var admin = AdminController("SuperAdmin");

            var bad = await admin.CreateSpecialOffer(new CreateSpecialOfferDto
            { Name = "X", Description = "", DiscountValue = 5, Type = 3, OfferKey = "First_Time" });
            Assert.IsType<BadRequestObjectResult>(bad.Result);
            Assert.Contains("lowercase", Message(bad.Result));

            var dup = await admin.CreateSpecialOffer(new CreateSpecialOfferDto
            { Name = "Y", Description = "", DiscountValue = 5, Type = 3, OfferKey = " first-time " });
            Assert.Contains("already used by \"First Time Customer\"", Message(dup.Result));

            Assert.Single(_context.SpecialOffers);
        }

        [Fact]
        public async Task AbsentKeyKeepsTheStoredOne_ExplicitNullOrBlankClearsIt()
        {
            _context.SpecialOffers.Add(ProductionFirstTime());
            await _context.SaveChangesAsync();
            var admin = AdminController("SuperAdmin");
            var offer = _context.SpecialOffers.Single();

            // An admin page from before the deploy: the body has no offerKey at all.
            var keep = JsonSerializer.Deserialize<UpdateSpecialOfferDto>(
                """{"name":"First Time Customer","description":"d","isPercentage":true,"discountValue":10,"isActive":true}""",
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            Assert.False(keep.OfferKeyProvided);
            Assert.IsType<OkObjectResult>((await admin.UpdateSpecialOffer(1, keep)).Result);
            Assert.Equal("first-time", _context.SpecialOffers.Single().OfferKey);

            var clear = JsonSerializer.Deserialize<UpdateSpecialOfferDto>(
                """{"name":"First Time Customer","description":"d","isPercentage":true,"discountValue":10,"isActive":true,"offerKey":null}""",
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            Assert.True(clear.OfferKeyProvided);
            Assert.IsType<OkObjectResult>((await admin.UpdateSpecialOffer(1, clear)).Result);
            Assert.Null(_context.SpecialOffers.Single().OfferKey);

            var set = UpdateOf(offer);
            set.OfferKey = "first-time";
            Assert.IsType<OkObjectResult>((await admin.UpdateSpecialOffer(1, set)).Result);
            var blank = UpdateOf(offer);
            blank.OfferKey = "   ";
            Assert.IsType<OkObjectResult>((await admin.UpdateSpecialOffer(1, blank)).Result);
            Assert.Null(_context.SpecialOffers.Single().OfferKey);
        }

        [Fact]
        public async Task OnlyASuperAdminChangesAKey_ButAnAdminCanSaveTheOfferUnchanged()
        {
            _context.SpecialOffers.Add(ProductionFirstTime());
            await _context.SaveChangesAsync();
            var admin = AdminController("Admin");
            var offer = _context.SpecialOffers.AsNoTracking().Single();

            var change = UpdateOf(offer);
            change.OfferKey = null;
            var refused = await admin.UpdateSpecialOffer(1, change);
            Assert.Equal(403, Assert.IsType<ObjectResult>(refused.Result).StatusCode);
            Assert.Equal("first-time", _context.SpecialOffers.AsNoTracking().Single().OfferKey);

            var same = UpdateOf(offer);
            same.Description = "Now 10% off";
            same.OfferKey = "first-time";
            Assert.IsType<OkObjectResult>((await admin.UpdateSpecialOffer(1, same)).Result);
            Assert.Equal("Now 10% off", _context.SpecialOffers.AsNoTracking().Single().Description);

            var create = await admin.CreateSpecialOffer(new CreateSpecialOfferDto
            { Name = "Z", Description = "", DiscountValue = 5, Type = 3, OfferKey = "z-offer" });
            Assert.Equal(403, Assert.IsType<ObjectResult>(create.Result).StatusCode);
        }

        [Fact]
        public async Task TheKeyReachesThePublicAndPerUserOfferLists()
        {
            _context.SpecialOffers.Add(ProductionFirstTime(name: "Renamed"));
            _context.UserSpecialOffers.Add(new UserSpecialOffer { Id = 1, UserId = 7, SpecialOfferId = 1, GrantedAt = DateTime.UtcNow });
            await _context.SaveChangesAsync();

            var mine = await _offers.GetUserAvailableOffers(7);
            Assert.Equal("first-time", Assert.Single(mine).OfferKey);

            var pub = await new SpecialOffersController(_offers, _context).GetPublicSpecialOffers();
            var list = Assert.IsType<List<PublicSpecialOfferDto>>(Assert.IsType<OkObjectResult>(pub.Result).Value);
            Assert.Equal("first-time", Assert.Single(list).OfferKey);
        }

        // ── "Most popular" plan ──────────────────────────────────────────────────────────────

        private AdminCatalogController Catalog() => new(_context, Audit(), NullLogger<AdminCatalogController>.Instance, null!)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        private async Task SeedProductionPlans()
        {
            // GET api/booking/subscriptions, 2026-10-04 (+ the migration's badge on Monthly).
            _context.Subscriptions.AddRange(
                new Subscription { Id = 1, Name = "One Time", DiscountPercentage = 0, SubscriptionDays = 0, DisplayOrder = 1 },
                new Subscription { Id = 2, Name = "Weekly", DiscountPercentage = 15, SubscriptionDays = 7, DisplayOrder = 2 },
                new Subscription { Id = 3, Name = "Bi-Weekly", DiscountPercentage = 10, SubscriptionDays = 14, DisplayOrder = 3 },
                new Subscription { Id = 4, Name = "Monthly", DiscountPercentage = 5, SubscriptionDays = 30, DisplayOrder = 4, IsMostPopular = true });
            await _context.SaveChangesAsync();
        }

        private static UpdateSubscriptionDto UpdateOf(Subscription s, bool? mostPopular) => new()
        {
            Name = s.Name, Description = s.Description, DiscountPercentage = s.DiscountPercentage,
            SubscriptionDays = s.SubscriptionDays, DisplayOrder = s.DisplayOrder, IsMostPopular = mostPopular
        };

        private int[] Popular() => _context.Subscriptions.AsNoTracking().Where(s => s.IsMostPopular).Select(s => s.Id).ToArray();

        [Fact]
        public async Task TickingMostPopularMovesTheBadge_SoOnlyOnePlanHoldsIt()
        {
            await SeedProductionPlans();
            var weekly = _context.Subscriptions.AsNoTracking().Single(s => s.Id == 2);

            Assert.IsType<OkObjectResult>((await Catalog().UpdateSubscription(2, UpdateOf(weekly, true))).Result);
            Assert.Equal(new[] { 2 }, Popular());

            var created = await Catalog().CreateSubscription(new CreateSubscriptionDto
            { Name = "Every 3 weeks", DiscountPercentage = 8, SubscriptionDays = 21, IsMostPopular = true });
            var dto = Assert.IsType<SubscriptionDto>(Assert.IsType<OkObjectResult>(created.Result).Value);
            Assert.Equal(new[] { dto.Id }, Popular());
        }

        [Fact]
        public async Task AnAbsentFlagKeepsTheBadge_FalseRemovesIt()
        {
            await SeedProductionPlans();
            var monthly = _context.Subscriptions.AsNoTracking().Single(s => s.Id == 4);

            // Renaming the plan (old admin page: no flag in the body) keeps the badge on it.
            var renamed = UpdateOf(monthly, null);
            renamed.Name = "Every Month";
            Assert.IsType<OkObjectResult>((await Catalog().UpdateSubscription(4, renamed)).Result);
            Assert.Equal(new[] { 4 }, Popular());

            Assert.IsType<OkObjectResult>((await Catalog().UpdateSubscription(4, UpdateOf(monthly, false))).Result);
            Assert.Empty(Popular());
        }

        [Fact]
        public async Task ThePublicPlanListCarriesTheFlagAndTheDays()
        {
            await SeedProductionPlans();
            var result = await Catalog().GetSubscriptions();
            var plans = Assert.IsType<List<SubscriptionDto>>(Assert.IsType<OkObjectResult>(result.Result).Value);

            Assert.Equal(new[] { 0, 7, 14, 30 }, plans.Select(p => p.SubscriptionDays));
            Assert.Equal(new[] { false, false, false, true }, plans.Select(p => p.IsMostPopular));
        }
    }
}
