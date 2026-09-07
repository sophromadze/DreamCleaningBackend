using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services.Interfaces;

namespace DreamCleaningBackend.Services
{
    /// <summary>
    /// The ONE implementation of "change what a cleaner is paid on an order".
    ///
    /// Two screens write these figures — the SuperAdmin Outgoing Payments page and the admin
    /// Orders panel's payout block — and they must not be two implementations. Everything that
    /// makes such a write safe lives here once: the write-back onto
    /// <see cref="Order.CleanerTotalSalary"/> through
    /// <see cref="CleanerPayrollCalculator.ApplyOrderTotalSalary"/> (which is what Statistics and
    /// Finances report as labour cost), the pinning of already-paid lines when the ORDER's rate
    /// moves, and the audit rows.
    ///
    /// Everything takes an already-loaded, TRACKED order and saves it. The callers load it
    /// differently on purpose — Outgoing Payments only ever touches finished, unrefunded orders,
    /// while the Orders panel edits the job an admin is looking at, which is routinely still
    /// Active — so the query stays with the caller and only the rules live here. What the caller
    /// must load is fixed, though: <c>OrderServices -> Service</c> (or the cleaner-hours test
    /// silently divides a duration twice) and <c>OrderCleaners -> Cleaner</c> (or the total
    /// reverts to the MaidsCount estimate and the audit row cannot name anybody).
    ///
    /// Audit rows are written per CLEANER, never per click. "Set the hours for everyone" is one
    /// button and several decisions, and six months later the question is "why is Ana being paid
    /// 4h15 on #324" — which a row naming Ana answers and a row saying "3 cleaners updated" does
    /// not. It is the same reasoning that already makes "Mark all paid" log one row per payout.
    /// </summary>
    public class CleanerPayrollEditService : ICleanerPayrollEditService
    {
        private readonly ApplicationDbContext _context;
        private readonly IAuditService _audit;

        public CleanerPayrollEditService(ApplicationDbContext context, IAuditService audit)
        {
            _context = context;
            _audit = audit;
        }

        /// <summary>
        /// Who a payout line is for, in a form an audit reader can act on. The name is captured
        /// INTO the audit row rather than looked up when it is read: an assignment can be removed
        /// and a cleaner record deactivated, and "we changed #418's hours" is not an acceptable
        /// answer six months later.
        /// </summary>
        public static string DescribeCleaner(OrderCleaner assignment) =>
            assignment.Cleaner == null
                ? $"Cleaner #{assignment.CleanerId}"
                : $"{assignment.Cleaner.FirstName} {assignment.Cleaner.LastName}".Trim();

        public async Task<bool> SetCleanerOverridesAsync(
            Order order, int orderCleanerId, UpdateCleanerPayrollDto dto)
        {
            var assignment = order.OrderCleaners.FirstOrDefault(oc => oc.Id == orderCleanerId);
            if (assignment == null) return false;

            // A PAID line used to be refused ("undo the payment first"). It is allowed since
            // 2026-09, because the thing that made it unsafe is gone: PaidAmount is a frozen
            // record of what was handed over, and raising this line's hours now leaves the
            // difference showing as still to pay rather than silently restating the payment.
            // Cleaners routinely report longer hours after they have been settled, and the undo /
            // re-pay dance that used to be required threw away the record of the first payment —
            // which is the one thing that must survive. See Helpers/CleanerPayoutSettlement.

            // Captured BEFORE the write, and against the order as it stands now — the automatic
            // split is what an un-overridden line is currently paid, so resolving it afterwards
            // would have the "before" side quote an "after" figure.
            var before = Snapshot(order, assignment, AutomaticMinutesFor(order));

            if (dto.UpdateHourlyRate)
                assignment.SalaryHourlyRate = dto.HourlyRate;

            if (dto.UpdateBillableMinutes)
                assignment.SalaryBillableMinutes = dto.BillableMinutes;

            ApplyOrderTotalSalary(order);
            order.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            var isReset =
                (dto.UpdateHourlyRate && dto.HourlyRate == null) ||
                (dto.UpdateBillableMinutes && dto.BillableMinutes == null);

            await LogOverrideAsync(order, assignment, before, isReset, appliedToEveryone: false);
            return true;
        }

        /// <summary>
        /// Sets the paid hours for EVERY assigned cleaner on the order at once — the "they all
        /// stayed another quarter of an hour" case, which is the routine one and used to mean
        /// opening each line in turn.
        ///
        /// A null clears every override instead, putting the whole order back on the automatic
        /// split. That is not the same as typing the automatic figure onto each line: a cleared
        /// override keeps tracking the order if it is re-priced later, an explicit one does not.
        ///
        /// UNASSIGNED staffing slots are deliberately NOT moved, and cannot be: an override lives
        /// on the assignment row and those slots have no cleaner behind them. They keep the
        /// automatic split, which is why both screens say so next to the control rather than
        /// letting an admin discover it in the total.
        /// </summary>
        /// <returns>How many assignment rows the call actually moved.</returns>
        public async Task<int> SetHoursForEveryCleanerAsync(Order order, decimal? billableMinutes)
        {
            var moved = new List<(OrderCleaner Assignment, PayrollSnapshot Before)>();
            var automaticBefore = AutomaticMinutesFor(order);

            foreach (var assignment in order.OrderCleaners.OrderBy(oc => oc.Id))
            {
                // A line already on this exact figure is left alone, so a re-save does not fill
                // the log with rows recording that nothing happened.
                if (assignment.SalaryBillableMinutes == billableMinutes) continue;

                moved.Add((assignment, Snapshot(order, assignment, automaticBefore)));
                assignment.SalaryBillableMinutes = billableMinutes;
            }

            if (moved.Count == 0) return 0;

            ApplyOrderTotalSalary(order);
            order.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            foreach (var (assignment, before) in moved)
                await LogOverrideAsync(order, assignment, before,
                    isReset: billableMinutes == null, appliedToEveryone: true);

            return moved.Count;
        }

        /// <summary>
        /// Sets the hourly rate for EVERY cleaner on the order. It writes through to
        /// <see cref="Order.CleanerHourlyRate"/>, so the order itself carries the new rate rather
        /// than a screen holding a private view of it, and it DROPS the per-cleaner rate overrides
        /// so nobody is left behind on an old figure.
        ///
        /// **Dropping the overrides is the point, and it is a change of behaviour (owner's call,
        /// 2026-09.)** It used to leave an explicit per-cleaner rate alone, on the reasoning that
        /// somebody had set it deliberately. In practice the control reads as "everyone on this
        /// job is paid X" — an owner who raised two cleaners to $21 and then set the order to $20
        /// got two cleaners still on $21 and a panel that looked broken. "Set it for everyone"
        /// now means everyone.
        ///
        /// The one exception is money that has already left: an **already-PAID line is pinned to
        /// the OLD rate** rather than re-priced. A line following the order rate has that rate
        /// written onto it as an explicit override first, and a paid line that already carried its
        /// own keeps it — so a settled payout never restates itself, and the reported cost of work
        /// already handed over cannot move. (Only a DURATION or hours change reopens a settled
        /// line, because that is a claim about the WORK, not about its price.)
        /// </summary>
        public async Task SetOrderHourlyRateAsync(Order order, decimal hourlyRate)
        {
            var previousRate = order.CleanerHourlyRate;
            var beforeTotal = order.CleanerTotalSalary;

            // Named, not counted. "2 lines pinned" is not something anybody can check against the
            // page six months later; "Ana Reyes, Marta Silva pinned to $21.00" is.
            var pinnedToOldRate = new List<string>();
            var movedOntoTheNewRate = new List<string>();

            foreach (var assignment in order.OrderCleaners)
            {
                if (assignment.IsPaid)
                {
                    // Freeze what was paid. A paid line already carrying its own rate needs
                    // nothing done to it — it is already pinned by that override.
                    if (assignment.SalaryHourlyRate == null)
                    {
                        assignment.SalaryHourlyRate = previousRate;
                        pinnedToOldRate.Add(DescribeCleaner(assignment));
                    }
                    continue;
                }

                if (assignment.SalaryHourlyRate != null)
                {
                    assignment.SalaryHourlyRate = null;
                    movedOntoTheNewRate.Add(DescribeCleaner(assignment));
                }
            }

            order.CleanerHourlyRate = OrderPricingCalculator.Round2(hourlyRate);

            ApplyOrderTotalSalary(order);
            order.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            await _audit.LogActionAsync(
                AuditEntityTypes.OrderCleanerHourlyRate,
                order.Id,
                "Update",
                new
                {
                    CleanerHourlyRate = previousRate,
                    CleanerTotalSalary = beforeTotal
                },
                new
                {
                    CleanerHourlyRate = order.CleanerHourlyRate,
                    CleanerTotalSalary = order.CleanerTotalSalary,
                    // The side effects of the change, recorded with it. Both are decisions the
                    // rate change made on the admin's behalf, and neither is visible anywhere else.
                    PaidLinesPinnedToOldRate = pinnedToOldRate.Count == 0 ? null : string.Join(", ", pinnedToOldRate),
                    OwnRatesDropped = movedOntoTheNewRate.Count == 0 ? null : string.Join(", ", movedOntoTheNewRate)
                },
                // Listed explicitly. The rate and the total always appear, in that order, so the
                // headline of the row is the change the admin made; the two side-effect fields
                // join only when they actually happened, because a row full of "None -> None"
                // is what made the old audit expansions unreadable.
                BuildRateChangeFields(pinnedToOldRate, movedOntoTheNewRate));
        }

        private static List<string> BuildRateChangeFields(List<string> pinned, List<string> dropped)
        {
            var fields = new List<string> { nameof(Order.CleanerHourlyRate), nameof(Order.CleanerTotalSalary) };
            if (pinned.Count > 0) fields.Add("PaidLinesPinnedToOldRate");
            if (dropped.Count > 0) fields.Add("OwnRatesDropped");
            return fields;
        }

        /// <summary>
        /// What one line was actually being PAID at a moment in time, for one side of an audit row.
        ///
        /// The figures are EFFECTIVE, not the override columns. Logging the raw columns was the
        /// obvious thing and it was wrong for the reader: a line following the order rate has a
        /// null override, so an admin who moved a cleaner from the order's $21 to $20 got a row
        /// reading "Hourly Rate: None -> $20.00" — which says the cleaner had no rate before,
        /// when they had $21. <see cref="RateSource"/> / <see cref="HoursSource"/> carry the part
        /// the null was really saying, in words, and only appear in the diff when they change.
        /// </summary>
        public readonly record struct PayrollSnapshot(
            decimal HourlyRate, string RateSource,
            decimal BillableMinutes, string HoursSource,
            decimal CleanerTotalSalary);

        private const string FollowsOrder = "The order's rate";
        private const string SetForCleaner = "Set for this cleaner";
        private const string AutomaticSplit = "Automatic split";
        private const string SetByHand = "Set by hand";

        /// <summary>
        /// The line as it stands right now. <paramref name="automaticMinutes"/> is the even split
        /// the line falls back to — it must be resolved by the caller from the SAME order state
        /// the snapshot describes, or a "before" row quotes an "after" figure.
        /// </summary>
        private static PayrollSnapshot Snapshot(Order order, OrderCleaner assignment, decimal automaticMinutes) =>
            new(assignment.SalaryHourlyRate ?? order.CleanerHourlyRate,
                assignment.SalaryHourlyRate.HasValue ? SetForCleaner : FollowsOrder,
                assignment.SalaryBillableMinutes ?? automaticMinutes,
                assignment.SalaryBillableMinutes.HasValue ? SetByHand : AutomaticSplit,
                order.CleanerTotalSalary);

        /// <summary>The even per-cleaner split this order's lines fall back to.</summary>
        private static decimal AutomaticMinutesFor(Order order) =>
            CleanerPayrollCalculator
                .Build(order, CleanerPayrollCalculator.HasCleanerHoursService(order), order.OrderCleaners)
                .AutomaticBillableMinutes;

        /// <summary>
        /// One audit row for one cleaner's payroll change.
        ///
        /// <c>Cleaner</c> and <c>AppliedTo</c> are on BOTH sides on purpose. They do not change,
        /// so the diff never lists them — they are the CONTEXT the change happened in, and the
        /// Audits tab renders equal-on-both-sides keys as plain details for exactly these
        /// purpose-built payloads. Putting the name on the "new" side alone would render it as
        /// "None -> Ana Reyes", which reads like the cleaner was assigned here. The same is true
        /// of the figure that did NOT move: an hours-only change shows the rate as context.
        /// </summary>
        private Task LogOverrideAsync(
            Order order, OrderCleaner assignment, PayrollSnapshot before, bool isReset, bool appliedToEveryone)
        {
            var who = DescribeCleaner(assignment);
            var appliedTo = appliedToEveryone
                ? "Every cleaner on this order"
                : "This cleaner only";
            var after = Snapshot(order, assignment, AutomaticMinutesFor(order));

            return _audit.LogActionAsync(
                AuditEntityTypes.CleanerPayrollOverride,
                order.Id,
                // "Reset" is a distinct action, not an update to null: it is the deliberate
                // "follow the order again" decision, and an admin scanning the log should be able
                // to filter for it.
                isReset ? "PayrollOverrideReset" : "PayrollOverrideSet",
                // The key names are the ones rows already in the database carry (HourlyRate /
                // BillableMinutes, not the column names) — the log holding two shapes for one kind
                // of event is a worse readability problem than the slightly loose names.
                new
                {
                    Cleaner = who,
                    AppliedTo = appliedTo,
                    HourlyRate = before.HourlyRate,
                    RateSource = before.RateSource,
                    BillableMinutes = before.BillableMinutes,
                    HoursSource = before.HoursSource,
                    CleanerTotalSalary = before.CleanerTotalSalary
                },
                new
                {
                    Cleaner = who,
                    AppliedTo = appliedTo,
                    HourlyRate = after.HourlyRate,
                    RateSource = after.RateSource,
                    BillableMinutes = after.BillableMinutes,
                    HoursSource = after.HoursSource,
                    CleanerTotalSalary = order.CleanerTotalSalary
                });
        }

        // Single-sourced so every caller answers "is TotalDuration already per-cleaner?" the same
        // way — that disagreement is a 2x error in the hours a cleaner is paid.
        private static void ApplyOrderTotalSalary(Order order) =>
            CleanerPayrollCalculator.ApplyOrderTotalSalary(
                order, CleanerPayrollCalculator.HasCleanerHoursService(order), order.OrderCleaners);
    }

    public interface ICleanerPayrollEditService
    {
        /// <summary>False when that assignment is not on that order.</summary>
        Task<bool> SetCleanerOverridesAsync(Order order, int orderCleanerId, UpdateCleanerPayrollDto dto);

        /// <summary>Returns how many assignment rows moved; 0 when there was nothing to change.</summary>
        Task<int> SetHoursForEveryCleanerAsync(Order order, decimal? billableMinutes);

        Task SetOrderHourlyRateAsync(Order order, decimal hourlyRate);
    }
}
