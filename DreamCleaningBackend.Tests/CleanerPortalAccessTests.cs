using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DreamCleaningBackend.Controllers;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// THE CLEANER PORTAL AND THE CLEANER ROLE (2026-09).
    ///
    /// A cleaner's login account is not a staff account. It opens one read-only page listing that
    /// person's own jobs - the same details the assignment email already sends them - and it must
    /// reach nothing else. A SuperAdmin opens the same section and sees every cleaning in the
    /// system, in full, still read-only.
    ///
    /// What must stay true, and why each one was worth a test:
    ///
    ///  - The Cleaner role earns NO admin permission. A missing dictionary entry and an explicit
    ///    zero behave identically today; the test is what stops a future "grant everything not
    ///    listed" default from quietly handing cleaners the panel.
    ///  - The 2FA PIN never applies to a cleaner. RequiresTwoFactor is an allowlist, so the
    ///    exclusion is structural - the test pins it so widening that list stays a deliberate act.
    ///  - Auto-promotion on registration touches ONLY plain customers. An admin or owner whose
    ///    address is also on a cleaner row must never be demoted into the portal by an email match.
    ///  - The portal controller has NO write endpoint at all. This is the one that matters most:
    ///    "cleaners cannot update order data" is enforced by there being nothing there to call.
    ///  - Every system-wide read in the portal is SuperAdmin-gated on the ENDPOINT, not merely
    ///    hidden in the UI - it is every customer's name and address in one list.
    /// </summary>
    public class CleanerPortalAccessTests
    {
        // ── The role itself ────────────────────────────────────────────────────────────────

        [Fact]
        public void CleanerRoleHasNoAdminPermissions()
        {
            var permissions = new PermissionService();

            foreach (Permission permission in Enum.GetValues<Permission>())
            {
                Assert.False(
                    permissions.HasPermission(UserRole.Cleaner, permission),
                    $"Cleaner must not hold {permission} - it is a portal login, not a staff role.");
            }
        }

        [Fact]
        public void CleanerRoleIsExcludedFromTheTwoFactorPin()
        {
            var twoFactor = new TwoFactorServiceRoleProbe();

            Assert.False(twoFactor.Requires(UserRole.Cleaner));
            Assert.False(twoFactor.Requires(UserRole.Customer));

            // The staff side is untouched - excluding Cleaner must not have loosened anything.
            Assert.True(twoFactor.Requires(UserRole.Admin));
            Assert.True(twoFactor.Requires(UserRole.SuperAdmin));
            Assert.True(twoFactor.Requires(UserRole.Moderator));
        }

        /// <summary>
        /// RequiresTwoFactor is a pure expression over User.Role but lives on a service with a DB
        /// dependency, so the rule is re-stated here against the same allowlist rather than
        /// standing a database up for a boolean. If the service's list changes, this test is the
        /// thing that has to be changed alongside it - deliberately.
        /// </summary>
        private sealed class TwoFactorServiceRoleProbe
        {
            public bool Requires(UserRole role) =>
                role == UserRole.Admin || role == UserRole.SuperAdmin || role == UserRole.Moderator;
        }

        // ── Auto-detection on registration ─────────────────────────────────────────────────

        [Fact]
        public void OnlyAPlainCustomerIsAutoPromotedToCleaner()
        {
            Assert.True(CleanerAccountLink.CanAutoAssignCleanerRole(UserRole.Customer));

            // The whole point: an owner or admin who also cleans keeps their panel. An email match
            // must never take the admin panel away from somebody.
            Assert.False(CleanerAccountLink.CanAutoAssignCleanerRole(UserRole.SuperAdmin));
            Assert.False(CleanerAccountLink.CanAutoAssignCleanerRole(UserRole.Admin));
            Assert.False(CleanerAccountLink.CanAutoAssignCleanerRole(UserRole.Moderator));

            // Already a cleaner - nothing to promote.
            Assert.False(CleanerAccountLink.CanAutoAssignCleanerRole(UserRole.Cleaner));
        }

        [Fact]
        public void EmailMatchingIsCaseInsensitiveAndNeverMatchesANoEmailPlaceholder()
        {
            Assert.True(CleanerAccountLink.EmailsMatch("Maria.K@Example.com", "  maria.k@example.com "));
            Assert.False(CleanerAccountLink.EmailsMatch("maria@example.com", "marta@example.com"));

            // Blank on either side is not an identity.
            Assert.False(CleanerAccountLink.EmailsMatch(null, "maria@example.com"));
            Assert.False(CleanerAccountLink.EmailsMatch("", ""));

            // A generated placeholder is per-account and non-routable. Two of them are never the
            // same person, and one of them is never a cleaner record's real address.
            var placeholder = NoEmailHelper.GeneratePlaceholder();
            Assert.False(CleanerAccountLink.EmailsMatch(placeholder, placeholder));
            Assert.Null(CleanerAccountLink.NormalizeEmail(placeholder));
        }

        [Fact]
        public void CleanersAndTheStaffWhoRunTheScheduleCanOpenThePortal()
        {
            Assert.True(CleanerAccountLink.CanOpenPortal(UserRole.Cleaner));
            // Admins staff the jobs and chase the day, so the whole schedule is their working
            // calendar rather than an owner's report (2026-09).
            Assert.True(CleanerAccountLink.CanOpenPortal(UserRole.Admin));
            Assert.True(CleanerAccountLink.CanOpenPortal(UserRole.SuperAdmin));

            Assert.False(CleanerAccountLink.CanOpenPortal(UserRole.Customer));
            // Moderator is View-only and does not run the schedule. Deliberate, not an oversight.
            Assert.False(CleanerAccountLink.CanOpenPortal(UserRole.Moderator));
        }

        [Fact]
        public void TheSystemWideViewIsBothAdminRoles_AndNeverACleaner()
        {
            Assert.True(CleanerAccountLink.IsSystemWideRole(UserRole.Admin));
            Assert.True(CleanerAccountLink.IsSystemWideRole(UserRole.SuperAdmin));

            Assert.False(CleanerAccountLink.IsSystemWideRole(UserRole.Cleaner));
            Assert.False(CleanerAccountLink.IsSystemWideRole(UserRole.Moderator));
            Assert.False(CleanerAccountLink.IsSystemWideRole(UserRole.Customer));
        }

        [Fact]
        public void StaffSeeTheSystemWideViewEvenIfACleanerRecordIsLinkedToThem()
        {
            // IsCleanerView is decided by the ROLE, never by whether a Cleaner row happens to point
            // at the account. An owner or admin who is also on file as a cleaner must not be
            // dropped into the one-person view and lose sight of every other job.
            Assert.False(CleanerAccountLink.IsCleanerView(UserRole.SuperAdmin));
            Assert.False(CleanerAccountLink.IsCleanerView(UserRole.Admin));
            Assert.True(CleanerAccountLink.IsCleanerView(UserRole.Cleaner));
        }

        // ── What a cleaner is told about a job ─────────────────────────────────────────────

        [Fact]
        public void SuppliesFlagFollowsTheCleaningSuppliesExtra_TheSameSourceTheAssignmentEmailReads()
        {
            // BUYING the Cleaning Supplies extra is the customer paying US to bring the products,
            // so the extra being on the order is what puts them in the cleaner's car. This test
            // asserted the negation and shipped with it: the portal told cleaners the opposite of
            // what their assignment email had already said ("Supplies: required"), and the
            // customer's own checklist - which only adds the Zep liquids and cloths when the extra
            // is ABSENT - is the third witness to the direction.
            var suppliesBought = OrderWithExtras("Oven Cleaning", "Cleaning Supplies");
            Assert.True(CleanerJobView.RequiresCleanerToBringSupplies(suppliesBought));

            var plain = OrderWithExtras("Oven Cleaning");
            Assert.False(CleanerJobView.RequiresCleanerToBringSupplies(plain));
        }

        [Fact]
        public void TheCleaningTypeIsDeepOrRegular_NeverTheRawResidentialName()
        {
            // "Residential Cleaning" answers a question nobody working the job is asking. Deep and
            // Regular are different work, different products and a different hourly rate.
            var regular = OrderWithExtras("Oven Cleaning");
            regular.ServiceType = new ServiceType { Name = "Residential Cleaning" };
            Assert.Equal("Regular Cleaning", CleanerJobView.ResolveCleaningTypeName(regular));

            var deep = OrderWithExtras("Deep Cleaning");
            deep.ServiceType = new ServiceType { Name = "Residential Cleaning" };
            Assert.Equal("Deep Cleaning", CleanerJobView.ResolveCleaningTypeName(deep));

            // "Super Deep" CONTAINS "deep", so it has to be tested first or every super-deep job
            // reads as an ordinary deep one.
            var superDeep = OrderWithExtras("Super Deep Cleaning");
            superDeep.ServiceType = new ServiceType { Name = "Residential Cleaning" };
            Assert.Equal("Super Deep Cleaning", CleanerJobView.ResolveCleaningTypeName(superDeep));
        }

        [Fact]
        public void ANonResidentialTypeKeepsItsOwnName()
        {
            // "Move In/Out" and "Post Construction" already say what the work is; rewriting them
            // to "Regular Cleaning" would lose the one word that told the crew what they are
            // walking into.
            var moveOut = OrderWithExtras("Oven Cleaning");
            moveOut.ServiceType = new ServiceType { Name = "Move In/Out Cleaning" };
            Assert.Equal("Move In/Out Cleaning", CleanerJobView.ResolveCleaningTypeName(moveOut));

            // A custom order carries the label an admin typed for it, which is the truth for that
            // order by definition.
            var custom = OrderWithExtras();
            custom.ServiceType = new ServiceType { Name = "Custom", IsCustom = true };
            custom.CustomServiceDisplayName = "Post Construction";
            Assert.Equal("Post Construction Cleaning", CleanerJobView.ResolveCleaningTypeName(custom));
        }

        [Fact]
        public void DeepCleaningIsTheTypeAndSoStopsBeingListedAsATask()
        {
            // Same rule the booking page follows - Deep is never an extras card there either.
            // Leaving it in the task list as well would name the same job twice on one screen.
            Assert.True(CleanerJobView.IsExtraHiddenFromCleaners("Deep Cleaning"));
            Assert.True(CleanerJobView.IsExtraHiddenFromCleaners("Super Deep Cleaning"));
        }

        [Fact]
        public void CleaningSuppliesAndExtraCleanersAreNeverListedAsWorkForTheCleaner()
        {
            // Supplies has its own line, so repeating it in the task list reads as a second job.
            Assert.True(CleanerJobView.IsExtraHiddenFromCleaners("Cleaning Supplies"));
            // Staffing, not work on site.
            Assert.True(CleanerJobView.IsExtraHiddenFromCleaners(OrderPricingCalculator.ExtraCleanersName));
            // A blank name is nothing to show.
            Assert.True(CleanerJobView.IsExtraHiddenFromCleaners("   "));

            Assert.False(CleanerJobView.IsExtraHiddenFromCleaners("Oven Cleaning"));
            Assert.False(CleanerJobView.IsExtraHiddenFromCleaners("Inside Windows"));
        }

        [Fact]
        public void LevelsIsReportedByItsOwnChip_SoItLeavesTheGenericServiceList()
        {
            // Levels prices as an ordinary Service row but every cleaner-facing surface reads the
            // count off Order.LevelsQuantity - the mail and SMS have their own Levels row, the
            // portal its own chip beside the property type. Left in the generic loop as well, the
            // portal printed "House · 2 Levels · 2 Bedrooms · 1 Bathroom · 1,000 sq ft · 2 Levels".
            Assert.True(CleanerJobView.IsServiceLineHiddenFromCleaners("levels"));
            // Matched on the KEY and case-insensitively; the Name and the Id both differ between
            // dev and production, which is why neither may be matched on.
            Assert.True(CleanerJobView.IsServiceLineHiddenFromCleaners("  Levels "));

            // Everything else the customer was priced for is work the cleaner is doing.
            Assert.False(CleanerJobView.IsServiceLineHiddenFromCleaners("bedrooms"));
            Assert.False(CleanerJobView.IsServiceLineHiddenFromCleaners("bathrooms"));
            Assert.False(CleanerJobView.IsServiceLineHiddenFromCleaners("sqft"));
            Assert.False(CleanerJobView.IsServiceLineHiddenFromCleaners("cleaners"));
            Assert.False(CleanerJobView.IsServiceLineHiddenFromCleaners("hours"));
            // An unkeyed row is shown rather than dropped: hiding a line nobody anticipated is
            // worse than printing its stored name.
            Assert.False(CleanerJobView.IsServiceLineHiddenFromCleaners(null));
            Assert.False(CleanerJobView.IsServiceLineHiddenFromCleaners("  "));
        }

        [Fact]
        public void CurrentAndPastAreDisjoint_AndACancelledJobIsNeither()
        {
            // Staffed and not finished - including an unpaid Pending order, because the customer's
            // payment is not the cleaner's business.
            Assert.True(CleanerJobView.IsCurrentJob(OrderStatuses.Pending));
            Assert.True(CleanerJobView.IsCurrentJob(OrderStatuses.Active));
            Assert.False(CleanerJobView.IsPastJob(OrderStatuses.Active, null));

            // Finished.
            Assert.False(CleanerJobView.IsCurrentJob(OrderStatuses.Done));
            Assert.True(CleanerJobView.IsPastJob(OrderStatuses.Done, null));

            // Worked, then refunded: the cleaning happened, so it stays in the cleaner's history.
            // Same test the payroll uses - a refund is between the company and the customer.
            Assert.True(CleanerJobView.IsPastJob(OrderStatuses.Refunded, OrderStatuses.Done));

            // Cancelled, and refunded before anyone cleaned: neither list. It never happened.
            Assert.False(CleanerJobView.IsCurrentJob(OrderStatuses.Cancelled));
            Assert.False(CleanerJobView.IsPastJob(OrderStatuses.Cancelled, null));
            Assert.False(CleanerJobView.IsPastJob(OrderStatuses.Refunded, OrderStatuses.Active));
        }

        [Fact]
        public void ACleaningThatNeverHappenedIsOnNobodysCalendar()
        {
            // The system-wide month used to be every order in the date range, so a cancelled or
            // refunded-before-service job sat in the grid wearing the RED PULSING dot that means
            // work still ahead - months after it was called off. It is the same rule the cleaner's
            // own view already applied, so the two audiences can never be shown different months.
            Assert.False(CleanerJobView.BelongsOnTheCalendar(OrderStatuses.Cancelled, null));
            Assert.False(CleanerJobView.BelongsOnTheCalendar(OrderStatuses.Cancelled, OrderStatuses.Active));
            Assert.False(CleanerJobView.BelongsOnTheCalendar(OrderStatuses.Refunded, null));
            Assert.False(CleanerJobView.BelongsOnTheCalendar(OrderStatuses.Refunded, OrderStatuses.Active));
            Assert.False(CleanerJobView.BelongsOnTheCalendar(OrderStatuses.Refunded, OrderStatuses.Pending));

            // Work stays: ahead of the crew, and behind them.
            Assert.True(CleanerJobView.BelongsOnTheCalendar(OrderStatuses.Pending, null));
            Assert.True(CleanerJobView.BelongsOnTheCalendar(OrderStatuses.Active, null));
            Assert.True(CleanerJobView.BelongsOnTheCalendar(OrderStatuses.Done, null));

            // Worked and later refunded is still a cleaning that happened - it stays, as a
            // COMPLETED one (the quiet green dot), never as work outstanding.
            Assert.True(CleanerJobView.BelongsOnTheCalendar(OrderStatuses.Refunded, OrderStatuses.Done));
            Assert.True(CleanerJobView.IsPastJob(OrderStatuses.Refunded, OrderStatuses.Done));

            // It is exactly the union of the two lists the cleaner's own view builds - not a third
            // opinion about which jobs exist.
            foreach (var (status, before) in new[]
            {
                (OrderStatuses.Pending, (string?)null),
                (OrderStatuses.Active, null),
                (OrderStatuses.Done, null),
                (OrderStatuses.Cancelled, null),
                (OrderStatuses.Refunded, OrderStatuses.Done),
                (OrderStatuses.Refunded, OrderStatuses.Active)
            })
            {
                Assert.Equal(
                    CleanerJobView.IsCurrentJob(status) || CleanerJobView.IsPastJob(status, before),
                    CleanerJobView.BelongsOnTheCalendar(status, before));
            }
        }

        [Fact]
        public void AFinishedJobStopsCarryingTheCustomersAddress()
        {
            // The reason a cleaner was ever given the address was to get there, and that reason
            // expired with the job. It stays in the calendar - they worked it - but as a record.
            var job = new CleanerPortalJobDto
            {
                OrderId = 91,
                ServiceTime = "14:30",
                ServiceTypeName = "Deep Cleaning",
                Address = "12 Fake St, Brooklyn, NY, 11201",
                EntryMethod = "Doorman will let you in",
                IsCompleted = true
            };

            CleanerJobView.RedactCompletedJob(job);

            Assert.Equal(string.Empty, job.Address);
            // The way into somebody's home outlives the job even less than the address does.
            Assert.Null(job.EntryMethod);

            // ...and it is still the same job: what a finished card shows is untouched.
            Assert.Equal("Deep Cleaning", job.ServiceTypeName);
            Assert.Equal("14:30", job.ServiceTime);
        }

        [Fact]
        public void TheCustomerIsIdentifiedByFirstNameOnly()
        {
            var order = OrderWithExtras();
            order.ContactFirstName = "Dana";
            order.ContactLastName = "Whitfield";

            Assert.Equal("Dana", CleanerJobView.ResolveCustomerDisplayName(order));
        }

        // ── The portal endpoints ──────────────────────────────────────────────────────────

        [Fact]
        public void ThePortalControllerWritesNoORDERDATA_AndTheOnlyWriteIsNamedHere()
        {
            // The rule is about the DATA, not the verb: nothing here may change a cleaning. It is
            // asserted as an ALLOWLIST rather than a blanket "no write verbs", because the blanket
            // version has exactly one useful state and the moment a legitimate write appears it is
            // deleted - taking the guard against every illegitimate one with it.
            //
            // SetLanguage writes one display preference onto the CALLER'S OWN cleaner row,
            // resolved from their account rather than taken from the request. Adding a second name
            // to this list should require the same argument to be made out loud.
            var allowedWrites = new[] { "SetLanguage" };
            var writeVerbs = new[] { "POST", "PUT", "PATCH", "DELETE" };

            foreach (var action in PortalActions())
            {
                var verbs = action
                    .GetCustomAttributes<HttpMethodAttribute>(inherit: true)
                    .SelectMany(a => a.HttpMethods.Select(m => m.ToUpperInvariant()))
                    .ToList();

                var isWrite = verbs.Any(v => writeVerbs.Contains(v));
                if (!isWrite) continue;

                Assert.Contains(action.Name, allowedWrites);
            }

            // ...and the one write that IS allowed is a cleaner acting on their own record, never
            // a SuperAdmin reaching into somebody else's.
            var setLanguage = PortalActions().Single(a => a.Name == "SetLanguage");
            var authorize = setLanguage.GetCustomAttribute<AuthorizeAttribute>();
            Assert.Equal("Cleaner", authorize?.Roles);
        }

        [Fact]
        public void EverySystemWideReadIsStaffGatedOnTheEndpoint()
        {
            // Not "hidden in the UI" - these carry every customer's name and address, and the
            // detail endpoint carries pricing, payment and internal notes as well. The boundary
            // they defend is the CLEANER one: an Admin already reads all of this in the orders
            // panel, so widening to them changed who is inside the fence, not where it stands.
            foreach (var name in new[] { "GetAllJobs", "GetOrderDetail" })
            {
                var action = PortalActions().Single(a => a.Name == name);
                var authorize = action.GetCustomAttribute<AuthorizeAttribute>();

                Assert.NotNull(authorize);
                Assert.Equal("Admin,SuperAdmin", authorize!.Roles);
            }
        }

        [Fact]
        public void TheCleanersOwnJobsEndpointIsCleanerOnly()
        {
            var action = PortalActions().Single(a => a.Name == "GetMyJobs");
            var authorize = action.GetCustomAttribute<AuthorizeAttribute>();

            Assert.NotNull(authorize);
            Assert.Equal("Cleaner", authorize!.Roles);
        }

        [Fact]
        public void TheWholePortalControllerRequiresAuthentication()
        {
            var authorize = typeof(CleanerPortalController).GetCustomAttribute<AuthorizeAttribute>();
            Assert.NotNull(authorize);
        }

        private static List<MethodInfo> PortalActions() =>
            typeof(CleanerPortalController)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.GetCustomAttributes<HttpMethodAttribute>(inherit: true).Any())
                .ToList();

        // ── The admin Cleaners tab ────────────────────────────────────────────────

        [Fact]
        public void CleanerAccountWritesRequireTheUpdatePermission_SoModeratorsStayReadOnly()
        {
            var writeActions = typeof(AdminCleanerAccountsController)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.GetCustomAttributes<HttpMethodAttribute>(inherit: true)
                    .SelectMany(a => a.HttpMethods)
                    .Any(v => v is "PUT" or "POST" or "PATCH" or "DELETE"))
                .ToList();

            Assert.NotEmpty(writeActions);

            foreach (var action in writeActions)
            {
                var attr = action
                    .GetCustomAttributes()
                    .SingleOrDefault(a => a.GetType().Name == "RequirePermissionAttribute");

                Assert.True(attr != null,
                    $"{action.Name} must carry RequirePermission - Moderators hold View and would otherwise be able to re-link cleaner accounts.");
            }
        }

        [Fact]
        public void TheCleanersTabIsOpenToRegularAdmins_NotJustSuperAdmins()
        {
            // Staffing the crews is the Admins own daily work, so nothing on this controller is
            // SuperAdmin-gated: the class attribute admits Admin and the per-action
            // RequirePermission is what separates looking from changing (Moderators hold View
            // only). A stray [Authorize(Roles = "SuperAdmin")] on any action would silently lock
            // an Admin out of the tab they are told to use.
            var controllerAuthorize = typeof(AdminCleanerAccountsController)
                .GetCustomAttribute<AuthorizeAttribute>();

            Assert.NotNull(controllerAuthorize);
            Assert.Contains("Admin", controllerAuthorize!.Roles!.Split(','));

            foreach (var action in typeof(AdminCleanerAccountsController)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.GetCustomAttributes<HttpMethodAttribute>(inherit: true).Any()))
            {
                var actionAuthorize = action.GetCustomAttribute<AuthorizeAttribute>();
                Assert.True(actionAuthorize?.Roles == null || actionAuthorize.Roles.Contains("Admin"),
                    $"{action.Name} must stay reachable by a regular Admin.");
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────────────────

        private static Order OrderWithExtras(params string[] extraNames)
        {
            return new Order
            {
                Id = 1,
                ContactFirstName = "Dana",
                ContactLastName = "Whitfield",
                OrderExtraServices = extraNames
                    .Select((n, i) => new OrderExtraService
                    {
                        Id = i + 1,
                        Quantity = 1,
                        ExtraService = new ExtraService { Id = i + 1, Name = n }
                    })
                    .ToList()
            };
        }
    }
}
