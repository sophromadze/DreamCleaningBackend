using System.Reflection;
using DreamCleaningBackend.Controllers;
using DreamCleaningBackend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Routing;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// WHO MAY SET THE STANDING DISCOUNTS AND THE POINTS ECONOMY (2026-09).
    ///
    /// Two admin surfaces moved to SuperAdmin-only for the same reason the Services tab did: the
    /// values on them are not one order's worth of damage.
    ///
    ///  - Discounts -> Loyalty's settings panel decides who is handed a standing percentage off
    ///    and how large it is, for every customer who stops booking.
    ///  - The Rewards tab (Bubble Rewards, moved into the admin panel out of the header dropdown)
    ///    sets the points economy every customer earns and spends against.
    ///
    /// Hiding the panel and the tab is the convenience; these attributes are the control. Both
    /// READS are gated as well as the writes, because nothing outside those two surfaces loads
    /// them.
    ///
    /// The tests below also guard the other direction. Both controllers carry endpoints that
    /// regular Admins do their daily work through -- one customer's loyalty percentage, one
    /// customer's points and referrals from the Users tab -- and promoting either attribute to
    /// the controller would take those away without anybody noticing until an Admin hit a 403.
    /// </summary>
    public class RewardsAndLoyaltySettingsAccessTests
    {
        [Fact]
        public void TheLoyaltySettingsEndpointsAreSuperAdminOnly()
        {
            var missing = Actions(typeof(AdminUsersController))
                .Where(a => a.Route == "loyalty-discount-settings")
                .Where(a => !IsSuperAdminOnly(a.Method))
                .Select(a => a.Method.Name)
                .ToList();

            Assert.True(
                missing.Count == 0,
                "These loyalty-settings endpoints are missing [Authorize(Roles = \"SuperAdmin\")], so a " +
                "regular Admin can still change the standing discount every returning customer gets: " +
                string.Join(", ", missing));

            // A renamed route would make the assertion above pass vacuously.
            Assert.Equal(2, Actions(typeof(AdminUsersController)).Count(a => a.Route == "loyalty-discount-settings"));
        }

        [Fact]
        public void OneCustomersLoyaltyDiscountStaysOpenToAdmins()
        {
            // The per-user endpoints are the Users tab's work: read and set THIS customer's
            // percentage. They are permission-gated (View / Update), not role-gated, and must
            // stay that way -- the settings are the company-wide policy, these are one account.
            var overReach = Actions(typeof(AdminUsersController))
                .Where(a => a.Route.Contains("loyalty-discount", StringComparison.Ordinal))
                .Where(a => a.Route != "loyalty-discount-settings")
                .Where(a => IsSuperAdminOnly(a.Method))
                .Select(a => $"{a.Method.Name} ({a.Route})")
                .ToList();

            Assert.True(
                overReach.Count == 0,
                "These per-customer loyalty endpoints became SuperAdmin-only, which locks regular " +
                "Admins out of the Users tab's loyalty controls: " + string.Join(", ", overReach));
        }

        [Fact]
        public void TheRewardsTabsOwnEndpointsAreSuperAdminOnly()
        {
            // Exactly what the Rewards tab loads: the settings list and the stats panel. Every
            // write on that controller was already SuperAdmin-only; these two reads were the gap.
            string[] tabRoutes = { "settings", "stats" };

            var missing = Actions(typeof(AdminRewardsController))
                .Where(a => tabRoutes.Contains(a.Route))
                .Where(a => !IsSuperAdminOnly(a.Method))
                .Select(a => $"{a.Method.Name} ({a.Route})")
                .ToList();

            Assert.True(
                missing.Count == 0,
                "These Rewards-tab endpoints are missing [Authorize(Roles = \"SuperAdmin\")], so a " +
                "regular Admin can still read the points economy the tab is hidden from them: " +
                string.Join(", ", missing));

            Assert.Equal(2, Actions(typeof(AdminRewardsController)).Count(a => tabRoutes.Contains(a.Route)));
        }

        [Fact]
        public void ThePerCustomerRewardEndpointsStayOpenToAdmins()
        {
            // These are called from the USERS tab, not from the Rewards tab: one customer's
            // points summary, their referral list, and the two grants an Admin hands out while a
            // customer is on the phone. Gating the controller instead of the two reads above
            // would break all four.
            string[] usersTabRoutes =
            {
                "users/{userId}/summary",
                "users/{userId}/grant-review-bonus",
                "users/{userId}/grant-credit",
                "referrals"
            };

            var overReach = Actions(typeof(AdminRewardsController))
                .Where(a => usersTabRoutes.Contains(a.Route))
                .Where(a => IsSuperAdminOnly(a.Method))
                .Select(a => $"{a.Method.Name} ({a.Route})")
                .ToList();

            Assert.True(
                overReach.Count == 0,
                "These per-customer reward endpoints became SuperAdmin-only, which breaks the Users " +
                "tab for regular Admins: " + string.Join(", ", overReach));

            Assert.Equal(
                usersTabRoutes.Length,
                Actions(typeof(AdminRewardsController)).Count(a => usersTabRoutes.Contains(a.Route)));
        }

        [Fact]
        public void BubbleRewardsIsNoLongerAGrantablePageView()
        {
            // The page-view grants unlock read-only access to a restricted page. Bubble Rewards is
            // not a page any more and not readable by a regular Admin at all, so offering the key
            // would let a SuperAdmin hand out a grant that does nothing. Parse() drops the stale
            // key from grant lists still carrying it, which is why no migration was needed.
            Assert.DoesNotContain("bubble-rewards", AdminViewablePages.All);
            Assert.False(AdminViewablePages.IsValid("bubble-rewards"));
            Assert.Empty(AdminViewablePages.Parse("[\"bubble-rewards\"]"));
            Assert.Equal(new[] { "statistics" }, AdminViewablePages.Parse("[\"statistics\",\"bubble-rewards\"]"));
        }

        private static IEnumerable<(MethodInfo Method, string Route)> Actions(Type controller)
        {
            var methods = controller.GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

            foreach (var method in methods)
            {
                foreach (var attribute in method.GetCustomAttributes<HttpMethodAttribute>())
                {
                    yield return (method, attribute.Template ?? "");
                }
            }
        }

        private static bool IsSuperAdminOnly(MethodInfo method) =>
            method.GetCustomAttributes<AuthorizeAttribute>()
                .Any(a => a.Roles == "SuperAdmin");
    }
}
