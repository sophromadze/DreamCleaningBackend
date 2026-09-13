using DreamCleaningBackend.Models.Commercial;

namespace DreamCleaningBackend.Helpers.Commercial
{
    /// <summary>
    /// The customer-facing ACH processing fee charged when an invoice is paid through Stripe's
    /// "Pay from Bank" option.
    ///
    /// THE FEE IS A PAYMENT-METHOD CHARGE, NOT PART OF THE BILL. It never touches the cleaning
    /// subtotal, the sales tax or the invoice total, and it is never persisted onto the invoice
    /// just because the customer opened the Stripe flow. It is computed here, shown to the
    /// customer BEFORE they authorize, charged as a separate Stripe line, and stored beside the
    /// payment as its own column - see <c>CommercialInvoicePayment.ProcessingFee</c>.
    ///
    /// Pure and static: no database, no clock, no configuration lookup of its own. The three
    /// numbers come from <see cref="BillingSettings"/>, which is the one place they are
    /// configured, so there is no magic 0.008 or $5.00 anywhere else in the codebase.
    ///
    /// NEVER TRUST A FEE FROM THE BROWSER. Every call site recomputes from the invoice's own
    /// balance; the public checkout DTO has no field a fee could arrive in.
    /// </summary>
    public static class AchProcessingFeeCalculator
    {
        /// <summary>
        /// Stripe's standard ACH Direct Debit pricing, and what a fresh <see cref="BillingSettings"/>
        /// row is created with. Kept here so the seed and the calculator cannot disagree.
        /// </summary>
        public const decimal DefaultRatePercent = 0.8m;

        public const decimal DefaultCapAmount = 5.00m;

        /// <summary>
        /// <c>min(round2(balance x rate), cap)</c>, and zero whenever the fee is switched off,
        /// the balance is not positive, or the method is not an ACH bank debit.
        ///
        /// Card payments deliberately carry NO fee: the owner's decision was to price ACH, and
        /// silently extending a surcharge to cards would be a commercial change nobody made.
        /// </summary>
        public static decimal Resolve(
            decimal balanceDue, BillingSettings settings, InvoicePaymentRecordMethod method)
        {
            if (settings == null) return 0m;
            if (method != InvoicePaymentRecordMethod.AchBankTransfer) return 0m;
            return Resolve(balanceDue, settings);
        }

        /// <summary>The ACH fee for a balance, ignoring which method the caller has in mind.</summary>
        public static decimal Resolve(decimal balanceDue, BillingSettings settings)
        {
            if (settings is not { AchCustomerFeeEnabled: true }) return 0m;
            if (balanceDue <= 0m) return 0m;

            var rate = Math.Max(0m, settings.AchCustomerFeeRatePercent);
            var cap = Math.Max(0m, settings.AchCustomerFeeCapAmount);

            var fee = InvoiceCalculator.Round2(balanceDue * rate / 100m);

            // A cap of zero means "no cap", not "no fee" - a zero cap with the fee switched on
            // would otherwise be an invisible way of disabling it that nobody could find.
            if (cap > 0m && fee > cap) fee = cap;

            return fee;
        }

        /// <summary>
        /// What Stripe is actually asked to debit: the invoice balance plus the fee. Kept as its
        /// own function because three call sites need the same sum and re-adding it by hand is
        /// how the charged amount and the amount shown to the customer drift apart.
        /// </summary>
        public static decimal ResolveTotalCharge(decimal balanceDue, decimal processingFee) =>
            InvoiceCalculator.Round2(balanceDue + processingFee);

        /// <summary>
        /// The customer-facing name. NEVER a bare "Fee": the customer is entitled to know what
        /// they are being charged for, and a vague label on a payment page reads as a hidden
        /// markup.
        /// </summary>
        public const string CustomerFacingLabel = "ACH Processing Fee";

        /// <summary>
        /// What manual bank transfer says instead. Deliberately not "No Fee" - the customer's OWN
        /// bank may still charge them for sending a transfer, and promising otherwise on our
        /// invoice would be a statement about somebody else's pricing.
        /// </summary>
        public const string ManualAchNoFeeNote = "No processing fee from Dream Cleaning NYC";
    }
}
