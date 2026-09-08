using DreamCleaningBackend.Models.Commercial;

namespace DreamCleaningBackend.Helpers.Commercial
{
    /// <summary>
    /// THE INVOICE STATE MACHINE. Pure, so every rule below is asserted directly in
    /// <c>InvoiceStatusPolicyTests</c> without a database or a clock.
    ///
    /// The governing idea is that PAYMENT ARITHMETIC OUTRANKS WORKFLOW. Status is not a field an
    /// endpoint assigns because a button was pressed - it is derived from what has been sent, seen
    /// and paid, and <see cref="Resolve"/> is the only thing that decides it. That is why there is
    /// no "mark as paid" that simply writes Paid: a fully-paid invoice becomes Paid because the
    /// payment rows say so.
    ///
    /// Two states are ABSORBING and are never recomputed away:
    ///   Draft - never sent, so nothing external can have happened to it.
    ///   Void  - a deliberate, reasoned, permanent cancellation.
    /// Every automatic transition in the system checks those two first. This is the rule the
    /// overdue sweep, the view tracker and the payment recorder each have to honour, and it is
    /// centralised here so they cannot honour it differently.
    /// </summary>
    public static class InvoiceStatusPolicy
    {
        /// <summary>Statuses that never change on their own.</summary>
        public static bool IsTerminal(InvoiceStatus status) =>
            status is InvoiceStatus.Void or InvoiceStatus.Draft;

        /// <summary>
        /// The canonical status for an invoice, given everything known about it.
        ///
        /// <paramref name="asOf"/> is passed rather than read from the clock so the overdue rule
        /// is testable and so the whole function stays pure.
        /// </summary>
        public static InvoiceStatus Resolve(
            InvoiceStatus current,
            decimal total,
            decimal amountPaid,
            DateTime dueDate,
            bool hasBeenSent,
            bool hasBeenViewed,
            DateTime asOf)
        {
            // Void is permanent, and a Draft has not entered the lifecycle yet. Neither is ever
            // recomputed - not by a payment, not by the overdue sweep, not by a view.
            if (current == InvoiceStatus.Void) return InvoiceStatus.Void;
            if (current == InvoiceStatus.Draft) return InvoiceStatus.Draft;

            var balance = InvoiceCalculator.ResolveBalance(total, amountPaid);

            // Fully paid wins over everything below, including overdue: an invoice settled after
            // its due date is Paid, not Overdue. A zero-total invoice counts as paid the moment it
            // is issued, which is what stops a $0.00 line item sitting permanently outstanding.
            if (balance <= 0m) return InvoiceStatus.Paid;

            // Something is owed and the date has passed. Checked BEFORE the partial-payment branch
            // so a part-paid invoice that has gone past due reads as Overdue - which is the state
            // an admin needs to see and chase, and the more urgent of the two true statements.
            if (dueDate.Date < asOf.Date) return InvoiceStatus.Overdue;

            if (amountPaid > 0m) return InvoiceStatus.PartiallyPaid;

            // Nothing paid, not yet due: the furthest the client has got with it.
            if (hasBeenViewed) return InvoiceStatus.Viewed;
            if (hasBeenSent) return InvoiceStatus.Sent;

            return current;
        }

        /// <summary>
        /// Whether opening the public page should move Sent to Viewed.
        ///
        /// ONLY from Sent. A Paid or Void invoice the client opens again must not regress, and an
        /// Overdue or Partially Paid one is already telling the admin something more useful than
        /// "seen".
        /// </summary>
        public static bool ShouldMarkViewed(InvoiceStatus current) => current == InvoiceStatus.Sent;

        /// <summary>
        /// Whether the nightly sweep should flip this invoice to Overdue. Mirrors the
        /// specification exactly: past due, money outstanding, and not in a state that owns
        /// itself.
        /// </summary>
        public static bool ShouldBecomeOverdue(
            InvoiceStatus current, decimal balanceDue, DateTime dueDate, DateTime asOf)
        {
            if (current is InvoiceStatus.Paid or InvoiceStatus.Void or InvoiceStatus.Draft) return false;
            if (current == InvoiceStatus.Overdue) return false;
            return balanceDue > 0m && dueDate.Date < asOf.Date;
        }

        /// <summary>
        /// Whether an invoice may be edited at all, and how freely.
        /// A Draft is fully editable; a Void invoice is frozen except for notes.
        /// </summary>
        public static bool CanEdit(InvoiceStatus status) => status != InvoiceStatus.Void;

        /// <summary>
        /// Whether editing this invoice should WARN first. True once the client has it - the
        /// figures on their copy no longer match what is in the database, and the admin should be
        /// told before they change them, not after.
        /// </summary>
        public static bool EditRequiresWarning(InvoiceStatus status) =>
            status is InvoiceStatus.Sent or InvoiceStatus.Viewed
                   or InvoiceStatus.PartiallyPaid or InvoiceStatus.Overdue or InvoiceStatus.Paid;

        /// <summary>
        /// Whether MONETARY fields may still be changed. A paid invoice's figures are settled
        /// history matched against money in the bank; changing them silently would break that
        /// reconciliation. Notes and references stay editable either way.
        /// </summary>
        public static bool CanEditMonetaryValues(InvoiceStatus status) =>
            status is not (InvoiceStatus.Paid or InvoiceStatus.Void);

        /// <summary>A Draft has never been sent, so there is nothing to void - delete it instead.</summary>
        public static bool CanVoid(InvoiceStatus status) =>
            status is not (InvoiceStatus.Void or InvoiceStatus.Paid);

        /// <summary>Only a Draft may be hard-deleted. Anything issued is voided, never removed.</summary>
        public static bool CanDelete(InvoiceStatus status) => status == InvoiceStatus.Draft;

        public static bool CanSend(InvoiceStatus status) => status != InvoiceStatus.Void;

        /// <summary>A void invoice collects no money; a paid one needs none.</summary>
        public static bool CanRecordPayment(InvoiceStatus status) =>
            status is not (InvoiceStatus.Void or InvoiceStatus.Draft);

        /// <summary>Chasing payment only makes sense while payment is outstanding and issued.</summary>
        public static bool CanSendReminder(InvoiceStatus status) =>
            status is InvoiceStatus.Sent or InvoiceStatus.Viewed
                   or InvoiceStatus.PartiallyPaid or InvoiceStatus.Overdue;

        /// <summary>Human label for a status badge. Kept beside the rules so the two agree.</summary>
        public static string Label(InvoiceStatus status) => status switch
        {
            InvoiceStatus.Draft => "Draft",
            InvoiceStatus.Sent => "Sent",
            InvoiceStatus.Viewed => "Viewed",
            InvoiceStatus.PartiallyPaid => "Partially Paid",
            InvoiceStatus.Paid => "Paid",
            InvoiceStatus.Overdue => "Overdue",
            InvoiceStatus.Void => "Void",
            _ => status.ToString()
        };
    }
}
