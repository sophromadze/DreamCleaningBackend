using System.Reflection;
using DreamCleaningBackend.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Routing;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// WHO MAY EDIT THE PRICE CATALOGUE (2026-09).
    ///
    /// The admin panel's "Services" tab — service types, services, included-amount thresholds,
    /// rate tiers, extra services and the pricing-configuration import/export — is SuperAdmin
    /// only. It is not one order's worth of damage: those rows are what every quote on the site
    /// is built from, so a mistake there re-prices the whole business.
    ///
    /// Hiding the tab is not the control; these attributes are. And they sit on each ACTION
    /// rather than on AdminCatalogController, because the rest of that controller (subscriptions
    /// and promo codes) is the Discounts tab, which regular Admins keep — so the guard has to be
    /// able to tell the two halves apart, which is exactly what this test does.
    ///
    /// The GETs are gated as well as the writes: nothing outside the tab reads them. Every other
    /// surface (booking, order edit, the admin orders panel, recreate-order) goes through the
    /// public api/booking/service-types.
    /// </summary>
    public class ServiceCatalogueAccessTests
    {
        // Route prefixes under api/admin that belong to the Services tab.
        private static readonly string[] CataloguePrefixes =
        {
            "service-types",
            "services",
            "extra-services",
            "pricing-configuration"
        };

        // ...and the ones in the same controller that must stay open to Admins.
        private static readonly string[] DiscountPrefixes =
        {
            "subscriptions",
            "promo-codes"
        };

        [Fact]
        public void EveryServiceCatalogueEndpointIsSuperAdminOnly()
        {
            var missing = Actions()
                .Where(a => Matches(a.Route, CataloguePrefixes))
                .Where(a => !IsSuperAdminOnly(a.Method))
                .Select(a => $"{a.Method.Name} ({a.Route})")
                .ToList();

            Assert.True(
                missing.Count == 0,
                "These Services-tab endpoints are missing [Authorize(Roles = \"SuperAdmin\")], so a " +
                "regular Admin can still edit the price catalogue the whole site quotes from: " +
                string.Join(", ", missing));
        }

        [Fact]
        public void TheDiscountsHalfOfTheSameControllerStaysOpenToAdmins()
        {
            // Guards the other direction: promoting the attribute to the controller (or pasting it
            // onto the wrong action) would silently lock Admins out of promo codes and plans.
            var overReach = Actions()
                .Where(a => Matches(a.Route, DiscountPrefixes))
                .Where(a => IsSuperAdminOnly(a.Method))
                .Select(a => $"{a.Method.Name} ({a.Route})")
                .ToList();

            Assert.True(
                overReach.Count == 0,
                "These Discounts-tab endpoints became SuperAdmin-only, which locks regular Admins " +
                "out of promo codes and subscription plans: " + string.Join(", ", overReach));
        }

        [Fact]
        public void TheCatalogueEndpointsAreActuallyFound()
        {
            // A renamed route or a moved controller would make both tests above pass vacuously.
            var found = Actions().Count(a => Matches(a.Route, CataloguePrefixes));
            Assert.True(found >= 25, $"Expected the Services-tab endpoints to be found, saw {found}.");
        }

        private static IEnumerable<(MethodInfo Method, string Route)> Actions()
        {
            var methods = typeof(AdminCatalogController).GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

            foreach (var method in methods)
            {
                foreach (var attribute in method.GetCustomAttributes<HttpMethodAttribute>())
                {
                    yield return (method, attribute.Template ?? "");
                }
            }
        }

        private static bool Matches(string route, string[] prefixes) =>
            prefixes.Any(p => route == p || route.StartsWith(p + "/", StringComparison.Ordinal));

        private static bool IsSuperAdminOnly(MethodInfo method) =>
            method.GetCustomAttributes<AuthorizeAttribute>()
                .Any(a => a.Roles == "SuperAdmin");
    }
}
