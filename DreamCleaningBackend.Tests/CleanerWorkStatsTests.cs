using System.Security.Claims;
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
using Microsoft.Extensions.Configuration;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// Cleaners dashboard → detail panel stats (2026-10): jobs, hours, hours per day, wages paid
    /// out, wages still owed, average wage per cleaning, tips.
    ///
    /// Fixture — Ana (418) on four completed jobs over three days, plus two orders that must be
    /// ignored:
    /// - #901 Aug 20: 4h across Ana + Ben at $21, $50 tip → Ana 2h, $42.00 wages + $25.00 tip, PAID $67.00
    /// - #902 Aug 20: 3h, Ana alone at $20, no tip → $60.00 wages, UNPAID
    /// - #903 Aug 21: 4h, Ana alone at $21, $10 tip → $84.00 + $10.00; she was paid $73.00 when it
    ///   was a 3h job, so $21.00 of WAGES is still owed (the routine top-up case)
    /// - #904 Aug 22: 2h, Ana alone at $20, $30 tip WITHHELD → $40.00 wages, paid $40.00, no tip
    /// - #905 Cancelled with Ana, #906 Done with Ben only — neither counts for Ana.
    ///
    /// Ana: 4 jobs, 11h, 3 days → 3h 40m a day; paid out $145.00, remaining $81.00 (wages only),
    /// average $56.50 per cleaning, tips $35.00.
    /// </summary>
    public class CleanerWorkStatsTests : IDisposable
    {
        private const int Ana = 418;
        private const int Ben = 419;
        private readonly ApplicationDbContext _context;

        public CleanerWorkStatsTests()
        {
            _context = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase($"cleaner-work-stats-{Guid.NewGuid()}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options);
        }

        public void Dispose() => _context.Dispose();

        private static int _nextAssignmentId = 100;

        private static Order NewOrder(int id, string status, DateTime date, decimal minutes, int maids,
            decimal rate, decimal tips, params OrderCleaner[] crew)
        {
            var order = new Order
            {
                Id = id,
                UserId = 42,
                Status = status,
                ServiceDate = date,
                ServiceTime = new TimeSpan(10, 0, 0),
                OrderDate = date.AddDays(-7),
                SubTotal = 300m,
                Tips = tips,
                Total = 300m + tips,
                TotalDuration = minutes,
                MaidsCount = maids,
                CleanerHourlyRate = rate,
                ServiceTypeId = 1,
                ContactFirstName = "Maia",
                ContactLastName = "Kv",
                ContactEmail = "maia@example.com",
                ServiceAddress = "1 Test St",
                City = "Brooklyn",
                State = "New York",
                ZipCode = "11201"
            };
            foreach (var oc in crew)
            {
                oc.OrderId = id;
                oc.Id = _nextAssignmentId++;
                order.OrderCleaners.Add(oc);
            }
            return order;
        }

        private static OrderCleaner On(int cleanerId, decimal? paid = null, bool withholdTips = false) => new()
        {
            CleanerId = cleanerId,
            IsPaid = paid.HasValue,
            PaidAmount = paid,
            TipsHiddenFromCleaner = withholdTips,
            TipsWithheldFromPayout = withholdTips
        };

        private static List<Order> Fixture() => new()
        {
            NewOrder(901, OrderStatuses.Done, new DateTime(2026, 8, 20), 240m, 2, 21m, 50m, On(Ana, paid: 67m), On(Ben)),
            NewOrder(902, OrderStatuses.Done, new DateTime(2026, 8, 20, 15, 0, 0), 180m, 1, 20m, 0m, On(Ana)),
            NewOrder(903, OrderStatuses.Done, new DateTime(2026, 8, 21), 240m, 1, 21m, 10m, On(Ana, paid: 73m)),
            NewOrder(904, OrderStatuses.Done, new DateTime(2026, 8, 22), 120m, 1, 20m, 30m, On(Ana, paid: 40m, withholdTips: true)),
            NewOrder(905, OrderStatuses.Cancelled, new DateTime(2026, 8, 23), 240m, 1, 21m, 20m, On(Ana)),
            NewOrder(906, OrderStatuses.Done, new DateTime(2026, 8, 24), 240m, 1, 21m, 20m, On(Ben))
        };

        // ── The figures ───────────────────────────────────────────────────────────────────────

        [Fact]
        public void WorkFigures_CountCompletedJobs_PayrollHours_AndDistinctDays()
        {
            var stats = CleanerWorkStats.Build(Fixture(), Ana, includePay: true);

            Assert.Equal(4, stats.JobsCompleted);
            Assert.Equal(660m, stats.TotalMinutesWorked);   // 2h + 3h + 4h + 2h
            Assert.Equal(3, stats.DaysWorked);              // two jobs on Aug 20 are one day
            Assert.Equal(220m, stats.AverageMinutesPerDay); // 11h / 3 = 3h 40m
        }

        [Fact]
        public void Money_IsWagesOnly_AndTipsAreTheirOwnFigure()
        {
            var stats = CleanerWorkStats.Build(Fixture(), Ana, includePay: true);

            Assert.True(stats.IncludesPay);
            Assert.Equal(145.00m, stats.PaidOut);           // 42 + 0 + 63 + 40
            Assert.Equal(81.00m, stats.RemainingToPay);     // 0 + 60 + 21 + 0
            Assert.Equal(56.50m, stats.AverageEarnedPerJob);// 226 / 4
            Assert.Equal(35.00m, stats.Tips);               // 25 + 0 + 10 + 0 (the withheld $30 is not hers)

            // Paid out + remaining is exactly the wages she earned — the $35 of tips she was also
            // handed is nowhere inside it.
            Assert.Equal(226.00m, stats.PaidOut + stats.RemainingToPay);
        }

        /// <summary>
        /// A paid line is one running figure for wages + tip. When it falls short because hours were
        /// added afterwards, the shortfall is WAGES — the tip was covered by the first payment.
        /// </summary>
        [Fact]
        public void APaymentCoversTheTipFirst_SoAShortfallReadsAsWagesOwed()
        {
            var order = NewOrder(903, OrderStatuses.Done, new DateTime(2026, 8, 21), 240m, 1, 21m, 10m, On(Ana, paid: 73m));

            var stats = CleanerWorkStats.Build(new[] { order }, Ana, includePay: true);

            Assert.Equal(63.00m, stats.PaidOut);
            Assert.Equal(21.00m, stats.RemainingToPay);
            Assert.Equal(10.00m, stats.Tips);
        }

        /// <summary>Hours cut after payment: nothing is owed, and the excess is not counted as wages.</summary>
        [Fact]
        public void AnOverpaidLine_OwesNothing_AndPaidOutIsCappedAtTheWages()
        {
            var order = NewOrder(907, OrderStatuses.Done, new DateTime(2026, 8, 25), 180m, 1, 20m, 0m, On(Ana, paid: 80m));

            var stats = CleanerWorkStats.Build(new[] { order }, Ana, includePay: true);

            Assert.Equal(60.00m, stats.PaidOut);
            Assert.Equal(0m, stats.RemainingToPay);
        }

        [Fact]
        public void WithoutPermission_TheMoneyIsNotBuiltAtAll()
        {
            var stats = CleanerWorkStats.Build(Fixture(), Ana, includePay: false);

            Assert.False(stats.IncludesPay);
            Assert.Null(stats.PaidOut);
            Assert.Null(stats.RemainingToPay);
            Assert.Null(stats.AverageEarnedPerJob);
            Assert.Null(stats.Tips);

            // The work figures are for everybody who can open the panel.
            Assert.Equal(4, stats.JobsCompleted);
            Assert.Equal(660m, stats.TotalMinutesWorked);
        }

        [Fact]
        public void ACleanerWithNoCompletedJobs_ReadsZero_NotAnError()
        {
            var stats = CleanerWorkStats.Build(Array.Empty<Order>(), Ana, includePay: true);

            Assert.Equal(0, stats.JobsCompleted);
            Assert.Equal(0m, stats.AverageMinutesPerDay);
            Assert.Equal(0m, stats.AverageEarnedPerJob);
            Assert.Equal(0m, stats.PaidOut);
        }

        // ── Who receives the money (the API, not just the screen) ─────────────────────────────

        private const int SuperAdminId = 1;
        private const int GrantedAdminId = 2;
        private const int UngrantedAdminId = 3;
        private const int ModeratorId = 4;

        private async Task SeedAsync()
        {
            _context.Users.AddRange(
                new User { Id = 42, Email = "maia@example.com", FirstName = "Maia", LastName = "Kv", PasswordHash = "x", PasswordSalt = "x" },
                new User { Id = SuperAdminId, Email = "sa@example.invalid", FirstName = "Sam", LastName = "Super", PasswordHash = "x", PasswordSalt = "x", Role = UserRole.SuperAdmin },
                new User { Id = GrantedAdminId, Email = "ga@example.invalid", FirstName = "Gina", LastName = "Granted", PasswordHash = "x", PasswordSalt = "x", Role = UserRole.Admin, CanEditOrdersWithoutApproval = true },
                new User { Id = UngrantedAdminId, Email = "ua@example.invalid", FirstName = "Una", LastName = "Ungranted", PasswordHash = "x", PasswordSalt = "x", Role = UserRole.Admin },
                // A Moderator holding the flag (left over from an earlier role) still sees no money.
                new User { Id = ModeratorId, Email = "mo@example.invalid", FirstName = "Mo", LastName = "Derator", PasswordHash = "x", PasswordSalt = "x", Role = UserRole.Moderator, CanEditOrdersWithoutApproval = true });
            _context.ServiceTypes.Add(new ServiceType { Id = 1, Name = "Regular Cleaning", Description = "" });
            _context.Cleaners.AddRange(
                new Cleaner { Id = Ana, FirstName = "Ana", LastName = "Reyes", Email = "ana@example.com", Phone = "+15550000418", IsActive = true },
                new Cleaner { Id = Ben, FirstName = "Ben", LastName = "Okafor", Email = "ben@example.com", Phone = "+15550000419", IsActive = true });
            _context.Orders.AddRange(Fixture());
            await _context.SaveChangesAsync();
        }

        private AdminCleanersController ControllerFor(int userId, UserRole role)
        {
            var service = new CleanerManagementService(_context, new ConfigurationBuilder().Build(), new RecordingAuditService());
            return new AdminCleanersController(service)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                        {
                            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                            new Claim("UserId", userId.ToString()),
                            new Claim("Role", role.ToString()),
                            new Claim(ClaimTypes.Role, role.ToString())
                        }, "test"))
                    }
                }
            };
        }

        private async Task<CleanerWorkStatsDto> StatsSeenBy(int userId, UserRole role)
        {
            var response = await ControllerFor(userId, role).GetById(Ana);
            var detail = Assert.IsType<CleanerDetailDto>(Assert.IsType<OkObjectResult>(response.Result).Value);
            return detail.WorkStats;
        }

        [Theory]
        [InlineData(SuperAdminId, UserRole.SuperAdmin)]
        [InlineData(GrantedAdminId, UserRole.Admin)]
        public async Task SuperAdminsAndGrantedAdmins_ReceiveThePay(int userId, UserRole role)
        {
            await SeedAsync();

            var stats = await StatsSeenBy(userId, role);

            Assert.True(stats.IncludesPay);
            Assert.Equal(145.00m, stats.PaidOut);
            Assert.Equal(81.00m, stats.RemainingToPay);
            Assert.Equal(56.50m, stats.AverageEarnedPerJob);
            Assert.Equal(35.00m, stats.Tips);
        }

        [Theory]
        [InlineData(UngrantedAdminId, UserRole.Admin)]
        [InlineData(ModeratorId, UserRole.Moderator)]
        public async Task EveryoneElse_GetsTheWorkFigures_AndNoMoney(int userId, UserRole role)
        {
            await SeedAsync();

            var stats = await StatsSeenBy(userId, role);

            Assert.False(stats.IncludesPay);
            Assert.Null(stats.PaidOut);
            Assert.Null(stats.RemainingToPay);
            Assert.Null(stats.AverageEarnedPerJob);
            Assert.Null(stats.Tips);
            Assert.Equal(4, stats.JobsCompleted);
            Assert.Equal(660m, stats.TotalMinutesWorked);
            Assert.Equal(3, stats.DaysWorked);
        }

        /// <summary>The grant is read live: revoking it hides the money on the very next request.</summary>
        [Fact]
        public async Task RevokingTheGrant_HidesThePayOnTheNextRequest()
        {
            await SeedAsync();
            Assert.True((await StatsSeenBy(GrantedAdminId, UserRole.Admin)).IncludesPay);

            var admin = await _context.Users.SingleAsync(u => u.Id == GrantedAdminId);
            admin.CanEditOrdersWithoutApproval = false;
            await _context.SaveChangesAsync();

            Assert.False((await StatsSeenBy(GrantedAdminId, UserRole.Admin)).IncludesPay);
        }
    }
}
