using System;
using System.Threading.Tasks;
using DreamCleaningBackend.Controllers;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// THE FOUR WORDS OF THE COMPANY → CUSTOMERS TAB.
    ///
    /// The tab exists to answer "how many customers came back", and the whole page is worthless if
    /// the four terms below ever collapse into each other. They are NOT synonyms:
    ///
    ///   Active     — booked at least once inside the window.
    ///   New        — their FIRST-EVER booking falls inside the window.
    ///   Returning  — they had already booked before the window opened.
    ///   Repeat     — 2+ bookings INSIDE the window.
    ///
    /// Retention is a fifth, separate question again: of the people served in the PREVIOUS window
    /// of the same length, how many were served in this one. A customer can be Returning without
    /// being Retained (they were here last year, not last month) and Retained without being Repeat
    /// (one booking in each of two months).
    ///
    /// The fixture below is built so that every one of those distinctions is exercised by a
    /// different person, and so that collapsing any two of the definitions breaks a count.
    /// </summary>
    public class CustomerStatisticsTests : IDisposable
    {
        // A whole calendar month, so the previous window resolves to the whole month before it.
        private static readonly DateTime WindowFrom = new(2026, 8, 1);
        private static readonly DateTime WindowTo = new(2026, 8, 31);

        private const int Alice = 1;   // July + August  → returning, retained
        private const int Bob = 2;     // August only    → new
        private const int Carol = 3;   // January + August → returning AND won back (>180 days away)
        private const int Dave = 4;    // twice in August, never before → new AND repeat
        private const int Erin = 5;    // July only      → lapsed
        private const int Frank = 6;   // cancelled August order → counts nowhere

        private readonly ApplicationDbContext _context;

        public CustomerStatisticsTests()
        {
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase($"customer-stats-{Guid.NewGuid()}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;

            _context = new ApplicationDbContext(options);
            Seed();
        }

        public void Dispose() => _context.Dispose();

        private void Seed()
        {
            foreach (var id in new[] { Alice, Bob, Carol, Dave, Erin, Frank })
            {
                _context.Users.Add(new User
                {
                    Id = id,
                    FirstName = $"User{id}",
                    LastName = "Test",
                    Email = $"user{id}@example.com",
                    Role = UserRole.Customer,
                    // Bob and Dave signed up in the window; everyone else earlier. Registration is
                    // a different cohort from "customers served" on purpose — see ActivationRate.
                    CreatedAt = id == Bob || id == Dave ? new DateTime(2026, 8, 3) : new DateTime(2025, 5, 1)
                });
            }

            AddOrder(101, Alice, new DateTime(2026, 7, 10), 200m);
            AddOrder(102, Alice, new DateTime(2026, 8, 12), 220m);

            AddOrder(103, Bob, new DateTime(2026, 8, 5), 150m);

            AddOrder(104, Carol, new DateTime(2026, 1, 15), 300m);
            AddOrder(105, Carol, new DateTime(2026, 8, 20), 310m);

            AddOrder(106, Dave, new DateTime(2026, 8, 4), 120m);
            AddOrder(107, Dave, new DateTime(2026, 8, 25), 130m);

            AddOrder(108, Erin, new DateTime(2026, 7, 22), 180m);

            // Cancelled: OrderBookedFilter drops it, so Frank never appears anywhere.
            AddOrder(109, Frank, new DateTime(2026, 8, 8), 500m, status: OrderStatuses.Cancelled);

            _context.SaveChanges();
        }

        private void AddOrder(int id, int userId, DateTime serviceDate, decimal total,
            string status = OrderStatuses.Done, DateTime? orderDate = null)
        {
            _context.Orders.Add(new Order
            {
                Id = id,
                UserId = userId,
                ServiceTypeId = 1,
                ServiceDate = serviceDate,
                // Defaults to the service date: most of these tests do not care WHEN it was booked.
                // The follow-up tests do, and pass it explicitly.
                OrderDate = orderDate ?? serviceDate,
                CreatedAt = serviceDate,
                Status = status,
                IsPaid = true,
                PaymentMethod = PaymentMethod.Normal,
                Total = total,
                SubTotal = total,
                ContactFirstName = "Test",
                ContactLastName = "Customer",
                ContactEmail = $"user{userId}@example.com",
                ServiceAddress = "1 Test St",
                City = "Brooklyn",
                State = "New York",
                ZipCode = "11201"
            });
        }

        private async Task<CustomerStatisticsDto> Load(DateTime? from = null, DateTime? to = null)
        {
            var controller = new AdminCustomerStatsController(_context);
            var result = await controller.GetCustomerStatistics(from ?? WindowFrom, to ?? WindowTo);
            var ok = Assert.IsType<OkObjectResult>(result.Result);
            return Assert.IsType<CustomerStatisticsDto>(ok.Value);
        }

        [Fact]
        public async Task ActiveNewAndReturning_AreThreeDifferentCounts()
        {
            var stats = await Load();

            // Alice, Bob, Carol, Dave. Frank's cancelled order buys him nothing.
            Assert.Equal(4, stats.ActiveCustomers);
            Assert.Equal(2, stats.NewCustomers);        // Bob, Dave
            Assert.Equal(2, stats.ReturningCustomers);  // Alice, Carol
            Assert.Equal(stats.ActiveCustomers, stats.NewCustomers + stats.ReturningCustomers);
            Assert.Equal(50m, stats.ReturningRate);
        }

        [Fact]
        public async Task Repeat_CountsBookingsInsideTheWindow_NotHavingBookedBefore()
        {
            var stats = await Load();

            // Dave alone: two cleanings in August. Alice also has two orders in total, but only
            // one of them is inside the window — she is Returning, not Repeat.
            Assert.Equal(1, stats.RepeatCustomers);
        }

        [Fact]
        public async Task Reactivated_CountsOnlyCustomersWhoHadBeenAwayLongerThanSixMonths()
        {
            var stats = await Load();

            // Carol (last seen January, 7 months earlier) yes; Alice (last seen July) no.
            Assert.Equal(1, stats.ReactivatedCustomers);
        }

        [Fact]
        public async Task Retention_MeasuresThePreviousWindow_NotAllOfHistory()
        {
            var stats = await Load();

            // July holds Alice and Erin. Alice came back, Erin did not.
            Assert.Equal(2, stats.PreviousActiveCustomers);
            Assert.Equal(1, stats.RetainedCustomers);
            Assert.Equal(1, stats.LapsedCustomers);
            Assert.Equal(50m, stats.RetentionRate);
            Assert.Equal(50m, stats.ChurnRate);

            // Carol is Returning but NOT Retained — she was absent from the previous window. If
            // the two definitions were ever merged, this is the assertion that catches it.
            Assert.NotEqual(stats.ReturningCustomers, stats.RetainedCustomers);
        }

        [Fact]
        public async Task OrdersAndSpend_SplitBetweenNewAndReturningCustomers()
        {
            var stats = await Load();

            // 220 (Alice) + 150 (Bob) + 310 (Carol) + 120 + 130 (Dave) = 930.
            Assert.Equal(5, stats.TotalOrders);
            Assert.Equal(930m, stats.TotalSpend);
            Assert.Equal(3, stats.NewCustomerOrders);          // Bob 1 + Dave 2
            Assert.Equal(2, stats.ReturningCustomerOrders);     // Alice 1 + Carol 1
            Assert.Equal(400m, stats.NewCustomerSpend);         // 150 + 120 + 130
            Assert.Equal(530m, stats.ReturningCustomerSpend);   // 220 + 310
            Assert.Equal(stats.TotalSpend, stats.NewCustomerSpend + stats.ReturningCustomerSpend);
        }

        [Fact]
        public async Task Frequency_BucketsCustomersByHowOftenTheyBookedInsideTheWindow()
        {
            var stats = await Load();

            var once = Assert.Single(stats.Frequency, f => f.Label == "1");
            var twice = Assert.Single(stats.Frequency, f => f.Label == "2");

            Assert.Equal(3, once.Customers);   // Alice, Bob, Carol
            Assert.Equal(1, twice.Customers);  // Dave
            // The buckets are people, and every person lands in exactly one of them.
            Assert.Equal(stats.ActiveCustomers, once.Customers + twice.Customers);
        }

        [Fact]
        public async Task Signups_AreARegistrationCohort_NotTheCustomersServed()
        {
            var stats = await Load();

            // Bob and Dave registered in August; both went on to book, so activation is 100%.
            // Alice and Carol were served in August but registered long before — they are
            // deliberately absent from this count.
            Assert.Equal(2, stats.Signups);
            Assert.Equal(2, stats.SignupsWhoBooked);
            Assert.Equal(100m, stats.ActivationRate);
        }

        [Fact]
        public async Task AllTimeWindow_HasNoBefore_SoEveryCustomerReadsAsNew()
        {
            var controller = new AdminCustomerStatsController(_context);
            var result = await controller.GetCustomerStatistics(null, null);
            var stats = Assert.IsType<CustomerStatisticsDto>(
                Assert.IsType<OkObjectResult>(result.Result).Value);

            // No window start means no "before it", so nobody can have booked earlier. The five
            // non-cancelled customers are all new, and there is no previous window to retain from.
            Assert.Equal(5, stats.ActiveCustomers);
            Assert.Equal(5, stats.NewCustomers);
            Assert.Equal(0, stats.ReturningCustomers);
            Assert.Equal(0, stats.PreviousActiveCustomers);
            Assert.Equal(0m, stats.RetentionRate);
        }

        // ── Retention: the 90-day backward window ────────────────────────────────
        //
        // The headline retention figure is BACKWARD-looking — "of the customers served this period,
        // the share who had also booked in the previous 90 days". A forward-looking cohort cannot
        // be measured until 90 days have passed, which would blank the three most recent months.

        [Fact]
        public async Task RecentlyActive_CountsReturningCustomersSeenWithinNinetyDays()
        {
            var stats = await Load();

            // Alice was last here on 10 July, 22 days before the window opened. Carol was last here
            // in January — returning, but not on a cadence with us.
            Assert.Equal(1, stats.RecentlyActiveCustomers);
            Assert.Equal(25m, stats.RecentlyActiveRate); // 1 of 4 customers served
            // The two questions must not collapse: both Alice and Carol "came back".
            Assert.Equal(2, stats.ReturningCustomers);
        }

        [Fact]
        public async Task RecentlyActive_IncludesAGapOfExactlyNinetyDays_AndExcludesNinetyOne()
        {
            // 3 May 2026 is exactly 90 days before 1 August 2026.
            var carolsPreviousVisit = _context.Orders.Find(104)!;
            carolsPreviousVisit.ServiceDate = new DateTime(2026, 5, 3);
            _context.SaveChanges();

            var onTheBoundary = await Load();
            Assert.Equal(2, onTheBoundary.RecentlyActiveCustomers);
            // Inside 90 days is also inside 180, so nobody reads as won back any more.
            Assert.Equal(0, onTheBoundary.ReactivatedCustomers);

            carolsPreviousVisit.ServiceDate = new DateTime(2026, 5, 2); // 91 days
            _context.SaveChanges();

            var justOutside = await Load();
            Assert.Equal(1, justOutside.RecentlyActiveCustomers);
        }

        [Fact]
        public async Task PeriodOverPeriodRetention_IsStillReported_ButIsADifferentQuestion()
        {
            var stats = await Load();

            // Kept for continuity and clearly labelled in the UI. It answers "who was here last
            // month AND this month" — Alice only — while RecentlyActive answers "who is on a
            // cadence with us". They are allowed to disagree; they must not be merged.
            Assert.Equal(1, stats.RetainedCustomers);
            Assert.Equal(50m, stats.RetentionRate);
            Assert.NotEqual(stats.RetentionRate, stats.RecentlyActiveRate);
        }

        // ── Recurring plans ──────────────────────────────────────────────────────
        //
        // The seeded "One Time" tier is a REAL Subscription row with SubscriptionDays = 0, and a
        // one-off booking stores its id rather than null. A null-check-only implementation would
        // therefore count every single cleaning as recurring and still pass every other test here.

        /// <summary>Adds a tier only if the seed has not already provided it.</summary>
        private void EnsureSubscription(int id, string name, int days, decimal discount)
        {
            if (_context.Subscriptions.Find(id) != null) return;
            _context.Subscriptions.Add(new Subscription
            {
                Id = id,
                Name = name,
                Description = name,
                SubscriptionDays = days,
                DiscountPercentage = discount,
                IsActive = true,
                DisplayOrder = id,
                CreatedAt = new DateTime(2026, 1, 1)
            });
            _context.SaveChanges();
        }

        [Fact]
        public async Task RecurringPlan_DoesNotCountAOneTimeOrder_EvenThoughItCarriesASubscriptionId()
        {
            EnsureSubscription(1, "One Time", days: 0, discount: 0m);

            var bobsOrder = _context.Orders.Find(103)!;
            bobsOrder.SubscriptionId = 1;
            _context.SaveChanges();

            var stats = await Load();
            Assert.Equal(0, stats.RecurringPlanCustomers);
        }

        [Fact]
        public async Task RecurringPlan_CountsAnOrderOnARepeatingTier()
        {
            EnsureSubscription(2, "Weekly", days: 7, discount: 15m);

            var bobsOrder = _context.Orders.Find(103)!;
            bobsOrder.SubscriptionId = 2;
            // Deliberately left at 0. The discount only lands from the SECOND order on a plan, so
            // reading recurrence off SubscriptionDiscountAmount misses the customer entirely —
            // that was the original bug.
            bobsOrder.SubscriptionDiscountAmount = 0m;
            _context.SaveChanges();

            var stats = await Load();
            Assert.Equal(1, stats.RecurringPlanCustomers);
        }

        // ── CRM follow-up attribution ────────────────────────────────────────────
        //
        // A booking counts as followed-up when a Call/Email/SMS was logged on a lead matching that
        // customer in the 90 days BEFORE they placed the order. The three things that must hold:
        // the lead matches on email/phone (captured leads carry no ClientId), only real outreach
        // types count, and a touch after the booking never counts backwards.

        /// <summary>Logs one outreach on a lead identified only by email or phone, as capture does.</summary>
        private void AddFollowUp(int leadId, DateTime at, string type = LeadActivityType.Call,
            string? email = null, string? phone = null)
        {
            if (_context.Leads.Find(leadId) == null)
            {
                _context.Leads.Add(new Lead
                {
                    Id = leadId,
                    Email = email,
                    Phone = phone,
                    Stage = LeadStage.Contacted,
                    Source = LeadSource.ContactForm,
                    Type = LeadType.Residential
                });
            }

            _context.LeadActivities.Add(new LeadActivity
            {
                LeadId = leadId,
                Type = type,
                Content = "test outreach",
                CreatedAt = at
            });
            _context.SaveChanges();
        }

        [Fact]
        public async Task FollowUp_CreditsAReturningCustomerChasedBeforeTheyBooked()
        {
            // Alice booked her August cleaning on July 28; she was called on July 20.
            var order = _context.Orders.Find(102)!;
            order.OrderDate = new DateTime(2026, 7, 28);
            _context.SaveChanges();
            AddFollowUp(leadId: 1, at: new DateTime(2026, 7, 20), email: "user1@example.com");

            var stats = await Load();

            Assert.Equal(1, stats.FollowedUpCustomers);
            Assert.Equal(1, stats.ReturningAfterFollowUp);
            // Carol also came back, but nobody logged a call to her.
            Assert.Equal(1, stats.ReturningWithoutFollowUp);
            Assert.Equal(50m, stats.FollowUpAssistedRate);
            Assert.Equal(220m, stats.FollowUpAssistedSpend);
        }

        [Fact]
        public async Task FollowUp_MatchesOnPhone_BecauseCapturedLeadsCarryNoClientId()
        {
            var alice = _context.Users.Find(Alice)!;
            alice.Phone = "(212) 555-0143";
            var order = _context.Orders.Find(102)!;
            order.OrderDate = new DateTime(2026, 7, 28);
            _context.SaveChanges();

            // Lead has no email at all and a differently formatted number — both sides normalize.
            AddFollowUp(leadId: 2, at: new DateTime(2026, 7, 25), phone: "1-212-555-0143");

            var stats = await Load();
            Assert.Equal(1, stats.ReturningAfterFollowUp);
        }

        [Fact]
        public async Task FollowUp_IgnoresTouchesAfterTheBooking_AndNonOutreachActivityTypes()
        {
            var order = _context.Orders.Find(102)!;
            order.OrderDate = new DateTime(2026, 7, 28);
            _context.SaveChanges();

            // A note before the booking is bookkeeping, not contact; a call after it cannot have
            // caused it. Neither may credit the booking.
            AddFollowUp(leadId: 3, at: new DateTime(2026, 7, 20),
                type: LeadActivityType.Note, email: "user1@example.com");
            AddFollowUp(leadId: 3, at: new DateTime(2026, 8, 2), type: LeadActivityType.Call);

            var stats = await Load();

            Assert.Equal(0, stats.FollowedUpCustomers);
            Assert.Equal(0, stats.ReturningAfterFollowUp);
            Assert.Equal(stats.ReturningCustomers, stats.ReturningWithoutFollowUp);
        }

        [Fact]
        public async Task FollowUp_TouchOlderThanTheLookback_DoesNotCreditTheBooking()
        {
            var order = _context.Orders.Find(102)!;
            order.OrderDate = new DateTime(2026, 7, 28);
            _context.SaveChanges();

            // 100 days before the booking — past the 90-day window, so it is not why she booked.
            AddFollowUp(leadId: 4, at: new DateTime(2026, 4, 19), email: "user1@example.com");

            var stats = await Load();
            Assert.Equal(0, stats.ReturningAfterFollowUp);
        }

        [Fact]
        public async Task FollowUpsLogged_CountsEffortInTheWindow_EvenWhenNobodyBooked()
        {
            // Outreach to a prospect who is not a customer at all. Effort is still effort.
            AddFollowUp(leadId: 5, at: new DateTime(2026, 8, 10), email: "stranger@example.com");
            AddFollowUp(leadId: 5, at: new DateTime(2026, 8, 11), type: LeadActivityType.Sms);

            var stats = await Load();

            Assert.Equal(2, stats.FollowUpsLogged);
            Assert.Equal(1, stats.LeadsFollowedUp);
            Assert.Equal(0, stats.FollowedUpCustomers);
        }

        [Fact]
        public async Task Trend_ReportsOneRowPerMonth_WithFirstEverBookingDecidingNew()
        {
            var controller = new AdminCustomerStatsController(_context);
            var result = await controller.GetCustomerTrend(months: 3);
            var points = Assert.IsType<List<CustomerTrendPointDto>>(
                Assert.IsType<OkObjectResult>(result.Result).Value);

            Assert.Equal(3, points.Count);
            // Oldest first, one calendar month apart.
            Assert.Equal(points[0].MonthStart.AddMonths(1), points[1].MonthStart);
            Assert.All(points, p => Assert.Equal(p.ActiveCustomers, p.NewCustomers + p.ReturningCustomers));
        }
    }
}
