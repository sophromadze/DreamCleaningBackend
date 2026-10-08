using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// Outgoing Payments, 2026-10: Orders / Unpaid cleaners / Paid cleaners.
    ///
    /// Every payment — one line, a whole order, or several cleaners at once — is a payout record
    /// that keeps exactly which order lines it covered, and the three tabs are three readings of
    /// those records. What is owed always comes from the payroll calculator.
    ///
    /// Fixture: Ana (418) worked order 900 alone (5h × $21 = $105.00) and order 901 with Ben (419)
    /// (4h across two = 2h each × $21 = $42.00 apiece).
    /// </summary>
    public class CleanerPayoutTests : IDisposable
    {
        private const int AdminId = 7;
        private readonly ApplicationDbContext _context;
        private readonly RecordingAuditService _audit = new();

        public CleanerPayoutTests()
        {
            _context = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase($"cleaner-payouts-{Guid.NewGuid()}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options);
        }

        public void Dispose() => _context.Dispose();

        private OutgoingPaymentService Service() =>
            new(_context, _audit, new CleanerPayrollEditService(_context, _audit));

        private async Task SeedAsync()
        {
            _context.Users.Add(new User
            {
                Id = AdminId, Email = "owner@example.com", FirstName = "Olga", LastName = "Owner",
                PasswordHash = "x", PasswordSalt = "x"
            });
            _context.Users.Add(new User
            {
                Id = 42, Email = "maia@example.com", FirstName = "Maia", LastName = "Kv",
                PasswordHash = "x", PasswordSalt = "x"
            });
            _context.ServiceTypes.Add(new ServiceType { Id = 1, Name = "Regular Cleaning", Description = "" });

            var ana = new Cleaner { Id = 418, FirstName = "Ana", LastName = "Reyes", Email = "ana@example.com", IsActive = true, PaymentMethod = CleanerPaymentMethod.Zelle };
            var ben = new Cleaner { Id = 419, FirstName = "Ben", LastName = "Okafor", Email = "ben@example.com", IsActive = true };
            _context.Cleaners.AddRange(ana, ben);

            _context.Orders.Add(NewOrder(900, new DateTime(2026, 8, 14), totalMinutes: 300, maids: 1));
            _context.Orders.Add(NewOrder(901, new DateTime(2026, 8, 20), totalMinutes: 240, maids: 2));

            _context.OrderCleaners.AddRange(
                new OrderCleaner { Id = 10, OrderId = 900, CleanerId = 418, Cleaner = ana },
                new OrderCleaner { Id = 11, OrderId = 901, CleanerId = 418, Cleaner = ana },
                new OrderCleaner { Id = 12, OrderId = 901, CleanerId = 419, Cleaner = ben });

            await _context.SaveChangesAsync();
        }

        private static Order NewOrder(int id, DateTime serviceDate, decimal totalMinutes, int maids) => new()
        {
            Id = id,
            UserId = 42,
            Status = OrderStatuses.Done,
            ServiceDate = serviceDate,
            OrderDate = serviceDate.AddDays(-10),
            SubTotal = 300m,
            Total = 326.63m,
            TotalDuration = totalMinutes,
            MaidsCount = maids,
            CleanerHourlyRate = 21m,
            TotalRefundedAmount = 0m,
            ServiceTypeId = 1,
            ContactFirstName = "Maia",
            ContactLastName = "Kv",
            ContactEmail = "maia@example.com",
            ServiceAddress = "1 Test St",
            City = "Brooklyn",
            State = "New York",
            ZipCode = "11201"
        };

        private static PayCleanersDto PayAll(UnpaidCleanersDto unpaid, string key = "confirm-key-0001", Func<UnpaidCleanerDto, bool>? which = null) => new()
        {
            IdempotencyKey = key,
            Reference = "ZELLE-77",
            Note = "August run",
            Cleaners = unpaid.Cleaners.Where(which ?? (_ => true)).Select(c => new PayCleanerSelectionDto
            {
                CleanerId = c.CleanerId,
                Lines = c.Lines.Select(l => new ExpectedPayoutLineDto { OrderCleanerId = l.OrderCleanerId, Amount = l.Outstanding }).ToList()
            }).ToList()
        };

        // ── Unpaid cleaners ───────────────────────────────────────────────────────────────────

        [Fact]
        public async Task UnpaidCleanersListsWhatEachIsOwed_FromThePayrollCalculator()
        {
            await SeedAsync();
            var unpaid = await Service().GetUnpaidCleanersAsync();

            var ana = unpaid.Cleaners.Single(c => c.CleanerId == 418);
            var ben = unpaid.Cleaners.Single(c => c.CleanerId == 419);

            Assert.Equal(147.00m, ana.TotalOutstanding);     // 105.00 + 42.00
            Assert.Equal(new[] { 900, 901 }, ana.Lines.Select(l => l.OrderId));   // oldest first
            Assert.Equal(42.00m, ben.TotalOutstanding);
            Assert.Equal(189.00m, unpaid.TotalOutstanding);
            Assert.Equal(CleanerPaymentMethod.Zelle, ana.PaymentMethod);
        }

        [Fact]
        public async Task PayingCleanersRecordsOnePayoutEachWithTheLinesItCovered()
        {
            await SeedAsync();
            var service = Service();

            var result = await service.PayCleanersAsync(PayAll(await service.GetUnpaidCleanersAsync()), AdminId);

            Assert.Equal(2, result.CleanerCount);
            Assert.Equal(189.00m, result.TotalPaid);
            Assert.False(result.AlreadyRecorded);

            var payouts = await _context.CleanerPayouts.Include(p => p.Lines).OrderBy(p => p.CleanerId).ToListAsync(cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(2, payouts.Count);

            var ana = payouts[0];
            Assert.Equal("Ana Reyes", ana.CleanerName);
            Assert.Equal(147.00m, ana.Amount);
            Assert.Equal(new[] { 900, 901 }, ana.Lines.OrderBy(l => l.OrderId).Select(l => l.OrderId));
            Assert.Equal("ZELLE-77", ana.Reference);
            Assert.Equal(AdminId, ana.CreatedByUserId);
            Assert.Equal("Olga Owner", ana.CreatedByName);
            // No method chosen: each cleaner's own saved one.
            Assert.Equal(CleanerPaymentMethod.Zelle, ana.PaidVia);
            Assert.Null(payouts[1].PaidVia);

            // The Orders tab reads the same state: every line on both orders is settled.
            var orders = await service.GetAsync(new OutgoingPaymentQuery());
            Assert.All(orders.Orders, o => Assert.True(o.IsFullyPaid));
            Assert.Empty((await service.GetUnpaidCleanersAsync()).Cleaners);

            // One audit row per line, naming its payout.
            Assert.Equal(3, _audit.Actions.Count(a => a.Action == "PayoutRecorded"));
        }

        [Fact]
        public async Task TheSameConfirmationTwiceRecordsTheMoneyOnce()
        {
            await SeedAsync();
            var service = Service();
            var dto = PayAll(await service.GetUnpaidCleanersAsync());

            await service.PayCleanersAsync(dto, AdminId);
            var again = await service.PayCleanersAsync(dto, AdminId);

            Assert.True(again.AlreadyRecorded);
            Assert.Equal(2, await _context.CleanerPayouts.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        }

        [Fact]
        public async Task AStaleConfirmationPaysNothing()
        {
            await SeedAsync();
            var service = Service();
            var stale = PayAll(await service.GetUnpaidCleanersAsync(), key: "first-dialog-01");

            // Somebody pays Ana's order-900 line from the Orders tab in the meantime.
            await service.MarkCleanerPaidAsync(900, 10, new MarkCleanerPaidDto(), AdminId);

            stale.IdempotencyKey = "second-dialog-1";
            await Assert.ThrowsAsync<CleanerPayoutConflictException>(() => service.PayCleanersAsync(stale, AdminId));

            // Only the Orders-tab payment exists; Ben was not paid either — all or nothing.
            Assert.Single(await _context.CleanerPayouts.ToListAsync(cancellationToken: TestContext.Current.CancellationToken));
            Assert.False((await _context.OrderCleaners.SingleAsync(oc => oc.Id == 12, cancellationToken: TestContext.Current.CancellationToken)).IsPaid);
        }

        [Fact]
        public async Task AnAmountThatDoesNotMatchWhatIsOwedIsRefused()
        {
            await SeedAsync();
            var service = Service();
            var dto = PayAll(await service.GetUnpaidCleanersAsync(), which: c => c.CleanerId == 419);
            dto.Cleaners[0].Lines[0].Amount = 40.00m;

            await Assert.ThrowsAsync<CleanerPayoutConflictException>(() => service.PayCleanersAsync(dto, AdminId));
            Assert.Empty(await _context.CleanerPayouts.ToListAsync(cancellationToken: TestContext.Current.CancellationToken));
        }

        [Fact]
        public async Task APaymentDateInTheFutureIsRefused()
        {
            await SeedAsync();
            var service = Service();
            var dto = PayAll(await service.GetUnpaidCleanersAsync());
            dto.PaidOn = DateTime.UtcNow.Date.AddDays(5);

            await Assert.ThrowsAsync<InvalidOperationException>(() => service.PayCleanersAsync(dto, AdminId));
        }

        // ── The tabs stay consistent ──────────────────────────────────────────────────────────

        [Fact]
        public async Task PayingALineOnTheOrdersTabTakesItOffUnpaidAndPutsItInHistory()
        {
            await SeedAsync();
            var service = Service();

            await service.MarkCleanerPaidAsync(901, 12, new MarkCleanerPaidDto { PaidVia = CleanerPaymentMethod.Cash }, AdminId);

            var unpaid = await service.GetUnpaidCleanersAsync();
            Assert.DoesNotContain(unpaid.Cleaners, c => c.CleanerId == 419);

            var history = await service.GetPayoutHistoryAsync(new CleanerPayoutHistoryQuery());
            var payout = Assert.Single(history.Payouts);
            Assert.Equal("Ben Okafor", payout.CleanerName);
            Assert.Equal(42.00m, payout.Amount);
            Assert.Equal(CleanerPaymentMethod.Cash, payout.PaidVia);
            Assert.Equal(901, Assert.Single(payout.Lines).OrderId);
        }

        [Fact]
        public async Task NewWorkAfterAPayoutBringsTheCleanerBackForTheNewAmountOnly()
        {
            await SeedAsync();
            var service = Service();
            await service.PayCleanersAsync(PayAll(await service.GetUnpaidCleanersAsync()), AdminId);

            // Ana reports a longer job on 900: 5h → 6h. Only the hour is owed now.
            var order = await _context.Orders.SingleAsync(o => o.Id == 900, cancellationToken: TestContext.Current.CancellationToken);
            order.TotalDuration = 360;
            await _context.SaveChangesAsync(TestContext.Current.CancellationToken);

            var unpaid = await service.GetUnpaidCleanersAsync();
            var ana = Assert.Single(unpaid.Cleaners);
            var line = Assert.Single(ana.Lines);
            Assert.True(line.IsTopUp);
            Assert.Equal(105.00m, line.AlreadyPaid);
            Assert.Equal(21.00m, line.Outstanding);

            await service.PayCleanersAsync(PayAll(unpaid, key: "top-up-key-0001"), AdminId);
            var topUp = await _context.CleanerPayouts.Include(p => p.Lines).OrderByDescending(p => p.Id).FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(21.00m, topUp.Amount);
            Assert.True(Assert.Single(topUp.Lines).IsTopUp);
            Assert.Equal(126.00m, (await _context.OrderCleaners.SingleAsync(oc => oc.Id == 10, cancellationToken: TestContext.Current.CancellationToken)).PaidAmount);
        }

        // ── Reverting ─────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task RevertingAPayoutMovesItsAmountsBackToUnpaid_AndKeepsTheHistory()
        {
            await SeedAsync();
            var service = Service();
            await service.PayCleanersAsync(PayAll(await service.GetUnpaidCleanersAsync()), AdminId);
            var anaPayout = await _context.CleanerPayouts.SingleAsync(p => p.CleanerId == 418, cancellationToken: TestContext.Current.CancellationToken);

            var reverted = await service.RevertPayoutAsync(anaPayout.Id, "Sent to the wrong account", AdminId);

            Assert.NotNull(reverted);
            Assert.Equal("Reverted", reverted!.Status);
            Assert.Equal(0m, reverted.ActiveAmount);
            Assert.Equal(147.00m, reverted.Amount);
            Assert.Equal("Sent to the wrong account", reverted.RevertReason);
            Assert.False(reverted.CanRevert);

            // Back on Unpaid, and the Orders tab agrees.
            var ana = Assert.Single((await service.GetUnpaidCleanersAsync()).Cleaners);
            Assert.Equal(147.00m, ana.TotalOutstanding);
            var assignment = await _context.OrderCleaners.SingleAsync(oc => oc.Id == 10, cancellationToken: TestContext.Current.CancellationToken);
            Assert.False(assignment.IsPaid);
            Assert.Null(assignment.PaidAmount);

            // Never deleted.
            Assert.Equal(2, await _context.CleanerPayouts.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal(2, _audit.Actions.Count(a => a.Action == "PayoutReversed"));
            Assert.All(_audit.Actions.Where(a => a.Action == "PayoutReversed"), a => Assert.Equal(AdminId, a.ActingUserId));

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.RevertPayoutAsync(anaPayout.Id, "again", AdminId));
        }

        [Fact]
        public async Task ARevertNeedsAReason()
        {
            await SeedAsync();
            var service = Service();
            await service.PayCleanersAsync(PayAll(await service.GetUnpaidCleanersAsync()), AdminId);
            var payout = await _context.CleanerPayouts.FirstAsync(cancellationToken: TestContext.Current.CancellationToken);

            await Assert.ThrowsAsync<InvalidOperationException>(() => service.RevertPayoutAsync(payout.Id, "  ", AdminId));
        }

        [Fact]
        public async Task UndoOnTheOrdersTabRevertsThatLineInsideItsPayout()
        {
            await SeedAsync();
            var service = Service();
            await service.PayCleanersAsync(PayAll(await service.GetUnpaidCleanersAsync()), AdminId);

            await service.UndoCleanerPaymentAsync(900, 10, AdminId);

            var history = await service.GetPayoutHistoryAsync(new CleanerPayoutHistoryQuery { CleanerId = 418 });
            var payout = Assert.Single(history.Payouts);
            Assert.Equal("PartiallyReverted", payout.Status);
            Assert.Equal(42.00m, payout.ActiveAmount);
            Assert.True(payout.Lines.Single(l => l.OrderId == 900).IsReverted);

            var unpaidAna = Assert.Single((await service.GetUnpaidCleanersAsync()).Cleaners);
            Assert.Equal(105.00m, unpaidAna.TotalOutstanding);
        }

        // ── History filters ───────────────────────────────────────────────────────────────────

        [Fact]
        public async Task HistoryFiltersByDayPaidAndByCleaner_AndTotalsWhatStillStands()
        {
            await SeedAsync();
            var service = Service();
            var unpaid = await service.GetUnpaidCleanersAsync();

            var early = PayAll(unpaid, key: "early-key-00001", which: c => c.CleanerId == 419);
            early.PaidOn = new DateTime(2026, 9, 1);
            await service.PayCleanersAsync(early, AdminId);

            var late = PayAll(unpaid, key: "late-key-000001", which: c => c.CleanerId == 418);
            late.PaidOn = new DateTime(2026, 9, 20);
            await service.PayCleanersAsync(late, AdminId);

            var september = await service.GetPayoutHistoryAsync(new CleanerPayoutHistoryQuery
            {
                From = new DateTime(2026, 9, 1), To = new DateTime(2026, 9, 10)
            });
            Assert.Equal("Ben Okafor", Assert.Single(september.Payouts).CleanerName);
            Assert.Equal(42.00m, september.TotalPaid);

            var all = await service.GetPayoutHistoryAsync(new CleanerPayoutHistoryQuery());
            Assert.Equal(new[] { 418, 419 }, all.Payouts.Select(p => p.CleanerId));   // newest first
            Assert.Equal(189.00m, all.TotalPaid);
            Assert.Equal(new[] { "Ana Reyes", "Ben Okafor" }, all.CleanerOptions.Select(o => o.Name));
        }

        // ── The startup import ────────────────────────────────────────────────────────────────

        [Fact]
        public async Task TheImportTurnsExistingPaidLinesIntoHistory_AndSettlesTheAugustBackfill()
        {
            await SeedAsync();

            // Order 900: marked paid by the August 2026 migration — no amount on record.
            var legacy = await _context.OrderCleaners.SingleAsync(oc => oc.Id == 10, cancellationToken: TestContext.Current.CancellationToken);
            legacy.IsPaid = true;
            legacy.PaymentNote = CleanerPayoutBackfill.LegacyNote;

            // Order 901, Ben: paid through the page before payout records, $42.00 on 2026-09-02.
            var recorded = await _context.OrderCleaners.SingleAsync(oc => oc.Id == 12, cancellationToken: TestContext.Current.CancellationToken);
            recorded.IsPaid = true;
            recorded.PaidAmount = 42.00m;
            recorded.PaidAt = new DateTime(2026, 9, 2, 15, 0, 0, DateTimeKind.Utc);
            recorded.PaidByUserId = AdminId;
            recorded.PaidVia = CleanerPaymentMethod.Cash;
            await _context.SaveChangesAsync(TestContext.Current.CancellationToken);

            var imported = await CleanerPayoutBackfill.RunAsync(_context, TestContext.Current.CancellationToken);
            Assert.Equal(2, imported);

            var payouts = await _context.CleanerPayouts.Include(p => p.Lines).OrderBy(p => p.CleanerId).ToListAsync(cancellationToken: TestContext.Current.CancellationToken);
            Assert.All(payouts, p => Assert.True(p.IsImported));

            // The August line is settled at what the payroll calculator says it was worth.
            Assert.Equal(105.00m, payouts[0].Amount);
            Assert.Equal(CleanerPayoutBackfill.LegacyNote, payouts[0].Note);
            Assert.Equal(new DateTime(2026, 8, 14), payouts[0].PaidOn);
            Assert.Equal(105.00m, (await _context.OrderCleaners.SingleAsync(oc => oc.Id == 10, cancellationToken: TestContext.Current.CancellationToken)).PaidAmount);

            Assert.Equal(42.00m, payouts[1].Amount);
            Assert.Equal(new DateTime(2026, 9, 2), payouts[1].PaidOn);
            Assert.Equal("Olga Owner", payouts[1].CreatedByName);
            Assert.Equal(CleanerPaymentMethod.Cash, payouts[1].PaidVia);

            // Settled, so neither shows up as owed — only Ana's unpaid order-901 line does.
            var unpaid = await Service().GetUnpaidCleanersAsync();
            var ana = Assert.Single(unpaid.Cleaners);
            Assert.Equal(901, Assert.Single(ana.Lines).OrderId);

            // Idempotent.
            Assert.Equal(0, await CleanerPayoutBackfill.RunAsync(_context, TestContext.Current.CancellationToken));
        }
    }
}
