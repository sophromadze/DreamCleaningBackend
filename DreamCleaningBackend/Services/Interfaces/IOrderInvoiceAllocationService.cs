namespace DreamCleaningBackend.Services.Interfaces
{
    /// <summary>
    /// What an order looked like BEFORE an invoice claimed it, returned across the seam so the
    /// commercial side can store it on the link row and put it back if the invoice is voided.
    ///
    /// PRIMITIVES ONLY, deliberately — see <see cref="IOrderInvoiceAllocationService"/>. The
    /// payment method travels as its underlying <c>int</c> rather than as the residential
    /// <c>PaymentMethod</c> enum, so no order-side type reaches the commercial namespaces.
    /// </summary>
    public readonly record struct OrderAllocationSnapshot(
        decimal PreviousTotal,
        int PreviousPaymentMethod,
        int? PreviousContractClientId);

    /// <summary>
    /// THE SEAM between commercial invoicing and a residential order's own money.
    ///
    /// Commercial invoicing decides WHICH orders an invoice covers and WHAT DOLLAR AMOUNT is
    /// allocated to each — that is invoice arithmetic, and it lives in
    /// <c>Services/Commercial/InvoiceOrderLinkService</c>. Writing that amount onto an Order is a
    /// different job: it needs the residential tax split, the residential status vocabulary and the
    /// residential audit stream, none of which commercial code may reach into (see
    /// <c>CommercialResidentialSeparationTests</c>, which forbids the commercial folders from
    /// naming <c>OrderPricingCalculator</c> at all).
    ///
    /// So the two halves talk through this interface, and <b>only primitives cross it</b> — an
    /// order id, an amount, an invoice id and its number. No commercial type reaches the order
    /// side and no order-pricing type reaches the commercial side, which is what keeps the two
    /// workflows separable while still letting one invoice settle four cleanings.
    /// </summary>
    public interface IOrderInvoiceAllocationService
    {
        /// <summary>
        /// Writes an invoice's negotiated allocation onto an order as its final agreed price, and
        /// makes the order an invoice-billed one.
        ///
        /// <paramref name="allocatedTotal"/> is TAX-INCLUSIVE — what the client pays for that
        /// visit — and is split into subtotal + tax the same way Custom Pricing splits an
        /// admin-typed amount, so the two add back to it exactly.
        ///
        /// <para><b>The order is ADOPTED by the invoice here, not before.</b> It is stamped with
        /// <c>ContractClientId</c> and switched to the Invoice payment method, because a cleaning
        /// that is about to be billed on a commercial invoice is not being collected any other
        /// way. This runs only when the invoice is SENT — a draft that is abandoned has changed
        /// nothing — and it is why the previous method is returned rather than discarded: voiding
        /// the invoice has to be able to put it back.</para>
        ///
        /// Captures the previous total on the order the first time it is called, and writes an
        /// audit row naming the invoice, the admin and both figures.
        /// </summary>
        Task<OrderAllocationSnapshot> ApplyAllocatedTotalAsync(
            int orderId, decimal allocatedTotal, int contractClientId,
            int invoiceId, string invoiceNumber, int? actingUserId);

        /// <summary>
        /// Marks an order settled and Active because the invoice covering it is fully paid.
        ///
        /// IDEMPOTENT AND TRANSACTIONAL: returns false without touching anything when the order
        /// has already been activated by this invoice, so a retried Stripe webhook or a repeated
        /// "Mark as paid" cannot double-transition it. <b>An order that is already Done or
        /// Cancelled is never downgraded</b> — the cleaning happening is a fact about the world
        /// and an invoice settling afterwards does not un-happen it.
        /// </summary>
        Task<bool> ActivateAfterInvoicePaidAsync(
            int orderId, int invoiceId, string invoiceNumber, DateTime paidAtUtc);

        /// <summary>
        /// Puts an order's price — and the billing arrangement it was under — back after an
        /// invoice that had allocated to it was voided. Audited like the forward direction; never
        /// applied to an order some OTHER live invoice has since claimed, nor to one this invoice
        /// had already activated.
        ///
        /// <paramref name="previousPaymentMethod"/> and <paramref name="previousContractClientId"/>
        /// come from the snapshot the forward call returned. They are nullable at the call site
        /// because rows committed before this was recorded have nothing to restore, and guessing
        /// would be worse than leaving the order as the voided invoice left it.
        /// </summary>
        Task RevertAllocatedTotalAsync(
            int orderId, decimal previousTotal, int? previousPaymentMethod,
            int? previousContractClientId, int invoiceId, string invoiceNumber, int? actingUserId);
    }
}
