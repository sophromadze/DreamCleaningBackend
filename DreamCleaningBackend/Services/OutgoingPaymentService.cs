using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models;
using Microsoft.EntityFrameworkCore;

namespace DreamCleaningBackend.Services
{
    /// <summary>
    /// Builds the SuperAdmin "Outgoing Payments" page: every finished job, who cleaned it, and
    /// what each of them is owed — and records the payouts.
    ///
    /// It exists because this used to be typed by hand into WhatsApp every week and the arithmetic
    /// went wrong. Everything on the page is therefore derived, never re-keyed: the money comes off
    /// the order, the split comes from <see cref="CleanerPayrollCalculator"/>, and the expected
    /// hourly rate comes from <see cref="OrderPricingCalculator.GetDefaultCleanerHourlyRate"/> so a
    /// rate somebody set wrong shows up as a warning instead of as a wrong payment.
    ///
    /// Paying is not a one-shot event. A line records what was HANDED OVER; if the order is later
    /// edited so the line is worth more — cleaners reporting they worked four hours rather than
    /// three and a half is the routine case — the difference is reported as still to pay and the
    /// order drops back out of "Paid". <see cref="Helpers.CleanerPayoutSettlement"/> is the single
    /// rule for that, and paying the difference tops the frozen amount up rather than replacing
    /// it. Nothing of the sort is ever shown for a line nobody has paid yet.
    ///
    /// Which orders qualify: <b>Done, with no refund of any size</b> (owner's call, 2026-08).
    /// Cancelled, fully refunded and part-refunded orders are all out — the page is a list of
    /// finished jobs to settle, and an order whose money went back is a conversation rather than
    /// a routine payout. The trade-off is that a cleaner who worked a later-refunded job has to be
    /// settled outside this page; widening the predicate in PerformedOrdersWithIncludes is the
    /// one-line change if that turns out to matter.
    /// </summary>
    public class OutgoingPaymentService : IOutgoingPaymentService
    {
        private readonly ApplicationDbContext _context;
        private readonly Interfaces.IAuditService _audit;

        public OutgoingPaymentService(ApplicationDbContext context, Interfaces.IAuditService audit)
        {
            _context = context;
            _audit = audit;
        }

        /// <summary>
        /// Who a payout line is for, in a form an audit reader can act on. The cleaner's name is
        /// captured INTO the audit row rather than looked up when it is read: an assignment can be
        /// removed and a cleaner record deactivated, and "we paid #418 something" is not an
        /// acceptable answer six months later.
        /// </summary>
        private static string DescribeCleaner(OrderCleaner assignment) =>
            assignment.Cleaner == null
                ? $"Cleaner #{assignment.CleanerId}"
                : $"{assignment.Cleaner.FirstName} {assignment.Cleaner.LastName}".Trim();

        public async Task<OutgoingPaymentListDto> GetAsync(OutgoingPaymentQuery query)
        {
            var orders = await LoadPerformedOrdersAsync(query);

            var rows = orders.Select(BuildOrderRow).ToList();

            // Paid-state filtering happens after the rows are built because "unpaid" is a fact
            // about the cleaner lines, not a column on the order.
            var filtered = query.PaidStatus switch
            {
                OutgoingPaymentPaidStatus.Unpaid => rows.Where(r => !r.IsFullyPaid).ToList(),
                OutgoingPaymentPaidStatus.Paid => rows.Where(r => r.IsFullyPaid).ToList(),
                _ => rows
            };

            if (query.WarningsOnly)
                filtered = filtered.Where(r => r.Warnings.Count > 0).ToList();

            var summary = BuildSummary(filtered);
            var totalCount = filtered.Count;

            var page = Math.Max(1, query.Page);
            var pageSize = Math.Clamp(query.PageSize, 1, 200);
            var paged = filtered
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToList();

            return new OutgoingPaymentListDto
            {
                Orders = paged,
                // The summary spans the WHOLE filtered range, not the page: "how much has to go
                // out this month" is the question, and it must not change as you page through.
                Summary = summary,
                TotalCount = totalCount,
                Page = page,
                PageSize = pageSize
            };
        }

        public async Task<OutgoingPaymentOrderDto?> GetOrderAsync(int orderId)
        {
            var order = await PerformedOrdersWithIncludes()
                .FirstOrDefaultAsync(o => o.Id == orderId);

            return order == null ? null : BuildOrderRow(order);
        }

        /// <summary>
        /// Applies a rate/hours override to one cleaner on one order and writes the resulting
        /// total back to <see cref="Order.CleanerTotalSalary"/>, which is what Statistics and
        /// Finances read. That write-back is the point of the page: the per-cleaner figures are
        /// the truth, and the order's single number has to follow them.
        /// </summary>
        public async Task<OutgoingPaymentOrderDto?> UpdateCleanerPayrollAsync(
            int orderId, int orderCleanerId, UpdateCleanerPayrollDto dto)
        {
            var order = await PerformedOrdersWithIncludes()
                .FirstOrDefaultAsync(o => o.Id == orderId);
            if (order == null) return null;

            var assignment = order.OrderCleaners.FirstOrDefault(oc => oc.Id == orderCleanerId);
            if (assignment == null) return null;

            // A PAID line used to be refused here ("undo the payment first"). It is allowed since
            // 2026-09, because the thing that made it unsafe is gone: PaidAmount is a frozen
            // record of what was handed over, and raising this line's hours now leaves the
            // difference showing as still to pay rather than silently restating the payment.
            // Cleaners routinely report longer hours after they have been settled, and the undo /
            // re-pay dance that used to be required threw away the record of the first payment —
            // which is the one thing that must survive. See Helpers/CleanerPayoutSettlement.

            // Captured BEFORE the write. A null here means "this line tracks the order rate/split"
            // and is materially different from a value that happens to equal it, so the audit row
            // records the null rather than resolving it to a number.
            var beforeRate = assignment.SalaryHourlyRate;
            var beforeMinutes = assignment.SalaryBillableMinutes;
            var beforeTotal = order.CleanerTotalSalary;

            if (dto.UpdateHourlyRate)
                assignment.SalaryHourlyRate = dto.HourlyRate;

            if (dto.UpdateBillableMinutes)
                assignment.SalaryBillableMinutes = dto.BillableMinutes;

            ApplyOrderTotalSalary(order);
            order.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            await _audit.LogActionAsync(
                AuditEntityTypes.CleanerPayrollOverride,
                order.Id,
                // "Reset" is a distinct action, not an update to null: it is the deliberate
                // "follow the order again" decision, and an admin scanning the log should be able
                // to filter for it.
                (dto.UpdateHourlyRate && dto.HourlyRate == null) || (dto.UpdateBillableMinutes && dto.BillableMinutes == null)
                    ? "PayrollOverrideReset"
                    : "PayrollOverrideSet",
                new
                {
                    Cleaner = DescribeCleaner(assignment),
                    HourlyRate = beforeRate,
                    BillableMinutes = beforeMinutes,
                    CleanerTotalSalary = beforeTotal
                },
                new
                {
                    Cleaner = DescribeCleaner(assignment),
                    HourlyRate = assignment.SalaryHourlyRate,
                    BillableMinutes = assignment.SalaryBillableMinutes,
                    CleanerTotalSalary = order.CleanerTotalSalary
                });

            return BuildOrderRow(order);
        }

        /// <summary>
        /// Changes the ORDER's hourly rate — what every assigned cleaner without a per-cleaner
        /// override is paid at — and writes it through to <see cref="Order.CleanerHourlyRate"/>,
        /// so the order itself carries the new rate rather than the page holding a private view
        /// of it.
        ///
        /// Two rules make the change safe:
        ///
        /// 1. **Already-PAID lines are pinned first.** Before the order moves, every paid
        ///    assignment that was following the order rate has that rate written onto it as an
        ///    explicit override. Their salary therefore stays exactly what was handed over —
        ///    otherwise raising the rate would retroactively inflate the reported cost of work
        ///    already settled at the old figure. This mirrors the refusal to edit a paid line
        ///    directly; the money that left is not up for revision.
        /// 2. **Explicit per-cleaner overrides are left alone.** Somebody set that cleaner's rate
        ///    on purpose, and a change to the order's default is not an instruction to discard it.
        ///    "Reset to automatic" on the line is how an override rejoins the order rate.
        /// </summary>
        public async Task<OutgoingPaymentOrderDto?> UpdateOrderHourlyRateAsync(int orderId, decimal hourlyRate)
        {
            var order = await PerformedOrdersWithIncludes()
                .FirstOrDefaultAsync(o => o.Id == orderId);
            if (order == null) return null;

            var previousRate = order.CleanerHourlyRate;
            var beforeTotal = order.CleanerTotalSalary;

            // Named, not counted. "2 lines pinned" is not something anybody can check against the
            // page six months later; "Ana Reyes, Marta Silva pinned to $21.00" is.
            var pinnedToOldRate = new List<string>();

            foreach (var assignment in order.OrderCleaners)
            {
                if (assignment.IsPaid && assignment.SalaryHourlyRate == null)
                {
                    assignment.SalaryHourlyRate = previousRate;
                    pinnedToOldRate.Add(DescribeCleaner(assignment));
                }
            }

            var keptOwnRate = order.OrderCleaners
                .Where(oc => !oc.IsPaid && oc.SalaryHourlyRate != null)
                .Select(DescribeCleaner)
                .ToList();

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
                    LinesKeepingTheirOwnRate = keptOwnRate.Count == 0 ? null : string.Join(", ", keptOwnRate)
                },
                // Listed explicitly. The rate and the total always appear, in that order, so the
                // headline of the row is the change the admin made; the two side-effect fields
                // join only when they actually happened, because a row full of "None -> None"
                // is what made the old audit expansions unreadable.
                BuildRateChangeFields(pinnedToOldRate, keptOwnRate));

            return BuildOrderRow(order);
        }

        private static List<string> BuildRateChangeFields(List<string> pinned, List<string> keptOwn)
        {
            var fields = new List<string> { nameof(Order.CleanerHourlyRate), nameof(Order.CleanerTotalSalary) };
            if (pinned.Count > 0) fields.Add("PaidLinesPinnedToOldRate");
            if (keptOwn.Count > 0) fields.Add("LinesKeepingTheirOwnRate");
            return fields;
        }

        public async Task<OutgoingPaymentOrderDto?> MarkCleanerPaidAsync(
            int orderId, int orderCleanerId, MarkCleanerPaidDto dto, int paidByUserId)
        {
            var order = await PerformedOrdersWithIncludes()
                .FirstOrDefaultAsync(o => o.Id == orderId);
            if (order == null) return null;

            var assignment = order.OrderCleaners.FirstOrDefault(oc => oc.Id == orderCleanerId);
            if (assignment == null) return null;

            // Freeze the total onto the order first, so the amount recorded against the payout is
            // the one the reported labour cost also carries.
            ApplyOrderTotalSalary(order);

            var line = BuildPayroll(order).Lines.FirstOrDefault(l => l.OrderCleanerId == orderCleanerId);
            var wasAlreadyPaid = assignment.IsPaid;
            var recorded = MarkPaid(assignment, line, dto.PaidVia, dto.PaymentNote, paidByUserId);

            order.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            // MarkPaid returns null on a line that was already covered; recording a payout that
            // did not happen would be worse than recording none. A line that WAS paid and is
            // short is a second, separate payment, so it gets its own row under its own action.
            if (recorded.HasValue)
                await LogPayoutAsync(order, wasAlreadyPaid ? "PayoutToppedUp" : "PayoutRecorded",
                    DescribeCleaner(assignment), recorded.Value, assignment.PaidAmount,
                    assignment.PaidVia, assignment.PaymentNote, paidByUserId);

            return BuildOrderRow(order);
        }

        /// <summary>
        /// One payout event. Deliberately a pseudo-entity (<see cref="AuditEntityTypes.CleanerPayout"/>)
        /// rather than an OrderCleaner update: the row records money handed over, and it must never
        /// be replayable by the generic Undo, which would flip IsPaid without anyone deciding to.
        /// </summary>
        /// <param name="amount">
        /// What moved in THIS event — the shortfall on a top-up, not the running total. An audit
        /// row for a $10.50 top-up that read "$84.00" would look like the whole job was paid twice.
        /// </param>
        /// <param name="totalPaidForLine">
        /// What the line has had in total once this event is applied, so a reader can see both
        /// "what went out today" and "where that leaves us" without adding up the log.
        /// </param>
        private Task LogPayoutAsync(
            Order order, string action, string who,
            decimal? amount, decimal? totalPaidForLine,
            CleanerPaymentMethod? via, string? note, int? actingUserId) =>
            _audit.LogActionAsync(
                AuditEntityTypes.CleanerPayout,
                order.Id,
                action,
                null,
                new
                {
                    Cleaner = who,
                    PaidAmount = amount,
                    TotalPaidForLine = totalPaidForLine,
                    PaidVia = via?.ToString(),
                    PaymentNote = note,
                    // The reported labour cost as it stood when the money went out, so a later
                    // rate change cannot make this row look like it paid the wrong figure.
                    CleanerTotalSalary = order.CleanerTotalSalary
                },
                actingUserId: actingUserId);

        public async Task<OutgoingPaymentOrderDto?> MarkOrderPaidAsync(
            int orderId, MarkOrderPaidDto dto, int paidByUserId)
        {
            var order = await PerformedOrdersWithIncludes()
                .FirstOrDefaultAsync(o => o.Id == orderId);
            if (order == null) return null;

            ApplyOrderTotalSalary(order);
            var payroll = BuildPayroll(order);

            // Each newly-paid line is audited individually, because "mark all paid" is one CLICK
            // but several payments — the money reaches different people through different
            // channels, and a single summary row could not answer "was Marta paid for #412".
            var newlyPaid = new List<(string Who, string Action, decimal Amount, decimal? Total, CleanerPaymentMethod? Via)>();

            // Every line that still owes something, not merely every UNPAID line: an order edited
            // after payday leaves paid lines short, and "Mark all paid" has to settle those too
            // or the button would leave the order it just settled still showing money owed.
            foreach (var assignment in order.OrderCleaners)
            {
                var line = payroll.Lines.FirstOrDefault(l => l.OrderCleanerId == assignment.Id);
                var wasAlreadyPaid = assignment.IsPaid;
                // Each cleaner is paid via their OWN saved method — a "pay all" is one action,
                // not one payment channel.
                var recorded = MarkPaid(assignment, line, null, dto.PaymentNote, paidByUserId);
                if (recorded.HasValue)
                    newlyPaid.Add((DescribeCleaner(assignment),
                        wasAlreadyPaid ? "PayoutToppedUp" : "PayoutRecorded",
                        recorded.Value, assignment.PaidAmount, assignment.PaidVia));
            }

            // "Everyone on this order" includes the staffing slots with nobody on file — they
            // were paid too, and leaving them out would make "mark all paid" a lie.
            var unassignedPayout = UnassignedSlotPayout(payroll);
            for (var slot = 0; slot < payroll.UnassignedCount; slot++)
            {
                var record = ResolveSlotRecord(order, slot);
                var slotWasPaid = record.IsPaid;
                var recorded = MarkSlotPaid(record, unassignedPayout, null, dto.PaymentNote, paidByUserId);
                if (recorded.HasValue)
                    newlyPaid.Add(($"Unassigned slot #{slot + 1}",
                        slotWasPaid ? "PayoutToppedUp" : "PayoutRecorded",
                        recorded.Value, record.PaidAmount, record.PaidVia));
            }

            order.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            foreach (var (who, action, amount, total, via) in newlyPaid)
                await LogPayoutAsync(order, action, who, amount, total, via, dto.PaymentNote, paidByUserId);

            return BuildOrderRow(order);
        }

        /// <summary>
        /// Marks ONE unassigned staffing slot paid. The slot has no cleaner record, so the payout
        /// row is created here on first use rather than existing up front — see
        /// <see cref="OrderUnassignedPayout"/> for why the table holds decisions, not arithmetic.
        /// </summary>
        public async Task<OutgoingPaymentOrderDto?> MarkUnassignedSlotPaidAsync(
            int orderId, int slotIndex, MarkCleanerPaidDto dto, int paidByUserId)
        {
            var order = await PerformedOrdersWithIncludes()
                .FirstOrDefaultAsync(o => o.Id == orderId);
            if (order == null) return null;

            ApplyOrderTotalSalary(order);
            var payroll = BuildPayroll(order);

            if (slotIndex < 0 || slotIndex >= payroll.UnassignedCount) return null;

            var slotRecord = ResolveSlotRecord(order, slotIndex);
            var slotWasPaid = slotRecord.IsPaid;
            var recorded = MarkSlotPaid(slotRecord, UnassignedSlotPayout(payroll),
                dto.PaidVia, dto.PaymentNote, paidByUserId);

            order.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            if (recorded.HasValue)
                await LogPayoutAsync(order, slotWasPaid ? "PayoutToppedUp" : "PayoutRecorded",
                    $"Unassigned slot #{slotIndex + 1}", recorded.Value, slotRecord.PaidAmount,
                    slotRecord.PaidVia, slotRecord.PaymentNote, paidByUserId);

            return BuildOrderRow(order);
        }

        /// <summary>Reverses a payout recorded against an unassigned staffing slot.</summary>
        public async Task<OutgoingPaymentOrderDto?> UndoUnassignedSlotPaymentAsync(int orderId, int slotIndex)
        {
            var order = await PerformedOrdersWithIncludes()
                .FirstOrDefaultAsync(o => o.Id == orderId);
            if (order == null) return null;

            var record = order.UnassignedPayouts.FirstOrDefault(p => p.SlotIndex == slotIndex);
            if (record == null) return null;

            // The reversal has to name what is being reversed. Clearing PaidAmount first and
            // logging afterwards would record a payout reversal of "None".
            var reversedAmount = record.PaidAmount;
            var reversedVia = record.PaidVia;
            var reversedNote = record.PaymentNote;

            record.IsPaid = false;
            record.PaidAmount = null;
            record.PaidVia = null;
            record.PaidAt = null;
            record.PaidByUserId = null;
            record.PaymentNote = null;

            order.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            await LogPayoutAsync(order, "PayoutReversed", $"Unassigned slot #{slotIndex + 1}",
                reversedAmount, 0m, reversedVia, reversedNote, null);

            return BuildOrderRow(order);
        }

        /// <summary>
        /// Reverses a payout marking. The frozen PaidAmount is cleared with it — the record only
        /// means anything while IsPaid stands, and leaving a stale amount behind would make an
        /// un-done payment look like a paid one in any future query.
        /// </summary>
        public async Task<OutgoingPaymentOrderDto?> UndoCleanerPaymentAsync(int orderId, int orderCleanerId)
        {
            var order = await PerformedOrdersWithIncludes()
                .FirstOrDefaultAsync(o => o.Id == orderId);
            if (order == null) return null;

            var assignment = order.OrderCleaners.FirstOrDefault(oc => oc.Id == orderCleanerId);
            if (assignment == null) return null;

            var reversedAmount = assignment.PaidAmount;
            var reversedVia = assignment.PaidVia;
            var reversedNote = assignment.PaymentNote;
            var who = DescribeCleaner(assignment);

            assignment.IsPaid = false;
            assignment.PaidAmount = null;
            assignment.PaidVia = null;
            assignment.PaidAt = null;
            assignment.PaidByUserId = null;
            assignment.PaymentNote = null;

            order.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            // A reversal clears the whole line, top-ups included — the running total it leaves
            // behind is zero, and saying so is what stops a reader adding the events up wrong.
            await LogPayoutAsync(order, "PayoutReversed", who, reversedAmount, 0m, reversedVia, reversedNote, null);

            return BuildOrderRow(order);
        }

        // ===== internals =====

        /// <summary>What ONE unassigned slot is handed: its wages plus its share of the tips.</summary>
        private static decimal UnassignedSlotPayout(CleanerPayrollCalculator.Result payroll) =>
            payroll.UnassignedCount == 0
                ? 0m
                : OrderPricingCalculator.Round2(
                    payroll.UnassignedSalaryEach + payroll.UnassignedTips / payroll.UnassignedCount);

        /// <summary>
        /// The payout row for one slot, created on first use. Attaching it to the order's loaded
        /// collection (rather than to the DbSet) keeps BuildOrderRow's view of the order correct
        /// in the same request, without a reload.
        /// </summary>
        private OrderUnassignedPayout ResolveSlotRecord(Order order, int slotIndex)
        {
            var record = order.UnassignedPayouts.FirstOrDefault(p => p.SlotIndex == slotIndex);
            if (record != null) return record;

            record = new OrderUnassignedPayout { OrderId = order.Id, SlotIndex = slotIndex };
            order.UnassignedPayouts.Add(record);
            _context.OrderUnassignedPayouts.Add(record);
            return record;
        }

        /// <summary>
        /// Records a payout against an unassigned staffing slot — the first one, or a TOP-UP when
        /// the slot's hours grew after it was settled. Same rules as <see cref="MarkPaid"/>.
        /// Returns what was handed over NOW, or null when the line was already covered.
        /// </summary>
        private static decimal? MarkSlotPaid(
            OrderUnassignedPayout record,
            decimal payout,
            CleanerPaymentMethod? paidVia,
            string? note,
            int paidByUserId)
        {
            var settlement = CleanerPayoutSettlement.Resolve(record.IsPaid, record.PaidAmount, payout);
            if (settlement.IsSettled) return null;

            var amount = settlement.Outstanding;

            record.IsPaid = true;
            record.PaidAmount = OrderPricingCalculator.Round2(settlement.PaidAmount + amount);
            record.PaidVia = paidVia ?? record.PaidVia;
            record.PaidAt = DateTime.UtcNow;
            record.PaidByUserId = paidByUserId;
            if (!string.IsNullOrWhiteSpace(note)) record.PaymentNote = note.Trim();
            return amount;
        }

        /// <summary>
        /// Records a payout against one cleaner — the first one, or a TOP-UP when the line is
        /// worth more now than what was handed over (2026-09). Returns the amount recorded NOW,
        /// or null when the line was already covered and nothing happened.
        ///
        /// Three details are load-bearing on the top-up path:
        ///
        /// * <see cref="OrderCleaner.PaidAmount"/> is ADDED to, never replaced. It reads as the
        ///   running total this person has had for this order, so the shortfall can still be
        ///   computed after the top-up and the first payment is not lost. Each payment is its own
        ///   audit row, which is where the two events stay separable.
        /// * The amount recorded is the SHORTFALL, not the whole payout — paying $84.00 again on
        ///   a line already paid $73.50 would double-count $73.50 of real money.
        /// * A blank note or method leaves the existing one in place rather than wiping the
        ///   record of the first payment.
        ///
        /// A never-paid line with a $0 payout is still marked paid: "nothing was owed and that
        /// has been checked off" is a decision worth recording, which is why the guard is
        /// "already settled" rather than "amount is zero".
        /// </summary>
        private static decimal? MarkPaid(
            OrderCleaner assignment,
            CleanerPayrollCalculator.CleanerLine? line,
            CleanerPaymentMethod? paidVia,
            string? note,
            int paidByUserId)
        {
            // Salary + tips: the cleaner is handed one figure, and that is what we record.
            var payout = line?.Payout ?? 0m;
            var settlement = CleanerPayoutSettlement.Resolve(assignment.IsPaid, assignment.PaidAmount, payout);
            if (settlement.IsSettled) return null;

            var amount = settlement.Outstanding;

            assignment.IsPaid = true;
            assignment.PaidAmount = OrderPricingCalculator.Round2(settlement.PaidAmount + amount);
            assignment.PaidVia = paidVia ?? assignment.PaidVia ?? assignment.Cleaner?.PaymentMethod;
            assignment.PaidAt = DateTime.UtcNow;
            assignment.PaidByUserId = paidByUserId;
            if (!string.IsNullOrWhiteSpace(note)) assignment.PaymentNote = note.Trim();
            return amount;
        }

        private IQueryable<Order> PerformedOrdersWithIncludes() =>
            _context.Orders
                .Include(o => o.ServiceType)
                .Include(o => o.OrderServices)
                    .ThenInclude(os => os.Service)
                .Include(o => o.OrderExtraServices)
                    .ThenInclude(oes => oes.ExtraService)
                .Include(o => o.OrderCleaners)
                    .ThenInclude(oc => oc.Cleaner)
                .Include(o => o.OrderCleaners)
                    .ThenInclude(oc => oc.PaidByUser)
                .Include(o => o.UnassignedPayouts)
                    .ThenInclude(p => p.PaidByUser)
                // DONE, and not refunded in any amount. Anything cancelled, fully refunded or
                // part-refunded is out (owner's call, 2026-08): the page is a list of finished
                // jobs to settle, and an order whose money went back is a conversation, not a
                // routine payout.
                //
                // The consequence is deliberate and worth knowing: a cleaner who worked a job that
                // was later part-refunded will not appear here, so that payout has to be handled
                // outside the page. Widening this back out is a one-line change if that bites.
                .Where(o => o.Status == OrderStatuses.Done && o.TotalRefundedAmount <= 0);

        private async Task<List<Order>> LoadPerformedOrdersAsync(OutgoingPaymentQuery query)
        {
            var q = PerformedOrdersWithIncludes();

            if (query.From.HasValue)
                q = q.Where(o => o.ServiceDate >= query.From.Value.Date);

            if (query.To.HasValue)
            {
                var toInclusive = query.To.Value.Date.AddDays(1);
                q = q.Where(o => o.ServiceDate < toInclusive);
            }

            if (!string.IsNullOrWhiteSpace(query.Search))
            {
                var term = query.Search.Trim();
                // Order number, customer name, or cleaner name — the three things somebody
                // reconciling a payout actually has in front of them. The order number is
                // matched by EQUALITY on a parsed int, not by string-contains: "5" must not drag
                // in every order from 50 to 599.
                var orderId = int.TryParse(term, out var parsedId) ? parsedId : (int?)null;

                q = q.Where(o =>
                    (orderId != null && o.Id == orderId) ||
                    o.ContactFirstName.Contains(term) ||
                    o.ContactLastName.Contains(term) ||
                    o.OrderCleaners.Any(oc =>
                        oc.Cleaner.FirstName.Contains(term) || oc.Cleaner.LastName.Contains(term)));
            }

            if (query.CleanerId.HasValue)
                q = q.Where(o => o.OrderCleaners.Any(oc => oc.CleanerId == query.CleanerId.Value));

            return await q
                .OrderByDescending(o => o.ServiceDate)
                .ThenByDescending(o => o.Id)
                .AsSplitQuery()
                .ToListAsync();
        }

        // Single-sourced so this page and the cleaner assignment mail cannot answer
        // "is TotalDuration already per-cleaner?" differently — that disagreement is a 2×
        // error in the hours a cleaner is quoted against the hours this page pays them.
        private static bool HasCleanerService(Order order) =>
            CleanerPayrollCalculator.HasCleanerHoursService(order);

        private static CleanerPayrollCalculator.Result BuildPayroll(Order order) =>
            CleanerPayrollCalculator.Build(order, HasCleanerService(order), order.OrderCleaners);

        private static void ApplyOrderTotalSalary(Order order) =>
            CleanerPayrollCalculator.ApplyOrderTotalSalary(
                order, HasCleanerService(order), order.OrderCleaners);

        /// <summary>
        /// The expected rate for this order, from the shared calculator: the effective service-type
        /// name (a custom order's own label) plus whether the deep-cleaning extra is on it. This is
        /// the yardstick the "rate differs from default" warning measures against.
        /// </summary>
        private static decimal ResolveExpectedHourlyRate(Order order)
        {
            var name = order.GetDisplayServiceTypeName();
            return OrderPricingCalculator.GetDefaultCleanerHourlyRate(
                HasDeepCleaningExtra(order) ? 1m : 0m, name);
        }

        /// <summary>
        /// Whether the deep-cleaning extra is on the order. Feeds two things: the expected hourly
        /// rate, and the Deep/Regular split in the table's short service-type label — which must
        /// agree, or the row would say "Regular" beside a rate warning that assumed Deep.
        /// "Super Deep" contains "deep cleaning" too and is meant to match.
        /// </summary>
        private static bool HasDeepCleaningExtra(Order order) =>
            order.OrderExtraServices.Any(oes =>
                oes.ExtraService != null &&
                oes.ExtraService.Name.Contains("deep cleaning", StringComparison.OrdinalIgnoreCase));

        private static OutgoingPaymentOrderDto BuildOrderRow(Order order)
        {
            var payroll = BuildPayroll(order);
            var expectedRate = ResolveExpectedHourlyRate(order);
            var assignmentsById = order.OrderCleaners.ToDictionary(oc => oc.Id);

            var cleaners = payroll.Lines.Select(line =>
            {
                var assignment = assignmentsById[line.OrderCleanerId];
                var cleaner = assignment.Cleaner;
                var settlement = CleanerPayoutSettlement.Resolve(
                    assignment.IsPaid, assignment.PaidAmount, line.Payout);

                return new OutgoingPaymentCleanerDto
                {
                    OrderCleanerId = line.OrderCleanerId,
                    CleanerId = line.CleanerId,
                    FirstName = cleaner?.FirstName ?? string.Empty,
                    LastName = cleaner?.LastName ?? string.Empty,
                    PaymentMethod = cleaner?.PaymentMethod,
                    PaymentDetails = cleaner?.PaymentDetails,
                    BillableMinutes = line.BillableMinutes,
                    HoursOverridden = line.HoursOverridden,
                    HourlyRate = line.HourlyRate,
                    RateOverridden = line.RateOverridden,
                    RateDiffersFromDefault = line.HourlyRate != expectedRate,
                    Salary = line.Salary,
                    Tips = line.Tips,
                    Payout = line.Payout,
                    IsPaid = assignment.IsPaid,
                    PaidAmount = assignment.PaidAmount,
                    PaidVia = assignment.PaidVia,
                    PaidAt = assignment.PaidAt,
                    PaidByName = assignment.PaidByUser != null
                        ? $"{assignment.PaidByUser.FirstName} {assignment.PaidByUser.LastName}".Trim()
                        : null,
                    PaymentNote = assignment.PaymentNote,
                    OutstandingPayout = settlement.Outstanding,
                    OverpaidAmount = settlement.Overpaid,
                    IsSettled = settlement.IsSettled,
                    IsTopUp = settlement.IsTopUp
                };
            }).ToList();

            // One reported line per staffing slot nobody is assigned to. The figures are real —
            // somebody worked those hours — so they are shown and counted; they simply cannot be
            // paid, because there is no person on file to pay.
            var slotRecords = order.UnassignedPayouts.ToDictionary(p => p.SlotIndex);

            var unassigned = new List<OutgoingPaymentCleanerDto>(payroll.UnassignedCount);
            for (var i = 0; i < payroll.UnassignedCount; i++)
            {
                var tipShare = payroll.UnassignedCount == 0
                    ? 0m
                    : OrderPricingCalculator.Round2(payroll.UnassignedTips / payroll.UnassignedCount);

                // A slot only has a row once somebody has acted on it; until then it is unpaid.
                slotRecords.TryGetValue(i, out var record);

                var slotPayout = OrderPricingCalculator.Round2(payroll.UnassignedSalaryEach + tipShare);
                var slotSettlement = CleanerPayoutSettlement.Resolve(
                    record?.IsPaid ?? false, record?.PaidAmount, slotPayout);

                unassigned.Add(new OutgoingPaymentCleanerDto
                {
                    // SlotIndex travels in CleanerId's place — it is the id the pay/unpay
                    // endpoints address this line by. OrderCleanerId stays 0: there is no
                    // assignment row, and anything keying off it must not mistake this for one.
                    OrderCleanerId = 0,
                    CleanerId = i,
                    SlotIndex = i,
                    IsUnassigned = true,
                    FirstName = "Unassigned cleaner",
                    LastName = string.Empty,
                    BillableMinutes = payroll.AutomaticBillableMinutes,
                    HourlyRate = order.CleanerHourlyRate,
                    RateDiffersFromDefault = order.CleanerHourlyRate != expectedRate,
                    Salary = payroll.UnassignedSalaryEach,
                    Tips = tipShare,
                    Payout = slotPayout,
                    IsPaid = record?.IsPaid ?? false,
                    PaidAmount = record?.PaidAmount,
                    PaidVia = record?.PaidVia,
                    PaidAt = record?.PaidAt,
                    PaidByName = record?.PaidByUser != null
                        ? $"{record.PaidByUser.FirstName} {record.PaidByUser.LastName}".Trim()
                        : null,
                    PaymentNote = record?.PaymentNote,
                    OutstandingPayout = slotSettlement.Outstanding,
                    OverpaidAmount = slotSettlement.Overpaid,
                    IsSettled = slotSettlement.IsSettled,
                    IsTopUp = slotSettlement.IsTopUp
                });
            }

            var totalSalary = payroll.TotalSalary;

            // Wages + the customer's tips, ALWAYS — including the unassigned slots. An order
            // nobody was assigned to still cost the company money, and reporting its payout as
            // $0 in the list made it look like there was nothing to settle.
            var totalPayout = OrderPricingCalculator.Round2(totalSalary + order.Tips);

            var row = new OutgoingPaymentOrderDto
            {
                OrderId = order.Id,
                ServiceTypeName = order.GetDisplayServiceTypeName("Cleaning"),
                IsCustomServiceType = order.ServiceType?.IsCustom == true,
                RawServiceTypeName = order.ServiceType?.Name ?? string.Empty,
                CustomServiceDisplayName = order.CustomServiceDisplayName,
                IsDeepCleaning = HasDeepCleaningExtra(order),
                ServiceDate = order.ServiceDate,
                ServiceTime = order.ServiceTime.ToString(@"hh\:mm"),
                Status = order.Status,
                PaymentMethod = order.PaymentMethod.ToString(),
                IsPaidByCustomer = order.IsPaid || order.PaymentMethod != Models.PaymentMethod.Normal,
                CustomerName = $"{order.ContactFirstName} {order.ContactLastName}".Trim(),
                ServiceAddress = order.ServiceAddress,
                City = order.City,

                SubTotal = order.SubTotal,
                Tax = order.Tax,
                // "Current total (no tips)": what the customer paid for the cleaning itself. Tips
                // are listed separately because they are the cleaner's money, not the company's.
                TotalWithoutTips = OrderPricingCalculator.Round2(order.Total - order.Tips),
                Tips = order.Tips,
                Total = order.Total,

                TotalDuration = order.TotalDuration,
                AutomaticMinutesPerCleaner = payroll.AutomaticBillableMinutes,
                MaidsCount = order.MaidsCount,
                SplitCount = payroll.SplitCount,
                OrderHourlyRate = order.CleanerHourlyRate,
                ExpectedHourlyRate = expectedRate,
                TotalSalary = totalSalary,
                TotalPayout = totalPayout,
                Cleaners = cleaners,
                UnassignedCleaners = unassigned
            };

            // Paid state spans BOTH lists. An unassigned slot is a real payout that can be
            // recorded, so an order is only "Paid" once every line — named or not — is settled.
            //
            // SETTLED, not merely paid: a line whose hours grew after the money went out is
            // short, and an order carrying a shortfall must stop reading "Paid" or the money
            // still owed is invisible on every screen (2026-09). That is why these read
            // IsSettled rather than IsPaid — see Helpers/CleanerPayoutSettlement.
            var allLines = cleaners.Concat(unassigned).ToList();
            row.IsFullyPaid = allLines.Count > 0 && allLines.All(c => c.IsSettled);
            row.IsPartiallyPaid = allLines.Any(c => c.IsPaid) && allLines.Any(c => !c.IsSettled);

            row.OutstandingPayout = OrderPricingCalculator.Round2(allLines.Sum(c => c.OutstandingPayout));
            // The already-paid half of it, kept separate so the panel can say "$10.50 still to
            // pay" on an edited order without ever using that wording on a plainly unpaid one.
            row.TopUpPayout = OrderPricingCalculator.Round2(
                allLines.Where(c => c.IsTopUp).Sum(c => c.OutstandingPayout));
            row.OverpaidAmount = OrderPricingCalculator.Round2(allLines.Sum(c => c.OverpaidAmount));

            row.Warnings = BuildWarnings(order, row, expectedRate);
            return row;
        }

        /// <summary>
        /// Everything a SuperAdmin should look at before paying. Built by the SHARED
        /// <see cref="OrderStaffingWarnings"/> since 2026-08-31, when the admin Orders panel grew
        /// the same block — the two screens describe the same job, so they must not be free to
        /// describe it differently. Nothing here ever blocks a payout.
        /// </summary>
        private static List<string> BuildWarnings(Order order, OutgoingPaymentOrderDto row, decimal expectedRate)
            => OrderStaffingWarnings.Build(new OrderStaffingWarnings.Input
            {
                ServiceTypeName = row.ServiceTypeName,
                ExpectedHourlyRate = expectedRate,
                AssignedHourlyRates = row.Cleaners.Select(c => c.HourlyRate).ToList(),
                SplitCount = row.SplitCount,
                UnassignedCount = row.UnassignedCleaners.Count,
                TotalSalary = row.TotalSalary,
                // The first slot's figure: every unstaffed slot is owed the same hours at the
                // same rate, so they are interchangeable. 0 when there are none, which is the
                // branch that never reads it.
                UnassignedPayoutEach = row.UnassignedCleaners.Count > 0 ? row.UnassignedCleaners[0].Payout : 0m,
                MaidsCount = order.MaidsCount,
                TotalDuration = order.TotalDuration,
                IsPaidByCustomer = row.IsPaidByCustomer
            });

        private static OutgoingPaymentSummaryDto BuildSummary(List<OutgoingPaymentOrderDto> rows)
        {
            // Unassigned staffing slots are included: the money is owed whether or not there is a
            // name on file, and a header that quietly omitted it would under-report what has to
            // go out. They are never paid, so they always land in the unpaid bucket.
            var lines = rows.SelectMany(r => r.Cleaners.Concat(r.UnassignedCleaners)).ToList();

            return new OutgoingPaymentSummaryDto
            {
                OrderCount = rows.Count,
                CleanerLineCount = lines.Count,
                TotalSalary = OrderPricingCalculator.Round2(lines.Sum(l => l.Salary)),
                TotalTips = OrderPricingCalculator.Round2(lines.Sum(l => l.Tips)),
                TotalPayout = OrderPricingCalculator.Round2(lines.Sum(l => l.Payout)),
                // Paid lines report what was FROZEN at pay time, not what they would compute to
                // now — that is the whole reason PaidAmount is stored.
                PaidPayout = OrderPricingCalculator.Round2(
                    lines.Where(l => l.IsPaid).Sum(l => l.PaidAmount ?? l.Payout)),
                // Every line's OWN outstanding figure, summed — the full payout on an unpaid
                // line, the shortfall on one whose hours grew after payment. Summing only the
                // unpaid lines (as this did until 2026-09) hid a top-up from the one header
                // figure that is supposed to say how much money has to go out.
                UnpaidPayout = OrderPricingCalculator.Round2(lines.Sum(l => l.OutstandingPayout)),
                TopUpPayout = OrderPricingCalculator.Round2(
                    lines.Where(l => l.IsTopUp).Sum(l => l.OutstandingPayout)),
                UnpaidCleanerCount = lines.Count(l => l.OutstandingPayout > 0m),
                OrdersWithWarnings = rows.Count(r => r.Warnings.Count > 0)
            };
        }
    }

    public enum OutgoingPaymentPaidStatus
    {
        All = 0,
        Unpaid = 1,
        Paid = 2
    }

    public class OutgoingPaymentQuery
    {
        public DateTime? From { get; set; }
        public DateTime? To { get; set; }
        public OutgoingPaymentPaidStatus PaidStatus { get; set; } = OutgoingPaymentPaidStatus.All;
        public bool WarningsOnly { get; set; }
        public string? Search { get; set; }
        public int? CleanerId { get; set; }
        public int Page { get; set; } = 1;
        public int PageSize { get; set; } = 20;
    }

    public interface IOutgoingPaymentService
    {
        Task<OutgoingPaymentListDto> GetAsync(OutgoingPaymentQuery query);
        Task<OutgoingPaymentOrderDto?> GetOrderAsync(int orderId);
        Task<OutgoingPaymentOrderDto?> UpdateCleanerPayrollAsync(int orderId, int orderCleanerId, UpdateCleanerPayrollDto dto);
        Task<OutgoingPaymentOrderDto?> UpdateOrderHourlyRateAsync(int orderId, decimal hourlyRate);
        Task<OutgoingPaymentOrderDto?> MarkCleanerPaidAsync(int orderId, int orderCleanerId, MarkCleanerPaidDto dto, int paidByUserId);
        Task<OutgoingPaymentOrderDto?> MarkOrderPaidAsync(int orderId, MarkOrderPaidDto dto, int paidByUserId);
        Task<OutgoingPaymentOrderDto?> UndoCleanerPaymentAsync(int orderId, int orderCleanerId);
        Task<OutgoingPaymentOrderDto?> MarkUnassignedSlotPaidAsync(int orderId, int slotIndex, MarkCleanerPaidDto dto, int paidByUserId);
        Task<OutgoingPaymentOrderDto?> UndoUnassignedSlotPaymentAsync(int orderId, int slotIndex);
    }
}
