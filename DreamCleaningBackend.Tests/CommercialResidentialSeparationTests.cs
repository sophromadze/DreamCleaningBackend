using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// THE TWO PAYMENT WORKFLOWS MUST STAY SEPARATE — but they now SHARE Stripe.
    ///
    ///   Residential: customer → website booking → Stripe PaymentIntent → charged at booking time.
    ///   Commercial:  contract → invoice → Stripe ACH Checkout (or a manual bank transfer an admin
    ///                records) → webhook → invoice ledger.
    ///
    /// THIS FILE CHANGED DELIBERATELY IN 2026-09, and the change is worth explaining because the
    /// original version forbade the word "Stripe" anywhere in the commercial code. That rule was
    /// correct when commercial invoicing was ACH-by-hand only, and it did exactly the job it was
    /// written for: adding online payments could not be done without someone first coming here,
    /// reading why the wall existed, and consciously deciding to move it. A rule that quietly
    /// stopped applying would have taught nobody anything.
    ///
    /// What replaces it is the boundary that still matters. Sharing Stripe INFRASTRUCTURE — one
    /// SDK, one set of keys, one webhook endpoint with one signature check — is good: a second
    /// webhook system would mean two things to configure and two places for a signature check to
    /// be got wrong. Sharing BUSINESS LOGIC would not be, so the tests below assert:
    ///
    ///   1. Commercial pricing never reaches into the residential order-pricing chain.
    ///   2. The residential checkout never depends on commercial invoicing.
    ///   3. Commercial Stripe objects are always identifiable as commercial, so the shared webhook
    ///      can route them apart and neither side can ever process the other's payment.
    ///
    /// Still a SOURCE-level test on purpose: what it prevents is somebody wiring the two together
    /// months from now, and that is caught by reading what a file reaches for, not by running it.
    /// </summary>
    public class CommercialResidentialSeparationTests
    {
        private static string BackendRoot()
        {
            // Walk up from the test assembly to the solution folder, then into the API project.
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "DreamCleaningBackend")))
                dir = dir.Parent;

            Assert.NotNull(dir);
            return Path.Combine(dir!.FullName, "DreamCleaningBackend");
        }

        private static IEnumerable<string> CommercialSourceFiles()
        {
            var root = BackendRoot();

            foreach (var folder in new[]
                     {
                         Path.Combine(root, "Services", "Commercial"),
                         Path.Combine(root, "Models", "Commercial"),
                         Path.Combine(root, "Helpers", "Commercial"),
                         Path.Combine(root, "DTOs", "Commercial")
                     })
            {
                if (!Directory.Exists(folder)) continue;
                foreach (var file in Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories))
                    yield return file;
            }

            foreach (var file in new[]
                     {
                         Path.Combine(root, "Controllers", "Admin", "AdminCommercialInvoicesController.cs"),
                         Path.Combine(root, "Controllers", "Admin", "AdminBillingSettingsController.cs"),
                         Path.Combine(root, "Controllers", "PublicInvoiceController.cs")
                     })
            {
                if (File.Exists(file)) yield return file;
            }
        }

        /// <summary>
        /// STRIPE IS CONFINED TO THE PAYMENT FILES.
        ///
        /// Only the two services whose job is talking to Stripe may mention it. Pricing, status,
        /// PDF rendering and email must stay processor-agnostic — the moment an invoice's TOTAL or
        /// its STATUS depends on which processor was used, the manual-ACH path and the online path
        /// start producing different answers for the same money.
        ///
        /// This is the rule that replaced the blanket ban, and it is the one that keeps the
        /// promise the blanket ban was really making.
        /// </summary>
        [Fact]
        public void StripeIsConfinedToTheCommercialPaymentServices()
        {
            // The only commercial files permitted to know Stripe exists.
            var allowed = new[]
            {
                "InvoiceCheckoutService.cs",        // creates Checkout Sessions
                "InvoiceStripePaymentService.cs",   // handles verified webhook events
                "CommercialInvoicePaymentAttempt.cs" // stores the Stripe ids
            };

            var offenders = new List<string>();

            foreach (var file in CommercialSourceFiles())
            {
                var name = Path.GetFileName(file);
                if (allowed.Contains(name)) continue;

                // Comments are stripped first: several of these files legitimately EXPLAIN their
                // relationship to Stripe without depending on it.
                var code = StripComments(File.ReadAllText(file));

                // A bare "Stripe" prefix on a column name (StripePaymentIntentId) is a stored
                // identifier, not a dependency, so the test looks for the SDK's own types.
                if (Regex.IsMatch(code, @"\busing Stripe\b")
                    || code.Contains("PaymentIntentService")
                    || code.Contains("SessionService")
                    || code.Contains("ChargeService")
                    || code.Contains("StripeConfiguration"))
                {
                    offenders.Add(name);
                }
            }

            Assert.True(offenders.Count == 0,
                "These commercial files reach into the Stripe SDK directly. Pricing, status, PDF and "
                + "email must stay processor-agnostic — route through InvoiceCheckoutService or "
                + "InvoiceStripePaymentService instead: " + string.Join(", ", offenders));
        }

        /// <summary>
        /// EVERY COMMERCIAL STRIPE OBJECT IS IDENTIFIABLE AS COMMERCIAL.
        ///
        /// The residential and commercial flows share one webhook endpoint, and it routes on the
        /// <c>type</c> metadata key. If a commercial Checkout Session were ever created without it,
        /// the resulting payment would fall through to the residential handler — which would look
        /// up an <c>orderId</c> that does not exist, log "unknown payment type", and leave a real
        /// customer's money sitting unreconciled against an invoice that still reads unpaid.
        ///
        /// Asserted structurally so the discriminator cannot be dropped from the session builder.
        /// </summary>
        [Fact]
        public void CommercialStripeObjectsCarryTheRoutingDiscriminator()
        {
            var checkout = Path.Combine(
                BackendRoot(), "Services", "Commercial", "InvoiceCheckoutService.cs");

            Assert.True(File.Exists(checkout), "InvoiceCheckoutService.cs was not found.");

            var code = StripComments(File.ReadAllText(checkout));

            // The metadata dictionary must set "type" from the shared constant, and must attach
            // itself to BOTH the session and the PaymentIntent — payment_intent.* events do not
            // inherit a session's metadata, and those are the events that move money.
            Assert.Contains("StripeCommercialInvoiceMetadata.TypeValue", code);
            Assert.Contains("PaymentIntentData", code);

            // The constant itself must not collide with a residential discriminator.
            var typeValue = DreamCleaningBackend.Services.Commercial
                .StripeCommercialInvoiceMetadata.TypeValue;

            Assert.Equal("commercial_invoice", typeValue);
            Assert.DoesNotContain(typeValue, new[] { "booking", "order_update", "gift_card" });
        }

        /// <summary>
        /// The amount sent to Stripe is derived from the invoice, never accepted from the caller.
        /// Asserted on the DTO, so the browser has no field to put an amount in.
        /// </summary>
        [Fact]
        public void TheCheckoutRequestCannotCarryAnAmount()
        {
            var names = typeof(DreamCleaningBackend.DTOs.Commercial.StartInvoiceCheckoutDto)
                .GetProperties().Select(p => p.Name).ToList();

            foreach (var forbidden in new[] { "Amount", "AmountCents", "Total", "BalanceDue", "Currency" })
                Assert.DoesNotContain(forbidden, names);
        }

        /// <summary>
        /// The invoicing code must not be pulling on the residential ORDER pricing chain either.
        /// A commercial invoice is priced from its own line items; borrowing OrderPricingCalculator
        /// would silently subject a commercial bill to residential rules (loyalty stacking,
        /// subscription discounts, the 8.875% tax constant).
        /// </summary>
        [Fact]
        public void CommercialInvoicingDoesNotUseTheResidentialOrderPricingChain()
        {
            var offenders = new List<string>();

            foreach (var file in CommercialSourceFiles())
            {
                var code = StripComments(File.ReadAllText(file));

                foreach (var forbidden in new[]
                         {
                             "OrderPricingCalculator",
                             "BookingCreationService",
                             "OrderRevenueMath",
                             "OrderPricingInputBuilder"
                         })
                {
                    if (code.Contains(forbidden))
                        offenders.Add($"{Path.GetFileName(file)} → {forbidden}");
                }
            }

            Assert.True(offenders.Count == 0,
                "Commercial invoicing borrowed from the residential order pricing chain: " +
                string.Join(", ", offenders));
        }

        /// <summary>
        /// The reverse direction. Nothing in the residential booking/order/Stripe path may depend
        /// on the commercial invoicing namespaces — if it did, changing an invoice rule could move
        /// what a residential customer is charged.
        /// </summary>
        [Fact]
        public void TheResidentialCheckoutDoesNotDependOnCommercialInvoicing()
        {
            var root = BackendRoot();

            var residentialFiles = new[]
            {
                Path.Combine(root, "Services", "StripeService.cs"),
                Path.Combine(root, "Services", "BookingCreationService.cs"),
                Path.Combine(root, "Services", "OrderPricingCalculator.cs"),
                Path.Combine(root, "Services", "OrderService.cs"),
                Path.Combine(root, "Controllers", "BookingController.cs"),
                Path.Combine(root, "Controllers", "StripeWebhookController.cs"),
                Path.Combine(root, "Controllers", "OrderController.cs")
            };

            var offenders = residentialFiles
                .Where(File.Exists)
                .Where(f => StripComments(File.ReadAllText(f))
                    .Contains("DreamCleaningBackend.Services.Commercial")
                    || StripComments(File.ReadAllText(f))
                        .Contains("DreamCleaningBackend.Models.Commercial"))
                .Select(Path.GetFileName)
                .ToList();

            Assert.True(offenders.Count == 0,
                "The residential checkout now depends on commercial invoicing, which couples two " +
                "workflows that must stay separate: " + string.Join(", ", offenders!));
        }

        /// <summary>
        /// The commercial invoice entity must not have grown residential payment columns. A
        /// PaymentIntentId or ChargeId on this table would be the first step toward the two
        /// systems sharing a settlement path.
        /// </summary>
        [Fact]
        public void TheInvoiceEntityCarriesNoCardPaymentIdentifiers()
        {
            var names = typeof(DreamCleaningBackend.Models.Commercial.CommercialInvoice)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => p.Name.ToLowerInvariant())
                .ToList();

            foreach (var forbidden in new[] { "paymentintent", "stripe", "chargeid", "cardlast4" })
            {
                Assert.DoesNotContain(names, n => n.Contains(forbidden));
            }
        }

        /// <summary>
        /// Strips // and /* */ so a comment EXPLAINING the separation does not trip the guard.
        /// The comments in these files say "Stripe" a lot, on purpose.
        /// </summary>
        private static string StripComments(string source)
        {
            var withoutBlock = Regex.Replace(source, @"/\*.*?\*/", "", RegexOptions.Singleline);
            var withoutLine = Regex.Replace(withoutBlock, @"//.*?$", "", RegexOptions.Multiline);
            return withoutLine;
        }
    }
}
