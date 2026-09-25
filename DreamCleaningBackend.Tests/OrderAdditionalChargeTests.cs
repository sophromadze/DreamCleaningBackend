using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// "ADDITIONAL AMOUNT DUE" MUST NOT DOUBLE WHEN AN ORDER IS EDITED DOWN AND BACK UP.
    ///
    /// Order #359 (September 2026) was billed $4,487.30 for a $2,243.65 increase — exactly twice —
    /// because a price DECREASE stored a negative <see cref="OrderUpdateHistory.AdditionalAmount"/>
    /// and, being negative, was marked <c>IsPaid</c> by the <c>&lt;= 0.01</c> rule. It therefore
    /// counted as money the customer had handed over, and <c>delta − alreadyPaid</c> subtracted a
    /// negative.
    ///
    /// Both halves of the fix are asserted here, because neither covers the other's case:
    /// <list type="bullet">
    /// <item><b>Write side</b> — <see cref="OrderAdditionalCharge.Collectable"/> floors new rows at
    /// zero, so the shape cannot be created again.</item>
    /// <item><b>Read side</b> — only POSITIVE paid rows count as collected, so the negative rows
    /// already sitting in production cannot trigger it either.</item>
    /// </list>
    ///
    /// Everything runs against the pure arithmetic, no database — same reason
    /// <c>CleanerPayrollCalculator</c>'s split is asserted that way.
    /// </summary>
    public class OrderAdditionalChargeTests
    {
        private static DateTime At(int minutesFromStart) =>
            new DateTime(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc).AddMinutes(minutesFromStart);

        /// <summary>
        /// An order as it stands NOW. <paramref name="initialTotal"/> is the booking-time snapshot,
        /// which is stamped when the order is PAID (see StripeWebhookController) — not when it is
        /// created. That detail is the whole reason #359 owed anything at all: the customer paid
        /// $500.00, so $500.00 is what "originally paid" means for that order.
        /// </summary>
        private static Order OrderAt(decimal currentTotal, decimal initialTotal,
            decimal tips = 0m, decimal initialTips = 0m) =>
            new Order
            {
                Id = 359,
                Total = currentTotal,
                Tips = tips,
                CompanyDevelopmentTips = 0m,
                InitialTotal = initialTotal,
                InitialTips = initialTips,
                InitialCompanyDevelopmentTips = 0m,
                IsPaid = true
            };

        private static OrderUpdateHistory Row(decimal originalTotal, decimal newTotal,
            decimal additionalAmount, bool isPaid, int minute) =>
            new OrderUpdateHistory
            {
                OrderId = 359,
                UpdatedAt = At(minute),
                OriginalTotal = originalTotal,
                NewTotal = newTotal,
                OriginalTips = 0m,
                OriginalCompanyDevelopmentTips = 0m,
                AdditionalAmount = additionalAmount,
                IsPaid = isPaid
            };

        /// <summary>The arithmetic exactly as it was written at all six call sites before the fix,
        /// kept here so the tests below can state what the bug produced rather than describe it.</summary>
        private static decimal LegacyOutstanding(Order order, IEnumerable<OrderUpdateHistory> rows)
        {
            var current = order.Total - order.Tips - order.CompanyDevelopmentTips;
            var original = order.InitialTotal - order.InitialTips - order.InitialCompanyDevelopmentTips;
            var delta = Math.Max(0m, current - original);
            var alreadyPaid = rows.Where(h => h.IsPaid).Sum(h => h.AdditionalAmount);
            return Math.Round(Math.Max(0m, delta - alreadyPaid), 2);
        }

        // ── Order #359 ────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// THE CASE. Created at $2,743.65, edited DOWN to $500.00, paid at $500.00, then edited
        /// back UP to $2,743.65. The customer owes the $2,243.65 difference once.
        ///
        /// The down-edit's row is the legacy shape — a negative amount, marked paid — so this is
        /// the READ-side half: the rows already in the database cannot produce the double bill.
        /// </summary>
        [Fact]
        public void Order359_EditedDownThenBackUp_OwesTheIncreaseOnce()
        {
            var order = OrderAt(currentTotal: 2743.65m, initialTotal: 500.00m);
            var history = new[]
            {
                Row(originalTotal: 2743.65m, newTotal: 500.00m, additionalAmount: -2243.65m, isPaid: true, minute: 0),
                Row(originalTotal: 500.00m, newTotal: 2743.65m, additionalAmount: 2243.65m, isPaid: false, minute: 10)
            };

            Assert.Equal(2243.65m, OrderAdditionalCharge.Outstanding(order, history));

            // What the customer's payment page and email actually showed: 2243.65 − (−2243.65).
            Assert.Equal(4487.30m, LegacyOutstanding(order, history));
        }

        /// <summary>
        /// The same sequence written the way the code writes rows NOW: the decrease is stored at
        /// zero. The WRITE-side half — and the answer has to be identical either way, or the fix
        /// would depend on which side of the deploy a given order was edited on.
        /// </summary>
        [Fact]
        public void Order359_WithTheDecreaseRowClamped_GivesTheSameAnswer()
        {
            var order = OrderAt(currentTotal: 2743.65m, initialTotal: 500.00m);
            var history = new[]
            {
                Row(originalTotal: 2743.65m, newTotal: 500.00m,
                    additionalAmount: OrderAdditionalCharge.Collectable(-2243.65m), isPaid: true, minute: 0),
                Row(originalTotal: 500.00m, newTotal: 2743.65m, additionalAmount: 2243.65m, isPaid: false, minute: 10)
            };

            Assert.Equal(0m, history[0].AdditionalAmount);
            Assert.Equal(2243.65m, OrderAdditionalCharge.Outstanding(order, history));
            Assert.Equal(2243.65m, LegacyOutstanding(order, history));
        }

        /// <summary>
        /// What the write side stores. The decrease is floored at zero and still marked settled by
        /// the unchanged <c>&lt;= 0.01</c> rule — a row that collects nothing is not a debt. The
        /// row itself is still written, with its real before/after totals: only this one field moves.
        /// </summary>
        [Fact]
        public void ADecreaseIsStoredAtZero_AndIsStillMarkedSettled()
        {
            Assert.Equal(0m, OrderAdditionalCharge.Collectable(-2243.65m));
            Assert.Equal(0m, OrderAdditionalCharge.Collectable(-0.01m));
            Assert.True(OrderAdditionalCharge.Collectable(-2243.65m) <= OrderAdditionalCharge.MinimumCollectableAmount);

            // An increase is untouched — the clamp must not quietly change what a real charge costs.
            Assert.Equal(2243.65m, OrderAdditionalCharge.Collectable(2243.65m));
            Assert.Equal(0.02m, OrderAdditionalCharge.Collectable(0.02m));
        }

        // ── Decreases owe nothing, in either direction ────────────────────────────────────────

        /// <summary>A price cut is not a refund the customer may collect here, and it is certainly
        /// not a negative bill. An order edited down owes zero and stays at zero.</summary>
        [Fact]
        public void APureDecrease_OwesNothing_AndNeverGoesNegative()
        {
            var order = OrderAt(currentTotal: 500.00m, initialTotal: 2743.65m);
            var history = new[]
            {
                Row(originalTotal: 2743.65m, newTotal: 500.00m, additionalAmount: -2243.65m, isPaid: true, minute: 0)
            };

            Assert.Equal(0m, OrderAdditionalCharge.Outstanding(order, history));

            // And the same once the write side stores that decrease as zero.
            var clamped = new[] { Row(originalTotal: 2743.65m, newTotal: 500.00m, additionalAmount: 0m, isPaid: true, minute: 0) };
            Assert.Equal(0m, OrderAdditionalCharge.Outstanding(order, clamped));
        }

        /// <summary>
        /// Down to $500.00, then back up to $1,200.00 — still below the $2,743.65 the customer
        /// paid. Nothing is owed. The delta is floored BEFORE the already-collected subtraction
        /// precisely so a later increase cannot be cancelled against a negative delta.
        /// </summary>
        [Fact]
        public void ADecreaseThenAnIncreaseBelowTheOriginal_OwesNothing()
        {
            var order = OrderAt(currentTotal: 1200.00m, initialTotal: 2743.65m);
            var history = new[]
            {
                Row(originalTotal: 2743.65m, newTotal: 500.00m, additionalAmount: -2243.65m, isPaid: true, minute: 0),
                Row(originalTotal: 500.00m, newTotal: 1200.00m, additionalAmount: 700.00m, isPaid: false, minute: 10)
            };

            Assert.Equal(0m, OrderAdditionalCharge.Outstanding(order, history));

            // The legacy arithmetic billed for the second edit even though the order had got cheaper.
            Assert.Equal(2243.65m, LegacyOutstanding(order, history));
        }

        /// <summary>Back to exactly what was paid: zero. Neither a charge nor a credit.</summary>
        [Fact]
        public void AnOrderReturnedToItsOriginalPrice_OwesNothing()
        {
            var order = OrderAt(currentTotal: 2743.65m, initialTotal: 2743.65m);
            var history = new[]
            {
                Row(originalTotal: 2743.65m, newTotal: 500.00m, additionalAmount: -2243.65m, isPaid: true, minute: 0),
                Row(originalTotal: 500.00m, newTotal: 2743.65m, additionalAmount: 2243.65m, isPaid: false, minute: 10)
            };

            Assert.Equal(0m, OrderAdditionalCharge.Outstanding(order, history));
            // The legacy arithmetic asked for the settled negative row back: delta 0 − (−2,243.65).
            // (It read 4,487.30 in this file, which is order #359's figure — that order was PAID at
            // 500, so its snapshot is 500, not the 2,743.65 this order returned to.)
            Assert.Equal(2243.65m, LegacyOutstanding(order, history));
        }

        // ── Real payments still count ─────────────────────────────────────────────────────────

        /// <summary>
        /// The behaviour the exclusion must NOT break: an increase that was genuinely paid for is
        /// still deducted. Booked at $300.00, raised to $500.00 and the $200.00 collected, then
        /// raised again to $650.00 — $150.00 left to collect, not $350.00.
        /// </summary>
        [Fact]
        public void AGenuineIncrease_WithAPriorPaidAdditional_OwesOnlyTheRemainder()
        {
            var order = OrderAt(currentTotal: 650.00m, initialTotal: 300.00m);
            var history = new[]
            {
                Row(originalTotal: 300.00m, newTotal: 500.00m, additionalAmount: 200.00m, isPaid: true, minute: 0),
                Row(originalTotal: 500.00m, newTotal: 650.00m, additionalAmount: 150.00m, isPaid: false, minute: 10)
            };

            Assert.Equal(150.00m, OrderAdditionalCharge.Outstanding(order, history));
            Assert.Equal(LegacyOutstanding(order, history), OrderAdditionalCharge.Outstanding(order, history));
        }

        /// <summary>Tips the customer already paid at booking are on BOTH sides of the comparison,
        /// so an unchanged tip neither inflates nor hides what is owed.</summary>
        [Fact]
        public void AnUnchangedTip_CancelsOut()
        {
            var order = OrderAt(currentTotal: 750.00m, initialTotal: 350.00m, tips: 50.00m, initialTips: 50.00m);
            var history = new[]
            {
                Row(originalTotal: 350.00m, newTotal: 750.00m, additionalAmount: 400.00m, isPaid: false, minute: 0)
            };

            Assert.Equal(400.00m, OrderAdditionalCharge.Outstanding(order, history));
        }

        /// <summary>
        /// Order #386, 2026-09. Booked and paid at $1,150.00, a $330.00 increase paid by card, then
        /// an admin added a $270.00 tip. The history row said "Unpaid +$270.00" while every surface
        /// computed $0.00 owed — the comparison subtracted tips from both sides, and the tip was the
        /// only thing that moved — so there was no Send button and the payment page said "Nothing
        /// to pay". A tip added after payment is collected like any other increase.
        /// </summary>
        [Fact]
        public void Order386_ATipAddedAfterPayment_IsOwed()
        {
            var order = OrderAt(currentTotal: 1750.00m, initialTotal: 1150.00m, tips: 270.00m, initialTips: 0m);
            var history = new[]
            {
                Row(originalTotal: 1150.00m, newTotal: 1480.00m, additionalAmount: 330.00m, isPaid: true, minute: 0),
                Row(originalTotal: 1480.00m, newTotal: 1750.00m, additionalAmount: 270.00m, isPaid: false, minute: 10)
            };

            Assert.Equal(270.00m, OrderAdditionalCharge.Outstanding(order, history));
        }

        /// <summary>The other half of #386: once the tip is taken back off (undo), nothing is owed
        /// even if the stale row were still there.</summary>
        [Fact]
        public void Order386_TipRemovedAgain_OwesNothing()
        {
            var order = OrderAt(currentTotal: 1480.00m, initialTotal: 1150.00m);
            var history = new[]
            {
                Row(originalTotal: 1150.00m, newTotal: 1480.00m, additionalAmount: 330.00m, isPaid: true, minute: 0),
                Row(originalTotal: 1480.00m, newTotal: 1750.00m, additionalAmount: 270.00m, isPaid: false, minute: 10)
            };

            Assert.Equal(0m, OrderAdditionalCharge.Outstanding(order, history));
        }

        // ── The InitialTotal = 0 fallback ─────────────────────────────────────────────────────

        /// <summary>
        /// Orders whose Initial* columns are all zero — placed before those columns were stamped,
        /// and a large share of production — have no booking snapshot at all. The earliest update
        /// row's OriginalTotal is the only surviving record of the booking price, so the fallback
        /// is load-bearing and has to keep working exactly as it did.
        /// </summary>
        [Fact]
        public void WithNoInitialSnapshot_TheFirstHistoryRowsOriginalTotalIsTheBookingPrice()
        {
            var order = OrderAt(currentTotal: 650.00m, initialTotal: 0m);
            Assert.False(OrderAdditionalCharge.HasInitialSnapshot(order));

            var history = new[]
            {
                Row(originalTotal: 300.00m, newTotal: 500.00m, additionalAmount: 200.00m, isPaid: true, minute: 0),
                Row(originalTotal: 500.00m, newTotal: 650.00m, additionalAmount: 150.00m, isPaid: false, minute: 10)
            };

            // 650.00 − 300.00 (the first row's original) − 200.00 collected.
            Assert.Equal(150.00m, OrderAdditionalCharge.Outstanding(order, history));
        }

        /// <summary>
        /// The fallback reads the EARLIEST row by UpdatedAt, not the first one handed to it. Rows
        /// arrive in whatever order a query returns them, and picking the wrong one silently
        /// measures the delta from the middle of the order's life.
        /// </summary>
        [Fact]
        public void TheFallbackTakesTheEarliestRow_WhateverOrderTheRowsArriveIn()
        {
            var order = OrderAt(currentTotal: 650.00m, initialTotal: 0m);
            var newestFirst = new[]
            {
                Row(originalTotal: 500.00m, newTotal: 650.00m, additionalAmount: 150.00m, isPaid: false, minute: 10),
                Row(originalTotal: 300.00m, newTotal: 500.00m, additionalAmount: 200.00m, isPaid: true, minute: 0)
            };

            Assert.Equal(150.00m, OrderAdditionalCharge.Outstanding(order, newestFirst));
        }

        /// <summary>
        /// The #359 shape on an unsnapshotted order: with no Initial* columns the fallback reads
        /// the $2,743.65 the order was created at, so the customer who never paid a penny of an
        /// increase is asked for nothing — and the negative row still cannot inflate it.
        /// </summary>
        [Fact]
        public void WithNoInitialSnapshot_ADownThenUpEditStillOwesNothing()
        {
            var order = OrderAt(currentTotal: 2743.65m, initialTotal: 0m);
            var history = new[]
            {
                Row(originalTotal: 2743.65m, newTotal: 500.00m, additionalAmount: -2243.65m, isPaid: true, minute: 0),
                Row(originalTotal: 500.00m, newTotal: 2743.65m, additionalAmount: 2243.65m, isPaid: false, minute: 10)
            };

            Assert.Equal(0m, OrderAdditionalCharge.Outstanding(order, history));
        }

        /// <summary>No history at all and no snapshot: there is nothing to compare against, so
        /// nothing is owed. Reads as zero rather than as "the whole total is an additional".</summary>
        [Fact]
        public void WithNeitherASnapshotNorAnyHistory_NothingIsOwed()
        {
            var order = OrderAt(currentTotal: 650.00m, initialTotal: 0m);

            Assert.Equal(0m, OrderAdditionalCharge.Outstanding(order, Array.Empty<OrderUpdateHistory>()));
            Assert.Equal(0m, OrderAdditionalCharge.OriginalTotal(order, null));
        }

        // ── The collected-to-date sum itself ──────────────────────────────────────────────────

        /// <summary>Stated directly, because this one line is the bug: a settled negative row is
        /// not money that arrived.</summary>
        [Fact]
        public void CollectedToDate_CountsPaidPositiveRowsOnly()
        {
            var rows = new[]
            {
                Row(originalTotal: 2743.65m, newTotal: 500.00m, additionalAmount: -2243.65m, isPaid: true, minute: 0),
                Row(originalTotal: 500.00m, newTotal: 700.00m, additionalAmount: 200.00m, isPaid: true, minute: 10),
                Row(originalTotal: 700.00m, newTotal: 900.00m, additionalAmount: 200.00m, isPaid: false, minute: 20),
                Row(originalTotal: 900.00m, newTotal: 900.00m, additionalAmount: 0m, isPaid: true, minute: 30)
            };

            Assert.Equal(200.00m, OrderAdditionalCharge.CollectedToDate(rows));
        }
    }
}
