using System.Text.Json;
using DreamCleaningBackend.Controllers;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services;
using DreamCleaningBackend.Tests.Billing;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// Deleting a service that holds an included-amount rule on ITSELF (Levels: "when Levels is 1,
    /// 1 is included"). That rule depends on nothing else, so it must not block the delete; it goes
    /// with the service in the same transaction. A rule on ANOTHER service still blocks, exactly
    /// as before — removing a source would leave that service billing from zero.
    /// </summary>
    public class ServiceDeleteSelfReferenceTests
    {
        private static AdminCatalogController Controller(ApplicationDbContext context) =>
            new(context, new AuditService(context, new HttpContextAccessor(), NullLogger<AuditService>.Instance),
                NullLogger<AdminCatalogController>.Instance, null!)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
            };

        private static string? Message(ActionResult result) =>
            result is BadRequestObjectResult bad
                ? JsonSerializer.SerializeToElement(bad.Value).GetProperty("message").GetString()
                : null;

        /// <summary>A service type with Bedrooms and Levels; Levels carries the self-rule.</summary>
        internal static async Task<(int BedroomsId, int LevelsId)> SeedAsync(ApplicationDbContext context)
        {
            var type = new ServiceType { Name = $"Self-ref type {Guid.NewGuid():N}", BasePrice = 90, TimeDuration = 120, IsActive = true };
            context.ServiceTypes.Add(type);
            await context.SaveChangesAsync();

            var bedrooms = new Service { Name = "Bedrooms", ServiceKey = "bedrooms", Cost = 22.5m, TimeDuration = 30, ServiceTypeId = type.Id, IsActive = true };
            var levels = new Service
            {
                Name = "Levels", ServiceKey = "levels", Cost = 35m, TimeDuration = 25, ServiceTypeId = type.Id,
                MinValue = 1, MaxValue = 4, ChargeAboveThreshold = true, IsActive = true
            };
            context.Services.AddRange(bedrooms, levels);
            await context.SaveChangesAsync();

            context.ServiceThresholds.Add(new ServiceThreshold
            {
                ServiceId = levels.Id, SourceServiceId = levels.Id, SourceQuantity = 1, IncludedQuantity = 1, CreatedAt = DateTime.UtcNow
            });
            await context.SaveChangesAsync();
            return (bedrooms.Id, levels.Id);
        }

        // One store, many contexts: the delete runs on a FRESH context, as a real request does, so
        // nothing seeded is already tracked (tracked rows would be cascaded by EF regardless).
        private readonly DbContextOptions<ApplicationDbContext> _options =
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase($"service-delete-{Guid.NewGuid()}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;

        private ApplicationDbContext InMemory() => new(_options);

        [Fact]
        public async Task OnlySelfReferenced_IsDeletedTogetherWithItsOwnRules()
        {
            int levelsId;
            await using (var seed = InMemory())
                (_, levelsId) = await SeedAsync(seed);

            await using var context = InMemory();
            var result = await Controller(context).DeleteService(levelsId);

            Assert.IsType<OkResult>(result);
            Assert.False(await context.Services.AnyAsync(s => s.Id == levelsId));
            Assert.False(await context.ServiceThresholds.AnyAsync(t => t.ServiceId == levelsId || t.SourceServiceId == levelsId));
        }

        [Fact]
        public async Task ReferencedByAnotherService_IsStillBlocked_AndNothingIsRemoved()
        {
            int bedroomsId, levelsId;
            await using (var seed = InMemory())
            {
                (bedroomsId, levelsId) = await SeedAsync(seed);
                seed.ServiceThresholds.Add(new ServiceThreshold
                {
                    ServiceId = bedroomsId, SourceServiceId = levelsId, SourceQuantity = 2, IncludedQuantity = 1, CreatedAt = DateTime.UtcNow
                });
                await seed.SaveChangesAsync();
            }

            await using var context = InMemory();
            var result = await Controller(context).DeleteService(levelsId);

            var message = Message(result);
            Assert.NotNull(message);
            // Only Bedrooms is named: the self-rule is not reported as a dependency.
            Assert.Contains("source for Bedrooms.", message);
            Assert.True(await context.Services.AnyAsync(s => s.Id == levelsId));
            Assert.Equal(2, await context.ServiceThresholds.CountAsync(t => t.SourceServiceId == levelsId));
        }
    }

    /// <summary>
    /// The same delete against a REAL MariaDB: the in-memory provider enforces no foreign keys, and
    /// the self-rule's SourceServiceId FK is Restrict. Skipped unless <c>DC_TEST_MARIADB</c> is set.
    /// </summary>
    public class ServiceDeleteSelfReferenceMariaDbTests : IClassFixture<MariaDbDatabase>
    {
        private readonly MariaDbDatabase _db;
        public ServiceDeleteSelfReferenceMariaDbTests(MariaDbDatabase db) => _db = db;

        [MariaDbFact]
        public async Task OnlySelfReferenced_DeletesInOneTransactionDespiteTheRestrictForeignKey()
        {
            int levelsId;
            await using (var seed = _db.CreateContext())
                (_, levelsId) = await ServiceDeleteSelfReferenceTests.SeedAsync(seed);

            await using (var context = _db.CreateContext())
            {
                var controller = new AdminCatalogController(context,
                    new AuditService(context, new HttpContextAccessor(), NullLogger<AuditService>.Instance),
                    NullLogger<AdminCatalogController>.Instance, null!)
                {
                    ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
                };
                Assert.IsType<OkResult>(await controller.DeleteService(levelsId));
            }

            await using var check = _db.CreateContext();
            Assert.False(await check.Services.AnyAsync(s => s.Id == levelsId));
            Assert.False(await check.ServiceThresholds.AnyAsync(t => t.ServiceId == levelsId || t.SourceServiceId == levelsId));
        }

        [MariaDbFact]
        public async Task ReferencedByAnotherService_IsStillBlocked()
        {
            int bedroomsId, levelsId;
            await using (var seed = _db.CreateContext())
            {
                (bedroomsId, levelsId) = await ServiceDeleteSelfReferenceTests.SeedAsync(seed);
                seed.ServiceThresholds.Add(new ServiceThreshold
                {
                    ServiceId = bedroomsId, SourceServiceId = levelsId, SourceQuantity = 2, IncludedQuantity = 1, CreatedAt = DateTime.UtcNow
                });
                await seed.SaveChangesAsync();
            }

            await using (var context = _db.CreateContext())
            {
                var controller = new AdminCatalogController(context,
                    new AuditService(context, new HttpContextAccessor(), NullLogger<AuditService>.Instance),
                    NullLogger<AdminCatalogController>.Instance, null!)
                {
                    ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
                };
                Assert.IsType<BadRequestObjectResult>(await controller.DeleteService(levelsId));
            }

            await using var check = _db.CreateContext();
            Assert.True(await check.Services.AnyAsync(s => s.Id == levelsId));
            Assert.Equal(2, await check.ServiceThresholds.CountAsync(t => t.SourceServiceId == levelsId));
        }
    }
}
