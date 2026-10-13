using DreamCleaningBackend.Controllers;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// 2026-10: the frontend's Node server keeps the booking catalogue in memory and polls
    /// <c>api/booking/catalogue-version</c> every 2 seconds. The stamp must move after any saved
    /// change to the catalogue - and only then, or every unrelated save (an order, a login) would
    /// make the Node server refetch the whole catalogue.
    /// </summary>
    public class CatalogueVersionTests : IDisposable
    {
        private readonly CatalogueVersion _version = new();
        private readonly ApplicationDbContext _db;

        public CatalogueVersionTests()
        {
            _db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase($"catalogue-version-{Guid.NewGuid()}", b => b.EnableNullChecks(false))
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .AddInterceptors(new CatalogueChangeInterceptor(_version)).Options);
        }

        public void Dispose() => _db.Dispose();

        [Fact]
        public async Task A_saved_catalogue_change_moves_the_stamp()
        {
            var before = _version.Current;
            _db.ServiceTypes.Add(new ServiceType { Id = 1, Name = "Residential Cleaning", ServiceKey = "residential", IsActive = true });
            await _db.SaveChangesAsync();
            var afterAdd = _version.Current;

            Assert.NotEqual(before, afterAdd);

            var type = await _db.ServiceTypes.SingleAsync();
            type.BasePrice = 95m;
            _db.SaveChanges();

            Assert.NotEqual(afterAdd, _version.Current);
        }

        [Theory]
        [InlineData("extra")]
        [InlineData("plan")]
        [InlineData("offer")]
        [InlineData("blocked")]
        public async Task Every_catalogue_table_counts(string which)
        {
            var before = _version.Current;
            switch (which)
            {
                case "extra": _db.ExtraServices.Add(new ExtraService { Id = 5, Name = "Oven", IsActive = true }); break;
                case "plan": _db.Subscriptions.Add(new Subscription { Id = 2, Name = "Weekly", SubscriptionDays = 7, IsActive = true }); break;
                case "offer": _db.SpecialOffers.Add(new SpecialOffer { Id = 3, Name = "Welcome", IsActive = true }); break;
                case "blocked": _db.BlockedTimeSlots.Add(new BlockedTimeSlot { Id = 4, Date = DateTime.Today.AddDays(3), IsFullDay = true }); break;
            }
            await _db.SaveChangesAsync();

            Assert.NotEqual(before, _version.Current);
        }

        [Fact]
        public async Task Unrelated_saves_leave_the_stamp_alone()
        {
            var before = _version.Current;
            _db.Users.Add(new User { Id = 7, FirstName = "Casey", LastName = "Client", Email = "casey@example.invalid", Role = UserRole.Customer });
            await _db.SaveChangesAsync();
            await _db.SaveChangesAsync(); // nothing pending at all

            Assert.Equal(before, _version.Current);
        }

        [Fact]
        public void The_endpoint_answers_from_memory_and_is_never_cached()
        {
            var controller = (BookingController)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(BookingController));
            controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };

            var result = Assert.IsType<OkObjectResult>(controller.GetCatalogueVersion(_version));

            Assert.Contains(_version.Current, System.Text.Json.JsonSerializer.Serialize(result.Value));
            Assert.Equal("no-store", controller.Response.Headers.CacheControl.ToString());
        }

        [Fact]
        public void A_restart_reads_as_a_change()
        {
            Assert.NotEqual(new CatalogueVersion().Current, new CatalogueVersion().Current);
        }
    }
}
