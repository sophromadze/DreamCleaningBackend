using DreamCleaningBackend.Data;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// 2026-10: the admin Orders list decides "Deep" / "Regular" for every order on the server
    /// (OrderListDto.IsDeepCleaning). It used to be the Orders tab fetching every residential order's
    /// full details, one request each (261 on a dev /admin load), to run the same rule in the browser.
    /// The rule must stay EXACTLY the frontend's resolveIsDeepResidential: keys and flags decide, a
    /// name is read only for an unkeyed row.
    /// </summary>
    public class ResidentialCleaningVariantTests : IDisposable
    {
        private readonly ApplicationDbContext _db;

        public ResidentialCleaningVariantTests()
        {
            _db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase($"residential-variant-{Guid.NewGuid()}", b => b.EnableNullChecks(false))
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options);
        }

        public void Dispose() => _db.Dispose();

        [Theory]
        // Flags decide first.
        [InlineData(null, "Anything", true, false, true)]
        [InlineData(null, "Anything", false, true, false)]          // Super Deep is not "Deep"
        [InlineData(null, "Anything", true, true, false)]            // flagged Super Deep wins
        // A key decides, and a keyed row is never read by name.
        [InlineData("deep-cleaning", "Whatever", false, false, true)]
        [InlineData("oven", "Deep Cleaning (oven)", false, false, false)]
        [InlineData("windows", "Super Deep Cleaning", false, false, false)]
        // Only an unkeyed, un-flagged row falls back to its name.
        [InlineData(null, "Deep Cleaning", false, false, true)]
        [InlineData("", "deep cleaning", false, false, true)]
        [InlineData(null, "Super Deep Cleaning", false, false, false)]
        [InlineData(null, "Oven", false, false, false)]
        public void Extras_follow_the_frontend_rule(string? key, string name, bool deep, bool superDeep, bool expected)
        {
            Assert.Equal(expected, ResidentialCleaningVariant.ExtraMakesDeep(key, name, deep, superDeep));
        }

        [Theory]
        [InlineData(null, "Deep Cleaning", true)]
        [InlineData("", "deep_cleaning", true)]
        [InlineData(null, "Super Deep Cleaning", false)]
        [InlineData("bedrooms", "Deep Cleaning", false)]   // keyed: never by name
        [InlineData(null, "Bedrooms", false)]
        public void Service_lines_count_only_when_unkeyed(string? key, string name, bool expected)
        {
            Assert.Equal(expected, ResidentialCleaningVariant.ServiceLineMakesDeep(key, name));
        }

        [Fact]
        public async Task The_whole_list_is_decided_from_one_grouped_load()
        {
            _db.ExtraServices.AddRange(
                new ExtraService { Id = 1, Name = "Deep Cleaning", ExtraServiceKey = "deep-cleaning", IsDeepCleaning = true, IsActive = true },
                new ExtraService { Id = 2, Name = "Super Deep Cleaning", IsSuperDeepCleaning = true, IsActive = true },
                new ExtraService { Id = 3, Name = "Oven", ExtraServiceKey = "oven", IsActive = true },
                new ExtraService { Id = 4, Name = "Deep Cleaning", IsActive = true });             // legacy, unkeyed
            _db.Services.AddRange(
                new Service { Id = 10, Name = "Deep Cleaning", ServiceKey = "" },                 // legacy line
                new Service { Id = 11, Name = "Bedrooms", ServiceKey = "bedrooms" });
            _db.OrderExtraServices.AddRange(
                new OrderExtraService { Id = 1, OrderId = 100, ExtraServiceId = 1 },
                new OrderExtraService { Id = 2, OrderId = 101, ExtraServiceId = 2 },
                new OrderExtraService { Id = 3, OrderId = 102, ExtraServiceId = 3 },
                new OrderExtraService { Id = 4, OrderId = 103, ExtraServiceId = 4 });
            _db.OrderServices.AddRange(
                new Models.OrderService { Id = 1, OrderId = 104, ServiceId = 10 },
                new Models.OrderService { Id = 2, OrderId = 105, ServiceId = 11 });
            await _db.SaveChangesAsync();

            var deep = await ResidentialCleaningVariant.LoadDeepOrderIdsAsync(_db);

            Assert.Equal(new[] { 100, 103, 104 }, deep.OrderBy(id => id));
        }
    }
}
