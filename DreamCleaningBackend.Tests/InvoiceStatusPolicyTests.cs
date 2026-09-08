using DreamCleaningBackend.Helpers.Commercial;
using DreamCleaningBackend.Models.Commercial;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// THE INVOICE STATE MACHINE.
    ///
    /// Status is DERIVED, never assigned because a button was pressed. These tests pin the two
    /// rules that make that safe: payment arithmetic outranks workflow, and Draft and Void are
    /// absorbing states that nothing recomputes away.
    ///
    /// The overdue clock is passed in rather than read, so every case below is deterministic.
    /// </summary>
    public class InvoiceStatusPolicyTests
    {
        private static readonly DateTime Today = new(2026, 9, 7);
        private static readonly DateTime PastDue = new(2026, 9, 1);
        private static readonly DateTime FutureDue = new(2026, 9, 20);

        private static InvoiceStatus Resolve(
            InvoiceStatus current, decimal total, decimal paid, DateTime due,
            bool sent = true, bool viewed = false)
            => InvoiceStatusPolicy.Resolve(current, total, paid, due, sent, viewed, Today);

        // ── The absorbing states ──────────────────────────────────────────────────────────────

        /// <summary>
        /// A DRAFT IS NEVER RECOMPUTED. It has not been sent, so nothing external can have
        /// happened to it - not a payment, not the due date passing, not a view.
        /// </summary>
        [Fact]
        public void Draft_StaysDraft_WhateverElseIsTrue()
        {
            Assert.Equal(InvoiceStatus.Draft, Resolve(InvoiceStatus.Draft, 100m, 100m, PastDue));
            Assert.Equal(InvoiceStatus.Draft, Resolve(InvoiceStatus.Draft, 100m, 0m, PastDue));
            Assert.Equal(InvoiceStatus.Draft, Resolve(InvoiceStatus.Draft, 100m, 50m, FutureDue));
        }

        /// <summary>
        /// VOID IS PERMANENT. A voided invoice that somehow receives money, or whose due date
        /// passes, stays void - the decision to cancel it is not undone by arithmetic.
        /// </summary>
        [Fact]
        public void Void_StaysVoid_WhateverElseIsTrue()
        {
            Assert.Equal(InvoiceStatus.Void, Resolve(InvoiceStatus.Void, 100m, 100m, PastDue));
            Assert.Equal(InvoiceStatus.Void, Resolve(InvoiceStatus.Void, 100m, 0m, PastDue));
        }

        // ── Payment arithmetic outranks workflow ──────────────────────────────────────────────

        [Fact]
        public void NothingPaid_AndNotYetDue_ReadsAsSentOrViewed()
        {
            Assert.Equal(InvoiceStatus.Sent,
                Resolve(InvoiceStatus.Sent, 100m, 0m, FutureDue, sent: true, viewed: false));
            Assert.Equal(InvoiceStatus.Viewed,
                Resolve(InvoiceStatus.Sent, 100m, 0m, FutureDue, sent: true, viewed: true));
        }

        [Fact]
        public void PartPaid_AndNotYetDue_IsPartiallyPaid()
        {
            Assert.Equal(InvoiceStatus.PartiallyPaid,
                Resolve(InvoiceStatus.Sent, 5000m, 2000m, FutureDue));
        }

        [Fact]
        public void PaidInFull_IsPaid()
        {
            Assert.Equal(InvoiceStatus.Paid, Resolve(InvoiceStatus.PartiallyPaid, 5000m, 5000m, FutureDue));
        }

        /// <summary>
        /// PAID BEATS OVERDUE. An invoice settled after its due date is Paid, not Overdue - the
        /// money arrived, and chasing it would be wrong.
        /// </summary>
        [Fact]
        public void PaidInFull_BeatsOverdue_EvenPastTheDueDate()
        {
            Assert.Equal(InvoiceStatus.Paid, Resolve(InvoiceStatus.Overdue, 925.43m, 925.43m, PastDue));
        }

        /// <summary>
        /// A zero-total invoice is paid the moment it is issued, which is what stops a $0.00 line
        /// item sitting permanently in the outstanding column.
        /// </summary>
        [Fact]
        public void ZeroTotal_CountsAsPaid()
        {
            Assert.Equal(InvoiceStatus.Paid, Resolve(InvoiceStatus.Sent, 0m, 0m, PastDue));
        }

        /// <summary>
        /// OVERDUE BEATS PARTIALLY PAID. Both are true of a part-paid invoice past its date, and
        /// Overdue is the one the admin needs to act on.
        /// </summary>
        [Fact]
        public void PastDueWithABalance_IsOverdue_EvenWhenPartlyPaid()
        {
            Assert.Equal(InvoiceStatus.Overdue, Resolve(InvoiceStatus.Sent, 100m, 0m, PastDue));
            Assert.Equal(InvoiceStatus.Overdue, Resolve(InvoiceStatus.PartiallyPaid, 5000m, 2000m, PastDue));
        }

        /// <summary>The full partial-payment sequence from the specification, end to end.</summary>
        [Fact]
        public void PartialPaymentSequence_WalksSentToPartiallyPaidToPaid()
        {
            Assert.Equal(InvoiceStatus.Sent, Resolve(InvoiceStatus.Sent, 5000m, 0m, FutureDue));
            Assert.Equal(InvoiceStatus.PartiallyPaid, Resolve(InvoiceStatus.Sent, 5000m, 2000m, FutureDue));
            Assert.Equal(InvoiceStatus.Paid, Resolve(InvoiceStatus.PartiallyPaid, 5000m, 5000m, FutureDue));
        }

        // ── View tracking ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// ONLY SENT BECOMES VIEWED. A Paid or Void invoice the client opens again must not
        /// regress, and an Overdue or part-paid one is already saying something more useful.
        /// </summary>
        [Fact]
        public void OpeningThePublicPage_PromotesOnlyASentInvoice()
        {
            Assert.True(InvoiceStatusPolicy.ShouldMarkViewed(InvoiceStatus.Sent));

            Assert.False(InvoiceStatusPolicy.ShouldMarkViewed(InvoiceStatus.Paid));
            Assert.False(InvoiceStatusPolicy.ShouldMarkViewed(InvoiceStatus.Void));
            Assert.False(InvoiceStatusPolicy.ShouldMarkViewed(InvoiceStatus.Overdue));
            Assert.False(InvoiceStatusPolicy.ShouldMarkViewed(InvoiceStatus.PartiallyPaid));
            Assert.False(InvoiceStatusPolicy.ShouldMarkViewed(InvoiceStatus.Viewed));
            Assert.False(InvoiceStatusPolicy.ShouldMarkViewed(InvoiceStatus.Draft));
        }

        // ── The overdue sweep ─────────────────────────────────────────────────────────────────

        [Fact]
        public void OverdueSweep_CatchesPastDueInvoicesWithABalance()
        {
            Assert.True(InvoiceStatusPolicy.ShouldBecomeOverdue(
                InvoiceStatus.Sent, 100m, PastDue, Today));
            Assert.True(InvoiceStatusPolicy.ShouldBecomeOverdue(
                InvoiceStatus.Viewed, 100m, PastDue, Today));
            Assert.True(InvoiceStatusPolicy.ShouldBecomeOverdue(
                InvoiceStatus.PartiallyPaid, 100m, PastDue, Today));
        }

        /// <summary>The three the specification names explicitly, plus the no-op on an already-overdue row.</summary>
        [Fact]
        public void OverdueSweep_NeverTouchesPaidVoidOrDraft()
        {
            Assert.False(InvoiceStatusPolicy.ShouldBecomeOverdue(
                InvoiceStatus.Paid, 100m, PastDue, Today));
            Assert.False(InvoiceStatusPolicy.ShouldBecomeOverdue(
                InvoiceStatus.Void, 100m, PastDue, Today));
            Assert.False(InvoiceStatusPolicy.ShouldBecomeOverdue(
                InvoiceStatus.Draft, 100m, PastDue, Today));
            Assert.False(InvoiceStatusPolicy.ShouldBecomeOverdue(
                InvoiceStatus.Overdue, 100m, PastDue, Today));
        }

        [Fact]
        public void OverdueSweep_LeavesASettledOrNotYetDueInvoiceAlone()
        {
            Assert.False(InvoiceStatusPolicy.ShouldBecomeOverdue(
                InvoiceStatus.Sent, 0m, PastDue, Today));      // nothing owed
            Assert.False(InvoiceStatusPolicy.ShouldBecomeOverdue(
                InvoiceStatus.Sent, 100m, FutureDue, Today));  // not due yet
        }

        /// <summary>Due TODAY is not yet overdue - the rule is strictly "past" the due date.</summary>
        [Fact]
        public void DueToday_IsNotYetOverdue()
        {
            Assert.False(InvoiceStatusPolicy.ShouldBecomeOverdue(
                InvoiceStatus.Sent, 100m, Today, Today));
            Assert.Equal(InvoiceStatus.Sent, Resolve(InvoiceStatus.Sent, 100m, 0m, Today));
        }

        // ── Editing and action rules ──────────────────────────────────────────────────────────

        [Fact]
        public void OnlyAVoidInvoiceIsFullyReadOnly()
        {
            Assert.True(InvoiceStatusPolicy.CanEdit(InvoiceStatus.Draft));
            Assert.True(InvoiceStatusPolicy.CanEdit(InvoiceStatus.Sent));
            Assert.True(InvoiceStatusPolicy.CanEdit(InvoiceStatus.Paid));
            Assert.False(InvoiceStatusPolicy.CanEdit(InvoiceStatus.Void));
        }

        /// <summary>
        /// A DRAFT NEEDS NO WARNING - nobody has seen it. Everything the client already has does.
        /// </summary>
        [Fact]
        public void EditingAnInvoiceTheClientAlreadyHas_Warns()
        {
            Assert.False(InvoiceStatusPolicy.EditRequiresWarning(InvoiceStatus.Draft));

            Assert.True(InvoiceStatusPolicy.EditRequiresWarning(InvoiceStatus.Sent));
            Assert.True(InvoiceStatusPolicy.EditRequiresWarning(InvoiceStatus.Viewed));
            Assert.True(InvoiceStatusPolicy.EditRequiresWarning(InvoiceStatus.PartiallyPaid));
            Assert.True(InvoiceStatusPolicy.EditRequiresWarning(InvoiceStatus.Overdue));
            Assert.True(InvoiceStatusPolicy.EditRequiresWarning(InvoiceStatus.Paid));
        }

        /// <summary>
        /// A PAID INVOICE'S FIGURES ARE SETTLED HISTORY, matched against money in the bank.
        /// Notes and references stay editable; the amounts do not.
        /// </summary>
        [Fact]
        public void MonetaryValuesAreFrozenOncePaidOrVoided()
        {
            Assert.True(InvoiceStatusPolicy.CanEditMonetaryValues(InvoiceStatus.Draft));
            Assert.True(InvoiceStatusPolicy.CanEditMonetaryValues(InvoiceStatus.Sent));
            Assert.True(InvoiceStatusPolicy.CanEditMonetaryValues(InvoiceStatus.Overdue));

            Assert.False(InvoiceStatusPolicy.CanEditMonetaryValues(InvoiceStatus.Paid));
            Assert.False(InvoiceStatusPolicy.CanEditMonetaryValues(InvoiceStatus.Void));
        }

        /// <summary>
        /// AN ISSUED INVOICE IS VOIDED, NEVER DELETED. Only a Draft - which nobody has seen and
        /// which has no external existence - may be removed outright.
        /// </summary>
        [Fact]
        public void OnlyADraftCanBeDeleted()
        {
            Assert.True(InvoiceStatusPolicy.CanDelete(InvoiceStatus.Draft));

            Assert.False(InvoiceStatusPolicy.CanDelete(InvoiceStatus.Sent));
            Assert.False(InvoiceStatusPolicy.CanDelete(InvoiceStatus.Viewed));
            Assert.False(InvoiceStatusPolicy.CanDelete(InvoiceStatus.Overdue));
            Assert.False(InvoiceStatusPolicy.CanDelete(InvoiceStatus.Paid));
            Assert.False(InvoiceStatusPolicy.CanDelete(InvoiceStatus.Void));
        }

        [Fact]
        public void APaidOrVoidInvoiceCannotBeVoided()
        {
            Assert.True(InvoiceStatusPolicy.CanVoid(InvoiceStatus.Draft));
            Assert.True(InvoiceStatusPolicy.CanVoid(InvoiceStatus.Sent));
            Assert.True(InvoiceStatusPolicy.CanVoid(InvoiceStatus.Overdue));

            Assert.False(InvoiceStatusPolicy.CanVoid(InvoiceStatus.Paid));
            Assert.False(InvoiceStatusPolicy.CanVoid(InvoiceStatus.Void));
        }

        [Fact]
        public void PaymentsCannotBeRecordedAgainstADraftOrAVoidInvoice()
        {
            Assert.True(InvoiceStatusPolicy.CanRecordPayment(InvoiceStatus.Sent));
            Assert.True(InvoiceStatusPolicy.CanRecordPayment(InvoiceStatus.Overdue));
            Assert.True(InvoiceStatusPolicy.CanRecordPayment(InvoiceStatus.PartiallyPaid));

            Assert.False(InvoiceStatusPolicy.CanRecordPayment(InvoiceStatus.Draft));
            Assert.False(InvoiceStatusPolicy.CanRecordPayment(InvoiceStatus.Void));
        }

        [Fact]
        public void RemindersOnlyApplyToAnIssuedInvoiceWithABalance()
        {
            Assert.True(InvoiceStatusPolicy.CanSendReminder(InvoiceStatus.Sent));
            Assert.True(InvoiceStatusPolicy.CanSendReminder(InvoiceStatus.Viewed));
            Assert.True(InvoiceStatusPolicy.CanSendReminder(InvoiceStatus.PartiallyPaid));
            Assert.True(InvoiceStatusPolicy.CanSendReminder(InvoiceStatus.Overdue));

            Assert.False(InvoiceStatusPolicy.CanSendReminder(InvoiceStatus.Draft));
            Assert.False(InvoiceStatusPolicy.CanSendReminder(InvoiceStatus.Paid));
            Assert.False(InvoiceStatusPolicy.CanSendReminder(InvoiceStatus.Void));
        }
    }
}
