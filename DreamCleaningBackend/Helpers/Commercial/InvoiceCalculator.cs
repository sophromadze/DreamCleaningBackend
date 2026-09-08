using DreamCleaningBackend.Models.Commercial;

namespace DreamCleaningBackend.Helpers.Commercial
{
    /// <summary>One line as the calculator sees it. Description is irrelevant to the arithmetic.</summary>
    public class InvoiceLineInput
    {
        public decimal Quantity { get; set; } = 1m;
        public decimal UnitPrice { get; set; }
    }

    /// <summary>Everything the calculator needs. Nothing derived is an input.</summary>
    public class InvoiceTotalsInput
    {
        public List<InvoiceLineInput> Lines { get; set; } = new();
        public InvoiceDiscountType DiscountType { get; set; } = InvoiceDiscountType.None;
        public decimal? DiscountValue { get; set; }
        public InvoiceTaxType TaxType { get; set; } = InvoiceTaxType.Exempt;
        public decimal? TaxRate { get; set; }
    }

    /// <summary>Every derived figure, all of them rounded to cents.</summary>
    public class InvoiceTotals
    {
        public List<decimal> LineAmounts { get; set; } = new();
        public decimal SubTotal { get; set; }
        public decimal DiscountAmount { get; set; }
        public decimal TaxAmount { get; set; }
        public decimal Total { get; set; }
    }

    /// <summary>
    /// THE ONLY PLACE COMMERCIAL INVOICE MONEY IS COMPUTED. Every write path recalculates through
    /// this; a subtotal, tax figure or total arriving from the browser is treated as a preview and
    /// discarded. That rule is what stops a hand-rolled request from invoicing a client one dollar
    /// for a nine-hundred-dollar job.
    ///
    /// EVERYTHING IS <c>decimal</c>. Not double, not float, anywhere in the chain - the same rule
    /// the residential side follows in OrderPricingCalculator, for the same reason: binary floating
    /// point cannot represent a cent, and the error compounds across lines.
    ///
    /// Rounding is half-away-from-zero (<c>MidpointRounding.AwayFromZero</c>), matching the rest of
    /// this codebase and matching JavaScript's <c>Math.round</c> for positive values, so the
    /// figures the Angular form previews and the figures the server persists agree to the cent.
    ///
    /// Pure and static: no database, no context, no clock. That is what lets the whole rule set be
    /// asserted directly in <c>InvoiceCalculatorTests</c>.
    /// </summary>
    public static class InvoiceCalculator
    {
        public static decimal Round2(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

        /// <summary>Quantity times rate, to the cent. Fractional quantities are supported.</summary>
        public static decimal LineAmount(decimal quantity, decimal unitPrice) =>
            Round2(quantity * unitPrice);

        /// <summary>
        /// Runs the whole chain: lines, subtotal, discount, tax, total.
        ///
        /// Order matters and is fixed - TAX IS COMPUTED ON THE DISCOUNTED SUBTOTAL, never on the
        /// gross. Taxing the pre-discount figure would charge the client tax on money they were
        /// never billed.
        /// </summary>
        public static InvoiceTotals Calculate(InvoiceTotalsInput input)
        {
            var result = new InvoiceTotals();

            foreach (var line in input.Lines)
            {
                var amount = LineAmount(line.Quantity, line.UnitPrice);
                result.LineAmounts.Add(amount);
                result.SubTotal += amount;
            }

            result.SubTotal = Round2(result.SubTotal);
            result.DiscountAmount = ResolveDiscount(result.SubTotal, input.DiscountType, input.DiscountValue);

            var discountedSubTotal = Round2(result.SubTotal - result.DiscountAmount);

            switch (input.TaxType)
            {
                case InvoiceTaxType.Added:
                    // Tax rides on top of what the client is billed.
                    result.TaxAmount = Round2(discountedSubTotal * RatePercent(input.TaxRate) / 100m);
                    result.Total = Round2(discountedSubTotal + result.TaxAmount);
                    break;

                case InvoiceTaxType.Included:
                    // The agreed amount already contains the tax, so the TOTAL is fixed and the
                    // tax is split back out of it BY SUBTRACTION - exactly as
                    // ContractPricingCalculator and the booking side's splitTaxInclusiveAmount do.
                    //
                    // Deriving it instead as round2(preTax * rate) drifts a cent on the amounts
                    // where no cent-valued subtotal satisfies the equation, and the invoice would
                    // then print a subtotal and a tax that do not add up to the total the client
                    // is being asked to pay.
                    result.Total = discountedSubTotal;
                    result.TaxAmount = Round2(
                        discountedSubTotal - Round2(discountedSubTotal / (1m + RatePercent(input.TaxRate) / 100m)));
                    break;

                case InvoiceTaxType.Exempt:
                default:
                    result.TaxAmount = 0m;
                    result.Total = discountedSubTotal;
                    break;
            }

            // A discount larger than the work, or a stray negative line, must never produce a
            // negative invoice - that is a credit note, which this system does not issue.
            if (result.Total < 0m) result.Total = 0m;

            return result;
        }

        /// <summary>
        /// Dollars off, whichever way it was entered.
        ///
        /// CAPPED AT THE SUBTOTAL in both modes: a fixed discount larger than the bill, or a
        /// percentage above 100, would otherwise turn into money owed to the client.
        /// </summary>
        public static decimal ResolveDiscount(decimal subTotal, InvoiceDiscountType type, decimal? value)
        {
            if (type == InvoiceDiscountType.None || value is null or <= 0m) return 0m;

            var amount = type switch
            {
                InvoiceDiscountType.FixedAmount => Round2(value.Value),
                InvoiceDiscountType.Percentage => Round2(subTotal * Math.Min(value.Value, 100m) / 100m),
                _ => 0m
            };

            return Math.Clamp(amount, 0m, subTotal);
        }

        /// <summary>A null or negative rate is zero, so a missing rate can never inflate a bill.</summary>
        private static decimal RatePercent(decimal? rate) => rate is > 0m ? rate.Value : 0m;

        /// <summary>
        /// What has actually been received: the signed sum of the payment rows. Reversals are
        /// negative rows, so they subtract here rather than being deleted - the history stays
        /// complete and the arithmetic still comes out right.
        /// </summary>
        public static decimal ResolveAmountPaid(IEnumerable<decimal> paymentAmounts) =>
            Round2(paymentAmounts.Sum());

        /// <summary>
        /// What is still owed. FLOORED AT ZERO: an overpayment is surfaced separately by
        /// <see cref="ResolveOverpayment"/> rather than shown as a negative balance, because a
        /// negative "balance due" on an invoice reads as a refund the system has promised.
        /// </summary>
        public static decimal ResolveBalance(decimal total, decimal amountPaid) =>
            Math.Max(0m, Round2(total - amountPaid));

        /// <summary>Anything received above the total. Zero in the normal case.</summary>
        public static decimal ResolveOverpayment(decimal total, decimal amountPaid) =>
            Math.Max(0m, Round2(amountPaid - total));

        /// <summary>
        /// The due date a set of terms produces. <c>Custom</c> keeps whatever the admin picked,
        /// which is why the caller passes it back in.
        /// </summary>
        public static DateTime ResolveDueDate(DateTime invoiceDate, InvoiceDueTerms terms, DateTime? customDate)
        {
            return terms switch
            {
                InvoiceDueTerms.DueOnReceipt => invoiceDate.Date,
                InvoiceDueTerms.Net7 => invoiceDate.Date.AddDays(7),
                InvoiceDueTerms.Net15 => invoiceDate.Date.AddDays(15),
                InvoiceDueTerms.Net30 => invoiceDate.Date.AddDays(30),
                InvoiceDueTerms.Custom => (customDate ?? invoiceDate).Date,
                _ => invoiceDate.Date.AddDays(15)
            };
        }
    }
}
