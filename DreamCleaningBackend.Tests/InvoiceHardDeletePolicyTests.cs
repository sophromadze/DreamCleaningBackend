using DreamCleaningBackend.Helpers.Commercial;
using DreamCleaningBackend.Models.Commercial;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// WHICH INVOICES MAY BE DESTROYED, as opposed to voided or archived.
    ///
    /// Void and Archive both keep the row. This policy governs the third option, which keeps
    /// nothing - so it exists to say no, and says yes only for an invoice that never touched
    /// money. Pure, so every rule is asserted without a database or Stripe.
    /// </summary>
    public class InvoiceHardDeletePolicyTests
    {
        private static InvoiceDeletionFacts Clean(InvoiceStatus status = InvoiceStatus.Draft) =>
            new()
            {
                Status = status,
                AmountPaid = 0m,
                PaymentCount = 0,
                ExternalPaymentAttemptCount = 0,
                CommittedOrderAllocationCount = 0
            };

        // ── what MAY be destroyed ──────────────────────────────────────────────

        /// <summary>
        /// A draft is the case the old <c>DeleteDraftAsync</c> already allowed, and it still does.
        /// The rest are the point of widening it: a test invoice that was sent, opened, went past
        /// due, or was voided, and that nobody ever paid a cent against.
        /// </summary>
        [Theory]
        [InlineData(InvoiceStatus.Draft)]
        [InlineData(InvoiceStatus.Sent)]
        [InlineData(InvoiceStatus.Viewed)]
        [InlineData(InvoiceStatus.Overdue)]
        [InlineData(InvoiceStatus.Void)]
        public void AnInvoiceWithNoFinancialActivityMayBeDestroyed(InvoiceStatus status)
        {
            Assert.Null(InvoiceHardDeletePolicy.DescribeBlocker(Clean(status)));
            Assert.True(InvoiceHardDeletePolicy.CanHardDelete(Clean(status)));
        }

        // ── payments ───────────────────────────────────────────────────────────

        /// <summary>A payment row is accounting history and is never destroyed by a UI button.</summary>
        [Fact]
        public void APaymentRowBlocksDestruction()
        {
            var facts = Clean(InvoiceStatus.Paid) with { PaymentCount = 1, AmountPaid = 500m };

            var blocker = InvoiceHardDeletePolicy.DescribeBlocker(facts);

            Assert.NotNull(blocker);
            Assert.Contains("financial activity", blocker);
            Assert.Contains("Void or archive it instead", blocker);
        }

        /// <summary>A part-paid invoice is the same answer for the same reason.</summary>
        [Fact]
        public void APartiallyPaidInvoiceBlocksDestruction()
        {
            var facts = Clean(InvoiceStatus.PartiallyPaid) with
            {
                PaymentCount = 1,
                AmountPaid = 250m
            };

            Assert.NotNull(InvoiceHardDeletePolicy.DescribeBlocker(facts));
        }

        /// <summary>
        /// A REFUNDED INVOICE IS NOT CLEAN AGAIN.
        ///
        /// A payment and its reversal net to zero paid, so a policy that only looked at
        /// AmountPaid would cheerfully destroy two rows describing money that moved in the real
        /// world. PaymentCount is tested independently of the amount precisely for this case.
        /// </summary>
        [Fact]
        public void AFullyReversedPaymentStillBlocksDestruction()
        {
            var facts = Clean(InvoiceStatus.Sent) with { PaymentCount = 2, AmountPaid = 0m };

            Assert.NotNull(InvoiceHardDeletePolicy.DescribeBlocker(facts));
        }

        /// <summary>
        /// And the mirror: money recorded with no row to explain it is still money. Neither test
        /// is redundant, because each catches a state the other misses.
        /// </summary>
        [Fact]
        public void MoneyRecordedWithNoPaymentRowStillBlocksDestruction()
        {
            var facts = Clean(InvoiceStatus.Sent) with { PaymentCount = 0, AmountPaid = 120m };

            Assert.NotNull(InvoiceHardDeletePolicy.DescribeBlocker(facts));
        }

        // ── Stripe ─────────────────────────────────────────────────────────────

        /// <summary>
        /// A checkout session or payment intent exists on STRIPE's side. We cannot remove it, and
        /// silently dropping our half leaves an object nothing local explains - even when the
        /// attempt failed.
        /// </summary>
        [Fact]
        public void StripeActivityBlocksDestructionEvenWithNoPaymentRow()
        {
            var facts = Clean(InvoiceStatus.Sent) with { ExternalPaymentAttemptCount = 1 };

            var blocker = InvoiceHardDeletePolicy.DescribeBlocker(facts);

            Assert.NotNull(blocker);
            Assert.Contains("Stripe", blocker);
        }

        // ── claimed cleanings ──────────────────────────────────────────────────

        /// <summary>
        /// Sending an invoice ADOPTS its cleanings onto the Invoice payment method. Deleting it
        /// would cascade the allocations away and leave those orders stamped as invoice-billed by
        /// an invoice that no longer exists - so the admin is sent to Void first, which is what
        /// hands them back.
        /// </summary>
        [Fact]
        public void CommittedOrderAllocationsBlockDestructionAndPointAtVoid()
        {
            var facts = Clean(InvoiceStatus.Sent) with { CommittedOrderAllocationCount = 2 };

            var blocker = InvoiceHardDeletePolicy.DescribeBlocker(facts);

            Assert.NotNull(blocker);
            Assert.Contains("claimed cleanings", blocker);
            Assert.Contains("Void it first", blocker);
        }

        /// <summary>
        /// An UNCOMMITTED allocation is only a draft proposal - nothing was written to the orders,
        /// so it protects nothing and the invoice stays deletable.
        /// </summary>
        [Fact]
        public void UncommittedAllocationsDoNotBlockDestruction()
        {
            Assert.Null(InvoiceHardDeletePolicy.DescribeBlocker(
                Clean() with { CommittedOrderAllocationCount = 0 }));
        }

        // ── the arithmetic/status disagreement ─────────────────────────────────

        /// <summary>
        /// Paid or PartiallyPaid with no payment rows should not exist. If the arithmetic and the
        /// status ever disagree, refuse - the status is not the half to trust when destroying
        /// something.
        /// </summary>
        [Theory]
        [InlineData(InvoiceStatus.Paid)]
        [InlineData(InvoiceStatus.PartiallyPaid)]
        public void APaidStatusBlocksEvenWithNoPaymentRows(InvoiceStatus status)
        {
            Assert.NotNull(InvoiceHardDeletePolicy.DescribeBlocker(Clean(status)));
        }

        /// <summary>Every refusal names what to do instead, so none of them is a dead end.</summary>
        [Fact]
        public void EveryRefusalOffersAnAlternative()
        {
            var cases = new[]
            {
                Clean(InvoiceStatus.Sent) with { PaymentCount = 1 },
                Clean(InvoiceStatus.Sent) with { AmountPaid = 10m },
                Clean(InvoiceStatus.Sent) with { ExternalPaymentAttemptCount = 1 },
                Clean(InvoiceStatus.Sent) with { CommittedOrderAllocationCount = 1 },
                Clean(InvoiceStatus.Paid)
            };

            foreach (var facts in cases)
            {
                var blocker = InvoiceHardDeletePolicy.DescribeBlocker(facts);
                Assert.NotNull(blocker);
                Assert.True(
                    blocker!.Contains("archive", StringComparison.OrdinalIgnoreCase)
                    || blocker.Contains("Void", StringComparison.OrdinalIgnoreCase),
                    $"Refusal gave the admin nowhere to go: {blocker}");
            }
        }
    }
}
