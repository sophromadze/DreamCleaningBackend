using DreamCleaningBackend.Models;

namespace DreamCleaningBackend.Helpers
{
    /// <summary>
    /// The order-edit top-up picture an ADDITIONAL invoice is resolved against: how much top-up
    /// money has been collected to date, and how much is still outstanding
    /// (<see cref="OrderAdditionalCharge"/>). Loaded by the service; the policy stays pure.
    /// </summary>
    public readonly record struct CustomerInvoiceTopUpState(
        decimal CollectedToDate,
        decimal Outstanding,
        IReadOnlyList<CustomerInvoiceTopUpPayment>? Payments = null)
    {
        /// <summary>
        /// The top-up payment that SETTLED an Additional invoice: the first one, oldest first, at
        /// which the running total reaches the invoice's target. Two invoices paid on different
        /// days must not both report the latest payment's time and method.
        /// </summary>
        public CustomerInvoiceTopUpPayment? SettlingPayment(decimal target)
        {
            var running = 0m;
            foreach (var p in Payments ?? Array.Empty<CustomerInvoiceTopUpPayment>())
            {
                running += p.Amount;
                if (running + 0.005m >= target) return p;
            }
            return null;
        }
    }

    /// <summary>One collected (positive, paid) order-edit top-up row.</summary>
    public record CustomerInvoiceTopUpPayment(decimal Amount, DateTime? PaidAt, PaymentMethod Method, string? Reference);

    /// <summary>
    /// The ONE answer to "what state is this regular customer invoice in?", derived from the
    /// invoice, its part-payment request and its order. No endpoint assigns a status — pressing a
    /// button records a fact (sent, voided, money received) and this reads the result, so the
    /// invoice cannot disagree with the order it bills.
    ///
    /// Precedence, in order:
    ///  1. Voided by an admin → Void (absorbing).
    ///  2. Money in → Paid: its own request was paid, OR the whole order is settled by any route
    ///     (paid in full from the profile, or switched to cash / Zelle). An invoice for an order
    ///     that owes nothing must never still read as owed.
    ///  3. Its request was withdrawn from the order's Payments card → Void.
    ///  4. The order was cancelled or refunded → Cancelled.
    ///  5. Otherwise it is owed: Sent once it has gone out, NotSent before that.
    ///
    /// An ADDITIONAL invoice (the extra an edit added after payment, 2026-09) has no request: it is
    /// Paid once the order's collected top-up reaches <see cref="CustomerInvoice.TopUpCollectedTarget"/>,
    /// and Void when the price was lowered back so nothing is outstanding any more.
    /// Pure, so every rule is asserted without a database (CustomerInvoiceTests).
    /// </summary>
    public static class CustomerInvoiceStatusPolicy
    {
        public const string NotSent = "NotSent";
        public const string Sent = "Sent";
        public const string Paid = "Paid";
        public const string Void = "Void";
        public const string Cancelled = "Cancelled";

        public static string Resolve(CustomerInvoice invoice, Order order, OrderPartialPayment? request,
            CustomerInvoiceTopUpState? topUp = null)
        {
            if (invoice.VoidedAt != null) return Void;

            if (invoice.Kind == CustomerInvoiceKind.Additional)
            {
                var state = topUp ?? default;
                if (IsTopUpSettled(invoice, state)) return Paid;
                if (OrderStatuses.IsCancelled(order.Status) || OrderStatuses.IsRefunded(order.Status))
                    return Cancelled;
                if (state.Outstanding < OrderAdditionalCharge.MinimumCollectableAmount) return Void;
                return invoice.FirstSentAt != null ? Sent : NotSent;
            }

            if (request == null) return Void;

            if (request.Status == OrderPartialPaymentStatus.Paid || IsOrderSettled(order))
                return Paid;

            if (request.Status == OrderPartialPaymentStatus.Cancelled) return Void;

            if (OrderStatuses.IsCancelled(order.Status) || OrderStatuses.IsRefunded(order.Status))
                return Cancelled;

            return invoice.FirstSentAt != null ? Sent : NotSent;
        }

        /// <summary>True while the invoice can still take a payment (card or bank transfer).</summary>
        public static bool IsPayable(string status) => status is NotSent or Sent;

        /// <summary>
        /// What the customer is asked to pay right now. A Full invoice bills the order's LIVE
        /// balance (an edit after issue moves it); a Split invoice bills its own slice, never
        /// more than is still owed; an Additional invoice what is left of its own amount, never
        /// more than the top-up still outstanding. Zero once the invoice is no longer payable.
        /// </summary>
        public static decimal AmountDue(CustomerInvoice invoice, Order order, OrderPartialPayment? request, string status,
            CustomerInvoiceTopUpState? topUp = null)
        {
            if (!IsPayable(status)) return 0m;

            if (invoice.Kind == CustomerInvoiceKind.Additional)
            {
                var state = topUp ?? default;
                var leftOnThisInvoice = (invoice.TopUpCollectedTarget ?? invoice.Amount) - state.CollectedToDate;
                return Services.OrderPricingCalculator.Round2(
                    Math.Max(0m, Math.Min(Math.Min(invoice.Amount, leftOnThisInvoice), state.Outstanding)));
            }

            var orderDue = OrderBalance.AmountDue(order);
            return invoice.Kind == CustomerInvoiceKind.Full || request == null
                ? orderDue
                : Math.Min(request.RequestedAmount, orderDue);
        }

        /// <summary>Additional invoices: the collected top-up has reached this invoice's target.</summary>
        public static bool IsTopUpSettled(CustomerInvoice invoice, CustomerInvoiceTopUpState state) =>
            state.CollectedToDate + 0.005m >= (invoice.TopUpCollectedTarget ?? decimal.MaxValue);

        /// <summary>Paid by card in full, or settled outside Stripe (cash / Zelle / check / other).</summary>
        private static bool IsOrderSettled(Order order) =>
            order.IsPaid || PaymentMethodRules.IsSettledOnRecord(order.PaymentMethod);
    }
}
