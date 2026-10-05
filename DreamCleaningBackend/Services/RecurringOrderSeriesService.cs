using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Helpers.Recurring;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services.Commercial;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace DreamCleaningBackend.Services
{
    /// <summary>A rule an admin can act on. Mapped to 400 by the controller.</summary>
    public class RecurringSeriesException : Exception
    {
        public RecurringSeriesException(string message) : base(message) { }
    }

    public interface IRecurringOrderSeriesService
    {
        Task<RecurringSeriesDto> CreateAsync(int templateOrderId, SaveRecurringSeriesDto dto, int adminUserId);
        Task<RecurringSeriesDto> UpdateAsync(int seriesId, SaveRecurringSeriesDto dto, int adminUserId);
        Task<RecurringSeriesDto> SetStateAsync(int seriesId, string action, int adminUserId);
        Task SkipAsync(int seriesId, int orderId, int adminUserId);
        Task PrepareDeleteAsync(Order order);
        Task<RecurringPricePreviewDto> PreviewAsync(int sourceOrderId, SaveRecurringSeriesDto dto);
        Task<RecurringSeriesDto?> GetAsync(int seriesId);
        Task<RecurringSeriesDto?> GetForOrderAsync(int orderId);
        Task<List<RecurringSeriesDto>> ListAsync(bool includeInactive);

        /// <summary>The executed contracts a plan built from this order may be linked to.</summary>
        Task<RecurringContractOptionsDto> GetContractOptionsAsync(int orderId);

        /// <summary>Fills the horizon for ONE series. Idempotent.</summary>
        Task<RecurringGenerationResultDto> GenerateAsync(int seriesId, int? actingUserId = null);

        /// <summary>Fills the horizon for every active series. What the background sweep calls.</summary>
        Task<int> GenerateAllAsync(CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// RECURRING CLEANINGS: the schedule, and the rolling generation of real orders from it.
    ///
    /// ══ WHAT AN OCCURRENCE IS ══
    ///
    /// An ordinary <see cref="Order"/>. It is created through <see cref="IBookingCreationService"/>
    /// — the single order-creation path — so it is priced by the shared calculator, gets the
    /// customer's live discounts through the ordinary engine, lands in Admin Orders and in My
    /// Orders, and can be edited, assigned, refunded and invoiced like anything else. Nothing about
    /// a recurring order is special except that a series decided its date.
    ///
    /// ══ WHAT IT COPIES ══
    ///
    /// The template order's BOOKING CONFIGURATION, through
    /// <see cref="OrderBookingSnapshot.ToBookingDto"/> — the same mapping the admin "recreate
    /// order" flow uses, so the two cannot drift. That helper's own comment carries the full
    /// copy/don't-copy list; the short version is that nothing transactional travels: no Stripe
    /// intent, no payment reference, no paid state, no history, no photos, no completion
    /// timestamps, no payouts, no cleaner-notification state, no audit events, and no discount
    /// slot. Tips DO travel — a customer who tips £20 a visit on a standing arrangement means it.
    ///
    /// <b>Discounts are re-derived, never repeated.</b> A consumed one-time loyalty discount is
    /// simply gone from the customer's account by the time the next occurrence is priced, so the
    /// ordinary engine gives the right answer with no special case. A LIFETIME loyalty discount is
    /// still on the account, so it keeps applying — which is exactly what "lifetime" means.
    ///
    /// ══ IDEMPOTENCE ══
    ///
    /// The unique index on (RecurringSeriesId, RecurrenceOccurrenceDate) is the guarantee, not the
    /// pre-check below it. Two sweeps running at once, a restart mid-pass, an admin double-clicking
    /// "generate now" — all of them collide at the database, and the loser skips that date. The
    /// pre-check exists so the normal case does no wasted work, not so the rule holds.
    ///
    /// ══ CLEANERS ══
    ///
    /// <b>NOTHING HERE EVER NOTIFIES A CLEANER.</b> With
    /// <see cref="RecurringOrderSeries.CopyCleanerAssignments"/> on, the template's assignments are
    /// copied onto each occurrence as <see cref="OrderCleaner"/> rows with
    /// <c>AssignmentNotificationSentAt</c> null and <c>AutoAssignedFromSeriesId</c> stamped. The
    /// existing "Send assignment email" / "Resend" controls remain the only thing that mails or
    /// texts anybody, and the panel says so on the badge. No second notification workflow exists
    /// and none may be added here.
    /// </summary>
    public class RecurringOrderSeriesService : IRecurringOrderSeriesService
    {
        private readonly ApplicationDbContext _context;
        private readonly IBookingCreationService _bookingCreation;
        private readonly IAuditService _audit;
        private readonly IRecurringCustomerPaymentService _payments;
        private readonly ILogger<RecurringOrderSeriesService> _logger;

        public RecurringOrderSeriesService(
            ApplicationDbContext context,
            IBookingCreationService bookingCreation,
            IAuditService audit,
            ILogger<RecurringOrderSeriesService> logger,
            IRecurringCustomerPaymentService payments)
        {
            _context = context;
            _bookingCreation = bookingCreation;
            _audit = audit;
            _logger = logger;
            _payments = payments;
        }

        // ── Create / update ───────────────────────────────────────────────────────────────────

        public async Task<RecurringSeriesDto> CreateAsync(
            int templateOrderId, SaveRecurringSeriesDto dto, int adminUserId)
        {
            var validation = RecurrenceCalculator.Validate(dto.IntervalUnit, dto.IntervalValue)
                ?? RecurrenceCalculator.ValidatePattern(dto.IntervalUnit, dto.ServiceDaysOfWeek,
                    dto.ServiceDaysOfMonth, dto.UpcomingOccurrenceTarget);
            if (validation != null) throw new RecurringSeriesException(validation);

            ValidateDiscount(dto);
            using var transaction = _context.Database.IsRelational()
                ? await _context.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable) : null;
            var order = await _context.Orders
                .Include(o => o.ServiceType)
                .FirstOrDefaultAsync(o => o.Id == templateOrderId)
                ?? throw new RecurringSeriesException("Order not found.");

            int? previousSeriesId = order.RecurringSeriesId;
            if (previousSeriesId.HasValue)
            {
                var previous = await _context.RecurringOrderSeries.FindAsync(previousSeriesId.Value);
                if (previous?.StoppedAt == null || previous.TemplateOrderId != order.Id || order.IsGeneratedByRecurringSeries)
                    throw new RecurringSeriesException(
                        "This order is already part of a recurring series. Edit that series instead.");

                var now = NyTimeHelper.NowNy;
                if (await _context.Orders.AnyAsync(o => o.RecurringSeriesId == previous.Id
                    && o.IsGeneratedByRecurringSeries
                    && (o.ServiceDate > now.Date || (o.ServiceDate == now.Date && o.ServiceTime > now.TimeOfDay))
                    && o.Status != OrderStatuses.Cancelled && o.Status != OrderStatuses.Refunded))
                    throw new RecurringSeriesException(
                        "Remove or cancel the previous plan's remaining future cleanings before starting a new plan from this order.");
                // Preserve the stopped schedule and its historical occurrences. Only the source
                // order's current-plan link moves when the replacement has been saved.
            }

            if (OrderStatuses.IsCancelled(order.Status))
                throw new RecurringSeriesException(
                    "A cancelled order cannot be used as the template for a recurring series.");

            var anchor = (dto.AnchorDate ?? order.ServiceDate).Date;

            if (dto.EndDate.HasValue && dto.EndDate.Value.Date < anchor)
                throw new RecurringSeriesException("The end date is before the first service date.");

            if (dto.ServiceTime.HasValue && (dto.ServiceTime < TimeSpan.Zero || dto.ServiceTime >= TimeSpan.FromDays(1)))
                throw new RecurringSeriesException("Choose a valid service time.");

            var contract = await ResolveContractAsync(order, dto.ContractId);

            var series = new RecurringOrderSeries
            {
                UserId = order.UserId,
                TemplateOrderId = order.Id,
                IntervalValue = dto.IntervalValue,
                IntervalUnit = dto.IntervalUnit,
                ServiceDaysOfWeek = RecurrenceCalculator.FormatDaysOfWeek(dto.ServiceDaysOfWeek?.Select(d => (DayOfWeek)d)),
                ServiceDaysOfMonth = RecurrenceCalculator.FormatDaysOfMonth(dto.ServiceDaysOfMonth),
                UpcomingOccurrenceTarget = dto.UpcomingOccurrenceTarget,
                ContractId = contract?.ContractId,
                AnchorDate = anchor,
                ServiceTime = dto.ServiceTime ?? order.ServiceTime,
                EndDate = dto.EndDate?.Date,
                IsActive = dto.IsActive,
                CopyCleanerAssignments = dto.CopyCleanerAssignments,
                // A weekly flat fee is billed by the contract's weekly invoice. A per-visit payment
                // request would ask the customer for money the invoice already covers.
                AutoRequestPayment = contract?.IsWeeklyFlatFee != true && (dto.AutoRequestPayment ?? true),
                RecurringLoyaltyDiscountPercent = dto.RecurringLoyaltyDiscountPercent,
                RecurringLoyaltyDiscountAmount = dto.RecurringLoyaltyDiscountAmount,
                Notes = string.IsNullOrWhiteSpace(dto.Notes) ? null : dto.Notes.Trim(),
                CreatedByUserId = adminUserId,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            _context.RecurringOrderSeries.Add(series);
            await _context.SaveChangesAsync();

            // The template joins its own series so the panel can show the schedule from the order
            // the admin set it up on. It is NOT marked as generated — a person booked it.
            order.RecurringSeriesId = series.Id;
            // The slot the template fills is its OWN service date. It used to be the anchor, which
            // is the same date unless the admin typed a different First cleaning — and then the
            // template silently claimed that first visit, so it was never generated (2026-10).
            order.RecurrenceOccurrenceDate = order.ServiceDate.Date;
            order.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            await _audit.LogActionAsync(
                AuditEntityTypes.RecurringOrderSeriesAction, series.Id, "RecurringSeriesCreated",
                oldValues: null,
                newValues: new
                {
                    SeriesId = series.Id,
                    PreviousSeriesId = previousSeriesId,
                    TemplateOrderId = order.Id,
                    CustomerUserId = order.UserId,
                    Recurrence = RecurrenceCalculator.Describe(RecurrenceRule.From(series)),
                    series.UpcomingOccurrenceTarget,
                    series.ContractId,
                    FirstServiceDate = anchor,
                    series.EndDate,
                    CopiesCleanerAssignments = series.CopyCleanerAssignments,
                    CleanersAreNotNotifiedAutomatically = true,
                    series.AutoRequestPayment,
                    series.RecurringLoyaltyDiscountPercent,
                    series.RecurringLoyaltyDiscountAmount
                },
                actingUserId: adminUserId);

            // The series and template link are required initial state. Build the response before
            // committing, so a failed initial write/read rolls the whole operation back.
            var response = await GetAsync(series.Id)
                ?? throw new RecurringSeriesException("Could not read the new recurring series.");
            if (transaction != null) { await transaction.CommitAsync(); await transaction.DisposeAsync(); }
            try
            {
                var generated = await GenerateAsync(series.Id, adminUserId);
                response = await GetAsync(series.Id) ?? response;
                response.GenerationWarnings = generated.Warnings;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Series {SeriesId} was created but initial generation needs retry", series.Id);
                response.GenerationWarnings.Add("The schedule was saved, but generation could not finish. Use Generate now to retry.");
            }
            return response;
        }

        public async Task<RecurringSeriesDto> UpdateAsync(
            int seriesId, SaveRecurringSeriesDto dto, int adminUserId)
        {
            var validation = RecurrenceCalculator.Validate(dto.IntervalUnit, dto.IntervalValue)
                ?? RecurrenceCalculator.ValidatePattern(dto.IntervalUnit, dto.ServiceDaysOfWeek,
                    dto.ServiceDaysOfMonth, dto.UpcomingOccurrenceTarget);
            if (validation != null) throw new RecurringSeriesException(validation);

            var series = await _context.RecurringOrderSeries
                .FirstOrDefaultAsync(s => s.Id == seriesId)
                ?? throw new RecurringSeriesException("Recurring series not found.");

            ValidateDiscount(dto);
            var before = Snapshot(series);

            if (series.StoppedAt.HasValue)
                throw new RecurringSeriesException("This recurring series has been stopped permanently.");

            // Once a plan keeps a COUNT it cannot quietly fall back to the legacy 30-day window —
            // a blank box on the form would otherwise change how much the plan generates.
            if (dto.UpcomingOccurrenceTarget == null && series.UpcomingOccurrenceTarget != null)
                throw new RecurringSeriesException("Choose how many upcoming cleanings to generate.");

            // The contract is re-validated only when it CHANGES: a plan linked to a contract that
            // has since completed must still be editable for its time or its cleaners.
            var contractChanged = dto.ContractId != series.ContractId;
            ContractBillingProfile? contract;
            if (contractChanged)
            {
                var template = await _context.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == series.TemplateOrderId)
                    ?? throw new RecurringSeriesException("The order this plan was built from no longer exists.");
                contract = await ResolveContractAsync(template, dto.ContractId);
            }
            else
            {
                contract = series.ContractId.HasValue
                    ? await ContractBillingProfile.LoadAsync(_context, series.ContractId.Value)
                    : null;
            }

            var daysOfWeek = RecurrenceCalculator.FormatDaysOfWeek(dto.ServiceDaysOfWeek?.Select(d => (DayOfWeek)d));
            var daysOfMonth = RecurrenceCalculator.FormatDaysOfMonth(dto.ServiceDaysOfMonth);

            var anchor = (dto.AnchorDate ?? series.AnchorDate).Date;
            if (dto.EndDate.HasValue && dto.EndDate.Value.Date < anchor)
                throw new RecurringSeriesException("The end date is before the first service date.");

            var serviceTime = dto.ServiceTime ?? series.ServiceTime;
            if (serviceTime < TimeSpan.Zero || serviceTime >= TimeSpan.FromDays(1))
                throw new RecurringSeriesException("Choose a valid service time.");
            var ruleChanged = series.IntervalValue != dto.IntervalValue
                || series.IntervalUnit != dto.IntervalUnit || series.AnchorDate != anchor
                || series.ServiceDaysOfWeek != daysOfWeek || series.ServiceDaysOfMonth != daysOfMonth
                || series.ServiceTime != serviceTime || series.EndDate != dto.EndDate?.Date
                || (series.RecurringLoyaltyDiscountPercent ?? 0) != (dto.RecurringLoyaltyDiscountPercent ?? 0)
                || (series.RecurringLoyaltyDiscountAmount ?? 0) != (dto.RecurringLoyaltyDiscountAmount ?? 0);
            var now = NyTimeHelper.NowNy;
            using var transaction = await RecurringPaymentAttemptGuard.LockAsync(_context, series.UserId);
            var future = await _context.Orders.Where(o => o.RecurringSeriesId == series.Id
                && o.IsGeneratedByRecurringSeries && o.ServiceDate >= now.Date
                && o.Status != OrderStatuses.Cancelled && o.Status != OrderStatuses.Refunded).ToListAsync();
            future = future.Where(o => o.ServiceDate.Date.Add(o.ServiceTime) > now).ToList();
            if (ruleChanged && future.Count > 0 && dto.FutureOrdersAction is not ("Keep" or "Regenerate"))
                throw new RecurringSeriesException("Choose Keep existing generated future orders or Regenerate eligible future schedule.");

            if (ruleChanged && future.Count > 0)
            {
                if (dto.FutureOrdersAction == "Keep")
                    series.GenerateAfterDate = future.Max(o => o.ServiceDate.Date > o.RecurrenceOccurrenceDate
                        ? o.ServiceDate.Date : o.RecurrenceOccurrenceDate ?? o.ServiceDate.Date);
                else
                {
                    // Keep paid, staffed/committed and billed orders. Retire only safe occurrences;
                    // release their date key so the new rule can replace a visit on the same day.
                    foreach (var occurrence in future)
                    {
                        if (await UnsafeToRetireAsync(occurrence, now) != null) continue;
                        await RetireAsync(occurrence, adminUserId, replacing: true);
                    }
                    // Any protected bookings remain the boundary for the new rule.
                    var retained = future.Where(o => !OrderStatuses.IsCancelled(o.Status)).ToList();
                    series.GenerateAfterDate = retained.Count == 0 ? null : retained.Max(o => o.ServiceDate.Date);
                }
            }

            series.IntervalValue = dto.IntervalValue;
            series.IntervalUnit = dto.IntervalUnit;
            series.ServiceDaysOfWeek = daysOfWeek;
            series.ServiceDaysOfMonth = daysOfMonth;
            series.UpcomingOccurrenceTarget = dto.UpcomingOccurrenceTarget;
            series.ContractId = contract?.ContractId;
            series.AnchorDate = anchor;
            series.ServiceTime = serviceTime;
            series.EndDate = dto.EndDate?.Date;
            series.IsActive = dto.IsActive;
            series.CopyCleanerAssignments = dto.CopyCleanerAssignments;
            series.AutoRequestPayment = contract?.IsWeeklyFlatFee != true
                && (dto.AutoRequestPayment ?? series.AutoRequestPayment);
            series.RecurringLoyaltyDiscountPercent = dto.RecurringLoyaltyDiscountPercent;
            series.RecurringLoyaltyDiscountAmount = dto.RecurringLoyaltyDiscountAmount;
            series.Notes = string.IsNullOrWhiteSpace(dto.Notes) ? null : dto.Notes.Trim();
            series.UpdatedByUserId = adminUserId;
            series.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            await _audit.LogActionAsync(
                AuditEntityTypes.RecurringOrderSeriesAction, series.Id, "RecurringSeriesUpdated",
                before, Snapshot(series), actingUserId: adminUserId);
            await _audit.LogActionAsync(AuditEntityTypes.RecurringOrderSeriesAction, series.Id,
                "RecurringScheduleChoice", null, new { dto.FutureOrdersAction, series.GenerateAfterDate }, actingUserId: adminUserId);
            if (transaction != null) { await transaction.CommitAsync(); await transaction.DisposeAsync(); }

            // Fill only the schedule allowed by the explicit choice and retained-order boundary.
            if (series.IsActive) await GenerateAsync(series.Id, adminUserId);

            return (await GetAsync(series.Id))!;
        }

        // ── Generation ────────────────────────────────────────────────────────────────────────

        public async Task<RecurringSeriesDto> SetStateAsync(int seriesId, string action, int adminUserId)
        {
            var series = await _context.RecurringOrderSeries.FindAsync(seriesId)
                ?? throw new RecurringSeriesException("Recurring series not found.");
            if (action is not ("pause" or "resume" or "stop"))
                throw new RecurringSeriesException("Unknown recurrence action.");
            if (series.StoppedAt.HasValue && action != "stop")
                throw new RecurringSeriesException("A stopped series cannot be resumed or edited.");
            var before = Snapshot(series);
            series.IsActive = action == "resume";
            if (action == "stop") series.StoppedAt ??= DateTime.UtcNow;
            series.UpdatedAt = DateTime.UtcNow;
            series.UpdatedByUserId = adminUserId;
            await _context.SaveChangesAsync();
            await _audit.LogActionAsync(AuditEntityTypes.RecurringOrderSeriesAction, seriesId,
                "RecurringSeries" + action, before, Snapshot(series), actingUserId: adminUserId);
            if (series.IsActive) await GenerateAsync(seriesId, adminUserId);
            return (await GetAsync(seriesId))!;
        }

        public async Task SkipAsync(int seriesId, int orderId, int adminUserId)
        {
            var order = await _context.Orders.FirstOrDefaultAsync(o => o.Id == orderId && o.RecurringSeriesId == seriesId)
                ?? throw new RecurringSeriesException("Recurring occurrence not found.");
            using var transaction = await RecurringPaymentAttemptGuard.LockAsync(_context, order.UserId);
            await _context.Entry(order).ReloadAsync();
            var reason = await UnsafeToRetireAsync(order, NyTimeHelper.NowNy);
            if (reason != null) throw new RecurringSeriesException(reason);
            await RetireAsync(order, adminUserId, replacing: false);
            if (transaction != null) await transaction.CommitAsync();
        }

        /// <summary>Called by permanent deletion while holding the customer's payment lock.</summary>
        public async Task PrepareDeleteAsync(Order order)
        {
            var reason = await UnsafeToRetireAsync(order, NyTimeHelper.NowNy, allowCancelled: true);
            if (reason != null) throw new RecurringSeriesException(reason);
            // These restricted FKs cannot outlive the order. Keep the canceled attempt itself;
            // its old client secret has been invalidated before any links are removed.
            _context.OrderPaymentBatchItems.RemoveRange(await _context.OrderPaymentBatchItems
                .Where(i => i.OrderId == order.Id).ToListAsync());
        }

        private async Task<string?> UnsafeToRetireAsync(Order order, DateTime now, bool allowCancelled = false)
        {
            if (!order.IsGeneratedByRecurringSeries || order.ServiceDate.Date.Add(order.ServiceTime) <= now
                || !(OrderStatuses.Is(order.Status, OrderStatuses.Pending)
                    || (allowCancelled && OrderStatuses.IsCancelled(order.Status))))
                return "Only future, uncommitted Pending generated cleanings can be skipped or replaced.";
            if (order.IsPaid || order.PaidAt != null || order.InvoicePaidAt != null
                || order.ManualPaymentRecordedAt != null
                || !string.IsNullOrEmpty(order.PaymentReference) || order.TotalRefundedAmount != 0
                || await _context.OrderUpdateHistories.AnyAsync(h => h.OrderId == order.Id)
                || await _context.OrderRefunds.AnyAsync(r => r.OrderId == order.Id)
                || await _context.OrderPaymentBatchItems.AnyAsync(i => i.OrderId == order.Id
                    && i.Batch!.Status == OrderPaymentBatchStatus.Paid)
                || await _context.CommercialInvoiceOrders.AnyAsync(i => i.OrderId == order.Id
                    && i.Invoice!.Status != Models.Commercial.InvoiceStatus.Void)
                || await _context.OrderCleaners.AnyAsync(c => c.OrderId == order.Id
                    && (c.IsPaid || c.PaidAt != null || c.AssignmentNotificationSentAt != null)))
                return "This cleaning has payment, invoice or staffing activity. Use the existing cancellation/reversal workflow.";
            try
            {
                // Share retry's authoritative Stripe check and whole-batch cancellation. Merely
                // opening Pay All (or an individual form) must not permanently reserve a visit.
                await _payments.PrepareIndividualPaymentAsync(order.Id);
            }
            catch (CombinedPaymentException ex) { return ex.Message; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not safely close payment attempts for recurring order {OrderId}", order.Id);
                return "Could not safely close the previous payment attempt. Please retry before removing this cleaning.";
            }
            return null;
        }

        private async Task RetireAsync(Order order, int adminUserId, bool replacing)
        {
            var before = new { order.Status, order.RecurrenceOccurrenceDate, order.ServiceDate };
            order.Status = OrderStatuses.Cancelled;
            order.CancellationReason = replacing ? "Retired by admin for recurrence regeneration" : "Recurring cleaning skipped by admin";
            if (replacing) order.RecurrenceOccurrenceDate = null;
            order.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
            await _audit.LogActionAsync(AuditEntityTypes.RecurringOrderSeriesAction, order.RecurringSeriesId!.Value,
                replacing ? "RecurringOccurrenceReplaced" : "RecurringOccurrenceSkipped", before,
                new { OrderId = order.Id, order.Status, order.ServiceDate, order.CancellationReason }, actingUserId: adminUserId);
        }

        public async Task<RecurringGenerationResultDto> GenerateAsync(int seriesId, int? actingUserId = null)
        {
            var series = await _context.RecurringOrderSeries
                .FirstOrDefaultAsync(s => s.Id == seriesId)
                ?? throw new RecurringSeriesException("Recurring series not found.");

            return await GenerateForSeriesAsync(series, NyTimeHelper.NowNy.Date, actingUserId);
        }

        public async Task<int> GenerateAllAsync(CancellationToken cancellationToken = default)
        {
            var today = NyTimeHelper.NowNy.Date;

            var ids = await _context.RecurringOrderSeries
                .Where(s => s.IsActive && s.StoppedAt == null && (s.EndDate == null || s.EndDate >= today))
                .Select(s => s.Id)
                .ToListAsync(cancellationToken);

            var created = 0;

            foreach (var id in ids)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var series = await _context.RecurringOrderSeries.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
                    if (series == null) continue;

                    var result = await GenerateForSeriesAsync(series, today, actingUserId: null);
                    created += result.CreatedCount;
                }
                catch (Exception ex)
                {
                    // One broken series must never stop the sweep — the others are somebody's
                    // cleanings next week.
                    _logger.LogError(ex, "Recurring generation failed for series {SeriesId}; continuing.", id);
                }
            }

            return created;
        }

        private async Task<RecurringGenerationResultDto> GenerateForSeriesAsync(
            RecurringOrderSeries series, DateTime today, int? actingUserId)
        {
            var result = new RecurringGenerationResultDto { SeriesId = series.Id };

            if (!series.IsActive || series.StoppedAt.HasValue)
            {
                result.Warnings.Add("This series is paused, so nothing was generated.");
                return result;
            }

            var invalid = RecurrenceCalculator.Validate(series.IntervalUnit, series.IntervalValue);
            if (invalid != null)
            {
                result.Warnings.Add(invalid);
                return result;
            }

            // What already exists for this series, by occurrence date. The unique index is the
            // real guard; this is the cheap path that stops us building a booking DTO per date.
            var seriesOrders = await LoadSlotsAsync(series.Id);
            var existingDates = seriesOrders
                .Where(o => o.OccurrenceDate != null)
                .Select(o => o.OccurrenceDate!.Value.Date)
                .ToHashSet();

            var rule = await BuildRuleAsync(series);
            var dates = PlanDates(series, rule, today, seriesOrders);

            if (dates.Count == 0) return result;

            var template = await LoadTemplateAsync(series);
            if (template == null)
            {
                result.Warnings.Add(
                    "The order this series was built from no longer exists, so nothing can be generated. "
                    + "Point the series at another order or pause it.");
                return result;
            }

            var createdDates = new List<DateTime>();

            foreach (var date in dates)
            {
                var current = await _context.RecurringOrderSeries.AsNoTracking().FirstAsync(s => s.Id == series.Id);
                if (!current.IsActive || current.StoppedAt.HasValue || current.UpdatedAt != series.UpdatedAt) break;
                if (existingDates.Contains(date.Date))
                {
                    result.SkippedExistingDates.Add(date.Date);
                    continue;
                }

                try
                {
                    var order = await CreateOccurrenceAsync(series, template, date);
                    if (order == null) continue;

                    result.CreatedOrderIds.Add(order.Id);
                    result.CreatedCount++;
                    createdDates.Add(date.Date);
                }
                catch (DbUpdateException ex) when (IsUniqueViolation(ex))
                {
                    // Another pass got there first. Exactly the outcome the index exists for.
                    _context.ChangeTracker.Clear();
                    result.SkippedExistingDates.Add(date.Date);
                    _logger.LogInformation(
                        "Recurring occurrence {Date:yyyy-MM-dd} for series {SeriesId} already existed.",
                        date, series.Id);
                }
                catch (Exception ex)
                {
                    _context.ChangeTracker.Clear();
                    _logger.LogError(ex,
                        "Failed to generate recurring occurrence {Date:yyyy-MM-dd} for series {SeriesId}.",
                        date, series.Id);
                    result.Warnings.Add($"Could not create the cleaning for {date:MMMM d, yyyy}: {ex.Message}");
                }
            }

            var reloaded = await _context.RecurringOrderSeries.FirstOrDefaultAsync(s => s.Id == series.Id);
            if (reloaded != null)
            {
                // A count-driven plan is generated through its furthest slot, not a fixed window.
                reloaded.GeneratedThroughDate = series.UpcomingOccurrenceTarget.HasValue
                    ? existingDates.Concat(createdDates).DefaultIfEmpty(today).Max()
                    : today.AddDays(RecurrenceCalculator.HorizonDays);
                reloaded.UpdatedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync();
            }

            if (result.CreatedCount > 0)
            {
                await _audit.LogActionAsync(
                    AuditEntityTypes.RecurringOrderSeriesAction, series.Id, "RecurringOrdersGenerated",
                    oldValues: null,
                    newValues: new
                    {
                        SeriesId = series.Id,
                        TemplateOrderId = series.TemplateOrderId,
                        CreatedOrderIds = result.CreatedOrderIds,
                        Dates = createdDates.Select(d => d.ToString("yyyy-MM-dd")).ToArray(),
                        series.UpcomingOccurrenceTarget,
                        CopiedCleanerAssignments = series.CopyCleanerAssignments,
                        // Stated in the payload on purpose: this is the fact somebody will come
                        // looking for when they ask why a cleaner never heard about the job.
                        CleanersNotified = false
                    },
                    actingUserId: actingUserId);
            }

            return result;
        }

        /// <summary>
        /// Builds ONE occurrence.
        ///
        /// Goes through <see cref="IBookingCreationService.CreateOrderAsync"/> like every other
        /// order in the system, so catalog pricing and tax use the shared calculator. Recurring
        /// loyalty uses only the higher lifetime or series percentage; source credits never repeat.
        ///
        ///  • <b>Status is always Pending.</b> Nothing has been paid for a cleaning three weeks
        ///    away, whatever method the template used, and Pending is the state the customer's
        ///    payment queue and the invoice-activation rule both expect.
        ///  • <b>The payment METHOD carries over</b> — including Invoice, which is how a
        ///    commercial client's weekly cleanings end up billable as a group.
        ///  • <b>BookedByAdminUserId is the series creator</b>, because a person did arrange this:
        ///    it makes the payment page collect the consents the customer never gave a booking
        ///    form, exactly as an admin phone booking does.
        ///  • <b>No customer email or SMS is sent from here at all.</b> Generation is bookkeeping.
        /// </summary>
        private async Task<Order?> CreateOccurrenceAsync(
            RecurringOrderSeries series, TemplateSnapshot template, DateTime occurrenceDate)
        {
            var dto = OrderBookingSnapshot.ToBookingDto(
                template.Order, template.ServiceType, template.Services, template.Extras);

            dto.ServiceDate = occurrenceDate.Date;
            dto.ServiceTime = series.ServiceTime.ToString(@"hh\:mm");
            dto.ApartmentId = template.ApartmentId;

            // A WEEKLY FLAT FEE cleaning is an operational record billed through the contract's
            // weekly invoice: it is created on the Invoice method for the contract's client, so
            // nothing — no automatic request, no Pay All, no AutoPay — ever asks for it on its
            // own. Per-visit contracts and residential plans keep the template's arrangement.
            var weeklyFlat = template.Contract?.IsWeeklyFlatFee == true;
            var paymentMethod = weeklyFlat ? PaymentMethod.Invoice : template.Order.PaymentMethod;

            // A tip is never inherited onto a weekly-flat-fee visit (2026-10): nobody collects it —
            // the invoice bills the weekly fee — yet payroll would pay it out, and committing the
            // invoice would carve it out of the taxable fee. A tip for such a visit, if ever wanted,
            // is an explicit act, not something recurrence copies.
            if (weeklyFlat) dto.Tips = 0;
            var contractClientId = weeklyFlat ? template.Contract!.ContractClientId : template.Order.ContractClientId;

            var options = new BookingCreationOptions
            {
                RecurringSeriesId = series.Id,
                RecurringLoyaltyDiscountPercent = series.RecurringLoyaltyDiscountPercent,
                RecurringLoyaltyDiscountAmount = series.RecurringLoyaltyDiscountAmount,
                ExcludeRecurringLoyalty = template.CommercialLoyaltyExcluded || weeklyFlat,
                RecurrenceOccurrenceDate = occurrenceDate.Date,
                InitialStatus = OrderStatuses.Pending,
                PaymentMethod = paymentMethod,
                ContractClientId = contractClientId,
                // A generated occurrence is not a recorded payment: no reference, no notes, no
                // "recorded at" stamp. BookingCreationService only writes those for a settled
                // method anyway (see PaymentMethodRules), and passing them would be a lie.
                BookedByAdminUserId = series.CreatedByUserId
            };

            var order = await _bookingCreation.CreateOrderAsync(
                dto, series.UserId,
                allowCustomPricing: template.ServiceType.IsCustom,
                options);

            order.RecurringSeriesId = series.Id;
            order.RecurrenceOccurrenceDate = occurrenceDate.Date;
            order.IsGeneratedByRecurringSeries = true;
            order.ContractClientId = contractClientId;
            order.ContractId = template.Contract?.ContractId;
            order.AssignedAdminId = template.Order.AssignedAdminId;

            if (weeklyFlat) await ZeroOperationalPricingAsync(order);

            await _context.SaveChangesAsync();

            if (series.CopyCleanerAssignments && template.CleanerIds.Count > 0)
                await CopyCleanerAssignmentsAsync(series, template, order.Id);

            return order;
        }

        /// <summary>
        /// A WEEKLY FLAT FEE visit is an OPERATIONAL record, not a charge (2026-10): its price
        /// columns and its service/extra line costs are zero, and every price surface shows
        /// "Billed weekly by contract …" instead (see <c>ContractBilledOrders</c>).
        ///
        /// What it KEEPS is everything the work needs: the service and extra lines (quantities and
        /// durations), TotalDuration, MaidsCount and the cleaner rate — so staffing, schedules,
        /// payroll hours and the cleaner's task list are exactly as before.
        ///
        /// Built through the ordinary creation path first and zeroed after, deliberately: that keeps
        /// the lines, durations and staffing identical to a priced occurrence instead of growing a
        /// second, price-free creation path. Sending the contract's invoice later writes each
        /// visit's share of the weekly fee onto it, exactly as before; the money is the invoice's.
        /// </summary>
        private async Task ZeroOperationalPricingAsync(Order order)
        {
            order.SubTotal = 0m;
            order.Tax = 0m;
            order.Tips = 0m;
            order.CompanyDevelopmentTips = 0m;
            order.Total = 0m;

            foreach (var line in await _context.OrderServices.Where(s => s.OrderId == order.Id).ToListAsync())
                line.Cost = 0m;
            foreach (var line in await _context.OrderExtraServices.Where(e => e.OrderId == order.Id).ToListAsync())
                line.Cost = 0m;
        }

        /// <summary>
        /// Copies the template's cleaners onto a generated occurrence.
        ///
        /// AND NOTIFIES NOBODY. No email, no SMS, no reminder scheduling, no call into
        /// CleanerService's assignment path — which is what sends the assignment mail. The rows are
        /// written straight, with <c>AssignmentNotificationSentAt</c> left null so the existing
        /// Send / Resend controls behave exactly as they do on a hand-assigned order.
        ///
        /// <c>Order.MaidsCount</c> is deliberately NOT raised here either. On a hand assignment
        /// that rule exists because an order left at the default 1 and then staffed with three had
        /// every MaidsCount-reading surface still saying one; here the occurrence was priced from a
        /// template that already carries the right count, and quietly raising it would change what
        /// the cleaners are paid for a job nobody has looked at yet.
        /// </summary>
        private async Task CopyCleanerAssignmentsAsync(
            RecurringOrderSeries series, TemplateSnapshot template, int orderId)
        {
            foreach (var cleanerId in template.CleanerIds)
            {
                _context.OrderCleaners.Add(new OrderCleaner
                {
                    OrderId = orderId,
                    CleanerId = cleanerId,
                    AssignedBy = series.CreatedByUserId,
                    AssignedAt = DateTime.UtcNow,
                    AutoAssignedFromSeriesId = series.Id,
                    AssignmentNotificationSentAt = null
                });
            }

            await _context.SaveChangesAsync();
        }

        // ── Reads ─────────────────────────────────────────────────────────────────────────────

        public async Task<RecurringSeriesDto?> GetAsync(int seriesId)
        {
            var series = await _context.RecurringOrderSeries
                .Include(s => s.User)
                .Include(s => s.CreatedByUser)
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == seriesId);

            return series == null ? null : await ProjectAsync(series);
        }

        public async Task<RecurringSeriesDto?> GetForOrderAsync(int orderId)
        {
            var seriesId = await _context.Orders
                .Where(o => o.Id == orderId)
                .Select(o => o.RecurringSeriesId)
                .FirstOrDefaultAsync();

            return seriesId == null ? null : await GetAsync(seriesId.Value);
        }

        public async Task<List<RecurringSeriesDto>> ListAsync(bool includeInactive)
        {
            var query = _context.RecurringOrderSeries
                .Include(s => s.User)
                .Include(s => s.CreatedByUser)
                .AsNoTracking()
                .AsQueryable();

            if (!includeInactive) query = query.Where(s => s.IsActive);

            var rows = await query.OrderByDescending(s => s.CreatedAt).ToListAsync();

            var result = new List<RecurringSeriesDto>(rows.Count);
            foreach (var row in rows) result.Add(await ProjectAsync(row));
            return result;
        }

        private async Task<RecurringSeriesDto> ProjectAsync(RecurringOrderSeries series)
        {
            var orders = await _context.Orders
                .Where(o => o.RecurringSeriesId == series.Id)
                .Select(o => new
                {
                    o.Id,
                    o.ServiceDate,
                    o.ServiceTime,
                    o.RecurrenceOccurrenceDate,
                    o.Status,
                    o.CancellationReason,
                    o.Total,
                    o.IsPaid,
                    o.InvoicePaidAt,
                    o.PaymentMethod,
                    o.IsGeneratedByRecurringSeries,
                    o.ContractId,
                    AssignedCleanerCount = _context.OrderCleaners.Count(oc => oc.OrderId == o.Id),
                    AutoAssignedNotNotified = _context.OrderCleaners.Count(
                        oc => oc.OrderId == o.Id
                              && oc.AutoAssignedFromSeriesId != null
                              && oc.AssignmentNotificationSentAt == null)
                })
                .OrderBy(o => o.ServiceDate)
                .ToListAsync();

            var dto = new RecurringSeriesDto
            {
                Id = series.Id,
                UserId = series.UserId,
                CustomerName = series.User == null
                    ? ""
                    : $"{series.User.FirstName} {series.User.LastName}".Trim(),
                TemplateOrderId = series.TemplateOrderId,
                IntervalValue = series.IntervalValue,
                IntervalUnit = series.IntervalUnit,
                IntervalLabel = RecurrenceCalculator.Describe(RecurrenceRule.From(series)),
                ServiceDaysOfWeek = RecurrenceCalculator.ParseDaysOfWeek(series.ServiceDaysOfWeek).Select(d => (int)d).ToList(),
                ServiceDaysOfMonth = RecurrenceCalculator.ParseDaysOfMonth(series.ServiceDaysOfMonth),
                UpcomingOccurrenceTarget = series.UpcomingOccurrenceTarget,
                AnchorDate = series.AnchorDate,
                ServiceTime = series.ServiceTime,
                EndDate = series.EndDate,
                IsActive = series.IsActive,
                StoppedAt = series.StoppedAt,
                CopyCleanerAssignments = series.CopyCleanerAssignments,
                AutoRequestPayment = series.AutoRequestPayment,
                RecurringLoyaltyDiscountPercent = series.RecurringLoyaltyDiscountPercent,
                RecurringLoyaltyDiscountAmount = series.RecurringLoyaltyDiscountAmount,
                GeneratedThroughDate = series.GeneratedThroughDate,
                Notes = series.Notes,
                CreatedAt = series.CreatedAt,
                CreatedByName = series.CreatedByUser == null
                    ? null
                    : $"{series.CreatedByUser.FirstName} {series.CreatedByUser.LastName}".Trim(),
                Occurrences = orders.Select(o => new RecurringOccurrenceDto
                {
                    OrderId = o.Id,
                    ServiceDate = o.ServiceDate,
                    ServiceTime = o.ServiceTime,
                    OccurrenceDate = o.RecurrenceOccurrenceDate,
                    Status = o.Status,
                    IsSkipped = o.CancellationReason == "Recurring cleaning skipped by admin",
                    Total = o.Total,
                    IsPaid = o.IsPaid || o.InvoicePaidAt != null,
                    IsTemplate = o.Id == series.TemplateOrderId,
                    WasGenerated = o.IsGeneratedByRecurringSeries,
                    PaymentMethod = o.PaymentMethod.ToString(),
                    ContractId = o.ContractId,
                    AssignedCleanerCount = o.AssignedCleanerCount,
                    AutoAssignedNotNotifiedCount = o.AutoAssignedNotNotified
                }).ToList()
            };

            var today = NyTimeHelper.NowNy.Date;
            var slots = orders.Select(o => new OccurrenceSlot(o.RecurrenceOccurrenceDate, o.ServiceDate, o.Status)).ToList();
            dto.UpcomingCount = slots.Count(s => s.IsUpcoming(today));

            if (series.ContractId.HasValue)
            {
                var profile = await ContractBillingProfile.LoadAsync(_context, series.ContractId.Value);
                if (profile != null)
                {
                    dto.Contract = ToOptionDto(profile);
                    dto.BillingControlledByContract = profile.IsWeeklyFlatFee;
                }
            }

            dto.PendingDates = series.IsActive && series.StoppedAt == null
                ? PlanDates(series, await BuildRuleAsync(series), today, slots)
                : new List<DateTime>();

            return dto;
        }

        // ── Which dates a pass should create ──────────────────────────────────────────────────

        /// <summary>One existing order of a plan, as far as date planning is concerned.</summary>
        private sealed record OccurrenceSlot(DateTime? OccurrenceDate, DateTime ServiceDate, string Status)
        {
            /// <summary>
            /// Counts toward the upcoming target: still to happen (today included — a visit stays
            /// upcoming until its day is over) and not cancelled, refunded or skipped.
            /// </summary>
            public bool IsUpcoming(DateTime today) =>
                ServiceDate.Date >= today && !OrderStatuses.IsCancelled(Status) && !OrderStatuses.IsRefunded(Status);
        }

        private async Task<List<OccurrenceSlot>> LoadSlotsAsync(int seriesId) =>
            (await _context.Orders
                .Where(o => o.RecurringSeriesId == seriesId)
                .Select(o => new { o.RecurrenceOccurrenceDate, o.ServiceDate, o.Status })
                .ToListAsync())
            .Select(o => new OccurrenceSlot(o.RecurrenceOccurrenceDate, o.ServiceDate, o.Status))
            .ToList();

        /// <summary>
        /// The plan's date rule. A contract-linked plan counts its weekly cycles from the
        /// contract's own service-week start, so a fortnightly plan's cycles are the contract's
        /// billing weeks; anything else counts from Sunday.
        /// </summary>
        private async Task<RecurrenceRule> BuildRuleAsync(RecurringOrderSeries series)
        {
            var weekStart = DayOfWeek.Sunday;
            if (series.ContractId.HasValue && series.ServiceDaysOfWeek != null)
            {
                var profile = await ContractBillingProfile.LoadAsync(_context, series.ContractId.Value);
                if (profile != null) weekStart = profile.WeekStart;
            }
            return RecurrenceRule.From(series, weekStart);
        }

        /// <summary>
        /// The dates one generation pass should create.
        ///
        /// COUNT MODE (<see cref="RecurringOrderSeries.UpcomingOccurrenceTarget"/> set): tops the
        /// plan up to the target — the upcoming cleanings that already exist count toward it, so a
        /// second pass asks for nothing, and a visit that has passed frees exactly one new slot.
        /// LEGACY MODE (null): every date inside the rolling 30-day horizon, as before.
        /// Either way a date the plan already holds (any status) is never offered again.
        /// </summary>
        private static List<DateTime> PlanDates(
            RecurringOrderSeries series, RecurrenceRule rule, DateTime today, List<OccurrenceSlot> slots)
        {
            var taken = slots.Where(s => s.OccurrenceDate != null).Select(s => s.OccurrenceDate!.Value.Date).ToList();

            if (series.UpcomingOccurrenceTarget is int target)
            {
                var needed = target - slots.Count(s => s.IsUpcoming(today));
                return RecurrenceCalculator.NextMissingOccurrences(rule, today, taken, needed, series.GenerateAfterDate);
            }

            var have = taken.ToHashSet();
            return RecurrenceCalculator.OccurrencesWithinHorizon(rule, today)
                .Where(d => !have.Contains(d)
                    && (!series.GenerateAfterDate.HasValue || d > series.GenerateAfterDate.Value))
                .ToList();
        }

        // ── Commercial contract linkage ───────────────────────────────────────────────────────

        public async Task<RecurringContractOptionsDto> GetContractOptionsAsync(int orderId)
        {
            var order = await _context.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == orderId)
                ?? throw new RecurringSeriesException("Order not found.");

            var result = new RecurringContractOptionsDto();
            var (clientId, contracts) = await LoadContractOptionsAsync(order);
            result.ContractClientId = clientId;
            result.Contracts = contracts.Select(ToOptionDto).ToList();
            if (result.Contracts.Count == 1) result.SuggestedContractId = result.Contracts[0].Id;
            return result;
        }

        /// <summary>
        /// The commercial client the order belongs to — its own, or the one its customer account
        /// is linked to — and that client's EXECUTED contracts (the statuses an invoice may be
        /// raised against). One rule for the panel's list and for the save guard below.
        /// </summary>
        private async Task<(int? ClientId, List<ContractBillingProfile> Contracts)> LoadContractOptionsAsync(Order order)
        {
            var clientId = order.ContractClientId ?? await _context.ContractClients
                .Where(c => c.SourceUserId == order.UserId && c.IsActive)
                .OrderBy(c => c.Id)
                .Select(c => (int?)c.Id)
                .FirstOrDefaultAsync();

            if (clientId == null) return (null, new List<ContractBillingProfile>());

            var statuses = Helpers.Commercial.ContractInvoiceEligibility.InvoiceableStatuses.ToList();
            var contracts = await _context.Contracts
                .Include(c => c.ServiceLocation)
                .Where(c => c.ContractClientId == clientId && !c.IsHidden && statuses.Contains(c.Status))
                .OrderBy(c => c.Id)
                .AsNoTracking()
                .ToListAsync();

            var profiles = new List<ContractBillingProfile>(contracts.Count);
            foreach (var contract in contracts)
                profiles.Add(await ContractBillingProfile.FromContractAsync(_context, contract));
            return (clientId, profiles);
        }

        private async Task<ContractBillingProfile?> ResolveContractAsync(Order order, int? contractId)
        {
            if (contractId == null) return null;
            var (_, contracts) = await LoadContractOptionsAsync(order);
            return contracts.FirstOrDefault(c => c.ContractId == contractId.Value)
                ?? throw new RecurringSeriesException(
                    "That contract is not an executed contract of this order's commercial client.");
        }

        private static RecurringContractOptionDto ToOptionDto(ContractBillingProfile p) => new()
        {
            Id = p.ContractId,
            ContractNumber = p.ContractNumber,
            Status = p.Status.ToString(),
            ContractClientId = p.ContractClientId,
            ServiceAddress = p.ServiceAddress,
            PricingBasis = p.PricingBasis.ToString(),
            IsWeeklyFlatFee = p.IsWeeklyFlatFee,
            VisitsPerWeek = p.VisitsPerWeek,
            PreTaxPrice = p.PreTaxPrice,
            SalesTaxAmount = p.SalesTaxAmount,
            TotalPrice = p.TotalPrice,
            WeekDefinition = p.WeekDefinition
        };

        // ── The template, loaded once per pass ────────────────────────────────────────────────

        private sealed class TemplateSnapshot
        {
            public required Order Order { get; init; }
            public required ServiceType ServiceType { get; init; }
            public required List<Models.OrderService> Services { get; init; }
            public required List<OrderExtraService> Extras { get; init; }
            public required List<int> CleanerIds { get; init; }
            public int? ApartmentId { get; init; }
            public bool CommercialLoyaltyExcluded { get; init; }

            /// <summary>The plan's linked contract, when it has one.</summary>
            public ContractBillingProfile? Contract { get; init; }
        }

        private async Task<TemplateSnapshot?> LoadTemplateAsync(RecurringOrderSeries series)
        {
            var order = await _context.Orders
                .Include(o => o.OrderServices)
                .Include(o => o.OrderExtraServices)
                .AsNoTracking()
                .AsSplitQuery()
                .FirstOrDefaultAsync(o => o.Id == series.TemplateOrderId);

            if (order == null) return null;

            // Thresholds and rate tiers are eager-loaded for the same reason every other pricing
            // path loads them: lazy loading is off, and an empty Thresholds collection silently
            // prices every service from unit one at a flat rate.
            var serviceType = await _context.ServiceTypes
                .Include(st => st.Services).ThenInclude(s => s.Thresholds)
                .Include(st => st.Services).ThenInclude(s => s.RateTiers)
                .AsSplitQuery()
                .FirstOrDefaultAsync(st => st.Id == order.ServiceTypeId);

            if (serviceType == null) return null;

            // Lines whose catalogue row has been deleted or deactivated are dropped rather than
            // re-priced — the same rule the recreate preview applies, and the alternative is a
            // failed insert every night.
            var liveServiceIds = serviceType.Services.Where(s => s.IsActive).Select(s => s.Id).ToHashSet();
            var liveExtraIds = await _context.ExtraServices
                .Where(e => e.IsActive).Select(e => e.Id).ToListAsync();
            var liveExtraSet = liveExtraIds.ToHashSet();

            var apartmentStillExists = order.ApartmentId != null && await _context.Apartments
                .AnyAsync(a => a.Id == order.ApartmentId.Value && a.UserId == order.UserId);

            var cleanerIds = series.CopyCleanerAssignments
                ? await _context.OrderCleaners
                    .Where(oc => oc.OrderId == order.Id)
                    .Select(oc => oc.CleanerId)
                    .Distinct()
                    .ToListAsync()
                : new List<int>();

            // Only cleaners who are still active can be carried forward — assigning a deactivated
            // cleaner would put a name on a job nobody can work.
            if (cleanerIds.Count > 0)
            {
                cleanerIds = await _context.Cleaners
                    .Where(c => cleanerIds.Contains(c.Id) && c.IsActive)
                    .Select(c => c.Id)
                    .ToListAsync();
            }

            return new TemplateSnapshot
            {
                Order = order,
                ServiceType = serviceType,
                Services = order.OrderServices.Where(s => liveServiceIds.Contains(s.ServiceId)).ToList(),
                Extras = order.OrderExtraServices.Where(e => liveExtraSet.Contains(e.ExtraServiceId)).ToList(),
                CleanerIds = cleanerIds,
                CommercialLoyaltyExcluded = !ResidentialLoyaltyPolicy.AppliesTo(order)
                    || await _context.CommercialInvoiceOrders.AnyAsync(l => l.OrderId == order.Id),
                ApartmentId = apartmentStillExists ? order.ApartmentId : null,
                Contract = series.ContractId.HasValue
                    ? await ContractBillingProfile.LoadAsync(_context, series.ContractId.Value)
                    : null
            };
        }

        /// <summary>
        /// One agreement, written either way — so BOTH being filled in is rejected rather than
        /// resolved by preferring one. An admin who typed a percentage and then a dollar amount
        /// meant to replace the first, and guessing which would quietly bill the wrong figure
        /// every fortnight.
        /// </summary>
        private static void ValidateDiscount(SaveRecurringSeriesDto dto)
        {
            if (dto.RecurringLoyaltyDiscountPercent.HasValue && dto.RecurringLoyaltyDiscountAmount.HasValue)
                throw new RecurringSeriesException(
                    "Choose either a percentage or a fixed amount for the recurring discount, not both.");
            if (dto.RecurringLoyaltyDiscountPercent is < 0 or > 100)
                throw new RecurringSeriesException("Recurring loyalty must be between 0% and 100%.");
            if (dto.RecurringLoyaltyDiscountAmount is < 0)
                throw new RecurringSeriesException("A fixed recurring discount cannot be negative.");
        }

        public async Task<RecurringPricePreviewDto> PreviewAsync(int sourceOrderId, SaveRecurringSeriesDto dto)
        {
            ValidateDiscount(dto);
            var template = await LoadTemplateAsync(new RecurringOrderSeries { TemplateOrderId = sourceOrderId })
                ?? throw new RecurringSeriesException("Source order or service type not found.");
            var source = template.Order;
            var booking = OrderBookingSnapshot.ToBookingDto(source, template.ServiceType, template.Services, template.Extras);
            booking.ServiceDate = dto.AnchorDate?.Date ?? source.ServiceDate;
            var input = await OrderPricingInputBuilder.FromBookingDtoAsync(_context, template.ServiceType, booking, template.ServiceType.IsCustom);
            var quote = OrderPricingCalculator.CalculateQuote(input);
            var customer = await _context.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == source.UserId);
            var commercial = template.CommercialLoyaltyExcluded;
            // Resolved against THIS quote's subtotal, exactly as a generated occurrence will be —
            // which is what lets a fixed amount be previewed at all, and is why the preview and
            // the real cleaning cannot disagree.
            var loyalty = RecurringDiscountPolicy.Resolve(customer, dto.RecurringLoyaltyDiscountPercent,
                dto.RecurringLoyaltyDiscountAmount, commercial, quote.SubTotal);
            var totals = OrderPricingCalculator.CalculateTotals(new OrderPricingCalculator.TotalsInput {
                SubTotal = quote.SubTotal, TaxOverride = quote.TaxOverride, LoyaltyDiscountAmount = loyalty.Amount, Tips = source.Tips
            });
            var preview = new RecurringPricePreviewDto { BaseCleaning = quote.SubTotal, LoyaltyPercent = loyalty.Percent,
                LoyaltySource = loyalty.Source, LoyaltyAmount = loyalty.Amount, Tax = totals.Tax, Tips = source.Tips,
                Total = totals.Total, CommercialLoyaltyExcluded = commercial,
                LoyaltyIsFixedAmount = loyalty.Source == "Recurring Series" && dto.RecurringLoyaltyDiscountAmount.HasValue };
            if (source.DiscountAmount > 0 || !string.IsNullOrEmpty(source.PromoCode))
                preview.SourceDiscounts.Add(new(string.IsNullOrEmpty(source.PromoCode) ? "Promo / offer" : $"Promo Code \"{source.PromoCode}\"", source.DiscountAmount));
            if (source.PointsRedeemed > 0 || source.PointsRedeemedDiscount > 0)
                preview.SourceDiscounts.Add(new("Bubble Points", source.PointsRedeemedDiscount));
            if (source.LoyaltyDiscountAmount > 0)
                preview.SourceDiscounts.Add(new(customer?.LoyaltyDiscountIsLifetime == true ? "Source-order loyalty" : "One-time Loyalty", source.LoyaltyDiscountAmount, source.LoyaltyDiscountPercentage));
            if (source.GiftCardAmountUsed > 0) preview.SourceDiscounts.Add(new("Gift card credit", source.GiftCardAmountUsed));
            if (source.RewardBalanceUsed > 0) preview.SourceDiscounts.Add(new("Reward credit", source.RewardBalanceUsed));
            if (source.SubscriptionDiscountAmount > 0) preview.SourceDiscounts.Add(new("Source plan discount", source.SubscriptionDiscountAmount));
            return preview;
        }

        private static object Snapshot(RecurringOrderSeries s) => new
        {
            s.IntervalValue,
            IntervalUnit = s.IntervalUnit.ToString(),
            Recurrence = RecurrenceCalculator.Describe(RecurrenceRule.From(s)),
            s.ServiceDaysOfWeek,
            s.ServiceDaysOfMonth,
            s.UpcomingOccurrenceTarget,
            s.ContractId,
            s.AnchorDate,
            s.EndDate,
            s.IsActive,
            s.StoppedAt,
            s.GenerateAfterDate,
            s.ServiceTime,
            s.CopyCleanerAssignments,
            s.AutoRequestPayment,
            s.RecurringLoyaltyDiscountPercent,
            s.RecurringLoyaltyDiscountAmount,
            s.Notes
        };

        private static bool IsUniqueViolation(DbUpdateException ex) =>
            ex.InnerException?.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) == true
            || ex.InnerException?.Message.Contains("unique", StringComparison.OrdinalIgnoreCase) == true;
    }
}
