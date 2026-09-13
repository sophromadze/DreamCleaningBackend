using System.Reflection;
using System.Text.RegularExpressions;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// Three things that all turn on the same distinction, which is why they are asserted together:
    ///
    ///  • <b>The Invoice payment method</b> is handled outside Stripe but is NOT settled by being
    ///    chosen. Every "has this been paid for?" query has to know the difference.
    ///  • <b>Recreating an order</b> copies the METHOD and nothing else about the money.
    ///  • <b>Lifetime loyalty</b> is a discount that survives being used, and switches the
    ///    inactivity automation off for that customer.
    /// </summary>
    public class InvoicePaymentMethodAndLoyaltyTests
    {
        // ── D19 / D20: Invoice is a real method, and it does not mean "paid" ──────────────────

        [Fact]
        public void InvoiceIsAFirstClassPaymentMethod()
        {
            Assert.True(Enum.IsDefined(typeof(PaymentMethod), PaymentMethod.Invoice));
            Assert.Equal(5, (int)PaymentMethod.Invoice);

            // The wire format the admin UI sends round-trips.
            Assert.Equal(PaymentMethod.Invoice, PaymentMethodRules.Parse("Invoice"));
            Assert.Equal(PaymentMethod.Invoice, PaymentMethodRules.Parse("invoice"));
            Assert.Null(PaymentMethodRules.Parse("Cheque"));
            Assert.Null(PaymentMethodRules.Parse(null));
        }

        [Fact]
        public void TheExistingMethodsKeepTheirNumbers()
        {
            // The enum is persisted as an int. Renumbering would silently relabel every historical
            // order, which is why Invoice was appended rather than inserted.
            Assert.Equal(0, (int)PaymentMethod.Normal);
            Assert.Equal(1, (int)PaymentMethod.Cash);
            Assert.Equal(2, (int)PaymentMethod.Zelle);
            Assert.Equal(3, (int)PaymentMethod.Check);
            Assert.Equal(4, (int)PaymentMethod.Other);
        }

        [Theory]
        [InlineData(PaymentMethod.Cash, true)]
        [InlineData(PaymentMethod.Zelle, true)]
        [InlineData(PaymentMethod.Check, true)]
        [InlineData(PaymentMethod.Other, true)]
        [InlineData(PaymentMethod.Invoice, false)]   // the whole point
        [InlineData(PaymentMethod.Normal, false)]
        public void OnlyMoneyThatHasALREADYArrivedCountsAsSettledOnRecord(
            PaymentMethod method, bool settled)
        {
            Assert.Equal(settled, PaymentMethodRules.IsSettledOnRecord(method));
        }

        [Theory]
        [InlineData(PaymentMethod.Normal, false)]
        [InlineData(PaymentMethod.Cash, true)]
        [InlineData(PaymentMethod.Invoice, true)]
        public void EverythingButStripeIsHandledOutsideIt(PaymentMethod method, bool outside)
        {
            Assert.Equal(outside, PaymentMethodRules.IsOutsideStripe(method));
        }

        // ── D21 / D22 / D23: an invoice-billed order stays pending until it is PAID ───────────

        [Fact]
        public void AnInvoiceOrderIsNotSettledUntilItsInvoiceIsPaid()
        {
            var order = new Order { PaymentMethod = PaymentMethod.Invoice, IsPaid = false };

            // Draft / Open / Viewed / Overdue invoice, or an ACH debit still processing — no
            // money has moved, so nothing stamps InvoicePaidAt and the order is not settled.
            Assert.False(OrderPaymentFilter.IsSettledInMemory(order));

            order.InvoicePaidAt = new DateTime(2026, 10, 20);
            Assert.True(OrderPaymentFilter.IsSettledInMemory(order));
        }

        [Fact]
        public void ACashOrderIsSettledWithoutAnyInvoiceTimestamp()
        {
            // The new clause must be a strict no-op for every method that existed before it.
            Assert.True(OrderPaymentFilter.IsSettledInMemory(
                new Order { PaymentMethod = PaymentMethod.Cash }));
            Assert.True(OrderPaymentFilter.IsSettledInMemory(
                new Order { PaymentMethod = PaymentMethod.Zelle }));
            Assert.True(OrderPaymentFilter.IsSettledInMemory(
                new Order { PaymentMethod = PaymentMethod.Normal, IsPaid = true }));
            Assert.False(OrderPaymentFilter.IsSettledInMemory(
                new Order { PaymentMethod = PaymentMethod.Normal, IsPaid = false }));
        }

        [Fact]
        public void ReportingQueriesCarryTheInvoiceClause()
        {
            // EF cannot translate a helper composed inside a bigger Where, so the money-facing
            // queries write the expression out — the arrangement CleanerPayrollCalculator already
            // documents. This asserts they were actually updated, since a missed one silently
            // reports an unpaid commercial job as revenue.
            foreach (var file in new[]
                     {
                         Path.Combine("Controllers", "Admin", "AdminStatisticsController.cs"),
                         Path.Combine("Helpers", "AdminBonusAttribution.cs")
                     })
            {
                var code = ReadBackendFile(file);

                var oldStyle = Regex.Matches(
                    code, @"o\.IsPaid \|\| o\.PaymentMethod != PaymentMethod\.Normal\)");

                Assert.True(oldStyle.Count == 0,
                    $"{file} still has a settled-order test that treats Invoice as paid. "
                    + "See Helpers/OrderPaymentFilter.");

                Assert.Contains("InvoicePaidAt", code);
            }
        }

        [Fact]
        public void ActivationNeverDowngradesACompletedOrder()
        {
            // Source-level, because the rule lives inside a transaction: an invoice settling after
            // the cleaning happened is bookkeeping catching up, not a reason to reopen the job.
            var code = ReadBackendFile(Path.Combine("Services", "OrderInvoiceAllocationService.cs"));

            Assert.Contains("OrderStatuses.Is(order.Status, OrderStatuses.Pending)", code);
            Assert.Contains("order.Status = OrderStatuses.Active", code);
        }

        [Fact]
        public void ActivationIsIdempotent()
        {
            var code = ReadBackendFile(Path.Combine("Services", "OrderInvoiceAllocationService.cs"));

            // The marker that makes a retried webhook a no-op.
            Assert.Contains("if (order.InvoicePaidAt != null) return false;", code);
        }

        [Fact]
        public void ManualMarkAsPaidAndTheStripeWebhookShareOneActivationPath()
        {
            // Two call sites, one method. A second implementation for the manual side is how the
            // two would come to behave differently.
            var controller = ReadBackendFile(
                Path.Combine("Controllers", "Admin", "AdminCommercialInvoicesController.cs"));
            var stripe = ReadBackendFile(
                Path.Combine("Services", "Commercial", "InvoiceStripePaymentService.cs"));

            Assert.Contains("ActivateCoveredOrdersAsync", controller);
            Assert.Contains("ActivateCoveredOrdersAsync", stripe);
        }

        // ── G37 / G38: recreate keeps the METHOD and no transaction state ─────────────────────

        [Fact]
        public void TheRecreatePreviewCarriesTheSourceOrdersPaymentMethod()
        {
            var property = typeof(ReorderPreviewDto)
                .GetProperty(nameof(ReorderPreviewDto.SourcePaymentMethod));

            Assert.NotNull(property);
            Assert.Equal(typeof(string), property!.PropertyType);
        }

        [Fact]
        public void TheModalNoLongerHardcodesCash()
        {
            // The defect, pinned: resetForm used to assign 'Cash' unconditionally, so recreating a
            // Stripe booking silently produced a cash job — marked Active, never charged, counted
            // as settled revenue and invisible to every unpaid-order sweep.
            var modal = ReadFrontendFile(
                "shared", "components", "recreate-order-modal", "recreate-order-modal.component.ts");

            Assert.DoesNotContain("this.paymentMethod = 'Cash';", modal);
            Assert.Contains("sourcePaymentMethod", modal);
        }

        [Fact]
        public void OldPaymentTransactionStateIsUNREPRESENTABLEInThePrefill()
        {
            // Not merely "not copied": the prefill is a CreateBookingDto, which has no field a
            // PaymentIntent, a reference, a paid flag or an invoice link could travel in. The
            // recreated order is a new financial transaction by construction.
            var names = typeof(CreateBookingDto).GetProperties().Select(p => p.Name).ToList();

            foreach (var forbidden in new[]
                     {
                         "PaymentIntentId", "PaymentReference", "IsPaid", "PaidAt",
                         "TransactionId", "AmountPaid", "PaymentMethod", "InvoiceId",
                         "CommercialInvoiceId", "InvoicePaidAt", "ManualPaymentRecordedAt"
                     })
            {
                Assert.DoesNotContain(forbidden, names);
            }
        }

        [Fact]
        public void TheRecreatePreviewAlsoCarriesNoTransactionState()
        {
            var names = typeof(ReorderPreviewDto).GetProperties().Select(p => p.Name).ToList();

            foreach (var forbidden in new[]
                     {
                         "PaymentIntentId", "PaymentReference", "IsPaid", "PaidAt",
                         "TransactionId", "AmountPaid"
                     })
            {
                Assert.DoesNotContain(forbidden, names);
            }
        }

        // ── H39–H44: lifetime loyalty ─────────────────────────────────────────────────────────

        [Fact]
        public void LifetimeIsItsOwnFlag_SeparateFromManualOverride()
        {
            var user = new User();

            // Default is the pre-existing ONE-TIME behaviour, so nothing about an existing account
            // changes when the column appears.
            Assert.False(user.LoyaltyDiscountIsLifetime);
            Assert.False(user.LoyaltyDiscountIsManualOverride);
        }

        [Fact]
        public void SettingADiscountDefaultsToOneTime()
        {
            // The interface's optional parameter is what keeps every pre-existing caller producing
            // exactly the discount it always did. Lifetime has to be asked for.
            var method = typeof(DreamCleaningBackend.Services.Interfaces.ILoyaltyDiscountService)
                .GetMethod(nameof(DreamCleaningBackend.Services.Interfaces.ILoyaltyDiscountService.SetManualAsync))!;

            var isLifetime = method.GetParameters().Single(p => p.Name == "isLifetime");

            Assert.True(isLifetime.HasDefaultValue);
            Assert.Equal(false, isLifetime.DefaultValue);
        }

        [Fact]
        public void ALifetimeDiscountIsNOTConsumedByAnOrder()
        {
            // Source-level: the consumption path is a transaction over the User row, and what
            // matters is that it RETURNS before zeroing anything for a lifetime customer.
            var code = ReadBackendFile(Path.Combine("Services", "LoyaltyDiscountService.cs"));

            var applyIndex = code.IndexOf("public async Task ApplyToOrderAsync", StringComparison.Ordinal);
            Assert.True(applyIndex > 0);

            var body = code[applyIndex..];
            var lifetimeIndex = body.IndexOf("if (user.LoyaltyDiscountIsLifetime)", StringComparison.Ordinal);
            var zeroIndex = body.IndexOf("user.LoyaltyDiscountPercentage = 0;", StringComparison.Ordinal);

            Assert.True(lifetimeIndex > 0, "ApplyToOrderAsync has no lifetime branch.");
            Assert.True(lifetimeIndex < zeroIndex,
                "The lifetime branch must return BEFORE the percentage is zeroed, or a lifetime "
                + "discount would be consumed like a one-time one.");
        }

        [Fact]
        public void ClearingALifetimeDiscountAlsoClearsTheFlag()
        {
            // Otherwise the customer would be frozen out of the 60/90-day automation forever with
            // nothing to show for it.
            var code = ReadBackendFile(Path.Combine("Services", "LoyaltyDiscountService.cs"));

            var clearIndex = code.IndexOf("public async Task<LoyaltyDiscountDto> ClearAsync", StringComparison.Ordinal);
            Assert.True(clearIndex > 0);

            var body = code[clearIndex..];
            Assert.Contains("user.LoyaltyDiscountIsLifetime = false;", body);
        }

        [Fact]
        public void AnActiveLifetimeDiscountSuppressesTheSixtyAndNinetyDayAutomation()
        {
            // The requirement is explicit and covers BOTH milestones plus the reminder sends, so
            // the guard has to sit before every milestone branch rather than inside one of them.
            var code = ReadBackendFile(Path.Combine("Services", "LoyaltyReengagementService.cs"));

            var processIndex = code.IndexOf("private async Task ProcessCandidateAsync", StringComparison.Ordinal);
            Assert.True(processIndex > 0);

            var body = code[processIndex..];
            var guardIndex = body.IndexOf("if (c.LoyaltyDiscountIsLifetime)", StringComparison.Ordinal);
            var day90Index = body.IndexOf("daysSinceLastOrder >= day90", StringComparison.Ordinal);
            var day60Index = body.IndexOf("daysSinceLastOrder >= day60", StringComparison.Ordinal);

            Assert.True(guardIndex > 0, "The reengagement worker has no lifetime guard.");
            Assert.True(guardIndex < day60Index && guardIndex < day90Index,
                "The lifetime guard must precede BOTH milestone branches — otherwise a lifetime "
                + "customer is still activated at 60 days or upgraded at 90.");
        }

        [Fact]
        public void ADiscountThatLosesTheStackingRoundStaysOnTheAccount()
        {
            // No stacking: the best single discount wins per order. Losing that round zeroes the
            // ORDER's loyalty amount, and ApplyToOrderAsync no-ops on an order that carries none —
            // so the entitlement is untouched. Asserted through the existing stacking gate.
            var (loyaltyAmount, loyaltyPct, subscriptionAmount, promoAmount) =
                OrderPricingCalculator.ResolveLoyaltyStacking(
                    loyaltyCandidateAmount: 20m, loyaltyCandidatePercentage: 10m,
                    subscriptionAmount: 0m, promoAmount: 50m);

            // Promo wins, loyalty is zeroed FOR THIS ORDER only.
            Assert.Equal(0m, loyaltyAmount);
            Assert.Equal(0m, loyaltyPct);
            Assert.Equal(50m, promoAmount);
            Assert.Equal(0m, subscriptionAmount);
        }

        [Fact]
        public void TheLoyaltyDtoReportsLifetimeAsItsOwnStatus()
        {
            var names = typeof(LoyaltyDiscountDto).GetProperties().Select(p => p.Name).ToList();
            Assert.Contains(nameof(LoyaltyDiscountDto.IsLifetime), names);

            var setNames = typeof(SetLoyaltyDiscountDto).GetProperties().Select(p => p.Name).ToList();
            Assert.Contains(nameof(SetLoyaltyDiscountDto.IsLifetime), setNames);
        }

        // ── helpers ───────────────────────────────────────────────────────────────────────────

        private static string ReadBackendFile(string relativePath)
        {
            var root = SolutionRoot();
            var path = Path.Combine(root, "DreamCleaningBackend", "DreamCleaningBackend", relativePath);
            Assert.True(File.Exists(path), $"{path} was not found.");
            return File.ReadAllText(path);
        }

        private static string ReadFrontendFile(params string[] parts)
        {
            var root = SolutionRoot();
            var path = Path.Combine(
                new[] { root, "DreamCleaningNG", "src", "app" }.Concat(parts).ToArray());
            Assert.True(File.Exists(path), $"{path} was not found.");
            return File.ReadAllText(path);
        }

        /// <summary>The folder holding both projects — the tests reach across to the Angular app
        /// for the handful of rules that are only enforceable on that side.</summary>
        private static string SolutionRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "DreamCleaningNG")))
                dir = dir.Parent;

            Assert.NotNull(dir);
            return dir!.FullName;
        }
    }
}
