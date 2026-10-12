using System.Reflection;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// Order panel → Cleaners → Hide tips / Pay tips (2026-10).
    ///
    /// The office can decide a cleaner did not earn the customer's tip (behaviour, damage). Two
    /// separate facts on the assignment row:
    /// - <c>TipsHiddenFromCleaner</c>: the assignment email / SMS print no tip for that cleaner;
    /// - <c>TipsWithheldFromPayout</c>: Outgoing Payments does not pay them the tip.
    ///
    /// Fixture: order 901, 4h across Ana (418) and Ben (419) at $21 = $42.00 each, $50 tip =
    /// $25.00 each. Ben's tip is the one held back.
    /// </summary>
    public class CleanerTipsWithholdingTests : IDisposable
    {
        private const int AdminId = 7;
        private readonly ApplicationDbContext _context;
        private readonly RecordingAuditService _audit = new();

        public CleanerTipsWithholdingTests()
        {
            _context = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase($"cleaner-tips-{Guid.NewGuid()}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options);
        }

        public void Dispose() => _context.Dispose();

        private OutgoingPaymentService Payments() =>
            new(_context, _audit, new CleanerPayrollEditService(_context, _audit));

        private EmailService Email() => new(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Email:EnableEmailSending"] = "false" })
                .Build(),
            NullLogger<EmailService>.Instance,
            _context);

        private async Task SeedAsync(bool hideBen, bool withholdBen)
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

            var ana = new Cleaner { Id = 418, FirstName = "Ana", LastName = "Reyes", Email = "ana@example.com", Phone = "+15550000418", IsActive = true };
            var ben = new Cleaner { Id = 419, FirstName = "Ben", LastName = "Okafor", Email = "ben@example.com", Phone = "+15550000419", IsActive = true };
            _context.Cleaners.AddRange(ana, ben);

            _context.Orders.Add(new Order
            {
                Id = 901,
                UserId = 42,
                Status = OrderStatuses.Done,
                ServiceDate = new DateTime(2026, 8, 20),
                ServiceTime = new TimeSpan(10, 0, 0),
                OrderDate = new DateTime(2026, 8, 10),
                SubTotal = 300m,
                Tips = 50m,
                Total = 376.63m,
                TotalDuration = 240m,
                MaidsCount = 2,
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
            });

            _context.OrderCleaners.AddRange(
                new OrderCleaner { Id = 11, OrderId = 901, CleanerId = 418, Cleaner = ana },
                new OrderCleaner
                {
                    Id = 12, OrderId = 901, CleanerId = 419, Cleaner = ben,
                    TipsHiddenFromCleaner = hideBen,
                    TipsWithheldFromPayout = withholdBen
                });

            await _context.SaveChangesAsync();
        }

        // ── The payroll split ─────────────────────────────────────────────────────────────────

        [Fact]
        public void AWithheldShareIsNotPaid_AndNobodyElsesShareMoves()
        {
            var result = CleanerPayrollCalculator.Build(
                totalDuration: 240m, maidsCount: 2, hasCleanerService: false,
                orderHourlyRate: 21m, tips: 50m,
                assignments: new List<CleanerPayrollCalculator.AssignmentInput>
                {
                    new() { OrderCleanerId = 11, CleanerId = 418 },
                    new() { OrderCleanerId = 12, CleanerId = 419, TipsWithheld = true }
                });

            var ana = result.Lines.Single(l => l.CleanerId == 418);
            var ben = result.Lines.Single(l => l.CleanerId == 419);

            Assert.Equal(25.00m, ana.Tips);
            Assert.Equal(67.00m, ana.Payout);
            Assert.False(ana.TipsWithheld);

            Assert.Equal(0m, ben.Tips);
            Assert.Equal(42.00m, ben.Payout);
            Assert.True(ben.TipsWithheld);
            Assert.Equal(25.00m, ben.WithheldTips);

            Assert.Equal(25.00m, result.WithheldTips);
            // Wages — the reported labour cost — never include tips, so they do not move.
            Assert.Equal(84.00m, result.TotalSalary);
        }

        [Fact]
        public void NothingWithheld_SharesStillReAddToTheOrderTipExactly()
        {
            var result = CleanerPayrollCalculator.Build(
                totalDuration: 360m, maidsCount: 3, hasCleanerService: false,
                orderHourlyRate: 21m, tips: 100m,
                assignments: new List<CleanerPayrollCalculator.AssignmentInput>
                {
                    new() { OrderCleanerId = 1, CleanerId = 1 },
                    new() { OrderCleanerId = 2, CleanerId = 2 },
                    new() { OrderCleanerId = 3, CleanerId = 3 }
                });

            Assert.Equal(100m, result.Lines.Sum(l => l.Tips));
            Assert.Equal(0m, result.WithheldTips);
        }

        // ── Outgoing Payments ─────────────────────────────────────────────────────────────────

        [Fact]
        public async Task OutgoingPayments_PaysTheWithheldCleanerSalaryOnly()
        {
            await SeedAsync(hideBen: true, withholdBen: true);

            var row = await Payments().GetOrderAsync(901);

            Assert.NotNull(row);
            var ana = row!.Cleaners.Single(c => c.CleanerId == 418);
            var ben = row.Cleaners.Single(c => c.CleanerId == 419);

            Assert.Equal(67.00m, ana.Payout);
            Assert.Equal(42.00m, ben.Payout);
            Assert.Equal(0m, ben.Tips);
            Assert.True(ben.TipsWithheld);
            Assert.Equal(25.00m, ben.WithheldTips);
            Assert.Equal(109.00m, row.TotalPayout);   // 84 wages + Ana's 25 tip

            var unpaid = await Payments().GetUnpaidCleanersAsync();
            Assert.Equal(42.00m, unpaid.Cleaners.Single(c => c.CleanerId == 419).TotalOutstanding);
            Assert.Equal(67.00m, unpaid.Cleaners.Single(c => c.CleanerId == 418).TotalOutstanding);
        }

        [Fact]
        public async Task HiddenButPaid_OutgoingPaymentsStillPaysTheTip()
        {
            await SeedAsync(hideBen: true, withholdBen: false);

            var row = await Payments().GetOrderAsync(901);

            var ben = row!.Cleaners.Single(c => c.CleanerId == 419);
            Assert.Equal(25.00m, ben.Tips);
            Assert.Equal(67.00m, ben.Payout);
            Assert.False(ben.TipsWithheld);
            Assert.Equal(134.00m, row.TotalPayout);
        }

        [Fact]
        public async Task WithheldAfterPayment_ReadsAsOverpaid_AndPayingItAgainOwesNothingNew()
        {
            await SeedAsync(hideBen: false, withholdBen: false);
            await Payments().MarkCleanerPaidAsync(901, 12, new MarkCleanerPaidDto(), AdminId);

            var line = await _context.OrderCleaners.SingleAsync(oc => oc.Id == 12);
            line.TipsHiddenFromCleaner = true;
            line.TipsWithheldFromPayout = true;
            await _context.SaveChangesAsync();

            var ben = (await Payments().GetOrderAsync(901))!.Cleaners.Single(c => c.CleanerId == 419);
            Assert.True(ben.IsSettled);
            Assert.Equal(25.00m, ben.OverpaidAmount);
            Assert.Equal(0m, ben.OutstandingPayout);
        }

        // ── What the cleaner is told ──────────────────────────────────────────────────────────

        [Fact]
        public async Task AssignmentSms_LeavesTheTipOutForTheHiddenCleanerOnly()
        {
            await SeedAsync(hideBen: true, withholdBen: true);
            var email = Email();

            var anaSms = await email.BuildCleanerAssignmentSmsBodyAsync((await _context.Cleaners.FindAsync(418))!, 901);
            var benSms = await email.BuildCleanerAssignmentSmsBodyAsync((await _context.Cleaners.FindAsync(419))!, 901);

            Assert.Contains("Tips: $25.00", anaSms);
            Assert.DoesNotContain("Tips", benSms);
            Assert.DoesNotContain("25.00", benSms);
        }

        [Fact]
        public async Task HiddenButPaid_TheSmsStillSaysNothingAboutTheTip()
        {
            // "Pay tips" is about Outgoing Payments only — the cleaner still is not told.
            await SeedAsync(hideBen: true, withholdBen: false);

            var benSms = await Email().BuildCleanerAssignmentSmsBodyAsync((await _context.Cleaners.FindAsync(419))!, 901);

            Assert.DoesNotContain("Tips", benSms);
        }

        [Fact]
        public async Task AssignmentEmail_PrintsADashForAHiddenTip_LikeAnOrderWithNoTip()
        {
            await SeedAsync(hideBen: true, withholdBen: true);
            var order = await _context.Orders
                .Include(o => o.OrderServices)
                .Include(o => o.OrderExtraServices)
                .Include(o => o.OrderCleaners).ThenInclude(oc => oc.Cleaner)
                .SingleAsync(o => o.Id == 901);

            var shown = BuildEmailBody(order, hideTips: false);
            var hidden = BuildEmailBody(order, hideTips: true);

            Assert.Contains("$25.00", shown);
            Assert.DoesNotContain("$25.00", hidden);
            Assert.Contains("Tips", hidden);   // the row is still there, reading "—"
        }

        private string BuildEmailBody(Order order, bool hideTips)
        {
            // Private builder: the send path needs SMTP, the body does not.
            var method = typeof(EmailService).GetMethod(
                "BuildCleanerAssignmentEmail", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var (_, body) = ((string, string))method.Invoke(Email(), new object?[]
            {
                "en", "Ben", order, "2h", "1 Test St", false, false, null, hideTips
            })!;
            return body;
        }

        // ── Bookkeeping ───────────────────────────────────────────────────────────────────────

        [Fact]
        public void TheDecisionIsAuditedAndCannotBeUndoneFromTheAuditsTab()
        {
            Assert.True(AuditEntityTypes.UndoBlockedEntityTypes.Contains(AuditEntityTypes.CleanerTipsDecision));
        }
    }
}
