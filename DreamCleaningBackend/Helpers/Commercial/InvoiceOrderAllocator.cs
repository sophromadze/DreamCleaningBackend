namespace DreamCleaningBackend.Helpers.Commercial
{
    /// <summary>One order taking part in an invoice's allocation, in the order it should be paid.</summary>
    public class InvoiceAllocationCandidate
    {
        public int OrderId { get; set; }

        /// <summary>The service date. Decides who gets the odd cent — see the allocator.</summary>
        public DateTime ServiceDate { get; set; }

        /// <summary>What the order charges today, before any negotiated group total.</summary>
        public decimal CurrentTotal { get; set; }
    }

    /// <summary>What one order ends up owing under the invoice.</summary>
    public class InvoiceAllocationLine
    {
        public int OrderId { get; set; }
        public DateTime ServiceDate { get; set; }
        public decimal PreviousTotal { get; set; }
        public decimal AllocatedAmount { get; set; }

        /// <summary>True when the allocation actually moves this order's price.</summary>
        public bool ChangesOrderTotal => AllocatedAmount != PreviousTotal;
    }

    /// <summary>
    /// Splitting a negotiated invoice total across the orders it covers.
    ///
    /// THE RULE IS EQUAL SHARES, NOT PROPORTIONAL ONES. Four weekly cleanings billed at an agreed
    /// $3,500 become $875.00 each, even if their list prices differed — that is what "we agreed
    /// $3,500 for the month" means to both sides, and it is the figure a client will check against
    /// four identical visits. Preserving the previous ratios instead would produce four different
    /// numbers nobody negotiated and nobody can reconcile against the agreement.
    ///
    /// ALL ARITHMETIC IS IN INTEGER CENTS. Dividing decimals and rounding each share is how the
    /// classic $100 / 3 = $33.33 × 3 = $99.99 shortfall happens; here the total is converted to
    /// cents, floor-divided, and the remainder handed out one cent at a time. The shares therefore
    /// sum to the invoice total EXACTLY, always, which is the only acceptable outcome when the
    /// same number is printed on an invoice and stored on four orders.
    ///
    /// THE REMAINDER GOES TO THE EARLIEST SERVICE DATES. $100 across three visits is
    /// $33.34 / $33.33 / $33.33, not a random cent. Deterministic, so re-running the allocation on
    /// the same set produces the same figures — a draft an admin reads on Monday and finalizes on
    /// Thursday must not quietly redistribute pennies in between.
    /// </summary>
    public static class InvoiceOrderAllocator
    {
        /// <summary>
        /// Distributes <paramref name="groupTotal"/> equally across the candidates.
        ///
        /// Pass the invoice's own total here — never a browser figure. The caller is responsible
        /// for having derived it from the line items (or from the admin's typed group total, which
        /// IS the negotiated number and is validated as money, not accepted as an allocation).
        /// </summary>
        public static List<InvoiceAllocationLine> DistributeEqually(
            IEnumerable<InvoiceAllocationCandidate> candidates, decimal groupTotal)
        {
            var ordered = candidates
                .OrderBy(c => c.ServiceDate)
                .ThenBy(c => c.OrderId)
                .ToList();

            if (ordered.Count == 0) return new List<InvoiceAllocationLine>();

            var totalCents = ToCents(groupTotal);
            if (totalCents < 0) totalCents = 0;

            var baseShare = totalCents / ordered.Count;
            var remainder = (int)(totalCents - (baseShare * ordered.Count));

            var lines = new List<InvoiceAllocationLine>(ordered.Count);

            for (var i = 0; i < ordered.Count; i++)
            {
                // The first `remainder` orders — the earliest service dates — carry one extra cent.
                var cents = baseShare + (i < remainder ? 1 : 0);

                lines.Add(new InvoiceAllocationLine
                {
                    OrderId = ordered[i].OrderId,
                    ServiceDate = ordered[i].ServiceDate,
                    PreviousTotal = ordered[i].CurrentTotal,
                    AllocatedAmount = FromCents(cents)
                });
            }

            return lines;
        }

        /// <summary>
        /// The allocation when the admin has NOT negotiated a group total: every order keeps
        /// exactly what it charges today, and the invoice totals their sum.
        ///
        /// Separate from <see cref="DistributeEqually"/> on purpose. Running four identically
        /// priced orders through the equal split would give the same answer, but four DIFFERENTLY
        /// priced ad-hoc orders would silently be levelled — and nobody asked for that.
        /// </summary>
        public static List<InvoiceAllocationLine> KeepCurrentTotals(
            IEnumerable<InvoiceAllocationCandidate> candidates) =>
            candidates
                .OrderBy(c => c.ServiceDate)
                .ThenBy(c => c.OrderId)
                .Select(c => new InvoiceAllocationLine
                {
                    OrderId = c.OrderId,
                    ServiceDate = c.ServiceDate,
                    PreviousTotal = c.CurrentTotal,
                    AllocatedAmount = c.CurrentTotal
                })
                .ToList();

        /// <summary>The default invoice total for a selection: the sum of what the orders charge.</summary>
        public static decimal SumCurrentTotals(IEnumerable<InvoiceAllocationCandidate> candidates) =>
            FromCents(candidates.Sum(c => ToCents(c.CurrentTotal)));

        /// <summary>
        /// The guarantee this class exists to provide, stated as an assertion the callers can make
        /// before they commit anything: the shares add up to the invoice, to the cent.
        /// </summary>
        public static bool SumsExactlyTo(IEnumerable<InvoiceAllocationLine> lines, decimal groupTotal) =>
            lines.Sum(l => ToCents(l.AllocatedAmount)) == ToCents(groupTotal);

        /// <summary>
        /// Cents from a decimal, THROUGH DECIMAL. `(long)(925.43d * 100)` is 92542 in binary
        /// floating point — an undercharge of a cent, and the same trap the Stripe checkout
        /// documents.
        /// </summary>
        public static long ToCents(decimal amount) =>
            (long)Math.Round(amount * 100m, 0, MidpointRounding.AwayFromZero);

        public static decimal FromCents(long cents) => cents / 100m;
    }
}
