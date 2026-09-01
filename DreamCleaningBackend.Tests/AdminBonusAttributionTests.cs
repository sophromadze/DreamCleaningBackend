using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// WHO GETS PAID FOR AN ORDER, AND HOW MUCH.
    ///
    /// Three slots, three independent rate pairs (the owner's figures, in GEL):
    ///
    ///   1. an ADMINISTRATOR books the order                    -> 10 / 10
    ///   2. a MANAGER books the order themselves                -> 15 / 25
    ///   3. a manager's share of their administrator's booking  ->  5 / 15
    ///
    /// First number = first-time customer, second = returning. A manager earns most on repeat
    /// business they close themselves, which is the incentive the table exists to create.
    ///
    /// What must stay true:
    ///   - a Manager who books an order themselves is paid ONCE, at the manager own-booking rate —
    ///     not the team rate, and not both slots,
    ///   - an administrator with nobody above them still earns their own slot in full,
    ///   - slot 2 is its own editable pair, not slot 1 + slot 3 (the defaults add up by
    ///     coincidence, and the two must be able to move independently),
    ///   - the booker's POSITION is snapshotted with the order, so a promotion cannot re-price
    ///     work somebody already did,
    ///   - a personal rate overrides the company default per FIELD and per SLOT.
    /// </summary>
    public class AdminBonusAttributionTests
    {
        // The owner's rates. Mirrors the seeded AdminBonusSetting row.
        private static AdminBonusSetting Defaults() => new()
        {
            AdministratorNewCustomerRate = 10m,
            AdministratorExistingCustomerRate = 10m,
            ManagerOwnBookingNewCustomerRate = 15m,
            ManagerOwnBookingExistingCustomerRate = 25m,
            ManagerTeamNewCustomerRate = 5m,
            ManagerTeamExistingCustomerRate = 15m,
            Currency = "GEL"
        };

        // ── Attribution ───────────────────────────────────────────────────────────────

        [Fact]
        public void Administrator_UnderAManager_FillsBothSlots()
        {
            var result = AdminBonusAttribution.Resolve(AdminPosition.Administrator, assigneeId: 7, assigneeManagerId: 3);

            Assert.Equal(7, result.BookerId);
            Assert.Equal(AdminPosition.Administrator, result.BookerPosition);
            Assert.Equal(3, result.ManagerId);
        }

        [Fact]
        public void Administrator_WithNoManager_StillEarnsTheirOwnSlotInFull()
        {
            // The team share simply goes unpaid. It is not redistributed to the administrator, and
            // its absence never disqualifies the order.
            var result = AdminBonusAttribution.Resolve(AdminPosition.Administrator, assigneeId: 7, assigneeManagerId: null);

            Assert.Equal(7, result.BookerId);
            Assert.Null(result.ManagerId);
        }

        [Fact]
        public void Manager_BookingItThemselves_FillsTheBookerSlotOnly()
        {
            // They are paid once, through the booker slot, at the manager own-booking rate.
            // Crediting the team slot as well would pay one person two shares of one order.
            var result = AdminBonusAttribution.Resolve(AdminPosition.Manager, assigneeId: 3, assigneeManagerId: null);

            Assert.Equal(3, result.BookerId);
            Assert.Equal(AdminPosition.Manager, result.BookerPosition);
            Assert.Null(result.ManagerId);
        }

        [Fact]
        public void AnAdministratorPointedAtThemselves_DoesNotCollectBothSlots()
        {
            // The endpoint refuses to store a self-reference, but a bad row must not turn into a
            // double payout if one ever reaches us.
            var result = AdminBonusAttribution.Resolve(AdminPosition.Administrator, assigneeId: 7, assigneeManagerId: 7);

            Assert.Equal(7, result.BookerId);
            Assert.Null(result.ManagerId);
        }

        [Fact]
        public void NoAssignee_PaysNobody()
        {
            Assert.Null(BonusAttribution.None.BookerId);
            Assert.Null(BonusAttribution.None.ManagerId);
        }

        // ── Rates ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void EachSlotGetsItsOwnDefaults()
        {
            var administratorBooks = AdminBonusAttribution.ResolveOwnBookingRates(
                AdminPosition.Administrator, Defaults(), null);
            var managerBooks = AdminBonusAttribution.ResolveOwnBookingRates(
                AdminPosition.Manager, Defaults(), null);
            var managerTeamShare = AdminBonusAttribution.ResolveTeamRates(Defaults(), null);

            Assert.Equal(10m, administratorBooks.NewCustomer);
            Assert.Equal(10m, administratorBooks.ExistingCustomer);

            Assert.Equal(15m, managerBooks.NewCustomer);
            Assert.Equal(25m, managerBooks.ExistingCustomer);

            Assert.Equal(5m, managerTeamShare.NewCustomer);
            Assert.Equal(15m, managerTeamShare.ExistingCustomer);
        }

        [Fact]
        public void AManagersOwnBookingRateIsIndependentOfTheOtherTwo()
        {
            // The defaults happen to satisfy 15 = 10 + 5 and 25 = 10 + 15, and that is a
            // coincidence of the numbers the owner chose — NOT a derivation. Raising what an
            // administrator earns must not move what a manager earns for taking the booking.
            var defaults = Defaults();
            defaults.AdministratorNewCustomerRate = 12m;
            defaults.AdministratorExistingCustomerRate = 12m;

            var managerBooks = AdminBonusAttribution.ResolveOwnBookingRates(
                AdminPosition.Manager, defaults, null);

            Assert.Equal(15m, managerBooks.NewCustomer);
            Assert.Equal(25m, managerBooks.ExistingCustomer);
        }

        [Fact]
        public void AnOverrideWinsPerField_TheOtherKeepsTrackingTheDefault()
        {
            // Half-set overrides are the point of nullable columns: a person can be moved off the
            // new-customer rate while their repeat rate follows whatever the company does next.
            var personal = new AdminBonusRateOverride
            {
                OwnBookingNewCustomerRate = 12m,
                OwnBookingExistingCustomerRate = null
            };

            var rates = AdminBonusAttribution.ResolveOwnBookingRates(
                AdminPosition.Administrator, Defaults(), personal);

            Assert.Equal(12m, rates.NewCustomer);
            Assert.Equal(10m, rates.ExistingCustomer);
            Assert.True(rates.NewCustomerIsCustom);
            Assert.False(rates.ExistingCustomerIsCustom);
            Assert.True(rates.HasCustomRate);
        }

        [Fact]
        public void AnOverrideWinsPerSlot_OwnBookingsAndTeamShareMoveSeparately()
        {
            // A manager can be paid more for closing business themselves without changing what
            // their team's work earns them, which is the reason there are two pairs at all.
            var personal = new AdminBonusRateOverride
            {
                OwnBookingNewCustomerRate = 16m,
                OwnBookingExistingCustomerRate = 26m
            };

            var own = AdminBonusAttribution.ResolveOwnBookingRates(AdminPosition.Manager, Defaults(), personal);
            var team = AdminBonusAttribution.ResolveTeamRates(Defaults(), personal);

            Assert.Equal(16m, own.NewCustomer);
            Assert.Equal(26m, own.ExistingCustomer);
            Assert.True(own.HasCustomRate);

            Assert.Equal(5m, team.NewCustomer);
            Assert.Equal(15m, team.ExistingCustomer);
            Assert.False(team.HasCustomRate);
        }

        [Fact]
        public void AZeroOverrideIsARealRate_NotAMissingOne()
        {
            // Null means "follow the default"; 0 means "this person earns nothing here". Treating
            // the two the same would quietly pay somebody who was deliberately zeroed out.
            var personal = new AdminBonusRateOverride
            {
                TeamBookingNewCustomerRate = 0m,
                TeamBookingExistingCustomerRate = 0m
            };

            var team = AdminBonusAttribution.ResolveTeamRates(Defaults(), personal);

            Assert.Equal(0m, team.NewCustomer);
            Assert.Equal(0m, team.ExistingCustomer);
            Assert.True(team.HasCustomRate);
        }

        // ── What it adds up to ────────────────────────────────────────────────────────

        [Fact]
        public void TheOwnersWorkedExample_AnAdministratorBooksForANewCustomer()
        {
            // 10 to the administrator who took it, 5 to their manager.
            var booker = AdminBonusAttribution.ResolveOwnBookingRates(AdminPosition.Administrator, Defaults(), null);
            var manager = AdminBonusAttribution.ResolveTeamRates(Defaults(), null);

            Assert.Equal(10m, AdminBonusAttribution.ComputeBonus(1, 0, booker));
            Assert.Equal(5m, AdminBonusAttribution.ComputeBonus(1, 0, manager));
        }

        [Fact]
        public void TheOwnersWorkedExample_AnAdministratorBooksForAReturningCustomer()
        {
            // 10 to the administrator, 15 to their manager. The manager's side is the half that moves.
            var booker = AdminBonusAttribution.ResolveOwnBookingRates(AdminPosition.Administrator, Defaults(), null);
            var manager = AdminBonusAttribution.ResolveTeamRates(Defaults(), null);

            Assert.Equal(10m, AdminBonusAttribution.ComputeBonus(0, 1, booker));
            Assert.Equal(15m, AdminBonusAttribution.ComputeBonus(0, 1, manager));
        }

        [Fact]
        public void TheOwnersWorkedExample_AManagerBooksItThemselves()
        {
            // 15 for a first-time customer, 25 for a returning one — and nobody else earns on it.
            var attribution = AdminBonusAttribution.Resolve(AdminPosition.Manager, assigneeId: 3, assigneeManagerId: null);
            var rates = AdminBonusAttribution.ResolveOwnBookingRates(attribution.BookerPosition, Defaults(), null);

            Assert.Equal(15m, AdminBonusAttribution.ComputeBonus(1, 0, rates));
            Assert.Equal(25m, AdminBonusAttribution.ComputeBonus(0, 1, rates));
            Assert.Null(attribution.ManagerId);
        }

        [Fact]
        public void AManagerBookingItThemselvesEarnsMoreThanTheirTeamShare()
        {
            // The whole point of splitting slot 2 out of slot 3: doing the work yourself has to pay
            // better than taking a share of somebody else's.
            var own = AdminBonusAttribution.ResolveOwnBookingRates(AdminPosition.Manager, Defaults(), null);
            var team = AdminBonusAttribution.ResolveTeamRates(Defaults(), null);

            Assert.True(own.NewCustomer > team.NewCustomer);
            Assert.True(own.ExistingCustomer > team.ExistingCustomer);
        }

        [Fact]
        public void AMixedMonthSumsBothRates()
        {
            var team = AdminBonusAttribution.ResolveTeamRates(Defaults(), null);

            // 4 first-timers and 6 repeats from the team: 4x5 + 6x15.
            Assert.Equal(110m, AdminBonusAttribution.ComputeBonus(4, 6, team));
        }

        [Fact]
        public void AManagersMonthCombinesBothSlots()
        {
            // 2 first-time bookings they took themselves (2x15), plus 3 returning customers their
            // administrators brought in (3x15).
            var own = AdminBonusAttribution.ResolveOwnBookingRates(AdminPosition.Manager, Defaults(), null);
            var team = AdminBonusAttribution.ResolveTeamRates(Defaults(), null);

            var total = AdminBonusAttribution.ComputeBonus(2, 0, own)
                        + AdminBonusAttribution.ComputeBonus(0, 3, team);

            Assert.Equal(75m, total);
        }

        [Fact]
        public void NoOrders_EarnsNothing()
        {
            var rates = AdminBonusAttribution.ResolveOwnBookingRates(AdminPosition.Manager, Defaults(), null);
            Assert.Equal(0m, AdminBonusAttribution.ComputeBonus(0, 0, rates));
        }

        [Fact]
        public void APromotionDoesNotRePriceWorkAlreadyDone()
        {
            // The position is snapshotted on the order, so an order taken while somebody was an
            // administrator keeps paying the administrator rate after they become a manager.
            var takenAsAdministrator = AdminBonusAttribution.ResolveOwnBookingRates(
                AdminPosition.Administrator, Defaults(), null);
            var takenAsManager = AdminBonusAttribution.ResolveOwnBookingRates(
                AdminPosition.Manager, Defaults(), null);

            Assert.Equal(10m, AdminBonusAttribution.ComputeBonus(0, 1, takenAsAdministrator));
            Assert.Equal(25m, AdminBonusAttribution.ComputeBonus(0, 1, takenAsManager));
        }

        // ── Which orders pay ──────────────────────────────────────────────────────────

        [Theory]
        // Delivered and paid through Stripe.
        [InlineData(OrderStatuses.Done, true, PaymentMethod.Normal, true)]
        // Delivered and paid in cash — IsPaid stays false by design for manual payments.
        [InlineData(OrderStatuses.Done, false, PaymentMethod.Cash, true)]
        // Delivered but never paid.
        [InlineData(OrderStatuses.Done, false, PaymentMethod.Normal, false)]
        // Paid but not delivered yet: earns nothing until the job is done.
        [InlineData(OrderStatuses.Active, true, PaymentMethod.Normal, false)]
        [InlineData(OrderStatuses.Pending, true, PaymentMethod.Normal, false)]
        // Cancelled, and fully refunded (a refund overwrites Status).
        [InlineData(OrderStatuses.Cancelled, true, PaymentMethod.Normal, false)]
        [InlineData(OrderStatuses.Refunded, true, PaymentMethod.Normal, false)]
        public void BonusEligible_PaysForDeliveredAndPaidWorkOnly(
            string status, bool isPaid, PaymentMethod method, bool expected)
        {
            var predicate = AdminBonusAttribution.BonusEligible.Compile();
            var order = new Order { Status = status, IsPaid = isPaid, PaymentMethod = method };

            Assert.Equal(expected, predicate(order));
        }

        [Theory]
        // The projection adds work that is booked and paid but not delivered yet...
        [InlineData(OrderStatuses.Active, true, PaymentMethod.Normal, true)]
        [InlineData(OrderStatuses.Pending, true, PaymentMethod.Normal, true)]
        // ...and still excludes work that earned nothing and never will.
        [InlineData(OrderStatuses.Cancelled, true, PaymentMethod.Normal, false)]
        [InlineData(OrderStatuses.Refunded, true, PaymentMethod.Normal, false)]
        [InlineData(OrderStatuses.Pending, false, PaymentMethod.Normal, false)]
        public void BonusEligibleOrProjected_ForecastsUnfinishedWork(
            string status, bool isPaid, PaymentMethod method, bool expected)
        {
            var predicate = AdminBonusAttribution.BonusEligibleOrProjected.Compile();
            var order = new Order { Status = status, IsPaid = isPaid, PaymentMethod = method };

            Assert.Equal(expected, predicate(order));
        }

        [Fact]
        public void TheProjectionIsAStrictSuperset_NothingPayableFallsOutOfIt()
        {
            // A forecast that dropped an order the payout includes would report a cost lower than
            // the one already committed.
            var payable = AdminBonusAttribution.BonusEligible.Compile();
            var projected = AdminBonusAttribution.BonusEligibleOrProjected.Compile();

            var statuses = new[]
            {
                OrderStatuses.Done, OrderStatuses.Active, OrderStatuses.Pending,
                OrderStatuses.Cancelled, OrderStatuses.Refunded
            };
            var methods = new[] { PaymentMethod.Normal, PaymentMethod.Cash, PaymentMethod.Zelle };

            foreach (var status in statuses)
                foreach (var method in methods)
                    foreach (var isPaid in new[] { true, false })
                    {
                        var order = new Order { Status = status, IsPaid = isPaid, PaymentMethod = method };
                        if (payable(order))
                            Assert.True(projected(order), $"{status}/{method}/paid={isPaid} pays out but is not projected");
                    }
        }
    }
}
