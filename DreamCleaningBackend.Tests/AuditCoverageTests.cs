using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// The audit-coverage sweep of 2026-08-31: every admin action that mutates persisted state
    /// has to leave a row, and the rows it leaves have to be readable and safe to leave alone.
    ///
    /// These tests cover the WRITE paths added by that sweep — mostly through
    /// <see cref="AuditService.LogActionAsync"/>, the general-purpose writer for actions that are
    /// EVENTS rather than field edits on one EF row (a cleaner was paid, a refund was issued, a
    /// change request was rejected).
    ///
    /// The two rules worth pinning, because getting either wrong is silent:
    ///  - a pseudo-entity must be UNDO-BLOCKED, or an admin gets an Undo button that resolves to
    ///    the wrong record or fails on click;
    ///  - changed fields must be DERIVED from the payloads, because hand-listing them at 120 call
    ///    sites is exactly how the partial-snapshot defect got in.
    /// </summary>
    public class AuditCoverageTests : IDisposable
    {
        private readonly ApplicationDbContext _context;
        private readonly AuditService _audit;

        public AuditCoverageTests()
        {
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase($"audit-coverage-{Guid.NewGuid()}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;

            _context = new ApplicationDbContext(options);
            _audit = new AuditService(_context, new HttpContextAccessor(), NullLogger<AuditService>.Instance);
        }

        public void Dispose() => _context.Dispose();

        private async Task<AuditLog> SingleLogAsync() =>
            Assert.Single(await _context.AuditLogs.ToListAsync());

        private static List<string> ChangedFields(AuditLog log) =>
            JsonConvert.DeserializeObject<List<string>>(log.ChangedFields ?? "[]") ?? new List<string>();

        private static JObject Values(string? json) =>
            JsonConvert.DeserializeObject<JObject>(json ?? "{}") ?? new JObject();

        // ===== LogActionAsync: the general-purpose writer =====

        [Fact]
        public async Task LogAction_DerivesChangedFields_FromTheTwoPayloads()
        {
            await _audit.LogActionAsync(
                AuditEntityTypes.OrderCleanerHourlyRate, 315, "Update",
                new { CleanerHourlyRate = 21m, CleanerTotalSalary = 210m },
                new { CleanerHourlyRate = 25m, CleanerTotalSalary = 250m });

            var log = await SingleLogAsync();
            var fields = ChangedFields(log);

            Assert.Contains("CleanerHourlyRate", fields);
            Assert.Contains("CleanerTotalSalary", fields);
        }

        [Fact]
        public async Task LogAction_NamesOnlyTheFieldsThatMoved()
        {
            // A form that posts every field on every save must not produce a wall of rows saying
            // nothing changed — that is what made the old audit expansions unreadable.
            await _audit.LogActionAsync(
                AuditEntityTypes.RewardSetting, 0, "Update",
                new { WelcomeBonus = "100", ReviewBonus = "50" },
                new { WelcomeBonus = "100", ReviewBonus = "75" });

            var fields = ChangedFields(await SingleLogAsync());

            Assert.Equal(new[] { "ReviewBonus" }, fields);
        }

        [Fact]
        public async Task LogAction_WritesNoRow_WhenAnUpdateChangedNothing()
        {
            // Same rule as LogUpdateAsync: a row that cannot describe a change is worse than none.
            await _audit.LogActionAsync(
                AuditEntityTypes.UserCommunicationPreference, 42, "Update",
                new { CanReceiveEmails = true },
                new { CanReceiveEmails = true });

            Assert.Empty(await _context.AuditLogs.ToListAsync());
        }

        [Fact]
        public async Task LogAction_StillWritesARow_ForANonUpdateActionWithAnIdenticalPayload()
        {
            // "Marked paid" is meaningful even when the payload matches the last one — the event
            // is the record, not the diff.
            await _audit.LogActionAsync(
                AuditEntityTypes.CleanerPayout, 315, "PayoutRecorded",
                null,
                new { Cleaner = "Ana Reyes", PaidAmount = 94.50m });

            var log = await SingleLogAsync();
            Assert.Equal("PayoutRecorded", log.Action);
        }

        [Fact]
        public async Task LogAction_KeepsTheWholePayload_WhenOnlyOneSideIsSupplied()
        {
            await _audit.LogActionAsync(
                AuditEntityTypes.OrderRefundAction, 296, "RefundIssued",
                null,
                new { AmountRefunded = 120.00m, Reason = "Customer cancelled", CustomerEmailSent = true });

            var log = await SingleLogAsync();
            var fields = ChangedFields(log);

            Assert.Contains("AmountRefunded", fields);
            Assert.Contains("Reason", fields);
            Assert.Contains("CustomerEmailSent", fields);
            Assert.Equal(120.00m, Values(log.NewValues)["AmountRefunded"]!.Value<decimal>());
        }

        [Fact]
        public async Task LogAction_HonoursAnExplicitActingUser_ForServiceLayerCallers()
        {
            // Payroll writes happen in a service that may not have the ambient claim to hand.
            await _audit.LogActionAsync(
                AuditEntityTypes.CleanerPayout, 315, "PayoutRecorded", null,
                new { Cleaner = "Ana Reyes" }, actingUserId: 7);

            Assert.Equal(7, (await SingleLogAsync()).UserId);
        }

        [Fact]
        public async Task LogAction_StripsTheHousekeepingTimestamp_LikeLogUpdateDoes()
        {
            await _audit.LogActionAsync(
                AuditEntityTypes.OrderAdminNote, 296, "Update",
                new { Notes = "old", UpdatedAt = new DateTime(2026, 8, 1) },
                new { Notes = "new", UpdatedAt = new DateTime(2026, 8, 31) });

            var fields = ChangedFields(await SingleLogAsync());

            Assert.Contains("Notes", fields);
            Assert.DoesNotContain("UpdatedAt", fields);
        }

        // ===== The undo block list =====

        [Fact]
        public void EveryMoneyMovingPseudoEntity_IsUndoBlocked()
        {
            // Replaying any of these would flip a payment flag without anyone deciding to, or
            // delete the record of money that has already left the business.
            foreach (var type in new[]
            {
                AuditEntityTypes.CleanerPayout,
                AuditEntityTypes.CleanerPayrollOverride,
                AuditEntityTypes.OrderCleanerHourlyRate,
                AuditEntityTypes.OrderRefundAction,
                AuditEntityTypes.OrderPaymentAction,
                AuditEntityTypes.OrderTransferAction,
            })
            {
                Assert.True(AuditEntityTypes.UndoBlockedEntityTypes.Contains(type), $"{type} must be undo-blocked");
            }
        }

        [Fact]
        public void EveryBlockedTypeCarriesAReason_BecauseTheTooltipRendersIt()
        {
            // A bare dash in the Undo column told an admin nothing and read as a broken page, so
            // the block list stores a reason per entry and the UI shows it. An entry with an empty
            // reason would put an empty tooltip on a disabled button.
            foreach (var kvp in AuditEntityTypes.UndoBlockedReasons)
            {
                Assert.False(string.IsNullOrWhiteSpace(kvp.Value), $"{kvp.Key} has no refusal reason");
            }
        }

        [Fact]
        public void LoyaltyDiscount_IsDeliberatelyNotBlocked()
        {
            // It is a pseudo-entity, but UndoAsync has an explicit hand-written path that writes
            // the four loyalty columns back onto the User row. Blocking it would remove a working
            // undo.
            Assert.DoesNotContain(
                AuditEntityTypes.UserLoyaltyDiscount,
                AuditEntityTypes.UndoBlockedEntityTypes);
        }

        [Fact]
        public void ResolveUndoBlockedReason_ExplainsAPseudoEntity()
        {
            var reason = AuditEntityTypes.ResolveUndoBlockedReason(new AuditLog
            {
                EntityType = AuditEntityTypes.CleanerPayout,
                Action = "PayoutRecorded",
            });

            Assert.False(string.IsNullOrWhiteSpace(reason));
        }

        [Fact]
        public void ResolveUndoBlockedReason_ExplainsANonFieldAction()
        {
            // An event verb has no OldValues to write back, so the button must be off — and say so
            // rather than failing on click.
            var reason = AuditEntityTypes.ResolveUndoBlockedReason(new AuditLog
            {
                EntityType = "Order",
                Action = "SomethingHappened",
            });

            Assert.Contains("nothing to write back", reason);
        }

        [Fact]
        public void ResolveUndoBlockedReason_ReturnsNull_ForAnOrdinaryReversibleUpdate()
        {
            var reason = AuditEntityTypes.ResolveUndoBlockedReason(new AuditLog
            {
                EntityType = "Expense",
                Action = "Update",
                ChangedFields = JsonConvert.SerializeObject(new[] { "Amount" }),
                OldValues = JsonConvert.SerializeObject(new { Amount = 50m }),
                NewValues = JsonConvert.SerializeObject(new { Amount = 60m }),
            });

            Assert.Null(reason);
        }

        [Fact]
        public void ResolveUndoBlockedReason_StillRefusesAFabricatedBeforeImage()
        {
            // The Phase 1 refusal was server-side only, so the button looked enabled and failed on
            // click. It now reaches the UI through the same resolver.
            var reason = AuditEntityTypes.ResolveUndoBlockedReason(new AuditLog
            {
                EntityType = "Order",
                Action = "Update",
                ChangedFields = JsonConvert.SerializeObject(new[] { "ServiceDate", "IsHidden" }),
                OldValues = JsonConvert.SerializeObject(new { ServiceDate = default(DateTime), IsHidden = false }),
                NewValues = JsonConvert.SerializeObject(new { ServiceDate = new DateTime(2026, 8, 14), IsHidden = true }),
            });

            Assert.Equal(AuditSnapshot.FabricatedBeforeImageMessage, reason);
        }

        // ===== Payroll: the screen where money leaves the business =====

        [Fact]
        public async Task MarkingACleanerPaid_RecordsWhoWasPaidAndHowMuch()
        {
            var service = await SeedPayrollOrderAsync();

            await service.MarkCleanerPaidAsync(
                orderId: 900, orderCleanerId: 10,
                new MarkCleanerPaidDto { PaidVia = CleanerPaymentMethod.Zelle, PaymentNote = "week 34" },
                paidByUserId: 7);

            var log = Assert.Single(await _context.AuditLogs
                .Where(a => a.EntityType == AuditEntityTypes.CleanerPayout)
                .ToListAsync());

            Assert.Equal("PayoutRecorded", log.Action);
            Assert.Equal(900, log.EntityId);

            var values = Values(log.NewValues);
            // The NAME, not the id: an assignment can be removed and a cleaner deactivated, and
            // "we paid #418 something" is not an answer six months later.
            Assert.Equal("Ana Reyes", values["Cleaner"]!.Value<string>());
            Assert.Equal("Zelle", values["PaidVia"]!.Value<string>());
            Assert.Equal("week 34", values["PaymentNote"]!.Value<string>());
        }

        [Fact]
        public async Task UndoingAPayment_RecordsTheAmountItReversed()
        {
            var service = await SeedPayrollOrderAsync();

            await service.MarkCleanerPaidAsync(900, 10, new MarkCleanerPaidDto(), 7);
            await service.UndoCleanerPaymentAsync(900, 10);

            var reversal = Assert.Single(await _context.AuditLogs
                .Where(a => a.Action == "PayoutReversed")
                .ToListAsync());

            // Captured BEFORE the fields are cleared. Logging afterwards would record a reversal
            // of "None", which documents nothing.
            var values = Values(reversal.NewValues);
            Assert.Equal("Ana Reyes", values["Cleaner"]!.Value<string>());
            Assert.NotEqual(JTokenType.Null, values["PaidAmount"]!.Type);
        }

        [Fact]
        public async Task MarkingAnAlreadyPaidCleanerPaid_WritesNoSecondRow()
        {
            // MarkPaid is a no-op on an already-paid line; recording a payout that did not happen
            // would be worse than recording none.
            var service = await SeedPayrollOrderAsync();

            await service.MarkCleanerPaidAsync(900, 10, new MarkCleanerPaidDto(), 7);
            await service.MarkCleanerPaidAsync(900, 10, new MarkCleanerPaidDto(), 7);

            var rows = await _context.AuditLogs
                .Where(a => a.EntityType == AuditEntityTypes.CleanerPayout && a.Action == "PayoutRecorded")
                .ToListAsync();

            Assert.Single(rows);
        }

        [Fact]
        public async Task ChangingTheOrderRate_RecordsTheMoveAndTheLinesItPinned()
        {
            var service = await SeedPayrollOrderAsync();

            // Pay the line first, so the rate change has an already-paid line to pin.
            await service.MarkCleanerPaidAsync(900, 10, new MarkCleanerPaidDto(), 7);
            await service.UpdateOrderHourlyRateAsync(900, 28m);

            var log = Assert.Single(await _context.AuditLogs
                .Where(a => a.EntityType == AuditEntityTypes.OrderCleanerHourlyRate)
                .ToListAsync());

            var fields = ChangedFields(log);
            Assert.Contains("CleanerHourlyRate", fields);
            // Named, not counted — "2 lines pinned" is not something anybody can check later.
            Assert.Contains("PaidLinesPinnedToOldRate", fields);
            Assert.Contains("Ana Reyes", Values(log.NewValues)["PaidLinesPinnedToOldRate"]!.Value<string>()!);
            Assert.Equal(21m, Values(log.OldValues)["CleanerHourlyRate"]!.Value<decimal>());
            Assert.Equal(28m, Values(log.NewValues)["CleanerHourlyRate"]!.Value<decimal>());
        }

        [Fact]
        public async Task SettingAPerCleanerOverride_RecordsTheNullItReplaced()
        {
            var service = await SeedPayrollOrderAsync();

            await service.UpdateCleanerPayrollAsync(900, 10, new UpdateCleanerPayrollDto
            {
                UpdateHourlyRate = true,
                HourlyRate = 30m
            });

            var log = Assert.Single(await _context.AuditLogs
                .Where(a => a.EntityType == AuditEntityTypes.CleanerPayrollOverride)
                .ToListAsync());

            Assert.Equal("PayrollOverrideSet", log.Action);
            // A null means "this line tracks the order rate", which is materially different from a
            // value that happens to equal it — so the null is recorded rather than resolved.
            Assert.Equal(JTokenType.Null, Values(log.OldValues)["HourlyRate"]!.Type);
            Assert.Equal(30m, Values(log.NewValues)["HourlyRate"]!.Value<decimal>());
        }

        [Fact]
        public async Task ResettingAnOverride_IsItsOwnAction_NotAnUpdateToNull()
        {
            // "Follow the order again" is a deliberate decision an admin should be able to filter
            // the log for.
            var service = await SeedPayrollOrderAsync();

            await service.UpdateCleanerPayrollAsync(900, 10, new UpdateCleanerPayrollDto
            {
                UpdateHourlyRate = true,
                HourlyRate = 30m
            });
            await service.UpdateCleanerPayrollAsync(900, 10, new UpdateCleanerPayrollDto
            {
                UpdateHourlyRate = true,
                HourlyRate = null
            });

            var actions = await _context.AuditLogs
                .Where(a => a.EntityType == AuditEntityTypes.CleanerPayrollOverride)
                .Select(a => a.Action)
                .ToListAsync();

            Assert.Contains("PayrollOverrideSet", actions);
            Assert.Contains("PayrollOverrideReset", actions);
        }

        /// <summary>
        /// One finished order, one assigned cleaner, wired to the real OutgoingPaymentService and
        /// the real AuditService so these tests exercise the production write path rather than a
        /// double.
        /// </summary>
        private async Task<OutgoingPaymentService> SeedPayrollOrderAsync()
        {
            var cleaner = new Cleaner
            {
                Id = 418,
                FirstName = "Ana",
                LastName = "Reyes",
                Email = "ana@example.com",
                IsActive = true,
            };

            var order = new Order
            {
                Id = 900,
                UserId = 42,
                Status = OrderStatuses.Done,
                ServiceDate = new DateTime(2026, 8, 14),
                OrderDate = new DateTime(2026, 8, 1),
                SubTotal = 300m,
                Total = 326.63m,
                TotalDuration = 300,
                MaidsCount = 1,
                CleanerHourlyRate = 21m,
                TotalRefundedAmount = 0m,
                ServiceTypeId = 1,
                // Required non-nullable columns. Nothing here is under test; they exist so the
                // in-memory provider will accept the row at all.
                ContactFirstName = "Maia",
                ContactLastName = "Kv",
                ContactEmail = "maia@example.com",
                ServiceAddress = "1 Test St",
                City = "Brooklyn",
                State = "New York",
                ZipCode = "11201",
            };

            var assignment = new OrderCleaner
            {
                Id = 10,
                OrderId = 900,
                CleanerId = 418,
                Cleaner = cleaner,
            };

            // The related rows the payout query Includes. Nothing here is under test; without
            // them the in-memory graph is incomplete and the include chain returns no order.
            _context.Users.Add(new User
            {
                Id = 42, Email = "maia@example.com", FirstName = "Maia", LastName = "Kv",
                PasswordHash = "x", PasswordSalt = "x",
            });
            _context.ServiceTypes.Add(new ServiceType { Id = 1, Name = "Regular Cleaning", Description = "" });

            _context.Cleaners.Add(cleaner);
            _context.Orders.Add(order);
            _context.OrderCleaners.Add(assignment);
            await _context.SaveChangesAsync();

            return new OutgoingPaymentService(_context, _audit);
        }
    }
}
