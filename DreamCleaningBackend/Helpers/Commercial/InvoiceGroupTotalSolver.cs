using DreamCleaningBackend.Models.Commercial;

namespace DreamCleaningBackend.Helpers.Commercial
{
    /// <summary>
    /// The admin typed an agreed total for a group of orders — what must the LINE ITEMS sum to so
    /// <see cref="InvoiceCalculator"/> reproduces exactly that total?
    ///
    /// The negotiated figure is always <b>what the client pays</b>, i.e. the invoice TOTAL. That
    /// is the only reading that survives contact with a phone call: "we agreed $3,500 for October"
    /// is never a pre-tax figure in anybody's head, and it is the number that has to appear at the
    /// bottom of the invoice and add up across the four orders.
    ///
    /// Per tax mode:
    ///
    ///  • <b>Exempt / Included</b> — the total IS the line sum, so the answer is the typed figure.
    ///    (Tax-inclusive splits the tax back out by subtraction and never moves the total.)
    ///  • <b>Added</b> — tax rides on top, so the lines must sum to less. The obvious
    ///    `total / (1 + rate)` is not enough on its own: the calculator rounds the tax to cents, so
    ///    for some totals no rounded subtotal reproduces the target and for others two do. The
    ///    estimate is therefore CORRECTED by a bounded search over neighbouring cents, which is
    ///    the same "verify rather than trust the algebra" shape the residential
    ///    <c>taxOverrideBase</c> uses.
    ///
    /// <b>A negotiated total and a discount are two ways of saying the same thing</b>, so the two
    /// are refused together rather than silently composed. An admin who wants "$3,701.72 less 5%"
    /// has that already; an admin who wants "$3,500" should type $3,500.
    /// </summary>
    public static class InvoiceGroupTotalSolver
    {
        /// <summary>Outcome of a solve. <see cref="Error"/> is null on success.</summary>
        public readonly struct Result
        {
            public Result(decimal lineSubTotal, decimal achievedTotal, string? error)
            {
                LineSubTotal = lineSubTotal;
                AchievedTotal = achievedTotal;
                Error = error;
            }

            /// <summary>What the invoice's line items must add up to.</summary>
            public decimal LineSubTotal { get; }

            /// <summary>The total the calculator will actually produce from that subtotal.</summary>
            public decimal AchievedTotal { get; }

            public string? Error { get; }
            public bool IsExact => Error == null;
        }

        public static Result Solve(
            decimal targetTotal,
            InvoiceTaxType taxType,
            decimal? taxRate,
            InvoiceDiscountType discountType,
            decimal? discountValue)
        {
            if (targetTotal < 0m)
                return new Result(0m, 0m, "The agreed total cannot be negative.");

            if (discountType != InvoiceDiscountType.None && discountValue is > 0m)
            {
                return new Result(0m, 0m,
                    "Remove the invoice discount before setting an agreed total — the agreed total "
                    + "is already the final amount, so applying both would discount it twice.");
            }

            if (taxType != InvoiceTaxType.Added)
            {
                // Exempt and Included: the total is the line sum. Nothing to solve.
                return new Result(targetTotal, targetTotal, null);
            }

            var rate = taxRate is > 0m ? taxRate.Value : 0m;
            if (rate <= 0m) return new Result(targetTotal, targetTotal, null);

            var targetCents = InvoiceOrderAllocator.ToCents(targetTotal);

            // Estimate, then check the neighbourhood. Two cents either side is far more than the
            // rounding can ever move it; the loop is here so the answer is VERIFIED rather than
            // assumed from the algebra.
            var estimate = (long)Math.Round(
                targetTotal / (1m + rate / 100m) * 100m, 0, MidpointRounding.AwayFromZero);

            for (var delta = 0; delta <= 3; delta++)
            {
                foreach (var candidate in delta == 0
                             ? new[] { estimate }
                             : new[] { estimate - delta, estimate + delta })
                {
                    if (candidate < 0) continue;

                    var subTotal = InvoiceOrderAllocator.FromCents(candidate);
                    var tax = InvoiceCalculator.Round2(subTotal * rate / 100m);
                    var total = InvoiceCalculator.Round2(subTotal + tax);

                    if (InvoiceOrderAllocator.ToCents(total) == targetCents)
                        return new Result(subTotal, total, null);
                }
            }

            // No cent-valued pre-tax subtotal produces this exact total with tax added on top.
            // Say so rather than issuing an invoice a cent away from what was agreed.
            var fallback = InvoiceOrderAllocator.FromCents(estimate);
            var fallbackTax = InvoiceCalculator.Round2(fallback * rate / 100m);

            return new Result(fallback, InvoiceCalculator.Round2(fallback + fallbackTax),
                $"An agreed total of {targetTotal:C} cannot be reached exactly with tax added on top "
                + $"at {rate:0.###}%. Switch the invoice to tax-inclusive, or adjust the agreed total "
                + "by a cent.");
        }
    }
}
