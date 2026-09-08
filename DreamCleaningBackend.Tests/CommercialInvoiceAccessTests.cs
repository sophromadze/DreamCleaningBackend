using System.Reflection;
using DreamCleaningBackend.Attributes;
using DreamCleaningBackend.Controllers;
using DreamCleaningBackend.Controllers.Admin;
using DreamCleaningBackend.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// WHO MAY DO WHAT WITH A COMMERCIAL INVOICE.
    ///
    /// Invoicing follows the app's ORDINARY role hierarchy, not the Contracts module's OrgTitle
    /// matrix: billing a commercial client is day-to-day work, and the people who arrange the
    /// cleaning are the people who invoice for it. Admin and SuperAdmin, gated by
    /// [RequirePermission] the same way Orders and Payroll are.
    ///
    /// Two things sit a level above, and both are marked per action rather than on the controller:
    ///   - VOIDING an invoice, a permanent financial record that a number was issued and cancelled.
    ///   - Editing the BANKING settings, which redirects every payment the business receives.
    ///
    /// Moderators hold Permission.View elsewhere and are kept out of the whole area by the
    /// controller-level role attribute, so widening a permission later cannot hand them billing.
    /// </summary>
    public class CommercialInvoiceAccessTests
    {
        // ── The admin invoices controller ─────────────────────────────────────────────────────

        [Fact]
        public void InvoicesController_IsAdminAndSuperAdminOnly()
        {
            var roles = typeof(AdminCommercialInvoicesController)
                .GetCustomAttributes<AuthorizeAttribute>()
                .Select(a => a.Roles)
                .ToList();

            Assert.Contains("Admin,SuperAdmin", roles);
        }

        /// <summary>
        /// A Moderator holds Permission.View across the app, so the controller-level role
        /// attribute is what keeps them out of billing entirely.
        /// </summary>
        [Fact]
        public void InvoicesController_DoesNotAdmitModerators()
        {
            var roles = typeof(AdminCommercialInvoicesController)
                .GetCustomAttributes<AuthorizeAttribute>()
                .Select(a => a.Roles ?? "")
                .ToList();

            Assert.All(roles, r => Assert.DoesNotContain("Moderator", r));
        }

        /// <summary>
        /// VOIDING IS SUPERADMIN-ONLY, and marked on the action. Promoting the attribute to the
        /// controller would lock Admins out of ordinary invoicing, which is the failure this shape
        /// exists to avoid — the same reasoning as the Services tab next door.
        /// </summary>
        [Fact]
        public void VoidingAnInvoiceIsSuperAdminOnly()
        {
            var method = typeof(AdminCommercialInvoicesController)
                .GetMethod(nameof(AdminCommercialInvoicesController.Void));

            Assert.NotNull(method);
            Assert.True(IsSuperAdminOnly(method!),
                "Voiding an invoice must carry [Authorize(Roles = \"SuperAdmin\")]: it permanently " +
                "reserves the number and makes the invoice uncollectable.");
        }

        /// <summary>
        /// The other direction: everyday invoicing must NOT have become SuperAdmin-only, or the
        /// admins who actually bill clients cannot work.
        /// </summary>
        [Fact]
        public void EverydayInvoicingStaysOpenToAdmins()
        {
            var everyday = new[]
            {
                nameof(AdminCommercialInvoicesController.List),
                nameof(AdminCommercialInvoicesController.Get),
                nameof(AdminCommercialInvoicesController.Create),
                nameof(AdminCommercialInvoicesController.Update),
                nameof(AdminCommercialInvoicesController.Send),
                nameof(AdminCommercialInvoicesController.RecordPayment),
                nameof(AdminCommercialInvoicesController.Duplicate),
                nameof(AdminCommercialInvoicesController.DownloadPdf),
                nameof(AdminCommercialInvoicesController.SendReminder)
            };

            var overReach = everyday
                .Select(name => typeof(AdminCommercialInvoicesController).GetMethod(name))
                .Where(m => m != null && IsSuperAdminOnly(m!))
                .Select(m => m!.Name)
                .ToList();

            Assert.True(overReach.Count == 0,
                "These everyday invoicing endpoints became SuperAdmin-only, locking out the admins " +
                "who bill clients: " + string.Join(", ", overReach));
        }

        /// <summary>
        /// Every endpoint carries a permission, not just a role. A missing [RequirePermission]
        /// would let any Admin-role account act regardless of what the permission table says.
        /// </summary>
        [Fact]
        public void EveryInvoiceEndpointIsPermissionGatedOrExplicitlySuperAdmin()
        {
            var ungated = Actions(typeof(AdminCommercialInvoicesController))
                .Where(a => !HasRequirePermission(a.Method) && !IsSuperAdminOnly(a.Method))
                .Select(a => $"{a.Method.Name} ({a.Route})")
                .ToList();

            Assert.True(ungated.Count == 0,
                "These invoice endpoints carry neither [RequirePermission] nor a SuperAdmin role " +
                "gate: " + string.Join(", ", ungated));
        }

        /// <summary>Writes must require Create or Update, never merely View.</summary>
        [Fact]
        public void WritingEndpointsRequireMoreThanView()
        {
            var writeMethods = new[]
            {
                (nameof(AdminCommercialInvoicesController.Create), Permission.Create),
                (nameof(AdminCommercialInvoicesController.Update), Permission.Update),
                (nameof(AdminCommercialInvoicesController.RecordPayment), Permission.Update),
                (nameof(AdminCommercialInvoicesController.Send), Permission.Update),
                (nameof(AdminCommercialInvoicesController.Duplicate), Permission.Create),
                (nameof(AdminCommercialInvoicesController.Delete), Permission.Delete)
            };

            foreach (var (name, expected) in writeMethods)
            {
                var method = typeof(AdminCommercialInvoicesController).GetMethod(name);
                Assert.NotNull(method);

                var permissions = method!.GetCustomAttributes<RequirePermissionAttribute>().ToList();
                Assert.True(permissions.Count > 0, $"{name} has no [RequirePermission].");
                Assert.DoesNotContain(permissions, p => PermissionOf(p) == Permission.View);
                Assert.Contains(permissions, p => PermissionOf(p) == expected);
            }
        }

        // ── Billing settings ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// THE READ AND THE WRITE ARE GATED DIFFERENTLY, and both halves matter.
        ///
        /// An Admin sending an invoice needs to see the payment instructions their client is
        /// about to receive, so the GET is open to them (masked). Only a SuperAdmin may CHANGE the
        /// destination account, because that silently redirects every future payment.
        /// </summary>
        [Fact]
        public void BillingSettings_ReadIsAdminAndWriteIsSuperAdminOnly()
        {
            var read = typeof(AdminBillingSettingsController)
                .GetMethod(nameof(AdminBillingSettingsController.Get));
            var write = typeof(AdminBillingSettingsController)
                .GetMethod(nameof(AdminBillingSettingsController.Update));

            Assert.NotNull(read);
            Assert.NotNull(write);

            Assert.False(IsSuperAdminOnly(read!),
                "Reading billing settings must stay open to Admins — they need to see what the " +
                "client receives.");

            Assert.True(IsSuperAdminOnly(write!),
                "Changing bank details must be SuperAdmin-only: it redirects every payment the " +
                "business receives.");
        }

        [Fact]
        public void BillingSettingsController_IsAdminAndSuperAdminOnly()
        {
            var roles = typeof(AdminBillingSettingsController)
                .GetCustomAttributes<AuthorizeAttribute>()
                .Select(a => a.Roles)
                .ToList();

            Assert.Contains("Admin,SuperAdmin", roles);
        }

        // ── The public controller ─────────────────────────────────────────────────────────────

        /// <summary>
        /// THE PUBLIC INVOICE PAGE IS ANONYMOUS AND MUST STAY THAT WAY. A commercial client has no
        /// account here, so the opaque token is the whole authorization; an auth guard would lock
        /// out exactly the people the endpoints exist for.
        /// </summary>
        [Fact]
        public void PublicInvoiceController_IsAnonymous()
        {
            var anonymous = typeof(PublicInvoiceController)
                .GetCustomAttributes<AllowAnonymousAttribute>()
                .Any();

            Assert.True(anonymous, "The public invoice controller must be [AllowAnonymous].");
        }

        /// <summary>
        /// THE PUBLIC SURFACE HAS EXACTLY ONE WRITE, AND IT MOVES NO MONEY.
        ///
        /// This test previously asserted the controller had NO non-GET endpoint at all, which was
        /// right while invoicing was manual-ACH only. Adding online payment needed one POST, and
        /// the guard caught it — which is the point. It is narrowed rather than deleted, because
        /// "the token cannot change an invoice" is still the rule worth protecting.
        ///
        /// What StartCheckout may do is deliberately tiny: create a Stripe Checkout Session for an
        /// amount IT derives from the invoice. It cannot alter a status, record a payment, change
        /// an amount, or void anything — settlement happens only through a signature-verified
        /// webhook, never through a request the customer's browser can make.
        /// </summary>
        [Fact]
        public void PublicInvoiceController_ExposesOnlyTheCheckoutWrite()
        {
            var writes = Actions(typeof(PublicInvoiceController))
                .Where(a => a.Verb is not "GET")
                .Select(a => a.Method.Name)
                .ToList();

            Assert.True(
                writes.Count == 1 && writes[0] == nameof(PublicInvoiceController.StartCheckout),
                "The public invoice controller must expose exactly one write — StartCheckout. The "
                + "token authorizes starting a payment, never changing an invoice. Found: "
                + (writes.Count == 0 ? "none" : string.Join(", ", writes)));
        }

        /// <summary>
        /// The public controller must not gain a way to record or confirm a payment. Money is only
        /// ever recorded from a signature-verified Stripe webhook or by an authenticated admin;
        /// an endpoint here that did it would take a customer's word for it.
        /// </summary>
        [Fact]
        public void PublicInvoiceController_CannotRecordOrConfirmPayments()
        {
            var names = typeof(PublicInvoiceController)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Select(m => m.Name.ToLowerInvariant())
                .ToList();

            foreach (var forbidden in new[]
                     { "recordpayment", "confirm", "markpaid", "settle", "void", "refund" })
            {
                Assert.DoesNotContain(names, n => n.Contains(forbidden));
            }
        }

        /// <summary>
        /// The row id must never be a route parameter here. Ids are sequential and enumerable, so
        /// /invoice/41 would let anyone walk the table; only the high-entropy token resolves one.
        /// </summary>
        [Fact]
        public void PublicInvoiceRoutes_AreKeyedByTokenNeverById()
        {
            foreach (var action in Actions(typeof(PublicInvoiceController)))
            {
                Assert.Contains("{token}", action.Route);
                Assert.DoesNotContain("{id", action.Route);
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────────────────────

        private static IEnumerable<(MethodInfo Method, string Route, string Verb)> Actions(Type controller)
        {
            var methods = controller.GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

            foreach (var method in methods)
            {
                foreach (var attribute in method.GetCustomAttributes<HttpMethodAttribute>())
                {
                    yield return (method, attribute.Template ?? "", attribute.HttpMethods.First());
                }
            }
        }

        private static bool IsSuperAdminOnly(MethodInfo method) =>
            method.GetCustomAttributes<AuthorizeAttribute>().Any(a => a.Roles == "SuperAdmin");

        private static bool HasRequirePermission(MethodInfo method) =>
            method.GetCustomAttributes<RequirePermissionAttribute>().Any();

        /// <summary>
        /// The attribute stores its permission in a private field, so it is read reflectively -
        /// exposing a public property purely for a test would widen the type's surface.
        /// </summary>
        private static Permission PermissionOf(RequirePermissionAttribute attribute)
        {
            var field = typeof(RequirePermissionAttribute)
                .GetField("_permission", BindingFlags.Instance | BindingFlags.NonPublic);
            return (Permission)field!.GetValue(attribute)!;
        }
    }
}
