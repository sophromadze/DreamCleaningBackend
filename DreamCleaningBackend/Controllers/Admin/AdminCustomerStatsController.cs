using DreamCleaningBackend.Attributes;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DreamCleaningBackend.Controllers
{
    /// <summary>
    /// Company → Customers tab: who booked in a period, how many of them had booked before, and
    /// how the money splits between newcomers and regulars. Same <c>api/admin</c> prefix and the
    /// same topic-controller pattern as the rest of <c>Controllers/Admin</c>.
    ///
    /// Two deliberate choices that make this tab agree with the rest of the admin area:
    ///   • Orders are filtered through <see cref="OrderBookedFilter.IsRealBooking"/> — cancelled,
    ///     fully-refunded and never-paid abandoned checkouts are not customers who showed up.
    ///   • Everything buckets by <c>ServiceDate</c>, like Statistics and Finances (the CRM Ads tab
    ///     is the odd one out, bucketing by CreatedAt so it lines up with that day's ad spend).
    ///
    /// The aggregates are composed in memory from a handful of bounded queries rather than in SQL:
    /// "was this customer's first booking before the window" is a per-customer lookup, not a sum of
    /// columns, and this business's order volume makes the round trip cheap.
    /// </summary>
    [Route("api/admin")]
    [ApiController]
    [Authorize(Roles = "Admin,SuperAdmin,Moderator")]
    public class AdminCustomerStatsController : AdminControllerBase
    {
        private readonly ApplicationDbContext _context;

        /// <summary>
        /// A returning customer who had been away longer than this counts as REACTIVATED — won
        /// back rather than merely still around. Matches the CRM's "Churned" threshold
        /// (CrmCustomersController.AtRiskDays) so the two pages tell the same story.
        /// </summary>
        private const int ReactivationDays = 180;

        /// <summary>
        /// A customer whose previous booking was within this many days of the window opening counts
        /// as RECENTLY ACTIVE — the headline retention figure. Chosen to sit comfortably above the
        /// observed rebooking cadence (production gaps for August 2026 ran 6–112 days, median 22),
        /// so a customer on any of the offered plans clears it and a genuine lapse does not.
        /// </summary>
        private const int RecentLookbackDays = 90;

        /// <summary>Months of history behind the median rebooking gap.</summary>
        private const int MedianWindowMonths = 12;

        /// <summary>
        /// Smallest sample any derived figure will be reported on. Mirrors MIN_SAMPLE on the
        /// frontend, which suppresses rates on thin denominators; the median is suppressed here
        /// instead because the client cannot recompute it from the payload.
        /// </summary>
        private const int MinReportableSample = 10;

        /// <summary>Months of history the trend chart asks for by default, and its hard ceiling.</summary>
        private const int DefaultTrendMonths = 12;
        private const int MaxTrendMonths = 36;

        private const int TopCustomerCount = 10;

        public AdminCustomerStatsController(ApplicationDbContext context)
        {
            _context = context;
        }

        /// <summary>
        /// A follow-up is an outreach an admin actually made. Notes, stage moves and system entries
        /// are bookkeeping, not contact, so they are not follow-ups.
        /// </summary>
        private static readonly string[] FollowUpActivityTypes =
        {
            LeadActivityType.Call, LeadActivityType.Email, LeadActivityType.Sms
        };

        /// <summary>
        /// How long a logged follow-up stays credited with a later booking. A call more than a
        /// quarter before someone books is not plausibly why they booked, and counting it would
        /// quietly attribute every organic return to whoever last touched the lead.
        /// </summary>
        private const int FollowUpLookbackDays = 90;

        /// <summary>One order, reduced to the things every figure on this tab is built from.</summary>
        /// <param name="ServiceDate">When the cleaning happens — what every window buckets on.</param>
        /// <param name="BookedAt">
        /// When the order was PLACED. Follow-up attribution needs this, not ServiceDate: a call in
        /// July that produced a booking made in July for an August cleaning came before the decision.
        /// <c>Order.OrderDate</c> and <c>LeadActivity.CreatedAt</c> are both <c>DateTime.UtcNow</c>
        /// at write time, so the two compare directly with no timezone conversion — unlike
        /// ServiceDate, which is a NY wall-clock date (see NyTimeHelper).
        /// </param>
        private sealed record BookingRow(
            int UserId, DateTime ServiceDate, DateTime BookedAt, decimal Spend, bool RecurringPlan);

        [HttpGet("customer-statistics")]
        [RequirePageView(AdminViewablePages.CustomerStats)]
        public async Task<ActionResult<CustomerStatisticsDto>> GetCustomerStatistics(
            [FromQuery] DateTime? from,
            [FromQuery] DateTime? to)
        {
            var rows = await LoadBookings(from, to);
            var dto = new CustomerStatisticsDto { From = from?.Date, To = to?.Date };

            var byCustomer = rows.GroupBy(r => r.UserId).ToList();
            var activeIds = byCustomer.Select(g => g.Key).ToHashSet();

            dto.ActiveCustomers = activeIds.Count;
            dto.TotalOrders = rows.Count;
            dto.TotalSpend = decimal.Round(rows.Sum(r => r.Spend), 2);

            // "Had they booked before this window opened?" — one grouped query over everything
            // earlier than `from`, which answers new-vs-returning AND (through the max date) how
            // long they had been away. With no `from` there is no "before", so everyone is new,
            // which is the correct reading of an all-time window.
            var priorLastOrder = await LoadLastOrderBefore(activeIds, from);

            var newIds = new HashSet<int>();
            var returningIds = new HashSet<int>();
            var reactivated = 0;
            var recentlyActive = 0;
            var repeatCount = 0;
            var recurringCount = 0;
            var frequency = new Dictionary<string, (int Customers, int Orders, decimal Spend)>();

            foreach (var g in byCustomer)
            {
                var orders = g.Count();
                var spend = g.Sum(r => r.Spend);

                if (priorLastOrder.TryGetValue(g.Key, out var lastBefore))
                {
                    returningIds.Add(g.Key);
                    // How long they had been away when the window opened splits the returning
                    // customers three ways: recently active (a real cadence), simply returning,
                    // and — past the churn threshold — won back.
                    var daysAway = from.HasValue
                        ? (from.Value.Date - lastBefore.Date).TotalDays
                        : 0;
                    if (from.HasValue && daysAway > ReactivationDays) reactivated++;
                    if (from.HasValue && daysAway <= RecentLookbackDays) recentlyActive++;
                }
                else
                {
                    newIds.Add(g.Key);
                }

                if (orders >= 2) repeatCount++;
                if (g.Any(r => r.RecurringPlan)) recurringCount++;

                var bucket = orders >= 4 ? "4+" : orders.ToString();
                frequency.TryGetValue(bucket, out var acc);
                frequency[bucket] = (acc.Customers + 1, acc.Orders + orders, acc.Spend + spend);
            }

            dto.NewCustomers = newIds.Count;
            dto.ReturningCustomers = returningIds.Count;
            dto.ReactivatedCustomers = reactivated;
            dto.RecentlyActiveCustomers = recentlyActive;
            dto.RecentlyActiveRate = Percent(recentlyActive, dto.ActiveCustomers);
            dto.RepeatCustomers = repeatCount;
            dto.RecurringPlanCustomers = recurringCount;

            dto.NewCustomerOrders = rows.Count(r => newIds.Contains(r.UserId));
            dto.ReturningCustomerOrders = dto.TotalOrders - dto.NewCustomerOrders;
            dto.NewCustomerSpend = decimal.Round(rows.Where(r => newIds.Contains(r.UserId)).Sum(r => r.Spend), 2);
            dto.ReturningCustomerSpend = decimal.Round(dto.TotalSpend - dto.NewCustomerSpend, 2);

            dto.ReturningRate = Percent(dto.ReturningCustomers, dto.ActiveCustomers);
            dto.NewRate = Percent(dto.NewCustomers, dto.ActiveCustomers);
            dto.RepeatRate = Percent(dto.RepeatCustomers, dto.ActiveCustomers);
            dto.RecurringPlanRate = Percent(dto.RecurringPlanCustomers, dto.ActiveCustomers);
            dto.RepeatOrderShare = Percent(dto.ReturningCustomerOrders, dto.TotalOrders);

            dto.OrdersPerCustomer = Ratio(dto.TotalOrders, dto.ActiveCustomers);
            dto.AverageOrderValue = Ratio(dto.TotalSpend, dto.TotalOrders);
            dto.SpendPerCustomer = Ratio(dto.TotalSpend, dto.ActiveCustomers);
            dto.NewCustomerAov = Ratio(dto.NewCustomerSpend, dto.NewCustomerOrders);
            dto.ReturningCustomerAov = Ratio(dto.ReturningCustomerSpend, dto.ReturningCustomerOrders);

            // ── Retention against the window immediately before this one, same length ──
            // Deliberately a different question from Returning: Returning asks "had they EVER
            // booked before", retention asks "of the people who were here LAST period, how many
            // came back this one" — the number that actually moves month to month.
            var previous = PreviousWindow(from, to);
            if (previous != null)
            {
                var prevRows = await LoadBookings(previous.Value.From, previous.Value.To);
                var prevIds = prevRows.Select(r => r.UserId).ToHashSet();
                dto.PreviousActiveCustomers = prevIds.Count;
                dto.RetainedCustomers = prevIds.Count(id => activeIds.Contains(id));
                dto.LapsedCustomers = dto.PreviousActiveCustomers - dto.RetainedCustomers;
                dto.RetentionRate = Percent(dto.RetainedCustomers, dto.PreviousActiveCustomers);
                dto.ChurnRate = dto.PreviousActiveCustomers > 0 ? 100m - dto.RetentionRate : 0m;
            }

            // ── Registrations: a different cohort entirely (User.CreatedAt, an order optional) ──
            // IsActive is NOT applied: blocking a customer today must not retroactively delete them
            // from a past month's signup count. Soft-deleted rows are excluded because they are gone.
            var signupQuery = _context.Users.Where(u => u.Role == UserRole.Customer && !u.IsDeleted);
            if (from.HasValue) signupQuery = signupQuery.Where(u => u.CreatedAt >= from.Value.Date);
            if (to.HasValue) signupQuery = signupQuery.Where(u => u.CreatedAt < to.Value.Date.AddDays(1));
            var signupIds = await signupQuery.Select(u => u.Id).ToListAsync();
            dto.Signups = signupIds.Count;
            dto.SignupsWhoBooked = signupIds.Count == 0 ? 0 : await _context.Orders
                .Where(OrderBookedFilter.IsRealBooking)
                .Where(o => signupIds.Contains(o.UserId))
                .Select(o => o.UserId)
                .Distinct()
                .CountAsync();
            dto.ActivationRate = Percent(dto.SignupsWhoBooked, dto.Signups);

            dto.Frequency = FrequencyBuckets
                .Select(label =>
                {
                    frequency.TryGetValue(label, out var acc);
                    return new CustomerFrequencyBucketDto
                    {
                        Label = label,
                        Customers = acc.Customers,
                        Orders = acc.Orders,
                        Spend = decimal.Round(acc.Spend, 2)
                    };
                })
                .ToList();

            dto.TopCustomers = await BuildTopCustomers(byCustomer, returningIds);

            await ApplyMedianRebookingGap(dto, to);
            await ApplyFollowUpMetrics(dto, byCustomer, returningIds, from, to);

            return Ok(dto);
        }

        /// <summary>
        /// "Customers typically rebook every N days" — the median gap between consecutive bookings
        /// over the twelve months ENDING WITH THE SELECTED WINDOW, so a historical view describes
        /// its own year rather than the one ending today.
        ///
        /// Median rather than mean: at these volumes a single customer returning after two years
        /// would drag an average by days. Deliberately wider than the selected period — a month
        /// holds roughly nine gaps, far too thin to report — which is also why this figure is the
        /// same in every compared column and is not offered as a comparison row.
        /// </summary>
        private async Task ApplyMedianRebookingGap(CustomerStatisticsDto dto, DateTime? to)
        {
            var windowEnd = (to?.Date ?? NyTimeHelper.NowNy.Date);
            var windowStart = windowEnd.AddMonths(-MedianWindowMonths);
            dto.MedianWindowFrom = windowStart;
            dto.MedianWindowTo = windowEnd;

            var rows = await LoadBookings(windowStart, windowEnd);

            // Every consecutive pair, per customer. A customer with one booking contributes no gap,
            // which is correct: they have not told us anything about their cadence yet.
            var gaps = new List<double>();
            foreach (var g in rows.GroupBy(r => r.UserId))
            {
                var dates = g.Select(r => r.ServiceDate.Date).OrderBy(d => d).ToList();
                for (var i = 1; i < dates.Count; i++)
                    gaps.Add((dates[i] - dates[i - 1]).TotalDays);
            }

            dto.MedianGapSampleSize = gaps.Count;
            dto.MedianDaysBetweenBookings = gaps.Count >= MinReportableSample ? Median(gaps) : null;
        }

        /// <summary>Middle value, or the mean of the middle two on an even-sized sample.</summary>
        private static decimal Median(List<double> values)
        {
            var sorted = values.OrderBy(v => v).ToList();
            var mid = sorted.Count / 2;
            var median = sorted.Count % 2 == 1
                ? sorted[mid]
                : (sorted[mid - 1] + sorted[mid]) / 2d;
            return decimal.Round((decimal)median, 1);
        }

        /// <summary>
        /// Month-by-month history for the trend chart. Same definitions as the single-window
        /// report above, evaluated once per calendar month, so a point here matches what picking
        /// that month in the period filter would show.
        /// </summary>
        [HttpGet("customer-statistics/trend")]
        [RequirePageView(AdminViewablePages.CustomerStats)]
        public async Task<ActionResult<List<CustomerTrendPointDto>>> GetCustomerTrend(
            [FromQuery] int months = DefaultTrendMonths)
        {
            months = Math.Clamp(months, 1, MaxTrendMonths);

            var todayNy = NyTimeHelper.NowNy.Date;
            var firstMonth = new DateTime(todayNy.Year, todayNy.Month, 1).AddMonths(-(months - 1));

            var rows = await LoadBookings(firstMonth, null);

            // First-ever booking per customer, over ALL history — a customer new to March is one
            // whose first booking is in March, not one merely absent from the loaded window.
            var ids = rows.Select(r => r.UserId).Distinct().ToList();
            var firstEver = await FirstBookingByUser(ids);

            var points = new List<CustomerTrendPointDto>(months);
            for (var i = 0; i < months; i++)
            {
                var start = firstMonth.AddMonths(i);
                var end = start.AddMonths(1);
                var monthRows = rows.Where(r => r.ServiceDate >= start && r.ServiceDate < end).ToList();
                var groups = monthRows.GroupBy(r => r.UserId).ToList();

                var active = groups.Count;
                var newCount = groups.Count(g =>
                    firstEver.TryGetValue(g.Key, out var first) && first >= start && first < end);

                points.Add(new CustomerTrendPointDto
                {
                    MonthStart = start,
                    Label = start.ToString("MMM yyyy"),
                    ActiveCustomers = active,
                    NewCustomers = newCount,
                    ReturningCustomers = active - newCount,
                    RepeatCustomers = groups.Count(g => g.Count() >= 2),
                    Orders = monthRows.Count,
                    Spend = decimal.Round(monthRows.Sum(r => r.Spend), 2),
                    ReturningRate = Percent(active - newCount, active)
                });
            }

            return Ok(points);
        }

        /// <summary>
        /// Credits the window's bookings to CRM follow-ups. Answers "of the customers who came
        /// back, how many had we actually chased?".
        ///
        /// Two limits that are properties of the data, not bugs, and that the UI states out loud:
        ///
        ///  1. <b>It is correlation, not cause.</b> A logged call before a booking is evidence the
        ///     follow-up may have worked, never proof — a customer who was going to rebook anyway
        ///     still counts if someone happened to call them. Read it as "how much of our repeat
        ///     business we are actively touching", not as a conversion rate.
        ///  2. <b>It only sees outreach that was LOGGED.</b> An admin who rings a customer from
        ///     their own phone and writes nothing in the CRM is invisible here, so this figure is a
        ///     FLOOR. It rises when the team logs more, which is worth knowing before reading a low
        ///     number as "follow-ups don't work".
        ///
        /// Identity comes from <see cref="LeadCustomerMatcher"/> — leads captured from the contact
        /// form or live chat carry no ClientId, so most matches land on email or phone.
        /// </summary>
        private async Task ApplyFollowUpMetrics(
            CustomerStatisticsDto dto,
            List<IGrouping<int, BookingRow>> byCustomer,
            HashSet<int> returningIds,
            DateTime? from,
            DateTime? to)
        {
            // Effort, measured over the calendar window rather than against any booking: outreach
            // to prospects who never book is still outreach, and hiding it would flatter the team.
            var effort = _context.LeadActivities.Where(a => FollowUpActivityTypes.Contains(a.Type));
            if (from.HasValue) effort = effort.Where(a => a.CreatedAt >= from.Value.Date);
            if (to.HasValue) effort = effort.Where(a => a.CreatedAt < to.Value.Date.AddDays(1));

            dto.FollowUpsLogged = await effort.CountAsync();
            dto.LeadsFollowedUp = await effort.Select(a => a.LeadId).Distinct().CountAsync();

            // Everything below is per-customer, so with nobody served there is nothing to credit.
            if (byCustomer.Count == 0)
            {
                dto.ReturningWithoutFollowUp = dto.ReturningCustomers;
                return;
            }

            // The earliest booking decision in the window sets how far back outreach can matter.
            var earliestBookedAt = byCustomer.Min(g => g.Min(r => r.BookedAt));
            var latestBookedAt = byCustomer.Max(g => g.Max(r => r.BookedAt));
            var lookbackStart = earliestBookedAt.AddDays(-FollowUpLookbackDays);

            var customerIds = byCustomer.Select(g => g.Key).ToList();
            var identities = await _context.Users
                .Where(u => customerIds.Contains(u.Id))
                .Select(u => new { u.Id, u.Email, u.Phone })
                .ToListAsync();
            var index = LeadCustomerMatcher.BuildIndex(
                identities.Select(u => new LeadCustomerMatcher.CustomerIdentity(u.Id, u.Email, u.Phone)));

            var outreach = await _context.LeadActivities
                .Where(a => FollowUpActivityTypes.Contains(a.Type))
                .Where(a => a.CreatedAt >= lookbackStart && a.CreatedAt <= latestBookedAt)
                .Select(a => new
                {
                    a.CreatedAt,
                    a.Lead.ClientId,
                    a.Lead.Email,
                    a.Lead.Phone
                })
                .ToListAsync();

            var touchesByCustomer = new Dictionary<int, List<DateTime>>();
            foreach (var touch in outreach)
            {
                if (!index.TryMatch(touch.ClientId, touch.Email, touch.Phone, out var customerId))
                    continue;
                if (!touchesByCustomer.TryGetValue(customerId, out var list))
                    touchesByCustomer[customerId] = list = new List<DateTime>();
                list.Add(touch.CreatedAt);
            }

            var followedUp = 0;
            var returningFollowedUp = 0;
            var assistedSpend = 0m;

            foreach (var g in byCustomer)
            {
                if (!touchesByCustomer.TryGetValue(g.Key, out var touches)) continue;

                // Their first booking DECISION in the window — the one an earlier call could have
                // produced. A later booking in the same window is that customer already retained.
                var decidedAt = g.Min(r => r.BookedAt);
                var windowOpens = decidedAt.AddDays(-FollowUpLookbackDays);
                if (!touches.Any(t => t >= windowOpens && t <= decidedAt)) continue;

                followedUp++;
                assistedSpend += g.Sum(r => r.Spend);
                if (returningIds.Contains(g.Key)) returningFollowedUp++;
            }

            dto.FollowedUpCustomers = followedUp;
            dto.ReturningAfterFollowUp = returningFollowedUp;
            dto.ReturningWithoutFollowUp = dto.ReturningCustomers - returningFollowedUp;
            dto.FollowUpAssistedRate = Percent(returningFollowedUp, dto.ReturningCustomers);
            dto.FollowUpAssistedSpend = decimal.Round(assistedSpend, 2);
        }

        // ───── helpers ─────

        /// <summary>Order-count buckets of the frequency breakdown, in display order.</summary>
        private static readonly string[] FrequencyBuckets = { "1", "2", "3", "4+" };

        private async Task<List<BookingRow>> LoadBookings(DateTime? from, DateTime? to)
        {
            var query = _context.Orders.Where(OrderBookedFilter.IsRealBooking);
            if (from.HasValue) query = query.Where(o => o.ServiceDate >= from.Value.Date);
            if (to.HasValue) query = query.Where(o => o.ServiceDate < to.Value.Date.AddDays(1));

            var rows = await query
                .Select(o => new
                {
                    o.UserId,
                    o.ServiceDate,
                    o.OrderDate,
                    Spend = o.Total - o.TotalRefundedAmount,
                    o.SubscriptionId,
                    // The tier the ORDER recorded. Read off the order rather than off the customer,
                    // because User.SubscriptionId is today's plan and would misreport history.
                    TierDays = o.Subscription != null ? (int?)o.Subscription.SubscriptionDays : null
                })
                .ToListAsync();

            return rows
                .Select(r => new BookingRow(
                    r.UserId, r.ServiceDate, r.OrderDate, r.Spend,
                    RecurringPlanRule.IsRecurringOrder(r.SubscriptionId, r.TierDays)))
                .ToList();
        }

        /// <summary>
        /// Latest real booking BEFORE the window, per customer. Presence in the result means the
        /// customer is returning; absence means this window holds their first booking ever.
        /// </summary>
        private async Task<Dictionary<int, DateTime>> LoadLastOrderBefore(HashSet<int> userIds, DateTime? from)
        {
            if (!from.HasValue || userIds.Count == 0) return new Dictionary<int, DateTime>();

            var ids = userIds.ToList();
            var cutoff = from.Value.Date;
            var rows = await _context.Orders
                .Where(OrderBookedFilter.IsRealBooking)
                .Where(o => ids.Contains(o.UserId) && o.ServiceDate < cutoff)
                .GroupBy(o => o.UserId)
                .Select(g => new { UserId = g.Key, Last = g.Max(o => o.ServiceDate) })
                .ToListAsync();

            return rows.ToDictionary(r => r.UserId, r => r.Last);
        }

        private async Task<Dictionary<int, DateTime>> FirstBookingByUser(List<int> userIds)
        {
            if (userIds.Count == 0) return new Dictionary<int, DateTime>();

            var rows = await _context.Orders
                .Where(OrderBookedFilter.IsRealBooking)
                .Where(o => userIds.Contains(o.UserId))
                .GroupBy(o => o.UserId)
                .Select(g => new { UserId = g.Key, First = g.Min(o => o.ServiceDate) })
                .ToListAsync();

            return rows.ToDictionary(r => r.UserId, r => r.First);
        }

        private async Task<List<CustomerStatsTopCustomerDto>> BuildTopCustomers(
            List<IGrouping<int, BookingRow>> byCustomer, HashSet<int> returningIds)
        {
            var top = byCustomer
                .Select(g => new { UserId = g.Key, Orders = g.Count(), Spend = g.Sum(r => r.Spend) })
                .OrderByDescending(x => x.Spend)
                .ThenByDescending(x => x.Orders)
                .Take(TopCustomerCount)
                .ToList();
            if (top.Count == 0) return new List<CustomerStatsTopCustomerDto>();

            var ids = top.Select(t => t.UserId).ToList();
            var names = await _context.Users
                .Where(u => ids.Contains(u.Id))
                .Select(u => new { u.Id, u.FirstName, u.LastName, u.Email, u.IsNoEmailUser })
                .ToListAsync();
            var nameMap = names.ToDictionary(n => n.Id);

            return top.Select(t =>
            {
                nameMap.TryGetValue(t.UserId, out var u);
                return new CustomerStatsTopCustomerDto
                {
                    UserId = t.UserId,
                    FullName = u == null ? $"Customer #{t.UserId}" : $"{u.FirstName} {u.LastName}".Trim(),
                    // A no-email customer's address is a generated non-routable placeholder that
                    // must never be displayed (see NoEmailHelper) — blank it rather than print it.
                    Email = u == null || u.IsNoEmailUser ? string.Empty : u.Email,
                    Orders = t.Orders,
                    Spend = decimal.Round(t.Spend, 2),
                    IsReturning = returningIds.Contains(t.UserId)
                };
            }).ToList();
        }

        /// <summary>
        /// The window of the same length ending the day before this one starts. Null for an
        /// open-ended window, which has no comparable predecessor.
        /// </summary>
        private static (DateTime From, DateTime To)? PreviousWindow(DateTime? from, DateTime? to)
        {
            if (!from.HasValue || !to.HasValue) return null;

            var start = from.Value.Date;
            var end = to.Value.Date;
            if (end < start) return null;

            var lengthDays = (int)(end - start).TotalDays + 1;
            return (start.AddDays(-lengthDays), start.AddDays(-1));
        }

        private static decimal Percent(int part, int whole) =>
            whole <= 0 ? 0m : decimal.Round(part * 100m / whole, 1);

        private static decimal Ratio(decimal total, int count) =>
            count <= 0 ? 0m : decimal.Round(total / count, 2);
    }
}
