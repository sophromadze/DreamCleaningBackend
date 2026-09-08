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
    /// How the client is expected to pay. ACH is the commercial default and the only one the
    /// first version actually instructs on; Card is reserved for a later Stripe path and Other
    /// covers cheques and anything arranged by hand.
    /// </summary>
    public enum InvoicePaymentMethod
    {
        AchBankTransfer = 0,
        Card = 1,
        Other = 2
    }

    /// <summary>
    /// How a recorded payment actually arrived. Wider than <see cref="InvoicePaymentMethod"/>
    /// because what an invoice ASKS for and what turns up are different questions - an ACH
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

    /// <summary>Which mail an <c>CommercialInvoiceEmailLog</c> row records.</summary>
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
