using DreamCleaningBackend.Data;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// An audit row that cannot describe a change is worse than no row at all.
    ///
    /// Found 2026-08-31: an admin retyped "Cleaners Total Salary" on order #315, a field the
    /// server discards by design once cleaners are assigned. Nothing on the order moved except
    /// the housekeeping UpdatedAt stamp, so LogUpdateAsync wrote a row whose only changed field
    /// was UpdatedAt - which the Audits tab hides, leaving audit #2112 rendering as a literally
    /// blank row between #2113 and #2111.
    /// </summary>
    public class AuditNoOpUpdateTests : IDisposable
    {
        private readonly ApplicationDbContext _context;
        private readonly AuditService _audit;

        public AuditNoOpUpdateTests()
        {
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase($"audit-noop-{Guid.NewGuid()}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;

            _context = new ApplicationDbContext(options);
            _audit = new AuditService(
                _context,
                new HttpContextAccessor(),
                NullLogger<AuditService>.Instance);
        }

        public void Dispose() => _context.Dispose();

        private static Order Order(decimal subTotal, DateTime updatedAt, string status = "Active") => new()
        {
            Id = 315,
            UserId = 42,
            Status = status,
            ServiceDate = new DateTime(2026, 8, 14),
            OrderDate = new DateTime(2026, 8, 1),
            SubTotal = subTotal,
            Total = subTotal,
            UpdatedAt = updatedAt
        };

        [Fact]
        public async Task NoRowIsWritten_WhenOnlyUpdatedAtMoved()
        {
            var before = Order(289.50m, new DateTime(2026, 8, 20, 10, 0, 0));
            var after = Order(289.50m, new DateTime(2026, 8, 31, 15, 30, 0));

            await _audit.LogUpdateAsync(before, after);

            Assert.Empty(await _context.AuditLogs.ToListAsync());
        }

        [Fact]
        public async Task ARowIsStillWritten_WhenARealFieldMovedAlongsideUpdatedAt()
        {
            var before = Order(289.50m, new DateTime(2026, 8, 20, 10, 0, 0));
            var after = Order(310.00m, new DateTime(2026, 8, 31, 15, 30, 0));

            await _audit.LogUpdateAsync(before, after);

            var log = Assert.Single(await _context.AuditLogs.ToListAsync());
            Assert.Contains("SubTotal", log.ChangedFields);
        }

        [Fact]
        public async Task UpdatedAtIsStrippedFromTheChangedFieldList_SoItNeverPadsARealRow()
        {
            var before = Order(289.50m, new DateTime(2026, 8, 20, 10, 0, 0), "Active");
            var after = Order(289.50m, new DateTime(2026, 8, 31, 15, 30, 0), "Done");

            await _audit.LogUpdateAsync(before, after);

            var log = Assert.Single(await _context.AuditLogs.ToListAsync());
            Assert.Contains("Status", log.ChangedFields);
            Assert.DoesNotContain("UpdatedAt", log.ChangedFields);
        }

        [Fact]
        public async Task ANonOrderEntityFollowsTheSameRule()
        {
            var before = new User { Id = 7, Email = "a@b.com", UpdatedAt = new DateTime(2026, 8, 1) };
            var after = new User { Id = 7, Email = "a@b.com", UpdatedAt = new DateTime(2026, 8, 31) };

            await _audit.LogUpdateAsync(before, after);

            Assert.Empty(await _context.AuditLogs.ToListAsync());
        }
    }
}
