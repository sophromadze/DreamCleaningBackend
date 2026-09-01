using DreamCleaningBackend.Models;

namespace DreamCleaningBackend.Helpers
{
    /// <summary>
    /// Who earns what on one order. The BOOKER is whoever took the booking, and their position is
    /// carried alongside because it decides which rate they are paid at. The MANAGER slot is the
    /// booker's manager, and is empty when the booker WAS a manager — that case is paid once,
    /// through the booker slot, at the manager's own-booking rate.
    /// </summary>
    public readonly record struct BonusAttribution(
        int? BookerId,
        AdminPosition BookerPosition,
        int? ManagerId)
    {
        public static BonusAttribution None => new(null, AdminPosition.Administrator, null);
    }

    /// <summary>
    /// The two rates for one slot, plus whether each came from a personal override or the company
    /// default — the shifts panel shows an "own rate" marker off these, so a custom figure is never
    /// mistaken for the default.
    /// </summary>
    public readonly record struct BonusRates(
        decimal NewCustomer,
        decimal ExistingCustomer,
        bool NewCustomerIsCustom,
        bool ExistingCustomerIsCustom)
    {
        public bool HasCustomRate => NewCustomerIsCustom || ExistingCustomerIsCustom;
    }

    /// <summary>
    /// SINGLE SOURCE OF TRUTH for the per-order staff bonus: who earns on an order, at what rate,
    /// and what that adds up to. Pure — no database, no clock — so every rule below is asserted
    /// directly in AdminBonusAttributionTests.
    ///
    /// THREE SLOTS, three independent rate pairs (owner's figures, 2026-08-31):
    ///
    ///   1. an ADMINISTRATOR books the order                      -> 10 / 10
    ///   2. a MANAGER books the order themselves                  -> 15 / 25
    ///   3. a manager's share of their administrator's booking    ->  5 / 15
    ///
    /// The second number of each pair is what a RETURNING customer pays out; the first is a
    /// first-time customer. A manager earns most on repeat business they close themselves, which is
    /// the incentive the table exists to create.
    ///
    /// Rules that are easy to get wrong, each covered by a test:
    ///
    /// 1. <b>A Manager booking an order themselves is paid ONCE, at the manager own-booking rate.</b>
    ///    They fill the booker slot; the team slot stays empty. Crediting them both would pay one
    ///    person two shares of one order.
    /// 2. <b>Slot 2 is not slot 1 plus slot 3.</b> The defaults add up that way today, and that is a
    ///    coincidence of the numbers the owner chose. Each is set by hand — raising what an
    ///    administrator earns is not an instruction to raise what a manager earns for booking.
    /// 3. <b>Attribution is snapshotted onto the order, not re-derived</b> — including the booker's
    ///    POSITION, because that is what selects between slot 1 and slot 2. Callers write the result
    ///    of <see cref="Resolve"/> to Order.BonusBookerId / BonusBookerPosition / BonusManagerId at
    ///    assignment time. Promoting somebody, or moving them under a new manager, therefore changes
    ///    who earns on FUTURE orders only.
    ///
    /// Rates, by contrast, are read live: editing one restates every month on screen, which is what
    /// makes a correction to a mistyped rate actually fix the affected payouts.
    /// </summary>
    public static class AdminBonusAttribution
    {
        /// <summary>
        /// Decides the slots an order pays out on, from the staff member it is being assigned to.
        /// <paramref name="assigneeManagerId"/> is that person's own ManagerId.
        /// </summary>
        public static BonusAttribution Resolve(AdminPosition position, int assigneeId, int? assigneeManagerId)
        {
            // A manager taking the booking fills the booker slot and nothing else — see rule 1.
            // Their own ManagerId is always null anyway (a manager does not report to a manager),
            // but this does not depend on that holding.
            if (position == AdminPosition.Manager)
                return new BonusAttribution(assigneeId, AdminPosition.Manager, null);

            // An administrator books; their manager (if any) earns the team share. A self-reference
            // would pay one person both slots, so it is dropped rather than trusted — the endpoint
            // refuses to store one, but a bad row must not become a double payout.
            var managerId = assigneeManagerId == assigneeId ? null : assigneeManagerId;
            return new BonusAttribution(assigneeId, AdminPosition.Administrator, managerId);
        }

        /// <summary>
        /// What the BOOKER of an order is paid — slot 1 or slot 2, selected by the position they
        /// held when they took it. The override wins per FIELD, so a custom new-customer rate can
        /// sit alongside a repeat-customer rate that still tracks the company default.
        /// </summary>
        public static BonusRates ResolveOwnBookingRates(
            AdminPosition bookerPosition,
            AdminBonusSetting defaults,
            AdminBonusRateOverride? personal)
        {
            var defaultNew = bookerPosition == AdminPosition.Manager
                ? defaults.ManagerOwnBookingNewCustomerRate
                : defaults.AdministratorNewCustomerRate;
            var defaultExisting = bookerPosition == AdminPosition.Manager
                ? defaults.ManagerOwnBookingExistingCustomerRate
                : defaults.AdministratorExistingCustomerRate;

            return new BonusRates(
                personal?.OwnBookingNewCustomerRate ?? defaultNew,
                personal?.OwnBookingExistingCustomerRate ?? defaultExisting,
                personal?.OwnBookingNewCustomerRate != null,
                personal?.OwnBookingExistingCustomerRate != null);
        }

        /// <summary>
        /// What a MANAGER is paid for an order one of their administrators booked — slot 3. Only
        /// ever applies to a manager, so there is no position to select on.
        /// </summary>
        public static BonusRates ResolveTeamRates(
            AdminBonusSetting defaults,
            AdminBonusRateOverride? personal)
        {
            return new BonusRates(
                personal?.TeamBookingNewCustomerRate ?? defaults.ManagerTeamNewCustomerRate,
                personal?.TeamBookingExistingCustomerRate ?? defaults.ManagerTeamExistingCustomerRate,
                personal?.TeamBookingNewCustomerRate != null,
                personal?.TeamBookingExistingCustomerRate != null);
        }

        /// <summary>What a slot's counted orders are worth.</summary>
        public static decimal ComputeBonus(int newCustomerOrders, int existingCustomerOrders, BonusRates rates)
            => newCustomerOrders * rates.NewCustomer + existingCustomerOrders * rates.ExistingCustomer;

        /// <summary>
        /// "This order actually pays out" — EF-translatable, use as <c>.Where(BonusEligible)</c>.
        ///
        /// Deliberately STRICTER than <see cref="OrderBookedFilter.IsRealBooking"/>, which asks
        /// whether a booking happened at all: a bonus is earned for work DELIVERED and money
        /// COLLECTED, so the job has to have reached Done as well. The paid test is the same one
        /// used everywhere else in the codebase — manual payments (cash/Zelle/check) carry
        /// IsPaid = false by design and qualify on PaymentMethod instead.
        /// </summary>
        public static readonly System.Linq.Expressions.Expression<Func<Order, bool>> BonusEligible = o =>
            o.Status == OrderStatuses.Done
            && (o.IsPaid || o.PaymentMethod != PaymentMethod.Normal);

        /// <summary>
        /// The same rule widened to jobs that have not happened yet — what the finances page's
        /// projection toggle folds in. A booked-and-paid cleaning still to be delivered WILL cost
        /// the company its bonuses, so a projection that leaves them out understates the month.
        ///
        /// Never use this to PAY anybody: a bonus is earned on delivery, and the shifts panel
        /// deliberately shows only <see cref="BonusEligible"/>. Refunded orders are excluded by both
        /// — a refund overwrites Status with "Refunded", so neither predicate matches it.
        /// </summary>
        public static readonly System.Linq.Expressions.Expression<Func<Order, bool>> BonusEligibleOrProjected = o =>
            (o.Status == OrderStatuses.Done
             || o.Status == OrderStatuses.Active
             || o.Status == OrderStatuses.Pending)
            && (o.IsPaid || o.PaymentMethod != PaymentMethod.Normal);
    }
}
