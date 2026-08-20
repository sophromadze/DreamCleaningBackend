namespace DreamCleaningBackend.DTOs
{
    /// <summary>
    /// Everything the Company → Customers tab reports for ONE window. Every figure is derived
    /// from the Orders table through <see cref="DreamCleaningBackend.Helpers.OrderBookedFilter"/>,
    /// bucketed by <c>ServiceDate</c> — the same basis Statistics and Finances use, so a month
    /// here lines up with the same month there.
    ///
    /// The vocabulary, once, because every rate below hangs off it:
    ///   • <b>Active</b>   — a customer with at least one real booking inside the window.
    ///   • <b>New</b>      — an active customer whose FIRST-EVER real booking falls in the window.
    ///   • <b>Returning</b> — an active customer who had already booked before the window opened.
    ///                        This is "how many customers came back", the headline of the tab.
    ///   • <b>Repeat</b>   — an active customer with 2+ bookings INSIDE the window (a different
    ///                        question from Returning: booked twice this month, not "booked before").
    /// </summary>
    public class CustomerStatisticsDto
    {
        public DateTime? From { get; set; }
        public DateTime? To { get; set; }

        // ── Who was here ──
        public int ActiveCustomers { get; set; }
        public int NewCustomers { get; set; }
        public int ReturningCustomers { get; set; }
        public int RepeatCustomers { get; set; }

        /// <summary>
        /// Returning customers whose previous booking was more than <c>ReactivationDays</c> before
        /// the window opened — customers won back rather than simply still around.
        /// </summary>
        public int ReactivatedCustomers { get; set; }

        // ── Retention ──
        // The headline is RecentlyActive, NOT the period-over-period pair below it. A deep clean or
        // a move-out customer legitimately does not rebook the following month, so month-over-month
        // retention runs under 15% for this business every month and reads to a non-technical
        // reader as collapse. It is kept, clearly labelled, but it is not the retention number.

        /// <summary>
        /// Active customers whose previous booking was within <c>RecentLookbackDays</c> of the
        /// window opening — customers on a real cadence with us. Backward-looking on purpose:
        /// a forward-looking "booked again within 90 days" cohort cannot be measured until 90 days
        /// have passed, which would blank the three most recent months — the same unreadability
        /// this metric exists to remove, just moved.
        /// </summary>
        public int RecentlyActiveCustomers { get; set; }
        /// <summary>RecentlyActiveCustomers over customers served. The headline retention figure.</summary>
        public decimal RecentlyActiveRate { get; set; }

        /// <summary>
        /// MEDIAN days between consecutive bookings over the twelve months ending with this
        /// window — "customers typically rebook every N days". Median, not mean: one customer
        /// returning after two years would drag an average badly at these volumes.
        ///
        /// Anchored to the window's END rather than to today, so opening June 2026 describes the
        /// year to June 2026. Deliberately NOT period-scoped: at ~9 returning customers a month
        /// the per-period sample is too small to be worth reporting, which also means it is the
        /// same figure in every compared column and so is not offered in the comparison table.
        /// Null when the sample is too small to report.
        /// </summary>
        public decimal? MedianDaysBetweenBookings { get; set; }
        /// <summary>Number of consecutive-booking gaps behind the median — its denominator.</summary>
        public int MedianGapSampleSize { get; set; }
        public DateTime MedianWindowFrom { get; set; }
        public DateTime MedianWindowTo { get; set; }

        // Period-over-period, kept for continuity but demoted. Labelled as period-over-period
        // everywhere it renders so nobody reads it as "the" retention rate.
        /// <summary>Active customers of the PREVIOUS window — the retention denominator.</summary>
        public int PreviousActiveCustomers { get; set; }
        /// <summary>Previous-window customers who booked again in this one.</summary>
        public int RetainedCustomers { get; set; }
        /// <summary>Previous-window customers who did NOT book in this one.</summary>
        public int LapsedCustomers { get; set; }

        // ── Rates, all 0–100 ──
        public decimal ReturningRate { get; set; }
        public decimal NewRate { get; set; }
        public decimal RepeatRate { get; set; }
        public decimal RetentionRate { get; set; }
        public decimal ChurnRate { get; set; }
        /// <summary>Share of the window's ORDERS placed by returning customers.</summary>
        public decimal RepeatOrderShare { get; set; }

        // ── Orders & money ──
        public int TotalOrders { get; set; }
        public int NewCustomerOrders { get; set; }
        public int ReturningCustomerOrders { get; set; }
        public decimal OrdersPerCustomer { get; set; }

        /// <summary>
        /// What customers actually paid, net of refunds (<c>Total − TotalRefundedAmount</c>) — the
        /// same tax-inclusive basis the CRM uses for lifetime value. Deliberately NOT the Finances
        /// page's revenue figure, which strips tax and tips out; this tab is about customers, not
        /// about the P&amp;L.
        /// </summary>
        public decimal TotalSpend { get; set; }
        public decimal NewCustomerSpend { get; set; }
        public decimal ReturningCustomerSpend { get; set; }
        public decimal AverageOrderValue { get; set; }
        public decimal SpendPerCustomer { get; set; }
        public decimal NewCustomerAov { get; set; }
        public decimal ReturningCustomerAov { get; set; }

        // ── Registrations (a different cohort: User.CreatedAt, orders optional) ──
        public int Signups { get; set; }
        /// <summary>Of those signups, how many have ever placed a real booking.</summary>
        public int SignupsWhoBooked { get; set; }
        public decimal ActivationRate { get; set; }

        /// <summary>Active customers with at least one window order on a recurring plan.</summary>
        public int RecurringPlanCustomers { get; set; }
        public decimal RecurringPlanRate { get; set; }

        // ── CRM follow-ups ──
        // "Did chasing people bring them back?" A booking counts as followed-up when a Call, Email
        // or SMS was logged against a CRM lead matching that customer in the LookbackDays before
        // they placed the order. See AdminCustomerStatsController for the two caveats that matter:
        // this is correlation, not proof of cause, and it only sees outreach an admin logged.

        /// <summary>Call/Email/SMS activities logged on any lead during the window — outreach effort.</summary>
        public int FollowUpsLogged { get; set; }
        /// <summary>Distinct leads that received one, i.e. how many PEOPLE were chased.</summary>
        public int LeadsFollowedUp { get; set; }
        /// <summary>Active customers whose booking was preceded by a logged follow-up.</summary>
        public int FollowedUpCustomers { get; set; }
        /// <summary>Of the customers who came back, how many we had chased first. The headline.</summary>
        public int ReturningAfterFollowUp { get; set; }
        /// <summary>Returning customers who came back with no logged follow-up behind them.</summary>
        public int ReturningWithoutFollowUp { get; set; }
        /// <summary>ReturningAfterFollowUp as a share of all returning customers.</summary>
        public decimal FollowUpAssistedRate { get; set; }
        /// <summary>Spend from the followed-up customers — what the chasing was worth.</summary>
        public decimal FollowUpAssistedSpend { get; set; }

        /// <summary>How many customers booked once, twice, … inside the window.</summary>
        public List<CustomerFrequencyBucketDto> Frequency { get; set; } = new();

        /// <summary>Biggest spenders of the window, most valuable first.</summary>
        public List<CustomerStatsTopCustomerDto> TopCustomers { get; set; } = new();
    }

    public class CustomerFrequencyBucketDto
    {
        /// <summary>"1", "2", "3", "4+".</summary>
        public string Label { get; set; } = string.Empty;
        public int Customers { get; set; }
        public int Orders { get; set; }
        public decimal Spend { get; set; }
    }

    public class CustomerStatsTopCustomerDto
    {
        public int UserId { get; set; }
        public string FullName { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public int Orders { get; set; }
        public decimal Spend { get; set; }
        /// <summary>False when this window holds their first-ever booking.</summary>
        public bool IsReturning { get; set; }
    }

    /// <summary>One month of the trend chart on the Customers tab.</summary>
    public class CustomerTrendPointDto
    {
        /// <summary>First day of the month (local), so the client can label it however it likes.</summary>
        public DateTime MonthStart { get; set; }
        public string Label { get; set; } = string.Empty;
        public int ActiveCustomers { get; set; }
        public int NewCustomers { get; set; }
        public int ReturningCustomers { get; set; }
        public int RepeatCustomers { get; set; }
        public int Orders { get; set; }
        public decimal Spend { get; set; }
        public decimal ReturningRate { get; set; }
    }
}
