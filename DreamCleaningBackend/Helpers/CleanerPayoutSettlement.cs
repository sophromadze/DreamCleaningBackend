using DreamCleaningBackend.Services;

namespace DreamCleaningBackend.Helpers
{
    /// <summary>
    /// Compares what was actually HANDED OVER on a payout line against what that line is worth
    /// NOW, and answers the only question the Outgoing Payments page cares about once money has
    /// moved: is there anything left to pay?
    ///
    /// It exists because of a real miss (2026-09). Two cleaners were paid $73.50 each for a
    /// 3h30 deep clean; the cleaners then said they had worked four hours, an admin added the
    /// hour in the orders panel, and the payout page dutifully redrew the line at $84.00 — while
    /// still showing it as PAID. The $10.50 a head that was genuinely still owed appeared
    /// nowhere: not on the line, not in the order's pill, not in the header's "still to pay".
    /// The page's whole purpose is that nothing owed goes unnoticed, so a line whose value has
    /// outgrown its payment has to read as unfinished.
    ///
    /// Three rules hold it together:
    ///
    /// 1. **Settlement is DERIVED, never a flip of <c>IsPaid</c>.** The obvious fix — set
    ///    IsPaid back to false when the figure moves — would throw away
    ///    <see cref="Models.OrderCleaner.PaidAmount"/>, and that frozen figure is the only
    ///    record of what left the company. Without it the difference cannot be computed at all
    ///    and the page would ask for the FULL $84.00 a second time. So IsPaid keeps meaning
    ///    "money has gone out on this line" and this type says whether that was enough.
    /// 2. **It only ever looks BACKWARD at a payment that happened.** A line nobody has paid is
    ///    not a shortfall — its outstanding amount is simply its payout, exactly as before, and
    ///    <see cref="Result.IsTopUp"/> is false. Nothing anywhere may show "additional to pay"
    ///    wording for a cleaner who has not been paid a first time.
    /// 3. **The mirror case is reported, not netted.** Hours edited DOWN after payment leave the
    ///    line overpaid rather than owing a negative amount. Outstanding floors at zero and the
    ///    excess is reported separately, because "we owe them −$10.50" is not something anybody
    ///    can act on, and quietly deducting it from another line would move money between two
    ///    people who were paid separately.
    /// </summary>
    public static class CleanerPayoutSettlement
    {
        public class Result
        {
            /// <summary>Has any money gone out on this line at all?</summary>
            public bool IsPaid { get; set; }

            /// <summary>Total handed over so far — the frozen figure, top-ups included. 0 when unpaid.</summary>
            public decimal PaidAmount { get; set; }

            /// <summary>What the line is worth now: salary + tips at the order's current figures.</summary>
            public decimal CurrentPayout { get; set; }

            /// <summary>
            /// Still to hand over. The full payout on an unpaid line; the SHORTFALL on a paid
            /// one. Never negative.
            /// </summary>
            public decimal Outstanding { get; set; }

            /// <summary>
            /// Handed over ABOVE what the line is now worth — hours or a rate edited down after
            /// payment. Reported for a human to sort out; nothing subtracts it automatically.
            /// </summary>
            public decimal Overpaid { get; set; }

            /// <summary>Nothing left to pay: money went out and it covered the line.</summary>
            public bool IsSettled { get; set; }

            /// <summary>
            /// Paid once already, and worth more now. This — not <see cref="Outstanding"/> being
            /// positive — is what turns on the "still to pay" wording, so an ordinary unpaid
            /// line never claims a top-up.
            /// </summary>
            public bool IsTopUp { get; set; }
        }

        /// <summary>
        /// The single rule. <paramref name="paidAmount"/> is the frozen figure and may be null on
        /// a line flagged paid by older data — treated as zero, which reports the whole payout as
        /// outstanding rather than silently writing it off.
        /// </summary>
        public static Result Resolve(bool isPaid, decimal? paidAmount, decimal currentPayout)
        {
            var payout = OrderPricingCalculator.Round2(currentPayout);

            if (!isPaid)
            {
                return new Result
                {
                    IsPaid = false,
                    PaidAmount = 0m,
                    CurrentPayout = payout,
                    Outstanding = Math.Max(0m, payout),
                    Overpaid = 0m,
                    IsSettled = false,
                    IsTopUp = false
                };
            }

            var paid = OrderPricingCalculator.Round2(paidAmount ?? 0m);
            var difference = OrderPricingCalculator.Round2(payout - paid);

            return new Result
            {
                IsPaid = true,
                PaidAmount = paid,
                CurrentPayout = payout,
                Outstanding = Math.Max(0m, difference),
                Overpaid = Math.Max(0m, -difference),
                IsSettled = difference <= 0m,
                IsTopUp = difference > 0m
            };
        }
    }
}
