namespace DreamCleaningBackend.Helpers.Recurring
{
    /// <summary>One upcoming recurring cleaning, as far as the payment rules care.</summary>
    public class RecurringPayableOccurrence
    {
        public bool IsOnlinePayable { get; set; } = true;
        public bool PaymentInFlight { get; set; }
        public string? PaymentIntentId { get; set; }
        public int OrderId { get; set; }

        /// <summary>Service date and time, NY wall clock.</summary>
        public DateTime ServiceDateTime { get; set; }

        /// <summary>True once the money for this occurrence has actually arrived.</summary>
        public bool IsPaid { get; set; }

        /// <summary>What is still owed. Zero on a paid or zero-value occurrence.</summary>
        public decimal AmountDue { get; set; }

        /// <summary>Cancelled / refunded occurrences are never payable and never unlock the next.</summary>
        public bool IsCancelled { get; set; }
    }

    /// <summary>The verdict for one occurrence.</summary>
    public class RecurringPayabilityResult
    {
        public int OrderId { get; set; }

        /// <summary>The customer MAY pay this one now (voluntarily, or because it is next).</summary>
        public bool IsPayable { get; set; }

        /// <summary>Its position in the unlock queue: 1 = the nearest unpaid one.</summary>
        public int QueuePosition { get; set; }

        /// <summary>Why it is not payable yet, for the customer-facing hint.</summary>
        public string? BlockedReason { get; set; }
    }

    /// <summary>
    /// WHEN a customer may pay a recurring occurrence, and when the SYSTEM may ask them to. Two
    /// different questions with two different answers, which is the whole reason this file exists
    /// as one place rather than two behaviours that drifted.
    ///
    /// <b>May the customer pay it?</b> — sequential unlock. Only the nearest unpaid upcoming
    /// occurrence is offered at first; paying it exposes the next, and so on. A customer who wants
    /// to prepay the whole horizon can, one at a time or in one go
    /// (<see cref="ResolveCombinedPaymentSet"/>), but the list never opens with four bills in it.
    ///
    /// <b>May the system ask?</b> — the 24-hour rule. After a cleaning happens we wait at least
    /// 24 hours before automatically requesting payment for the next one. The customer has just
    /// had a service; chasing them for the following one the same evening is the fastest way to
    /// make a recurring plan feel like a subscription trap. Voluntary prepayment is unaffected —
    /// that is the customer's initiative, not ours.
    ///
    /// Pure and clock-injected. Nothing here reads <c>DateTime.UtcNow</c>.
    /// </summary>
    public static class RecurringPaymentPolicy
    {
        /// <summary>The Option A cooling-off window between a cleaning and the next payment ask.</summary>
        public static readonly TimeSpan MinimumGapAfterPreviousCleaning = TimeSpan.FromHours(24);

        /// <summary>
        /// Which upcoming occurrences the customer may pay, in service-date order.
        ///
        /// Cancelled occurrences are skipped entirely — they are neither payable nor a gate on the
        /// one behind them, because waiting for a cancelled cleaning to be paid would strand the
        /// rest of the series forever. Already-paid ones are likewise transparent.
        /// </summary>
        public static List<RecurringPayabilityResult> ResolvePayability(
            IEnumerable<RecurringPayableOccurrence> occurrences)
        {
            var ordered = occurrences
                .OrderBy(o => o.ServiceDateTime)
                .ThenBy(o => o.OrderId)
                .ToList();

            var results = new List<RecurringPayabilityResult>();
            var queuePosition = 0;
            var unlockedNext = true;

            foreach (var occurrence in ordered)
            {
                if (!occurrence.IsOnlinePayable || occurrence.PaymentInFlight)
                {
                    if (occurrence.PaymentInFlight && !occurrence.IsPaid && !occurrence.IsCancelled)
                        unlockedNext = false;
                    results.Add(new RecurringPayabilityResult { OrderId = occurrence.OrderId,
                        BlockedReason = occurrence.PaymentInFlight ? "Payment is already being processed."
                            : "Paid separately using the arranged payment method; excluded from Pay all upcoming." });
                    continue;
                }
                if (occurrence.IsCancelled)
                {
                    results.Add(new RecurringPayabilityResult
                    {
                        OrderId = occurrence.OrderId,
                        IsPayable = false,
                        QueuePosition = 0,
                        BlockedReason = "This cleaning was cancelled."
                    });
                    continue;
                }

                if (occurrence.IsPaid || occurrence.AmountDue <= 0m)
                {
                    results.Add(new RecurringPayabilityResult
                    {
                        OrderId = occurrence.OrderId,
                        IsPayable = false,
                        QueuePosition = 0,
                        BlockedReason = null
                    });
                    continue;
                }

                queuePosition++;

                results.Add(new RecurringPayabilityResult
                {
                    OrderId = occurrence.OrderId,
                    IsPayable = unlockedNext,
                    QueuePosition = queuePosition,
                    BlockedReason = unlockedNext
                        ? null
                        : "Available to pay once the cleaning before it has been paid."
                });

                // Only the first unpaid one is open. Everything behind it waits.
                unlockedNext = false;
            }

            return results;
        }

        /// <summary>
        /// The order ids a "Pay all upcoming" request legitimately covers: every unpaid,
        /// non-cancelled occurrence in the horizon, nearest first.
        ///
        /// Deliberately IGNORES the sequential unlock. That gate exists so the customer is not
        /// presented with four bills at once; it is not a restriction on someone who has actively
        /// chosen to settle the lot. What it is NOT is a licence to trust a browser-supplied list —
        /// the caller derives the ids from the customer's own series and re-reads every amount
        /// server-side.
        /// </summary>
        public static List<int> ResolveCombinedPaymentSet(
            IEnumerable<RecurringPayableOccurrence> occurrences) =>
            occurrences
                .Where(o => o.IsOnlinePayable && !o.PaymentInFlight && !o.IsCancelled && !o.IsPaid && o.AmountDue > 0m)
                .OrderBy(o => o.ServiceDateTime)
                .ThenBy(o => o.OrderId)
                .Select(o => o.OrderId)
                .ToList();

        /// <summary>
        /// May the system PROACTIVELY request payment for <paramref name="occurrenceServiceDate"/>
        /// right now?
        ///
        /// <paramref name="previousCleaningEnd"/> is when the previous cleaning IN THE SAME SERIES
        /// finished (its service date/time plus its duration, or simply its start — the caller
        /// decides how precise it can be). Null means there was no previous cleaning, which is the
        /// very first occurrence: nothing to wait for, so the ask is allowed.
        ///
        /// Returns false when the previous cleaning has not happened yet either — a request for
        /// next month's visit before this month's has been performed is exactly the "aggressive"
        /// behaviour the rule exists to stop.
        /// </summary>
        public static bool CanAutomaticallyRequestPayment(
            DateTime? previousCleaningEnd,
            DateTime occurrenceServiceDate,
            DateTime now)
        {
            if (occurrenceServiceDate.Date < now.Date)
            {
                // The cleaning has already happened and is still unpaid — chasing it is not a
                // "future request", it is ordinary collection, and the gap rule does not apply.
                return true;
            }

            if (previousCleaningEnd == null) return true;

            return now >= previousCleaningEnd.Value.Add(MinimumGapAfterPreviousCleaning);
        }

        /// <summary>
        /// When the earliest automatic request for this occurrence becomes allowed. Surfaced to
        /// the admin panel so "why has nothing been sent?" has a visible answer rather than
        /// looking like a bug.
        /// </summary>
        public static DateTime? EarliestAutomaticRequestAt(DateTime? previousCleaningEnd) =>
            previousCleaningEnd?.Add(MinimumGapAfterPreviousCleaning);
    }
}
