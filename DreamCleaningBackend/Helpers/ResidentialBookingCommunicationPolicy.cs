using DreamCleaningBackend.Models;

namespace DreamCleaningBackend.Helpers
{
    /// <summary>
    /// "MAY WE SEND THIS ORDER THE ORDINARY RESIDENTIAL BOOKING MESSAGES?" — one definition, for
    /// every path that can create an order.
    ///
    /// The residential booking confirmation ("Thank you for choosing Dream Cleaning! Your booking
    /// has been confirmed.") and its SMS, plus the residential "please pay for this cleaning" link,
    /// are written for a private customer who books on the website and settles the order itself.
    /// A commercial cleaning billed through a <c>CommercialInvoice</c> settles
    /// nothing of the sort: the client is billed on the invoice, chased on the invoice, and receipts
    /// come from the invoice. Sending them the residential mail tells them a job is confirmed and
    /// paid for under a flow they are not in, and it arrives from a template that names none of the
    /// commercial references their accounts payable works from.
    ///
    /// <b>THE SIGNAL IS THE ORDER, NEVER THE CUSTOMER.</b> A commercial client very often ALSO
    /// books ordinary residential cleanings — a business owner's own apartment, a one-off card job
    /// for a site the contract does not cover. Suppressing on "this User is a business" would mute
    /// those, which is why nothing here looks at <c>User.IsBusiness</c>, at a role string, or at an
    /// email domain. What is asked is how THIS order is being handled:
    ///
    ///   <see cref="PaymentMethod.Invoice"/> ⇒ the order is billed on a commercial invoice
    ///   ⇒ the commercial invoice flow owns every money-facing message about it.
    ///
    /// <see cref="Order.ContractClientId"/> is the commercial link, and an Invoice order is
    /// REQUIRED to carry one — both the create path and the SuperAdmin payment-method switch reject
    /// the method without a client, because an order no invoice can pick up would never be billed.
    /// It is therefore reported here as the authoritative commercial signal, but the method alone is
    /// what suppresses: an Invoice order that somehow reached the database with a null client is
    /// still an order nobody is charging through Stripe, and the residential "your booking is
    /// confirmed" mail would be no less wrong for it.
    ///
    /// <b>What this does NOT touch.</b> Cleaner assignment mail and SMS (a different audience
    /// entirely — the cleaner still has to be told where to go), internal/admin notifications, the
    /// commercial invoice's own email/receipt/status messages, and every residential order paid by
    /// card, cash, Zelle, cheque or Other. Those last keep their existing behaviour exactly.
    /// </summary>
    public static class ResidentialBookingCommunicationPolicy
    {
        /// <summary>
        /// True when this order is a commercial cleaning billed through a commercial invoice, so
        /// the invoice flow — not the residential booking flow — owns talking to the customer
        /// about the money.
        /// </summary>
        public static bool IsCommercialInvoiceBacked(Order order) =>
            IsCommercialInvoiceBacked(order.PaymentMethod, order.ContractClientId);

        /// <summary>
        /// Overload for callers that have decided the method and the client but have not yet built
        /// (or reloaded) the entity — the admin create-for-user path is one.
        /// </summary>
        public static bool IsCommercialInvoiceBacked(PaymentMethod paymentMethod, int? contractClientId)
        {
            // The client id is the commercial link, and it is REQUIRED on an Invoice order by both
            // write paths. It is accepted here so a call site passes the whole commercial signal
            // rather than half of it, but it deliberately does not gate the answer — an Invoice
            // order with a missing client is still not a residential booking. See the type comment.
            _ = contractClientId;
            return paymentMethod == PaymentMethod.Invoice;
        }

        /// <summary>
        /// May the ordinary residential booking confirmation email / SMS and the residential
        /// payment-link communication go out for this order? False only for commercial
        /// invoice-backed orders.
        /// </summary>
        public static bool ShouldSendResidentialBookingCommunication(Order order) =>
            !IsCommercialInvoiceBacked(order);

        /// <inheritdoc cref="ShouldSendResidentialBookingCommunication(Order)"/>
        public static bool ShouldSendResidentialBookingCommunication(PaymentMethod paymentMethod, int? contractClientId) =>
            !IsCommercialInvoiceBacked(paymentMethod, contractClientId);

        /// <summary>
        /// Why the send was skipped, for an admin-facing result. Null when nothing was suppressed.
        /// Worded as the REASON rather than "notifications are off", the same rule
        /// <see cref="NoEmailHelper"/> exists for: an admin who is told the wrong cause goes
        /// hunting for a setting that was never the problem.
        /// </summary>
        public static string? SuppressionReason(Order order) =>
            IsCommercialInvoiceBacked(order)
                ? "this order is billed through a commercial invoice — the customer is contacted from the invoice, not the residential booking templates"
                : null;
    }
}
