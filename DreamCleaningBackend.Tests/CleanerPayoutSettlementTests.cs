using DreamCleaningBackend.Helpers;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// What is still owed on a payout line after the money has already gone out.
    ///
    /// The case that produced this rule, from production (2026-09): two cleaners were paid $73.50
    /// each for a 3h30 deep clean; they then reported four hours, an admin added the hour in the
    /// orders panel, and the Outgoing Payments page redrew the line at $84.00 while still showing
    /// it as PAID. The $10.50 a head that was genuinely owed appeared nowhere. Every figure in
    /// the first test below is that order.
    /// </summary>
    public class CleanerPayoutSettlementTests
    {
        // ===== The order that prompted this =====

        [Fact]
        public void AnOrderThatGrewAfterPayday_OwesTheDifference_NotTheWholePayoutAgain()
        {
            var settlement = CleanerPayoutSettlement.Resolve(isPaid: true, paidAmount: 73.50m, currentPayout: 84.00m);

            Assert.Equal(10.50m, settlement.Outstanding);
            Assert.True(settlement.IsTopUp);
            Assert.False(settlement.IsSettled);
            // The frozen record of what actually left the company is untouched.
            Assert.Equal(73.50m, settlement.PaidAmount);
            Assert.Equal(0m, settlement.Overpaid);
        }

        [Fact]
        public void APaidLineWorthTheSame_IsSettled()
        {
            var settlement = CleanerPayoutSettlement.Resolve(isPaid: true, paidAmount: 94.50m, currentPayout: 94.50m);

            Assert.True(settlement.IsSettled);
            Assert.False(settlement.IsTopUp);
            Assert.Equal(0m, settlement.Outstanding);
        }

        // ===== A line nobody has paid is not a shortfall =====

        [Fact]
        public void AnUnpaidLine_OwesItsWholePayout_AndIsNeverATopUp()
        {
            var settlement = CleanerPayoutSettlement.Resolve(isPaid: false, paidAmount: null, currentPayout: 84.00m);

            Assert.Equal(84.00m, settlement.Outstanding);
            // The whole point: "additional to pay" wording must never reach a cleaner who has not
            // been paid a first time. IsTopUp is the switch every surface reads for that.
            Assert.False(settlement.IsTopUp);
            Assert.False(settlement.IsSettled);
            Assert.Equal(0m, settlement.PaidAmount);
        }

        [Fact]
        public void AnUnpaidLineIsNotSettledEvenAtZero_SoMarkingItPaidStillRecordsADecision()
        {
            var settlement = CleanerPayoutSettlement.Resolve(isPaid: false, paidAmount: null, currentPayout: 0m);

            Assert.False(settlement.IsSettled);
            Assert.Equal(0m, settlement.Outstanding);
        }

        // ===== The mirror case =====

        [Fact]
        public void HoursCutAfterPayment_ReportOverpayment_AndNeverANegativeAmountOwed()
        {
            var settlement = CleanerPayoutSettlement.Resolve(isPaid: true, paidAmount: 84.00m, currentPayout: 73.50m);

            Assert.Equal(10.50m, settlement.Overpaid);
            // Floored at zero: "we owe them -$10.50" is not something anybody can act on, and
            // netting it against another line would move money between two separate people.
            Assert.Equal(0m, settlement.Outstanding);
            Assert.True(settlement.IsSettled);
            Assert.False(settlement.IsTopUp);
        }

        // ===== Older data =====

        [Fact]
        public void APaidLineWithNoRecordedAmount_OwesTheWholePayoutRatherThanBeingWrittenOff()
        {
            // The historic backfill left PaidAmount null on rows paid before the page existed.
            // Treating null as "covered" would silently forgive money; treating it as zero asks a
            // human to look, which is the safe direction for a payout.
            var settlement = CleanerPayoutSettlement.Resolve(isPaid: true, paidAmount: null, currentPayout: 84.00m);

            Assert.Equal(84.00m, settlement.Outstanding);
            Assert.True(settlement.IsTopUp);
        }

        // ===== Cents =====

        [Fact]
        public void TheDifferenceIsRoundedToCents_SoATopUpCanNeverBeAFractionOfOne()
        {
            var settlement = CleanerPayoutSettlement.Resolve(isPaid: true, paidAmount: 73.505m, currentPayout: 84.004m);

            Assert.Equal(10.50m, settlement.Outstanding);
            Assert.Equal(73.51m, settlement.PaidAmount);
            Assert.Equal(84.00m, settlement.CurrentPayout);
        }

        [Fact]
        public void ASubCentDifference_CountsAsSettled_RatherThanLeavingAPennyOwed()
        {
            var settlement = CleanerPayoutSettlement.Resolve(isPaid: true, paidAmount: 94.50m, currentPayout: 94.502m);

            Assert.True(settlement.IsSettled);
            Assert.Equal(0m, settlement.Outstanding);
        }

        // ===== Paying the difference =====

        [Fact]
        public void PayingTheDifference_SettlesTheLine_AndTheTotalPaidIsTheWholePayout()
        {
            var first = CleanerPayoutSettlement.Resolve(isPaid: true, paidAmount: 73.50m, currentPayout: 84.00m);

            // What MarkPaid does: ADD the shortfall to the frozen figure rather than replacing it,
            // so the running total is everything this person has had for this order. Replacing it
            // would lose the first payment; paying the full payout again would double-count $73.50.
            var totalPaid = first.PaidAmount + first.Outstanding;
            Assert.Equal(84.00m, totalPaid);

            var after = CleanerPayoutSettlement.Resolve(isPaid: true, paidAmount: totalPaid, currentPayout: 84.00m);
            Assert.True(after.IsSettled);
            Assert.Equal(0m, after.Outstanding);
        }

        [Fact]
        public void AnOrderCanGrowTwice_AndOnlyTheLatestDifferenceIsOwed()
        {
            var afterFirstTopUp = CleanerPayoutSettlement.Resolve(isPaid: true, paidAmount: 84.00m, currentPayout: 94.50m);
            Assert.Equal(10.50m, afterFirstTopUp.Outstanding);

            var settled = CleanerPayoutSettlement.Resolve(isPaid: true, paidAmount: 94.50m, currentPayout: 94.50m);
            Assert.True(settled.IsSettled);
        }
    }
}
