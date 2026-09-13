using System.Reflection;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Helpers.Recurring;
using DreamCleaningBackend.Models;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// RECURRING SERIES: which dates exist, and who may pay for them.
    ///
    /// Both halves are pure and clock-injected on purpose, so the rules that actually matter —
    /// month-end clamping, anchor-relative stepping, the sequential payment unlock and the
    /// 24-hour cooling-off window — are asserted directly rather than through a generated order
    /// and a database.
    /// </summary>
    public class RecurringOrderRulesTests
    {
        private static readonly DateTime Today = new(2026, 10, 1);

        // ── A1 / A2: the ordinary intervals ───────────────────────────────────────────────────

        [Fact]
        public void EveryTwoWeeks_FillsTheRollingThirtyDayHorizon()
        {
            // Anchored on the day the horizon opens, so the anchor itself is the first occurrence.
            var dates = RecurrenceCalculator.OccurrencesWithinHorizon(
                anchor: new DateTime(2026, 10, 1),
                unit: RecurrenceIntervalUnit.Weeks,
                intervalValue: 2,
                today: Today);

            Assert.Equal(
                new[]
                {
                    new DateTime(2026, 10, 1),
                    new DateTime(2026, 10, 15),
                    new DateTime(2026, 10, 29)
                },
                dates);
        }

        [Fact]
        public void EveryThreeDays_IsSupportedAndSpacedCorrectly()
        {
            var dates = RecurrenceCalculator.OccurrencesWithinHorizon(
                anchor: new DateTime(2026, 10, 2),
                unit: RecurrenceIntervalUnit.Days,
                intervalValue: 3,
                today: Today);

            Assert.Equal(new DateTime(2026, 10, 2), dates.First());
            Assert.All(dates.Zip(dates.Skip(1)), pair =>
                Assert.Equal(3, (pair.Second - pair.First).TotalDays));

            // The horizon is inclusive of today + 30 days and nothing beyond it.
            Assert.All(dates, d => Assert.True(d <= Today.AddDays(30)));
        }

        [Fact]
        public void AnOccurrenceInThePastIsNeverGenerated()
        {
            var dates = RecurrenceCalculator.OccurrencesWithinHorizon(
                anchor: new DateTime(2026, 1, 5),
                unit: RecurrenceIntervalUnit.Weeks,
                intervalValue: 1,
                today: Today);

            Assert.NotEmpty(dates);
            Assert.All(dates, d => Assert.True(d >= Today));
        }

        // ── A3: safe month arithmetic ─────────────────────────────────────────────────────────

        [Fact]
        public void MonthlyFromTheThirtyFirst_ClampsToFebruaryAndThenRecovers()
        {
            var anchor = new DateTime(2026, 1, 31);

            // February clamps...
            Assert.Equal(new DateTime(2026, 2, 28),
                RecurrenceCalculator.AddInterval(anchor, RecurrenceIntervalUnit.Months, 1, 1));

            // ...and March goes back to the 31st, because every step is measured from the ANCHOR.
            // Walking forward from the clamped date instead would give March 28 and the customer
            // would silently lose their end-of-month slot forever.
            Assert.Equal(new DateTime(2026, 3, 31),
                RecurrenceCalculator.AddInterval(anchor, RecurrenceIntervalUnit.Months, 1, 2));

            Assert.Equal(new DateTime(2026, 4, 30),
                RecurrenceCalculator.AddInterval(anchor, RecurrenceIntervalUnit.Months, 1, 3));
        }

        [Fact]
        public void MonthlyClampsToTheTwentyNinthInALeapYear()
        {
            Assert.Equal(new DateTime(2028, 2, 29),
                RecurrenceCalculator.AddInterval(
                    new DateTime(2028, 1, 31), RecurrenceIntervalUnit.Months, 1, 1));
        }

        [Fact]
        public void EveryTwoMonths_StepsTwoWholeMonths()
        {
            var anchor = new DateTime(2026, 10, 15);

            Assert.Equal(new DateTime(2026, 12, 15),
                RecurrenceCalculator.AddInterval(anchor, RecurrenceIntervalUnit.Months, 2, 1));
            Assert.Equal(new DateTime(2027, 2, 15),
                RecurrenceCalculator.AddInterval(anchor, RecurrenceIntervalUnit.Months, 2, 2));
        }

        // ── A4: daily is out of scope, and is REFUSED rather than half-supported ───────────────

        [Fact]
        public void EveryOneDay_IsRejected()
        {
            var error = RecurrenceCalculator.Validate(RecurrenceIntervalUnit.Days, 1);

            Assert.NotNull(error);
            Assert.Contains("Daily recurrence is not supported", error);
            Assert.False(RecurrenceCalculator.IsValid(RecurrenceIntervalUnit.Days, 1));

            // And it generates nothing even if a row somehow carried it — the generator asks the
            // same validator before it does anything.
            Assert.Empty(RecurrenceCalculator.OccurrencesWithinHorizon(
                Today, RecurrenceIntervalUnit.Days, 1, Today));
        }

        [Fact]
        public void TwoDaysAndUpAreAllowed()
        {
            Assert.Null(RecurrenceCalculator.Validate(RecurrenceIntervalUnit.Days, 2));
            Assert.Null(RecurrenceCalculator.Validate(RecurrenceIntervalUnit.Days, 3));

            // One WEEK is not the daily case and must stay allowed.
            Assert.Null(RecurrenceCalculator.Validate(RecurrenceIntervalUnit.Weeks, 1));
            Assert.Null(RecurrenceCalculator.Validate(RecurrenceIntervalUnit.Months, 1));
        }

        [Fact]
        public void AZeroOrNegativeIntervalIsRejected()
        {
            Assert.NotNull(RecurrenceCalculator.Validate(RecurrenceIntervalUnit.Weeks, 0));
            Assert.NotNull(RecurrenceCalculator.Validate(RecurrenceIntervalUnit.Weeks, -3));
        }

        // ── A5: idempotence ───────────────────────────────────────────────────────────────────

        [Fact]
        public void TheSameHorizonAlwaysProducesTheSameDates()
        {
            // The generator's idempotence is ultimately the unique index on
            // (RecurringSeriesId, RecurrenceOccurrenceDate), but that only holds if the date SET
            // is stable — a calculator that drifted would produce new "unseen" dates every pass
            // and the index would never fire.
            var first = RecurrenceCalculator.OccurrencesWithinHorizon(
                new DateTime(2026, 9, 3), RecurrenceIntervalUnit.Weeks, 2, Today);

            var second = RecurrenceCalculator.OccurrencesWithinHorizon(
                new DateTime(2026, 9, 3), RecurrenceIntervalUnit.Weeks, 2, Today);

            Assert.Equal(first, second);
        }

        [Fact]
        public void TheIdempotencyKeyIsPartOfTheOrderModel()
        {
            // Structural: the duplicate guard is a UNIQUE INDEX on these two columns, so they have
            // to exist and stay nullable (every non-recurring order carries null for both, and
            // MySQL never treats two NULLs as equal).
            var series = typeof(Order).GetProperty(nameof(Order.RecurringSeriesId));
            var occurrence = typeof(Order).GetProperty(nameof(Order.RecurrenceOccurrenceDate));

            Assert.NotNull(series);
            Assert.NotNull(occurrence);
            Assert.Equal(typeof(int?), series!.PropertyType);
            Assert.Equal(typeof(DateTime?), occurrence!.PropertyType);
        }

        // ── A6: an ordinary order is untouched ────────────────────────────────────────────────

        [Fact]
        public void AnOrdinaryOrderIsNotPartOfAnySeries()
        {
            var order = new Order();

            Assert.Null(order.RecurringSeriesId);
            Assert.Null(order.RecurrenceOccurrenceDate);
            Assert.False(order.IsGeneratedByRecurringSeries);

            // And the new invoice columns default to "nothing has happened", so no existing
            // reporting query changes its answer for a legacy row.
            Assert.Null(order.InvoicePaidAt);
            Assert.Null(order.ContractClientId);
            Assert.Null(order.PreInvoiceAllocationTotal);
        }

        [Fact]
        public void ExistingOrdersAreNeverConvertedIntoSeriesAutomatically()
        {
            // Structural, and the requirement is explicit: only an order an admin explicitly
            // configures becomes recurring. Nothing in the codebase may set RecurringSeriesId
            // from a background pass over existing orders, so the ONLY writer is the series
            // service. Asserted by looking for a backfill service, which must not exist.
            var backfill = typeof(DreamCleaningBackend.Services.RecurringOrderSeriesService)
                .Assembly.GetTypes()
                .Where(t => t.Name.Contains("Recurring", StringComparison.OrdinalIgnoreCase)
                            && t.Name.Contains("Backfill", StringComparison.OrdinalIgnoreCase))
                .ToList();

            Assert.Empty(backfill);
        }

        // ── C13 / C14: the sequential payment unlock ──────────────────────────────────────────

        private static RecurringPayableOccurrence Occurrence(
            int id, int day, bool paid = false, bool cancelled = false, decimal due = 150m) => new()
            {
                OrderId = id,
                ServiceDateTime = new DateTime(2026, 10, day, 9, 0, 0),
                IsPaid = paid,
                AmountDue = paid ? 0m : due,
                IsCancelled = cancelled
            };

        [Fact]
        public void OnlyTheNearestUnpaidOccurrenceIsPayableAtFirst()
        {
            var results = RecurringPaymentPolicy.ResolvePayability(new[]
            {
                Occurrence(1, 4),
                Occurrence(2, 18),
                Occurrence(3, 25)
            });

            Assert.True(results.Single(r => r.OrderId == 1).IsPayable);
            Assert.False(results.Single(r => r.OrderId == 2).IsPayable);
            Assert.False(results.Single(r => r.OrderId == 3).IsPayable);

            Assert.Equal(1, results.Single(r => r.OrderId == 1).QueuePosition);
            Assert.Equal(3, results.Single(r => r.OrderId == 3).QueuePosition);
            Assert.NotNull(results.Single(r => r.OrderId == 2).BlockedReason);
        }

        [Fact]
        public void PayingTheNearestOneExposesTheNext()
        {
            var results = RecurringPaymentPolicy.ResolvePayability(new[]
            {
                Occurrence(1, 4, paid: true),
                Occurrence(2, 18),
                Occurrence(3, 25)
            });

            Assert.False(results.Single(r => r.OrderId == 1).IsPayable); // nothing left to pay
            Assert.True(results.Single(r => r.OrderId == 2).IsPayable);
            Assert.False(results.Single(r => r.OrderId == 3).IsPayable);
        }

        [Fact]
        public void ResultsAreOrderedNearestFirstWhateverOrderTheyArriveIn()
        {
            var results = RecurringPaymentPolicy.ResolvePayability(new[]
            {
                Occurrence(3, 25),
                Occurrence(1, 4),
                Occurrence(2, 18)
            });

            Assert.Equal(new[] { 1, 2, 3 }, results.Select(r => r.OrderId));
        }

        [Fact]
        public void ACancelledOccurrenceDoesNotBlockTheOneBehindIt()
        {
            // Waiting for a cancelled cleaning to be paid would strand the rest of the series.
            var results = RecurringPaymentPolicy.ResolvePayability(new[]
            {
                Occurrence(1, 4, cancelled: true),
                Occurrence(2, 18)
            });

            Assert.False(results.Single(r => r.OrderId == 1).IsPayable);
            Assert.True(results.Single(r => r.OrderId == 2).IsPayable);
        }

        // ── C16: pay all upcoming ─────────────────────────────────────────────────────────────

        [Fact]
        public void PayAllCoversEveryUnpaidOccurrenceInServiceDateOrder()
        {
            var ids = RecurringPaymentPolicy.ResolveCombinedPaymentSet(new[]
            {
                Occurrence(3, 25),
                Occurrence(1, 4),
                Occurrence(2, 18, paid: true),
                Occurrence(4, 30, cancelled: true)
            });

            // Paid and cancelled ones are excluded; the rest come back nearest first.
            Assert.Equal(new[] { 1, 3 }, ids);
        }

        [Fact]
        public void PayAllIgnoresTheSequentialGate()
        {
            // The gate stops the page opening with four bills on it. It is not a restriction on
            // somebody who has actively chosen to settle the lot.
            var ids = RecurringPaymentPolicy.ResolveCombinedPaymentSet(new[]
            {
                Occurrence(1, 4), Occurrence(2, 18), Occurrence(3, 25)
            });

            Assert.Equal(3, ids.Count);
        }

        [Fact]
        public void TheCombinedPaymentRequestCannotCarryAnAmountOrOrderIds()
        {
            // Structural, the same shape StartInvoiceCheckoutDto uses: the server derives both
            // from the signed-in customer's own orders, so a tampered request cannot express a
            // total of its choosing or name somebody else's cleaning.
            var names = typeof(StartCombinedPaymentDto).GetProperties().Select(p => p.Name).ToList();

            foreach (var forbidden in new[] { "Amount", "Total", "OrderIds", "UserId", "CustomerId" })
                Assert.DoesNotContain(forbidden, names);
        }

        // ── C15: the 24-hour rule ─────────────────────────────────────────────────────────────

        [Fact]
        public void NoAutomaticRequestWithinTwentyFourHoursOfThePreviousCleaning()
        {
            var previousEnd = new DateTime(2026, 10, 4, 13, 0, 0);   // finished 1pm on the 4th
            var occurrence = new DateTime(2026, 10, 18, 9, 0, 0);

            // Same evening — refused.
            Assert.False(RecurringPaymentPolicy.CanAutomaticallyRequestPayment(
                previousEnd, occurrence, new DateTime(2026, 10, 4, 20, 0, 0)));

            // One minute short — still refused.
            Assert.False(RecurringPaymentPolicy.CanAutomaticallyRequestPayment(
                previousEnd, occurrence, new DateTime(2026, 10, 5, 12, 59, 0)));

            // Exactly 24 hours — allowed.
            Assert.True(RecurringPaymentPolicy.CanAutomaticallyRequestPayment(
                previousEnd, occurrence, new DateTime(2026, 10, 5, 13, 0, 0)));
        }

        [Fact]
        public void TheFirstOccurrenceHasNothingToWaitFor()
        {
            Assert.True(RecurringPaymentPolicy.CanAutomaticallyRequestPayment(
                previousCleaningEnd: null,
                occurrenceServiceDate: new DateTime(2026, 10, 4),
                now: new DateTime(2026, 10, 1)));
        }

        [Fact]
        public void AnUnpaidCleaningThatHasALREADYHappenedMayStillBeChased()
        {
            // The gap rule restrains a request for a FUTURE visit. Collecting on one that has
            // already been delivered is ordinary, and the rule must not block it.
            Assert.True(RecurringPaymentPolicy.CanAutomaticallyRequestPayment(
                previousCleaningEnd: new DateTime(2026, 10, 4, 13, 0, 0),
                occurrenceServiceDate: new DateTime(2026, 10, 4, 9, 0, 0),
                now: new DateTime(2026, 10, 5, 8, 0, 0)));
        }

        [Fact]
        public void TheEarliestRequestTimeIsSurfacedForTheAdminPanel()
        {
            var end = new DateTime(2026, 10, 4, 13, 0, 0);
            Assert.Equal(new DateTime(2026, 10, 5, 13, 0, 0),
                RecurringPaymentPolicy.EarliestAutomaticRequestAt(end));

            Assert.Null(RecurringPaymentPolicy.EarliestAutomaticRequestAt(null));
        }

        // ── B8/B9: copying an assignment never notifies anybody ───────────────────────────────

        [Fact]
        public void ACopiedCleanerAssignmentIsMarkedAutoAssignedAndNotNotified()
        {
            // Shape assertion on the entity, because "was this cleaner told?" is answered by two
            // columns together and the panel's badge depends on both existing.
            var auto = typeof(OrderCleaner).GetProperty(nameof(OrderCleaner.AutoAssignedFromSeriesId));
            var notified = typeof(OrderCleaner).GetProperty(nameof(OrderCleaner.AssignmentNotificationSentAt));

            Assert.NotNull(auto);
            Assert.NotNull(notified);
            Assert.Equal(typeof(int?), auto!.PropertyType);
            Assert.Equal(typeof(DateTime?), notified!.PropertyType);

            // A freshly copied row: assigned, and nobody has been told.
            var row = new OrderCleaner { AutoAssignedFromSeriesId = 7 };
            Assert.Null(row.AssignmentNotificationSentAt);
        }

        [Fact]
        public void TheGeneratorSendsNoCleanerNotificationAtAll()
        {
            // SOURCE-LEVEL, on purpose: what this guards against is somebody "helpfully" wiring
            // the assignment mail into generation months from now, and that is caught by reading
            // what the file reaches for rather than by running it. The existing Send / Resend
            // controls must stay the only thing that contacts a cleaner.
            var source = ReadBackendFile("Services", "RecurringOrderSeriesService.cs");
            var code = StripComments(source);

            foreach (var forbidden in new[]
                     {
                         "SendCleanerAssignmentEmail",
                         "SendAssignmentEmail",
                         "AssignCleanersToOrderAsync",
                         "IEmailService",
                         "ISmsService"
                     })
            {
                Assert.False(code.Contains(forbidden),
                    $"RecurringOrderSeriesService reaches for {forbidden}. Generating a recurring "
                    + "order must never notify a cleaner — the existing Send / Resend controls are "
                    + "the only path.");
            }
        }

        [Fact]
        public void TheGeneratorSendsNoCustomerBookingConfirmationEither()
        {
            var code = StripComments(ReadBackendFile("Services", "RecurringOrderSeriesService.cs"));

            Assert.DoesNotContain("SendCustomerBookingConfirmation", code);
            Assert.DoesNotContain("SendBookingConfirmationSms", code);
        }

        private static string ReadBackendFile(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "DreamCleaningBackend")))
                dir = dir.Parent;

            Assert.NotNull(dir);

            var path = Path.Combine(
                new[] { dir!.FullName, "DreamCleaningBackend" }.Concat(parts).ToArray());

            Assert.True(File.Exists(path), $"{path} was not found.");
            return File.ReadAllText(path);
        }

        private static string StripComments(string source)
        {
            var withoutBlock = System.Text.RegularExpressions.Regex.Replace(
                source, @"/\*.*?\*/", "", System.Text.RegularExpressions.RegexOptions.Singleline);
            return System.Text.RegularExpressions.Regex.Replace(
                withoutBlock, @"//.*?$", "", System.Text.RegularExpressions.RegexOptions.Multiline);
        }
    }
}
