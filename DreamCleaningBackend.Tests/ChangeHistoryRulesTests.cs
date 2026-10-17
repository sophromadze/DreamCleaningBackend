using DreamCleaningBackend.Data;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services;
using DreamCleaningBackend.Services.ChangeSets;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// The pure rules behind the change history (2026-10): what is kept forever, and how a
    /// previewed change is compared with what the apply actually did. The end-to-end behaviour is
    /// covered on MariaDB by Mcp/ChangeSetIntegrationTests.
    /// </summary>
    public class ChangeHistoryRulesTests
    {
        [Fact]
        public void TheCatalogueAndExpenseHistoryIsNeverPruned_EverythingElseStillIs()
        {
            var old = DateTime.UtcNow.AddDays(-400);
            var rows = new[] { "ServiceType", "Service", "ServiceThreshold", "ServiceRateTier", "ExtraService", "PollQuestion", "Subscription", "Expense", "ExpenseCategory", "Order", "User" }
                .Select((t, i) => new AuditLog { Id = i + 1, EntityType = t, Action = "Update", CreatedAt = old })
                .Append(new AuditLog { Id = 99, EntityType = "Order", Action = "Update", CreatedAt = DateTime.UtcNow })
                .AsQueryable();

            var pruned = AuditLogCleanupService.Prunable(rows, DateTime.UtcNow.AddDays(-180)).Select(r => r.EntityType).ToList();

            Assert.Equal(new[] { "Order", "User" }, pruned);
        }

        [Fact]
        public void TheApplyComparison_MapsOnlyRealForeignKeysToNewRecords()
        {
            using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase($"normalize-{Guid.NewGuid()}").Options);

            // A dry run created expense #5 through connector grant #5; the apply created #6, same grant.
            static AuditLog Created(int id) => new()
            {
                EntityType = nameof(Expense), EntityId = id, Action = "Create",
                NewValues = JsonConvert.SerializeObject(new { Id = id, Name = "Gloves", Amount = 9.5m, CategoryId = 1, McpGrantId = 5, CreatedAt = DateTime.UtcNow })
            };

            var preview = ChangeSetService.Normalize(new List<AuditLog> { Created(5) }, db.Model);
            var apply = ChangeSetService.Normalize(new List<AuditLog> { Created(6) }, db.Model);

            // The grant id equals the dry run's new id by coincidence; it must stay a plain number.
            Assert.Equal(preview, apply);
            Assert.Contains("McpGrantId=5", preview.Single());
        }

        [Fact]
        public void AForeignKeyToARecordCreatedInTheSameChange_ComparesByPosition()
        {
            using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase($"normalize-{Guid.NewGuid()}").Options);

            static List<AuditLog> Run(int serviceId, int tierId) => new()
            {
                new() { EntityType = nameof(Service), EntityId = serviceId, Action = "Create",
                        NewValues = JsonConvert.SerializeObject(new { Id = serviceId, Name = "Bathrooms", ServiceTypeId = 1 }) },
                new() { EntityType = nameof(ServiceRateTier), EntityId = tierId, Action = "Create",
                        NewValues = JsonConvert.SerializeObject(new { Id = tierId, ServiceId = serviceId, FromQuantity = 0m }) }
            };

            Assert.Equal(ChangeSetService.Normalize(Run(10, 20), db.Model), ChangeSetService.Normalize(Run(11, 21), db.Model));
            Assert.NotEqual(ChangeSetService.Normalize(Run(10, 20), db.Model).Last(),
                ChangeSetService.Normalize(new List<AuditLog> { Run(10, 20)[1] }, db.Model).Single());
        }

        [Fact]
        public void AFingerprintIgnoresOnlyTheHousekeepingTimestamp()
        {
            var a = JsonConvert.SerializeObject(new { Id = 1, Cost = 22.5m, UpdatedAt = DateTime.UtcNow });
            var b = JsonConvert.SerializeObject(new { Id = 1, Cost = 22.5m, UpdatedAt = DateTime.UtcNow.AddHours(1) });
            var c = JsonConvert.SerializeObject(new { Id = 1, Cost = 25m, UpdatedAt = DateTime.UtcNow });

            Assert.Equal(ChangeSetService.FingerprintOf(a), ChangeSetService.FingerprintOf(b));
            Assert.NotEqual(ChangeSetService.FingerprintOf(a), ChangeSetService.FingerprintOf(c));
        }
    }
}
