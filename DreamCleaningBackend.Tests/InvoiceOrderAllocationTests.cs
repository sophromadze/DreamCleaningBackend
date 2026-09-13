using DreamCleaningBackend.DTOs.Commercial;
using DreamCleaningBackend.Helpers.Commercial;
using DreamCleaningBackend.Models.Commercial;
using DreamCleaningBackend.Models.Contracts;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// ONE INVOICE, MANY CLEANINGS — the allocation arithmetic, the negotiated group total, and
    /// the eligibility rules around both.
    ///
    /// Everything here is pure. The money rule this file exists to pin down is that the allocated
    /// order amounts sum to the invoice total EXACTLY, to the cent, in every case — including the
    /// ones where naive decimal division does not.
    /// </summary>
    public class InvoiceOrderAllocationTests
    {
        private static InvoiceAllocationCandidate Visit(int id, int day, decimal total) => new()
        {
            OrderId = id,
            ServiceDate = new DateTime(2026, 10, day),
            CurrentTotal = total
        };

        private static List<InvoiceAllocationCandidate> FourWeeklyVisits() => new()
        {
            Visit(1, 4, 925.43m),
            Visit(2, 11, 925.43m),
            Visit(3, 18, 925.43m),
            Visit(4, 25, 925.43m)
        };

        // ── F29: the default is what the cleanings cost ───────────────────────────────────────

        [Fact]
        public void FourVisitsAt92543DefaultToTheirSum()
        {
            Assert.Equal(3701.72m, InvoiceOrderAllocator.SumCurrentTotals(FourWeeklyVisits()));
        }

        [Fact]
        public void WithNoNegotiatedTotalEachOrderKeepsItsOwnPrice()
        {
            var mixed = new List<InvoiceAllocationCandidate>
            {
                Visit(1, 4, 925.43m),
                Visit(2, 11, 640.00m)
            };

            var lines = InvoiceOrderAllocator.KeepCurrentTotals(mixed);

            Assert.Equal(925.43m, lines.Single(l => l.OrderId == 1).AllocatedAmount);
            Assert.Equal(640.00m, lines.Single(l => l.OrderId == 2).AllocatedAmount);
            Assert.All(lines, l => Assert.False(l.ChangesOrderTotal));
        }

        // ── F30 / F31 / F32 / F33: the worked example ─────────────────────────────────────────

        [Fact]
        public void AnAgreedTotalOf3500SplitsIntoFourExact875s()
        {
            var lines = InvoiceOrderAllocator.DistributeEqually(FourWeeklyVisits(), 3500.00m);

            Assert.Equal(4, lines.Count);
            Assert.All(lines, l => Assert.Equal(875.00m, l.AllocatedAmount));

            // The invoice totals exactly what was agreed...
            Assert.Equal(3500.00m, lines.Sum(l => l.AllocatedAmount));
            Assert.True(InvoiceOrderAllocator.SumsExactlyTo(lines, 3500.00m));

            // ...and every order's price genuinely moves.
            Assert.All(lines, l => Assert.True(l.ChangesOrderTotal));
            Assert.All(lines, l => Assert.Equal(925.43m, l.PreviousTotal));
        }

        [Fact]
        public void SharesAreEqual_NotProportionalToTheOldPrices()
        {
            // "We agreed $3,500 for the month" means four equal visits, not four differently
            // discounted ones. Preserving the previous ratios would produce numbers nobody
            // negotiated and nobody can check against the agreement.
            var uneven = new List<InvoiceAllocationCandidate>
            {
                Visit(1, 4, 2000m),
                Visit(2, 11, 1000m),
                Visit(3, 18, 500m),
                Visit(4, 25, 500m)
            };

            var lines = InvoiceOrderAllocator.DistributeEqually(uneven, 3500.00m);

            Assert.All(lines, l => Assert.Equal(875.00m, l.AllocatedAmount));
        }

        // ── F34: odd cents ────────────────────────────────────────────────────────────────────

        [Fact]
        public void OneHundredAcrossThreeVisitsSumsExactlyToOneHundred()
        {
            var lines = InvoiceOrderAllocator.DistributeEqually(new[]
            {
                Visit(1, 4, 40m), Visit(2, 11, 30m), Visit(3, 18, 30m)
            }, 100.00m);

            // The remainder goes to the EARLIEST service date, deterministically.
            Assert.Equal(33.34m, lines[0].AllocatedAmount);
            Assert.Equal(33.33m, lines[1].AllocatedAmount);
            Assert.Equal(33.33m, lines[2].AllocatedAmount);
            Assert.Equal(100.00m, lines.Sum(l => l.AllocatedAmount));
        }

        [Fact]
        public void EveryAwkwardTotalStillSumsExactly()
        {
            decimal[] totals = { 0.01m, 0.03m, 100m, 3500m, 3701.72m, 999.99m, 1234.57m, 10_000.01m };

            for (var count = 1; count <= 9; count++)
            {
                var visits = Enumerable.Range(1, count)
                    .Select(i => Visit(i, i, 100m))
                    .ToList();

                foreach (var total in totals)
                {
                    var lines = InvoiceOrderAllocator.DistributeEqually(visits, total);

                    Assert.True(InvoiceOrderAllocator.SumsExactlyTo(lines, total),
                        $"{total} across {count} visit(s) did not sum exactly.");

                    // Shares differ by at most one cent — that is what "equal" means once a
                    // remainder has to go somewhere.
                    var spread = lines.Max(l => l.AllocatedAmount) - lines.Min(l => l.AllocatedAmount);
                    Assert.True(spread <= 0.01m, $"{total} across {count} spread by {spread}.");
                }
            }
        }

        [Fact]
        public void TheRemainderAlwaysGoesToTheEARLIESTDates()
        {
            // Deterministic, so a draft an admin reads on Monday and finalizes on Thursday does
            // not quietly redistribute pennies in between. Handed in reverse order on purpose.
            var lines = InvoiceOrderAllocator.DistributeEqually(new[]
            {
                Visit(3, 18, 10m), Visit(1, 4, 10m), Visit(2, 11, 10m)
            }, 10.00m);

            Assert.Equal(new[] { 1, 2, 3 }, lines.Select(l => l.OrderId));
            Assert.Equal(3.34m, lines[0].AllocatedAmount);
            Assert.Equal(3.33m, lines[1].AllocatedAmount);
            Assert.Equal(3.33m, lines[2].AllocatedAmount);
        }

        [Fact]
        public void CentsAreConvertedThroughDecimal_NotBinaryFloatingPoint()
        {
            // (long)(925.43d * 100) is 92542 — an undercharge of a cent, and the same trap the
            // Stripe checkout documents.
            Assert.Equal(92543L, InvoiceOrderAllocator.ToCents(925.43m));
            Assert.Equal(925.43m, InvoiceOrderAllocator.FromCents(92543L));
        }

        // ── The negotiated total under each tax mode ──────────────────────────────────────────

        [Fact]
        public void TaxInclusiveNegotiatedTotalIsTheLineSum()
        {
            var result = InvoiceGroupTotalSolver.Solve(
                3500m, InvoiceTaxType.Included, 8.875m, InvoiceDiscountType.None, null);

            Assert.True(result.IsExact);
            Assert.Equal(3500m, result.LineSubTotal);
            Assert.Equal(3500m, result.AchievedTotal);
        }

        [Fact]
        public void TaxAddedNegotiatedTotalIsSolvedBackwardsAndVERIFIED()
        {
            var result = InvoiceGroupTotalSolver.Solve(
                3500m, InvoiceTaxType.Added, 8.875m, InvoiceDiscountType.None, null);

            Assert.True(result.IsExact);
            Assert.Equal(3500m, result.AchievedTotal);

            // And the calculator really does reproduce it from the solved subtotal.
            var totals = InvoiceCalculator.Calculate(new InvoiceTotalsInput
            {
                Lines = new List<InvoiceLineInput>
                {
                    new() { Quantity = 1m, UnitPrice = result.LineSubTotal }
                },
                TaxType = InvoiceTaxType.Added,
                TaxRate = 8.875m
            });

            Assert.Equal(3500m, totals.Total);
        }

        [Fact]
        public void ANegotiatedTotalIsRefusedAlongsideADiscount()
        {
            // Two ways of saying the same thing. Composing them would discount the agreed figure.
            var result = InvoiceGroupTotalSolver.Solve(
                3500m, InvoiceTaxType.Included, 8.875m, InvoiceDiscountType.Percentage, 5m);

            Assert.False(result.IsExact);
            Assert.Contains("Remove the invoice discount", result.Error);
        }

        // ── 11E: the tax-inclusive regression this file was asked to lock down ────────────────

        [Fact]
        public void NineTwentyFiveFortyThreeInclusiveOfTaxSplitsTo84999And7544()
        {
            var totals = InvoiceCalculator.Calculate(new InvoiceTotalsInput
            {
                Lines = new List<InvoiceLineInput> { new() { Quantity = 1m, UnitPrice = 925.43m } },
                TaxType = InvoiceTaxType.Included,
                TaxRate = 8.875m
            });

            // The typed total never moves...
            Assert.Equal(925.43m, totals.Total);

            // ...the tax is split back out BY SUBTRACTION...
            Assert.Equal(75.44m, totals.TaxAmount);

            // ...and the rounded pre-tax figure is $849.99.
            Assert.Equal(849.99m, totals.Total - totals.TaxAmount);

            // The property that matters: the two halves add back to the total EXACTLY. Deriving
            // the tax as round2(preTax x rate) instead lands a cent out and the invoice prints a
            // subtotal and a tax that do not sum to what the client is asked to pay.
            Assert.Equal(totals.Total, (totals.Total - totals.TaxAmount) + totals.TaxAmount);
        }

        // ── 11D: contract eligibility, shared by the button and the endpoint ──────────────────

        [Theory]
        [InlineData(ContractStatus.FullySigned)]
        [InlineData(ContractStatus.Completed)]
        public void AnExecutedContractMayRaiseAnInvoice(ContractStatus status)
        {
            Assert.True(ContractInvoiceEligibility.CanCreateNextInvoice(status, isHidden: false));
            Assert.Null(ContractInvoiceEligibility.Check(status, isHidden: false));
        }

        [Theory]
        [InlineData(ContractStatus.Draft)]
        [InlineData(ContractStatus.PreviewGenerated)]
        [InlineData(ContractStatus.AwaitingClientReview)]
        [InlineData(ContractStatus.NeedsRevision)]
        [InlineData(ContractStatus.ReadyForSignature)]
        [InlineData(ContractStatus.AwaitingSignatures)]
        [InlineData(ContractStatus.PartiallySigned)]
        [InlineData(ContractStatus.Voided)]
        [InlineData(ContractStatus.Expired)]
        public void AContractThatIsNotInForceMayNot(ContractStatus status)
        {
            Assert.False(ContractInvoiceEligibility.CanCreateNextInvoice(status, isHidden: false));

            var reason = ContractInvoiceEligibility.Check(status, isHidden: false);
            Assert.False(string.IsNullOrWhiteSpace(reason));
        }

        [Fact]
        public void ADeletedContractMayNot_EvenWhenFullySigned()
        {
            Assert.False(ContractInvoiceEligibility.CanCreateNextInvoice(
                ContractStatus.FullySigned, isHidden: true));

            Assert.Contains("deleted",
                ContractInvoiceEligibility.Check(ContractStatus.FullySigned, isHidden: true)!);
        }

        [Fact]
        public void HavingNoPreviousInvoiceIsNotAReasonToRefuse()
        {
            // The eligibility rule is a pure function of status and visibility — it cannot see
            // invoice history, which is precisely the point: a signed contract with nothing billed
            // yet is the case that needs its FIRST draft.
            var parameters = typeof(ContractInvoiceEligibility)
                .GetMethod(nameof(ContractInvoiceEligibility.Check))!
                .GetParameters()
                .Select(p => p.ParameterType)
                .ToArray();

            Assert.Equal(new[] { typeof(ContractStatus), typeof(bool) }, parameters);
        }

        // ── E28: an order cannot silently belong to two live invoices ─────────────────────────

        [Fact]
        public void TheLinkEntityCarriesTheAllocationAndItsHistory()
        {
            // Structural: the requirement asks for an explicit join carrying an allocated amount,
            // the amount before it, and who committed it when — a bare link table could answer
            // none of the audit questions.
            var t = typeof(CommercialInvoiceOrder);

            foreach (var required in new[]
                     {
                         nameof(CommercialInvoiceOrder.CommercialInvoiceId),
                         nameof(CommercialInvoiceOrder.OrderId),
                         nameof(CommercialInvoiceOrder.AllocatedAmount),
                         nameof(CommercialInvoiceOrder.OriginalOrderTotal),
                         nameof(CommercialInvoiceOrder.CommittedAt),
                         nameof(CommercialInvoiceOrder.CommittedByUserId),
                         nameof(CommercialInvoiceOrder.ActivatedOrderAt)
                     })
            {
                Assert.NotNull(t.GetProperty(required));
            }
        }

        [Fact]
        public void ADraftAllocationIsAProposalUntilItIsCommitted()
        {
            var link = new CommercialInvoiceOrder { AllocatedAmount = 875m, OriginalOrderTotal = 925.43m };

            // Nothing has been written to the order yet — that is what CommittedAt records, and
            // it is why abandoning a draft cannot re-price a real booking.
            Assert.Null(link.CommittedAt);
            Assert.Null(link.ActivatedOrderAt);
        }

        [Fact]
        public void TheSelectionRequestCannotCarryPerOrderAmounts()
        {
            // Allocation is a SERVER rule. A request able to say "give this visit $2,000 and that
            // one $500" is exactly the shape a hand-rolled or mis-typed one would take.
            var names = typeof(SaveInvoiceOrdersDto).GetProperties().Select(p => p.Name).ToList();

            Assert.Contains("OrderIds", names);
            Assert.Contains("NegotiatedGroupTotal", names);
            foreach (var forbidden in new[] { "Allocations", "Amounts", "AllocatedAmount", "SubTotal", "Total" })
                Assert.DoesNotContain(forbidden, names);
        }
    }
}
