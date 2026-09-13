using System.Text.RegularExpressions;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// A COMMERCIAL CLEANING BILLED ON AN INVOICE IS NOT A RESIDENTIAL BOOKING.
    ///
    /// The defect these tests lock down: an admin created an order for a business client with
    /// Payment Method = Invoice, and the customer received "Thank you for choosing Dream Cleaning!
    /// Your booking has been confirmed." plus the matching SMS. Those templates belong to the
    /// residential flow — the customer books on the website, pays through Stripe, and is chased
    /// with a residential payment link. A commercial client is billed, chased and receipted from
    /// the commercial INVOICE, and the residential mail names none of the references their
    /// accounts payable work from.
    ///
    /// The rule is <see cref="ResidentialBookingCommunicationPolicy"/>, and the tests come in two
    /// halves on purpose:
    ///
    ///  • <b>Behavioural</b> — the predicate itself, over every method / customer combination,
    ///    including the edge case that actually matters: a business customer's ORDINARY
    ///    residential card order keeps every message it has always had. The signal is the ORDER,
    ///    never the customer.
    ///  • <b>Source-level</b> — that each creation path routes through the shared rule rather than
    ///    re-deciding it, and that the things this must NOT touch (commercial invoice email,
    ///    cleaner assignment notifications, recurring generation) are still exactly where they
    ///    were. What is being prevented is a future path quietly growing its own answer, and that
    ///    is caught by reading what a file reaches for.
    /// </summary>
    public class ResidentialBookingCommunicationTests
    {
        private static string BackendRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "DreamCleaningBackend")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return Path.Combine(dir!.FullName, "DreamCleaningBackend");
        }

        private static string ReadSource(params string[] relativeParts)
        {
            var path = Path.Combine(new[] { BackendRoot() }.Concat(relativeParts).ToArray());
            Assert.True(File.Exists(path), $"Expected source file not found: {path}");
            return File.ReadAllText(path);
        }

        /// <summary>An ordinary private customer's order.</summary>
        private static Order ResidentialOrder(PaymentMethod method) => new()
        {
            Id = 101,
            UserId = 7,
            PaymentMethod = method,
            ContractClientId = null,
            ContactEmail = "customer@example.com",
            ContactPhone = "+19295551234"
        };

        /// <summary>A commercial order billed to a ContractClient through an invoice.</summary>
        private static Order CommercialInvoiceOrder(int? contractClientId = 42, int? userId = 7) => new()
        {
            Id = 202,
            UserId = userId ?? 0,
            PaymentMethod = PaymentMethod.Invoice,
            ContractClientId = contractClientId,
            ContactEmail = "ap@acme-corp.example",
            ContactPhone = "+19295559876"
        };

        // ── 1 / 2 / 6: residential communication is untouched ────────────────────────────────

        [Fact]
        public void ResidentialCardOrder_StillSendsTheBookingConfirmation()
        {
            var order = ResidentialOrder(PaymentMethod.Normal);

            Assert.True(ResidentialBookingCommunicationPolicy.ShouldSendResidentialBookingCommunication(order));
            Assert.False(ResidentialBookingCommunicationPolicy.IsCommercialInvoiceBacked(order));
            Assert.Null(ResidentialBookingCommunicationPolicy.SuppressionReason(order));
        }

        [Theory]
        [InlineData(PaymentMethod.Cash)]
        [InlineData(PaymentMethod.Zelle)]
        [InlineData(PaymentMethod.Check)]
        [InlineData(PaymentMethod.Other)]
        public void EveryOtherOutsideStripeMethod_KeepsItsExistingCommunication(PaymentMethod method)
        {
            // Cash / Zelle / Check / Other are money that has ALREADY arrived from a private
            // customer. Nothing about this change may alter what they receive — the manual-payment
            // branch of the admin create path exists precisely to send them a real confirmation.
            var order = ResidentialOrder(method);

            Assert.True(ResidentialBookingCommunicationPolicy.ShouldSendResidentialBookingCommunication(order));
            Assert.Null(ResidentialBookingCommunicationPolicy.SuppressionReason(order));
        }

        [Fact]
        public void ABusinessCustomersOrdinaryCardOrder_IsStillAResidentialBooking()
        {
            // THE EDGE CASE THE WHOLE DESIGN TURNS ON. A commercial client books their own
            // apartment, or a one-off site the contract does not cover, and pays by card. That is
            // an ordinary residential booking and must behave like one. Suppressing on "this
            // customer is a business" would have muted it — which is why the predicate reads the
            // ORDER's payment method and never the account.
            var order = new Order
            {
                Id = 303,
                UserId = 7,
                PaymentMethod = PaymentMethod.Normal,
                // Linked to a commercial client, and STILL residential: this order is not billed
                // on an invoice.
                ContractClientId = 42,
                ContactEmail = "owner@acme-corp.example"
            };

            Assert.True(ResidentialBookingCommunicationPolicy.ShouldSendResidentialBookingCommunication(order));
        }

        // ── 3 / 4 / 5: commercial invoice-backed orders are suppressed ───────────────────────

        [Fact]
        public void BusinessLinkedInvoiceOrder_SendsNoResidentialBookingEmailOrSms()
        {
            var order = CommercialInvoiceOrder();

            Assert.True(ResidentialBookingCommunicationPolicy.IsCommercialInvoiceBacked(order));
            Assert.False(ResidentialBookingCommunicationPolicy.ShouldSendResidentialBookingCommunication(order));

            // Email and SMS are ONE decision, not two — they are the same message on two channels,
            // so there is deliberately no way to suppress one and keep the other.
            var reason = ResidentialBookingCommunicationPolicy.SuppressionReason(order);
            Assert.NotNull(reason);
            Assert.Contains("commercial invoice", reason!, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void StandaloneContractClientWithNoWebsiteAccount_IsAlsoSuppressed()
        {
            // Plenty of commercial clients have no website login at all. The order still carries
            // the ContractClient link and the Invoice method, which is the whole signal.
            var order = CommercialInvoiceOrder(contractClientId: 88, userId: 0);

            Assert.False(ResidentialBookingCommunicationPolicy.ShouldSendResidentialBookingCommunication(order));
        }

        [Fact]
        public void AnInvoiceOrderWithNoClientRecorded_IsStillNotAResidentialBooking()
        {
            // Both write paths REQUIRE a client on an Invoice order, so this should not exist.
            // If it ever does, it is an order nobody is charging through Stripe — mailing it the
            // residential "your booking is confirmed" template would be no less wrong.
            var order = CommercialInvoiceOrder(contractClientId: null);

            Assert.False(ResidentialBookingCommunicationPolicy.ShouldSendResidentialBookingCommunication(order));
        }

        [Fact]
        public void ThePredicateNeverLooksAtTheCustomerRecord()
        {
            // Guards the design, not a behaviour: an email domain, a role string or User.IsBusiness
            // would each mute a business customer's legitimate residential bookings.
            var source = ReadSource("Helpers", "ResidentialBookingCommunicationPolicy.cs");
            var code = Regex.Replace(source, @"//.*?$|/\*.*?\*/", "", RegexOptions.Multiline | RegexOptions.Singleline);

            Assert.DoesNotContain("IsBusiness", code);
            Assert.DoesNotContain("UserRole", code);
            Assert.DoesNotContain("ContactEmail", code);
            Assert.DoesNotContain("EndsWith", code);
        }

        // ── The creation paths route through the shared rule ─────────────────────────────────

        [Fact]
        public void AdminCreateOrderPath_GatesItsCustomerSendsOnTheSharedRule()
        {
            // The exact path that produced the defect: create-for-user's manual-payment branch,
            // entered by every method that is not Stripe — Invoice included.
            var source = ReadSource("Controllers", "BookingController.cs");

            var collapsed = Regex.Replace(source, @"\s+", " ");
            Assert.Contains(
                "ResidentialBookingCommunicationPolicy.ShouldSendResidentialBookingCommunication( paymentMethod, dto.ContractClientId)",
                collapsed);

            // Suppressed before the send, never retracted after.
            var gateIndex = source.IndexOf("residentialCommunicationAllowed", StringComparison.Ordinal);
            var firstConfirmationSend = source.IndexOf("SendCustomerBookingConfirmationAsync", StringComparison.Ordinal);
            Assert.True(gateIndex > 0 && firstConfirmationSend > gateIndex,
                "The eligibility gate must be decided before the first booking-confirmation send.");
        }

        [Fact]
        public void ConfirmPaymentPath_GatesItsCustomerSendsOnTheSharedRule()
        {
            var source = ReadSource("Controllers", "BookingController.cs");

            Assert.Contains(
                "ResidentialBookingCommunicationPolicy.ShouldSendResidentialBookingCommunication(order)",
                source);
            Assert.Contains("if (sendResidentialConfirmation && !isAppleHiddenMail", source);
            Assert.Contains("if (sendResidentialConfirmation && !string.IsNullOrWhiteSpace(contactPhone))", source);
        }

        [Fact]
        public void AdminConfirmationResendPath_GatesOnTheSharedRuleAndSaysWhy()
        {
            // One private helper serves both the after-a-saved-card-charge send and the admin's
            // manual "Send Updated Confirmation", so gating it covers both. It REPORTS the skip
            // rather than silently doing nothing — an admin told the wrong cause goes hunting for
            // a setting that was never the problem (the NoEmailHelper rule).
            var source = ReadSource("Controllers", "Admin", "AdminOrdersController.cs");

            Assert.Contains(
                "ResidentialBookingCommunicationPolicy.ShouldSendResidentialBookingCommunication(order)",
                source);
            Assert.Contains("ResidentialBookingCommunicationPolicy.SuppressionReason(order)", source);
        }

        [Fact]
        public void EverySendCustomerBookingConfirmationCallSiteIsCovered()
        {
            // The guard against a FOURTH call site appearing without the rule. If this fails,
            // either gate the new site or add it here deliberately.
            var callSites = new[]
            {
                Path.Combine("Controllers", "BookingController.cs"),
                Path.Combine("Controllers", "Admin", "AdminOrdersController.cs")
            };

            var root = BackendRoot();
            var found = Directory
                .EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                            && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                .Where(f => File.ReadAllText(f).Contains("_emailService.SendCustomerBookingConfirmationAsync")
                            || File.ReadAllText(f).Contains("emailService.SendCustomerBookingConfirmationAsync"))
                .Select(f => f.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar))
                .OrderBy(f => f)
                .ToList();

            Assert.Equal(callSites.OrderBy(f => f).ToList(), found);

            foreach (var file in found)
                Assert.Contains("ResidentialBookingCommunicationPolicy", File.ReadAllText(Path.Combine(root, file)));
        }

        // ── 7: recurring generation ──────────────────────────────────────────────────────────

        [Fact]
        public void RecurringGeneration_DoesNotSendAnyCustomerBookingConfirmation()
        {
            // Generation is bookkeeping: it mails nobody, so a generated commercial occurrence
            // cannot confirm itself to the client. Asserted rather than assumed, because the
            // obvious "helpful" change to this service would be to start confirming occurrences.
            foreach (var file in new[] { "RecurringOrderSeriesService.cs", "RecurringOrderGenerationService.cs" })
            {
                var source = ReadSource("Services", file);
                Assert.DoesNotContain("SendCustomerBookingConfirmationAsync", source);
                Assert.DoesNotContain("SendBookingConfirmationSmsAsync", source);
            }
        }

        [Fact]
        public void RecurringPaymentRequests_SkipEveryOrderNotSettledThroughStripe()
        {
            // The one message the recurring worker can emit is the residential payment link, and
            // it is already limited to Stripe orders — which excludes Invoice. A commercial
            // occurrence is chased through its invoice.
            var source = ReadSource("Services", "RecurringOrderGenerationService.cs");
            Assert.Contains("if (order.PaymentMethod != PaymentMethod.Normal) continue;", source);
        }

        [Fact]
        public void AGeneratedCommercialOccurrence_AnswersThePolicyTheSameWayItsTemplateDoes()
        {
            // The occurrence copies the METHOD and the client, so it must give the same answer as
            // the order it was generated from — no per-occurrence exception.
            var template = CommercialInvoiceOrder();
            var occurrence = new Order
            {
                Id = 404,
                UserId = template.UserId,
                PaymentMethod = template.PaymentMethod,
                ContractClientId = template.ContractClientId,
                IsGeneratedByRecurringSeries = true,
                RecurringSeriesId = 9
            };

            Assert.False(ResidentialBookingCommunicationPolicy.ShouldSendResidentialBookingCommunication(occurrence));
            Assert.Equal(
                ResidentialBookingCommunicationPolicy.ShouldSendResidentialBookingCommunication(template),
                ResidentialBookingCommunicationPolicy.ShouldSendResidentialBookingCommunication(occurrence));
        }

        // ── 8 / 9: what must stay untouched ──────────────────────────────────────────────────

        [Fact]
        public void CommercialInvoiceEmailIsUnaffected()
        {
            // Sending / reminding / receipting an invoice is the commercial flow doing its own job.
            // It must know nothing about this residential rule.
            var source = ReadSource("Services", "Commercial", "InvoiceEmailService.cs");

            Assert.Contains("SendInvoiceAsync", source);
            Assert.Contains("SendReminderAsync", source);
            Assert.Contains("SendPaymentReceiptAsync", source);
            Assert.DoesNotContain("ResidentialBookingCommunicationPolicy", source);
        }

        [Fact]
        public void CleanerNotificationsAreUnaffected()
        {
            // A different audience entirely. The cleaner still has to be told where to go and when,
            // whoever is being billed and however. No cleaner-facing path may consult this rule.
            var root = BackendRoot();
            var cleanerFiles = new[]
            {
                Path.Combine(root, "Services", "CleanerService.cs"),
                Path.Combine(root, "Services", "CleanerNotificationService.cs"),
                Path.Combine(root, "Services", "CleanerPortalService.cs"),
                Path.Combine(root, "Helpers", "CleanerJobView.cs")
            };

            foreach (var file in cleanerFiles.Where(File.Exists))
                Assert.DoesNotContain("ResidentialBookingCommunicationPolicy", File.ReadAllText(file));

            // And the Send / Resend assignment controls still exist on their own terms.
            Assert.Contains(
                "SendCleanerAssignmentNotificationAsync",
                ReadSource("Services", "Interfaces", "IEmailService.cs"));
        }

        [Fact]
        public void AdminAndCompanyNotificationsAreUnaffected()
        {
            // Internal notification of a new order always runs: the order genuinely exists and
            // staff must see it, commercial or not.
            var source = ReadSource("Controllers", "BookingController.cs");
            var gateIndex = source.IndexOf("residentialCommunicationAllowed", StringComparison.Ordinal);
            var adminNotify = source.IndexOf("NotifyAdminsNewOrder", StringComparison.Ordinal);

            Assert.True(adminNotify > 0);
            Assert.True(adminNotify < gateIndex,
                "Admin notification must stay outside (and ahead of) the customer-communication gate.");
            Assert.Contains("SendCompanyBookingNotificationAsync", source);
        }
    }
}
