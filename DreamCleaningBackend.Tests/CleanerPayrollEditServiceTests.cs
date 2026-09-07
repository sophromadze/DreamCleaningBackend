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
    /// <see cref="CleanerPayrollEditService"/> — the ONE place a cleaner's pay on an order is
    /// changed, shared by the SuperAdmin Outgoing Payments page and the admin Orders panel.
    ///
    /// What these pin down is what the two screens can never be allowed to disagree about: that a
    /// change re-sums <c>Order.CleanerTotalSalary</c> (which Statistics and Finances report), that
    /// "set the hours for everyone" leaves a row NAMING each cleaner rather than one row saying
    /// "3 updated", and that the things a bulk change deliberately does NOT move stay unmoved.
    /// </summary>
    public class CleanerPayrollEditServiceTests : IDisposable
    {
        private readonly ApplicationDbContext _context;
        private readonly CleanerPayrollEditService _service;

        public CleanerPayrollEditServiceTests()
        {
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase($"payroll-edit-{Guid.NewGuid()}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;

            _context = new ApplicationDbContext(options);
            var audit = new AuditService(_context, new HttpContextAccessor(), NullLogger<AuditService>.Instance);
            _service = new CleanerPayrollEditService(_context, audit);
        }

        public void Dispose() => _context.Dispose();

        private static JObject Values(string? json) =>
            JsonConvert.DeserializeObject<JObject>(json ?? "{}") ?? new JObject();

        private async Task<List<AuditLog>> PayrollLogsAsync() =>
            await _context.AuditLogs
                .Where(a => a.EntityType == AuditEntityTypes.CleanerPayrollOverride)
                .OrderBy(a => a.Id)
                .ToListAsync();

        [Fact]
        public async Task SettingHoursForEveryone_OverridesEveryAssignedLine_AndReSumsTheOrder()
        {
            // 12h across 3 staffing slots is 4h each automatically. The crew reports 4h15.
            var order = await SeedAsync(totalDuration: 720, maidsCount: 3, assignedCleaners: 2);

            var moved = await _service.SetHoursForEveryCleanerAsync(order, 255m);

            Assert.Equal(2, moved);
            Assert.All(order.OrderCleaners, oc => Assert.Equal(255m, oc.SalaryBillableMinutes));

            // 2 x 4.25h x $21 = $178.50, plus the unstaffed slot still at the automatic 4h x $21
            // = $84.00. The slot is NOT moved — there is no assignment row to hang an override on
            // — and it is still counted, because somebody worked those hours.
            Assert.Equal(262.50m, order.CleanerTotalSalary);
        }

        [Fact]
        public async Task SettingHoursForEveryone_LogsOneRowPerCleaner_NamingEachOne()
        {
            // "3 cleaners updated" cannot answer "why is Ana paid 4h15 on #900" six months later.
            var order = await SeedAsync(totalDuration: 720, maidsCount: 2, assignedCleaners: 2);

            await _service.SetHoursForEveryCleanerAsync(order, 255m);

            var logs = await PayrollLogsAsync();
            Assert.Equal(2, logs.Count);

            var names = logs.Select(l => Values(l.NewValues)["Cleaner"]!.Value<string>()).ToList();
            Assert.Contains("Ana Reyes", names);
            Assert.Contains("Marta Silva", names);
            Assert.All(logs, l => Assert.Equal("PayrollOverrideSet", l.Action));
        }

        [Fact]
        public async Task ABulkChange_SaysSoOnBOTHSidesOfTheRow_SoTheDiffStaysAboutTheNumbers()
        {
            // Cleaner and AppliedTo are context, not changes. On the "new" side alone they would
            // render as "None -> Ana Reyes", which reads like the cleaner was assigned here.
            var order = await SeedAsync(totalDuration: 720, maidsCount: 1, assignedCleaners: 1);

            await _service.SetHoursForEveryCleanerAsync(order, 255m);

            var log = Assert.Single(await PayrollLogsAsync());
            Assert.Equal("Every cleaner on this order", Values(log.OldValues)["AppliedTo"]!.Value<string>());
            Assert.Equal("Every cleaner on this order", Values(log.NewValues)["AppliedTo"]!.Value<string>());
            Assert.Equal("Ana Reyes", Values(log.OldValues)["Cleaner"]!.Value<string>());

            var changed = JsonConvert.DeserializeObject<List<string>>(log.ChangedFields ?? "[]")!;
            Assert.DoesNotContain("Cleaner", changed);
            Assert.DoesNotContain("AppliedTo", changed);
            Assert.Contains("BillableMinutes", changed);
        }

        [Fact]
        public async Task ASingleLineEdit_SaysItAppliedToThatCleanerOnly()
        {
            var order = await SeedAsync(totalDuration: 720, maidsCount: 2, assignedCleaners: 2);
            var first = order.OrderCleaners.OrderBy(oc => oc.Id).First();

            await _service.SetCleanerOverridesAsync(order, first.Id, new UpdateCleanerPayrollDto
            {
                UpdateBillableMinutes = true,
                BillableMinutes = 300m
            });

            var log = Assert.Single(await PayrollLogsAsync());
            Assert.Equal("This cleaner only", Values(log.NewValues)["AppliedTo"]!.Value<string>());
            Assert.Equal(300m, order.OrderCleaners.Single(oc => oc.Id == first.Id).SalaryBillableMinutes);
            Assert.Null(order.OrderCleaners.Single(oc => oc.Id != first.Id).SalaryBillableMinutes);
        }

        [Fact]
        public async Task NullHours_ClearsEveryOverride_RatherThanTypingTheAutomaticFigureBack()
        {
            // A cleared override keeps tracking the order if it is re-priced later; a re-typed one
            // does not. That difference is the whole reason "reset" sends null.
            var order = await SeedAsync(totalDuration: 720, maidsCount: 2, assignedCleaners: 2);
            await _service.SetHoursForEveryCleanerAsync(order, 255m);

            await _service.SetHoursForEveryCleanerAsync(order, null);

            Assert.All(order.OrderCleaners, oc => Assert.Null(oc.SalaryBillableMinutes));
            Assert.Contains(await PayrollLogsAsync(), l => l.Action == "PayrollOverrideReset");
            // Back to 6h each at $21.
            Assert.Equal(252m, order.CleanerTotalSalary);
        }

        [Fact]
        public async Task ALineAlreadyOnThatFigure_IsNotRewrittenAndLogsNothing()
        {
            // A re-save must not fill the log with rows recording that nothing happened.
            var order = await SeedAsync(totalDuration: 720, maidsCount: 2, assignedCleaners: 2);
            await _service.SetHoursForEveryCleanerAsync(order, 255m);

            var moved = await _service.SetHoursForEveryCleanerAsync(order, 255m);

            Assert.Equal(0, moved);
            Assert.Equal(2, (await PayrollLogsAsync()).Count);
        }

        [Fact]
        public async Task SettingHoursForEveryone_LeavesAnExplicitRATEAlone()
        {
            // Somebody set that rate on purpose. An hours change is not an instruction to discard
            // it — the two overrides are independent and only "reset" drops both.
            var order = await SeedAsync(totalDuration: 720, maidsCount: 2, assignedCleaners: 2);
            var lead = order.OrderCleaners.OrderBy(oc => oc.Id).First();
            lead.SalaryHourlyRate = 30m;
            await _context.SaveChangesAsync();

            await _service.SetHoursForEveryCleanerAsync(order, 255m);

            Assert.Equal(30m, order.OrderCleaners.Single(oc => oc.Id == lead.Id).SalaryHourlyRate);
            // 4.25h x $30 + 4.25h x $21 = $127.50 + $89.25.
            Assert.Equal(216.75m, order.CleanerTotalSalary);
        }

        [Fact]
        public async Task RaisingTheHoursOfAPaidLine_IsAllowed_AndLeavesThePaymentFrozen()
        {
            // Cleaners routinely report longer hours after they have been settled. The shortfall
            // is reported as still to pay (CleanerPayoutSettlement); the record of what was
            // actually handed over must not move with it.
            var order = await SeedAsync(totalDuration: 420, maidsCount: 2, assignedCleaners: 2);
            foreach (var oc in order.OrderCleaners)
            {
                oc.IsPaid = true;
                oc.PaidAmount = 73.50m;
            }
            await _context.SaveChangesAsync();

            await _service.SetHoursForEveryCleanerAsync(order, 240m);

            Assert.All(order.OrderCleaners, oc =>
            {
                Assert.True(oc.IsPaid);
                Assert.Equal(73.50m, oc.PaidAmount);
                Assert.Equal(240m, oc.SalaryBillableMinutes);
            });
            Assert.Equal(168m, order.CleanerTotalSalary);
        }

        [Fact]
        public async Task ChangingTheOrderRate_PinsAlreadyPaidLines_ButNotUnpaidOnes()
        {
            // Raising a rate must never retroactively inflate the reported cost of work already
            // settled at the old figure. Only an HOURS change reopens a settled line.
            var order = await SeedAsync(totalDuration: 720, maidsCount: 2, assignedCleaners: 2);
            var paid = order.OrderCleaners.OrderBy(oc => oc.Id).First();
            paid.IsPaid = true;
            paid.PaidAmount = 126m;
            await _context.SaveChangesAsync();

            await _service.SetOrderHourlyRateAsync(order, 28m);

            Assert.Equal(21m, order.OrderCleaners.Single(oc => oc.Id == paid.Id).SalaryHourlyRate);
            Assert.Null(order.OrderCleaners.Single(oc => oc.Id != paid.Id).SalaryHourlyRate);
            Assert.Equal(28m, order.CleanerHourlyRate);
            // 6h at the pinned $21 plus 6h at the new $28.
            Assert.Equal(294m, order.CleanerTotalSalary);
        }

        [Fact]
        public async Task ChangingTheOrderRate_MOVES_ACleanerWhoHadTheirOwnRate()
        {
            // Owner's call, 2026-09. It used to leave an explicit per-cleaner rate alone: an owner
            // who raised two cleaners to $21 and then set the order to $20 got two cleaners still
            // on $21 and a panel that looked broken. "Set it for everyone" means everyone.
            var order = await SeedAsync(totalDuration: 720, maidsCount: 2, assignedCleaners: 2);
            var lead = order.OrderCleaners.OrderBy(oc => oc.Id).First();
            lead.SalaryHourlyRate = 21m;
            await _context.SaveChangesAsync();

            await _service.SetOrderHourlyRateAsync(order, 20m);

            // The override is DROPPED, not overwritten with the same number: the line goes back to
            // tracking the order, so the next rate change moves it too.
            Assert.All(order.OrderCleaners, oc => Assert.Null(oc.SalaryHourlyRate));
            Assert.Equal(20m, order.CleanerHourlyRate);
            Assert.Equal(240m, order.CleanerTotalSalary); // 2 x 6h x $20
        }

        [Fact]
        public async Task ChangingTheOrderRate_NamesWhoseOwnRateItDropped()
        {
            var order = await SeedAsync(totalDuration: 720, maidsCount: 2, assignedCleaners: 2);
            order.OrderCleaners.OrderBy(oc => oc.Id).First().SalaryHourlyRate = 30m;
            await _context.SaveChangesAsync();

            await _service.SetOrderHourlyRateAsync(order, 20m);

            var log = Assert.Single(await _context.AuditLogs
                .Where(a => a.EntityType == AuditEntityTypes.OrderCleanerHourlyRate)
                .ToListAsync());

            // Named, not counted — the reach of the change is not something anybody can
            // reconstruct from the page six months later.
            Assert.Contains("Ana Reyes", Values(log.NewValues)["OwnRatesDropped"]!.Value<string>()!);
        }

        [Fact]
        public async Task AnEditToOneLine_RecordsWhatItWasBEINGPAID_NotTheEmptyOverride()
        {
            // The bug this pins: the row logged the raw override columns, so a cleaner moved off
            // the order's $21 onto $20 produced "Hourly Rate: None -> $20.00" — which reads as
            // "this cleaner had no rate", when they had $21. The source of the figure is carried
            // in words instead.
            var order = await SeedAsync(totalDuration: 720, maidsCount: 2, assignedCleaners: 2);
            var line = order.OrderCleaners.OrderBy(oc => oc.Id).First();

            await _service.SetCleanerOverridesAsync(order, line.Id, new UpdateCleanerPayrollDto
            {
                UpdateHourlyRate = true,
                HourlyRate = 20m,
                UpdateBillableMinutes = true,
                BillableMinutes = 180m
            });

            var log = Assert.Single(await PayrollLogsAsync());
            var before = Values(log.OldValues);
            var after = Values(log.NewValues);

            Assert.Equal(21m, before["HourlyRate"]!.Value<decimal>());   // the order's rate, not null
            Assert.Equal(360m, before["BillableMinutes"]!.Value<decimal>()); // the automatic 6h split
            Assert.Equal("The order's rate", before["RateSource"]!.Value<string>());
            Assert.Equal("Automatic split", before["HoursSource"]!.Value<string>());

            Assert.Equal(20m, after["HourlyRate"]!.Value<decimal>());
            Assert.Equal(180m, after["BillableMinutes"]!.Value<decimal>());
            Assert.Equal("Set for this cleaner", after["RateSource"]!.Value<string>());
            Assert.Equal("Set by hand", after["HoursSource"]!.Value<string>());
        }

        [Fact]
        public async Task AnUnknownAssignment_IsReported_RatherThanSilentlyDoingNothing()
        {
            var order = await SeedAsync(totalDuration: 720, maidsCount: 1, assignedCleaners: 1);

            var applied = await _service.SetCleanerOverridesAsync(order, 9999, new UpdateCleanerPayrollDto
            {
                UpdateBillableMinutes = true,
                BillableMinutes = 300m
            });

            Assert.False(applied);
            Assert.Empty(await PayrollLogsAsync());
        }

        /// <summary>
        /// One order at $21/hr with the requested staffing. `maidsCount` above `assignedCleaners`
        /// leaves unstaffed slots, which are counted in the total but carry no assignment row —
        /// the case a bulk hours change must leave alone.
        /// </summary>
        private async Task<Order> SeedAsync(decimal totalDuration, int maidsCount, int assignedCleaners)
        {
            var names = new[] { ("Ana", "Reyes"), ("Marta", "Silva"), ("Lena", "Ortiz") };

            var order = new Order
            {
                Id = 900,
                UserId = 42,
                Status = OrderStatuses.Done,
                ServiceDate = new DateTime(2026, 9, 4),
                OrderDate = new DateTime(2026, 9, 1),
                SubTotal = 300m,
                Total = 326.63m,
                TotalDuration = totalDuration,
                MaidsCount = maidsCount,
                CleanerHourlyRate = 21m,
                TotalRefundedAmount = 0m,
                ServiceTypeId = 1,
                // Required non-nullable columns; nothing here is under test.
                ContactFirstName = "Maia",
                ContactLastName = "Kv",
                ContactEmail = "maia@example.com",
                ServiceAddress = "1 Test St",
                City = "Brooklyn",
                State = "New York",
                ZipCode = "11201",
            };

            _context.Orders.Add(order);

            for (var i = 0; i < assignedCleaners; i++)
            {
                var (first, last) = names[i];
                var cleaner = new Cleaner
                {
                    Id = 400 + i,
                    FirstName = first,
                    LastName = last,
                    Email = $"{first.ToLowerInvariant()}@example.com",
                    IsActive = true,
                };
                _context.Cleaners.Add(cleaner);
                _context.OrderCleaners.Add(new OrderCleaner
                {
                    Id = 10 + i,
                    OrderId = order.Id,
                    CleanerId = cleaner.Id,
                    Cleaner = cleaner,
                });
            }

            await _context.SaveChangesAsync();

            // Re-read through the same includes the callers use, so the service sees the graph it
            // is documented to require.
            return await _context.Orders
                .Include(o => o.OrderServices)
                    .ThenInclude(os => os.Service)
                .Include(o => o.OrderCleaners)
                    .ThenInclude(oc => oc.Cleaner)
                .FirstAsync(o => o.Id == order.Id);
        }
    }
}
