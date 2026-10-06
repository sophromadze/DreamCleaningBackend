namespace DreamCleaningBackend.Models.Commercial
{
    /// <summary>
    /// Lifecycle of a commercial invoice.
    ///
    /// Draft and Void are the two ends nothing moves out of casually: a Draft has never been seen
    /// by the client, and a Void invoice is a permanent record that its number was issued and
    /// cancelled. Everything between them is driven by what has been sent, seen and paid, and the
    /// arithmetic wins - see <c>InvoiceStatusPolicy</c>.
    /// </summary>
    public enum InvoiceStatus
    {
        Draft = 0,
        Sent = 1,
        Viewed = 2,
        PartiallyPaid = 3,
        Paid = 4,
        Overdue = 5,
        Void = 6
    }

    /// <summary>
    /// How sales tax relates to the amounts typed on the invoice. There is no default that is
    /// safe to assume: existing Dream Cleaning commercial agreements were quoted both ways, and
    /// picking one silently would misstate what the client owes.
    /// </summary>
    public enum InvoiceTaxType
    {
        /// <summary>No tax. Total = subtotal - discount.</summary>
        Exempt = 0,

        /// <summary>
        /// The agreed amount ALREADY contains the tax. The total is unchanged and the tax is
        /// split back out by subtraction for reporting - see <c>InvoiceCalculator</c>.
        /// </summary>
        Included = 1,

        /// <summary>Tax is computed on the discounted subtotal and added on top.</summary>
        Added = 2
    }

    /// <summary>Fixed dollars off, or a percentage of the subtotal.</summary>
    public enum InvoiceDiscountType
    {
        None = 0,
        FixedAmount = 1,
        Percentage = 2
    }

    /// <summary>
    /// The payment methods ONE invoice offers its customer — a set, chosen per invoice
    /// (2026-10). Stored as a bit mask in <c>CommercialInvoice.PaymentMethods</c>.
    ///
    /// This is the WHOLE answer to "what may the customer use". The Billing Settings toggles only
    /// decide which boxes are pre-ticked on a new invoice; once an invoice exists, the public page,
    /// the PDF, the email and the checkout endpoint all read this set and nothing else. Before it
    /// existed the invoice carried a single "payment method" that no payment path ever read — an
    /// admin could pick Card and the page would still offer card only if a global switch said so.
    ///
    /// Each member is a route the invoice page can actually offer:
    /// <list type="bullet">
    /// <item><see cref="OnlineBank"/> — "Pay from Bank", Stripe ACH Direct Debit. The only one
    /// that carries a customer fee (the ACH processing fee in Billing Settings).</item>
    /// <item><see cref="Card"/> — "Pay by Card" through Stripe Checkout. No fee to the customer,
    /// the same as card on a regular invoice.</item>
    /// <item><see cref="ManualBankTransfer"/> — the customer pushes an ACH/wire from their own
    /// bank using the printed details; an admin records it. Needs complete bank details.</item>
    /// <item><see cref="Other"/> — a cheque or anything arranged directly with us. Nothing to
    /// click: the page tells the customer to contact us, and an admin records the payment.</item>
    /// </list>
    /// Append, never renumber: the values are persisted.
    /// </summary>
    [Flags]
    public enum InvoicePaymentMethods
    {
        None = 0,
        OnlineBank = 1,
        Card = 2,
        ManualBankTransfer = 4,
        Other = 8
    }

    /// <summary>
    /// How a recorded payment actually arrived. Wider than <see cref="InvoicePaymentMethods"/>
    /// because what an invoice OFFERS and what turns up are different questions - an ACH
    /// invoice is routinely settled by cheque.
    /// </summary>
    public enum InvoicePaymentRecordMethod
    {
        AchBankTransfer = 0,
        WireTransfer = 1,
        Check = 2,
        Card = 3,
        Cash = 4,
        Other = 5
    }

    /// <summary>Standard net terms, or a date the admin picks.</summary>
    public enum InvoiceDueTerms
    {
        DueOnReceipt = 0,
        Net7 = 1,
        Net15 = 2,
        Net30 = 3,
        Custom = 4
    }

    /// <summary>Which mail a <c>CommercialInvoiceEmailLog</c> row records.</summary>
    public enum InvoiceEmailType
    {
        InvoiceSent = 0,
        InvoiceResent = 1,
        PaymentReceipt = 2,
        Reminder = 3
    }

    /// <summary>Outcome of a send attempt, as far as the SMTP layer can tell us.</summary>
    public enum InvoiceEmailStatus
    {
        Sent = 0,
        Failed = 1
    }

    /// <summary>
    /// Cadence for a recurring invoice template. The template table exists in v1 so recurring
    /// billing can be switched on later without a schema migration; nothing generates from it yet.
    /// </summary>
    public enum InvoiceRecurrenceFrequency
    {
        Weekly = 0,
        Biweekly = 1,
        Monthly = 2,
        Custom = 3
    }
}
