using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models;
using Microsoft.EntityFrameworkCore;

namespace DreamCleaningBackend.Services
{
    /// <summary>
    /// The cleaner side of Outgoing Payments (2026-10): who is owed (Unpaid cleaners), paying
    /// several of them at once, and the payout history (Paid cleaners), with reverting.
    ///
    /// It reads the SAME rows the Orders tab builds — <see cref="BuildOrderRow"/> over the same
    /// finished-order query — and writes through the same <see cref="CleanerPayoutLedger"/>, so
    /// the three tabs are three views of one ledger and cannot drift apart: paying a cleaner here
    /// settles the order lines on the Orders tab, paying a line there takes it off this tab's
    /// list, and new unpaid work brings a cleaner back here for the new amount only.
    /// </summary>
    public partial class OutgoingPaymentService
    {
        private const string ConflictMessage =
            "Some of these amounts changed after the confirmation opened — a payment was recorded "
            + "elsewhere or the hours were edited. Nothing was paid. Review the refreshed list and "
            + "confirm again.";

        // ── Unpaid cleaners ───────────────────────────────────────────────────────────────────

        public async Task<UnpaidCleanersDto> GetUnpaidCleanersAsync()
        {
            // Every finished order, not one month: money owed for a job in March is still owed in
            // October. Read-only, so untracked.
            var orders = await PerformedOrdersWithIncludes()
                .AsNoTracking()
                .AsSplitQuery()
                .ToListAsync();

            var cleanersById = orders
                .SelectMany(o => o.OrderCleaners)
                .Where(oc => oc.Cleaner != null)
                .GroupBy(oc => oc.CleanerId)
                .ToDictionary(g => g.Key, g => g.First().Cleaner!);

            var result = new UnpaidCleanersDto();
            var lines = new List<(int CleanerId, UnpaidCleanerLineDto Line)>();

            foreach (var order in orders)
            {
                // The Orders tab's own row — same calculator, same settlement rule, same figures.
                var row = BuildOrderRow(order);

                foreach (var c in row.Cleaners.Where(c => c.OutstandingPayout > 0m))
                {
                    lines.Add((c.CleanerId, new UnpaidCleanerLineDto
                    {
                        OrderCleanerId = c.OrderCleanerId,
                        OrderId = row.OrderId,
                        ServiceDate = row.ServiceDate,
                        ServiceTime = row.ServiceTime,
                        ServiceTypeName = row.ServiceTypeName,
                        RawServiceTypeName = row.RawServiceTypeName,
                        CustomServiceDisplayName = row.CustomServiceDisplayName,
                        IsCustomServiceType = row.IsCustomServiceType,
                        IsDeepCleaning = row.IsDeepCleaning,
                        CustomerName = row.CustomerName,
                        BillableMinutes = c.BillableMinutes,
                        HourlyRate = c.HourlyRate,
                        Payout = c.Payout,
                        AlreadyPaid = c.IsTopUp ? c.PaidAmount ?? 0m : 0m,
                        Outstanding = c.OutstandingPayout,
                        IsTopUp = c.IsTopUp
                    }));
                }

                foreach (var slot in row.UnassignedCleaners.Where(s => s.OutstandingPayout > 0m))
                {
                    result.UnassignedOutstanding += slot.OutstandingPayout;
                    result.UnassignedSlotCount++;
                }
            }

            result.Cleaners = lines
                .GroupBy(l => l.CleanerId)
                .Select(g =>
                {
                    cleanersById.TryGetValue(g.Key, out var cleaner);
                    var cleanerLines = g.Select(x => x.Line)
                        .OrderBy(l => l.ServiceDate).ThenBy(l => l.OrderId)
                        .ToList();
                    return new UnpaidCleanerDto
                    {
                        CleanerId = g.Key,
                        FirstName = cleaner?.FirstName ?? $"Cleaner #{g.Key}",
                        LastName = cleaner?.LastName ?? string.Empty,
                        IsActive = cleaner?.IsActive ?? false,
                        PaymentMethod = cleaner?.PaymentMethod,
                        PaymentDetails = cleaner?.PaymentDetails,
                        TotalOutstanding = OrderPricingCalculator.Round2(cleanerLines.Sum(l => l.Outstanding)),
                        TopUpOutstanding = OrderPricingCalculator.Round2(
                            cleanerLines.Where(l => l.IsTopUp).Sum(l => l.Outstanding)),
                        Lines = cleanerLines
                    };
                })
                .OrderBy(c => c.FirstName).ThenBy(c => c.LastName).ThenBy(c => c.CleanerId)
                .ToList();

            result.TotalOutstanding = OrderPricingCalculator.Round2(result.Cleaners.Sum(c => c.TotalOutstanding));
            result.LineCount = result.Cleaners.Sum(c => c.Lines.Count);
            result.UnassignedOutstanding = OrderPricingCalculator.Round2(result.UnassignedOutstanding);
            return result;
        }

        // ── Paying several cleaners at once ──────────────────────────────────────────────────

        public async Task<PayCleanersResultDto> PayCleanersAsync(PayCleanersDto dto, int userId)
        {
            var key = (dto.IdempotencyKey ?? string.Empty).Trim();
            if (key.Length < 8)
                throw new InvalidOperationException("The confirmation is missing its one-time key. Reopen it and try again.");

            var selections = dto.Cleaners ?? new List<PayCleanerSelectionDto>();
            if (selections.Count == 0 || selections.Any(s => s.Lines == null || s.Lines.Count == 0))
                throw new InvalidOperationException("Choose at least one cleaner with something owed.");

            if (selections.GroupBy(s => s.CleanerId).Any(g => g.Count() > 1))
                throw new InvalidOperationException("A cleaner was listed twice.");

            var requested = selections.SelectMany(s => s.Lines.Select(l => (s.CleanerId, Line: l))).ToList();
            if (requested.GroupBy(r => r.Line.OrderCleanerId).Any(g => g.Count() > 1))
                throw new InvalidOperationException("An order line was listed twice.");
            if (requested.Any(r => r.Line.Amount <= 0m))
                throw new InvalidOperationException("Every line paid must have an amount owed.");

            var today = NyTimeHelper.NowNy.Date;
            var paidOn = (dto.PaidOn ?? today).Date;
            if (paidOn > today)
                throw new InvalidOperationException("The payment date cannot be in the future.");

            var cleanerIds = selections.Select(s => s.CleanerId).ToList();

            // A retried request (a double-click, a timeout followed by a retry) finds the payouts
            // its key already made and records nothing new.
            var already = await FindPayoutsByKeyAsync(key, cleanerIds);
            if (already.Count > 0) return ToPayResult(already, alreadyRecorded: true);

            var assignmentIds = requested.Select(r => r.Line.OrderCleanerId).ToList();

            // Which orders to lock is read BEFORE the transaction opens, so the lock is its first
            // statement. A plain read first would fix the transaction's snapshot, and MariaDB then
            // refuses the locking read of a row another payment changed in the meantime. Whether
            // each line still belongs to the cleaner and still owes the amount is checked again
            // under the lock below, so nothing read here is trusted.
            var orderIds = await _context.OrderCleaners
                .Where(oc => assignmentIds.Contains(oc.Id))
                .Select(oc => oc.OrderId)
                .Distinct()
                .ToListAsync();

            await using var transaction = await _context.Database.BeginTransactionAsync();
            await _ledger.LockOrdersAsync(orderIds);

            // Asked again UNDER the lock: the twin of this request may have committed while we
            // waited for it.
            already = await FindPayoutsByKeyAsync(key, cleanerIds);
            if (already.Count > 0) return ToPayResult(already, alreadyRecorded: true);

            var orders = await PerformedOrdersWithIncludes()
                .Where(o => orderIds.Contains(o.Id))
                .AsSplitQuery()
                .ToListAsync();

            var payrolls = new Dictionary<int, CleanerPayrollCalculator.Result>();
            var toPay = new List<CleanerPayoutLedger.LineToPay>();

            foreach (var (cleanerId, expected) in requested)
            {
                var order = orders.FirstOrDefault(o => o.OrderCleaners.Any(oc => oc.Id == expected.OrderCleanerId));
                var assignment = order?.OrderCleaners.First(oc => oc.Id == expected.OrderCleanerId);

                // Gone from the finished-order list (refunded, reopened) or moved to another
                // cleaner since the list was read: not what the admin confirmed.
                if (order == null || assignment == null || assignment.CleanerId != cleanerId)
                    throw new CleanerPayoutConflictException(ConflictMessage);

                if (!payrolls.TryGetValue(order.Id, out var payroll))
                {
                    // Freeze the total onto the order first, exactly as a single payment does.
                    ApplyOrderTotalSalary(order);
                    payroll = BuildPayroll(order);
                    payrolls[order.Id] = payroll;
                }

                var line = payroll.Lines.FirstOrDefault(l => l.OrderCleanerId == assignment.Id);
                var settlement = CleanerPayoutSettlement.Resolve(assignment.IsPaid, assignment.PaidAmount, line?.Payout ?? 0m);

                // THE DOUBLE-PAYMENT GUARD. Recomputed by whoever holds the lock; a line somebody
                // else paid in the meantime now owes nothing (or less), and paying the figure the
                // dialog showed would pay it twice.
                if (settlement.Outstanding <= 0m
                    || OrderPricingCalculator.Round2(expected.Amount) != settlement.Outstanding)
                    throw new CleanerPayoutConflictException(ConflictMessage);

                toPay.Add(new CleanerPayoutLedger.LineToPay(order, assignment, settlement.Outstanding, settlement.IsTopUp));
            }

            var payouts = _ledger.Record(toPay, await BuildPayoutDetailsAsync(
                userId, dto.PaidVia, dto.Reference, dto.Note, paidOn, key));

            foreach (var order in orders.Where(o => payrolls.ContainsKey(o.Id)))
                order.UpdatedAt = DateTime.UtcNow;

            try
            {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                // The idempotency index: the same key got there first by a route the lock did not
                // cover. Nothing of THIS request was written.
                throw new CleanerPayoutConflictException(
                    "This payment has already been recorded. Refresh to see it under Paid cleaners.");
            }

            await transaction.CommitAsync();

            await LogRecordedPayoutsAsync(orders, payouts, userId);
            return ToPayResult(payouts, alreadyRecorded: false);
        }

        private Task<List<CleanerPayout>> FindPayoutsByKeyAsync(string key, IEnumerable<int> cleanerIds)
        {
            var keys = cleanerIds.Select(id => CleanerPayoutLedger.PayoutKey(key, id)).ToList();
            return _context.CleanerPayouts.AsNoTracking()
                .Where(p => p.IdempotencyKey != null && keys.Contains(p.IdempotencyKey))
                .ToListAsync();
        }

        private static PayCleanersResultDto ToPayResult(IReadOnlyCollection<CleanerPayout> payouts, bool alreadyRecorded) => new()
        {
            PayoutIds = payouts.Select(p => p.Id).OrderBy(id => id).ToList(),
            CleanerCount = payouts.Select(p => p.CleanerId).Distinct().Count(),
            TotalPaid = OrderPricingCalculator.Round2(payouts.Sum(p => p.Amount)),
            AlreadyRecorded = alreadyRecorded
        };

        private static bool IsUniqueViolation(DbUpdateException ex) =>
            ex.InnerException?.Message.Contains("Duplicate entry", StringComparison.OrdinalIgnoreCase) == true
            || ex.InnerException?.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) == true;

        // ── Paid cleaners: history ───────────────────────────────────────────────────────────

        public async Task<CleanerPayoutHistoryDto> GetPayoutHistoryAsync(CleanerPayoutHistoryQuery query)
        {
            var q = _context.CleanerPayouts.AsNoTracking().AsQueryable();

            if (query.CleanerId.HasValue) q = q.Where(p => p.CleanerId == query.CleanerId.Value);
            if (query.From.HasValue) q = q.Where(p => p.PaidOn >= query.From.Value.Date);
            if (query.To.HasValue)
            {
                var toExclusive = query.To.Value.Date.AddDays(1);
                q = q.Where(p => p.PaidOn < toExclusive);
            }

            var page = Math.Max(1, query.Page);
            var pageSize = Math.Clamp(query.PageSize, 1, 100);
            var totalCount = await q.CountAsync();

            // What is still standing across the whole filter, not the page.
            var filteredIds = q.Select(p => p.Id);
            var totalPaid = await _context.CleanerPayoutLines.AsNoTracking()
                .Where(l => l.RevertedAt == null && filteredIds.Contains(l.CleanerPayoutId))
                .SumAsync(l => (decimal?)l.Amount) ?? 0m;

            var payouts = await q
                .Include(p => p.Lines)
                .Include(p => p.RevertedByUser)
                .OrderByDescending(p => p.PaidOn).ThenByDescending(p => p.Id)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            // Grouped in memory: one narrow row per payout, and the latest name wins per cleaner.
            var options = (await _context.CleanerPayouts.AsNoTracking()
                    .Select(p => new { p.CleanerId, p.CleanerName, p.Id })
                    .ToListAsync())
                .GroupBy(p => p.CleanerId)
                .Select(g => new { CleanerId = g.Key, Name = g.OrderByDescending(p => p.Id).First().CleanerName })
                .ToList();

            return new CleanerPayoutHistoryDto
            {
                Payouts = await MapPayoutsAsync(payouts),
                TotalCount = totalCount,
                Page = page,
                PageSize = pageSize,
                TotalPaid = OrderPricingCalculator.Round2(totalPaid),
                CleanerOptions = options
                    .Select(o => new CleanerPayoutCleanerOptionDto { CleanerId = o.CleanerId, Name = o.Name })
                    .OrderBy(o => o.Name)
                    .ToList()
            };
        }

        private async Task<List<CleanerPayoutDto>> MapPayoutsAsync(IReadOnlyCollection<CleanerPayout> payouts)
        {
            var orderIds = payouts.SelectMany(p => p.Lines).Select(l => l.OrderId).Distinct().ToList();

            // The covered orders' labels, read now: the money is frozen on the line, but which job it
            // was is better described by the order as it stands (a renamed custom label included).
            var orders = await _context.Orders.AsNoTracking()
                .Where(o => orderIds.Contains(o.Id))
                .Include(o => o.ServiceType)
                .Include(o => o.OrderExtraServices).ThenInclude(oes => oes.ExtraService)
                .AsSplitQuery()
                .ToDictionaryAsync(o => o.Id);

            return payouts.Select(p =>
            {
                var active = p.Lines.Where(l => l.RevertedAt == null).ToList();
                var status = p.RevertedAt != null || (p.Lines.Count > 0 && active.Count == 0)
                    ? "Reverted"
                    : active.Count < p.Lines.Count ? "PartiallyReverted" : "Active";

                return new CleanerPayoutDto
                {
                    Id = p.Id,
                    CleanerId = p.CleanerId,
                    CleanerName = p.CleanerName,
                    Amount = p.Amount,
                    ActiveAmount = OrderPricingCalculator.Round2(active.Sum(l => l.Amount)),
                    PaidOn = p.PaidOn,
                    PaidVia = p.PaidVia,
                    Reference = p.Reference,
                    Note = p.Note,
                    CreatedByName = p.CreatedByName,
                    CreatedAt = p.CreatedAt,
                    IsImported = p.IsImported,
                    Status = status,
                    RevertedAt = p.RevertedAt,
                    RevertedByName = p.RevertedByUser == null
                        ? null
                        : $"{p.RevertedByUser.FirstName} {p.RevertedByUser.LastName}".Trim(),
                    RevertReason = p.RevertReason,
                    CanRevert = status != "Reverted",
                    Lines = p.Lines
                        .OrderBy(l => l.ServiceDate).ThenBy(l => l.OrderId).ThenBy(l => l.Id)
                        .Select(l =>
                        {
                            orders.TryGetValue(l.OrderId, out var order);
                            return new CleanerPayoutLineDto
                            {
                                Id = l.Id,
                                OrderId = l.OrderId,
                                ServiceDate = l.ServiceDate,
                                ServiceTypeName = order?.GetDisplayServiceTypeName("Cleaning") ?? "Cleaning",
                                RawServiceTypeName = order?.ServiceType?.Name ?? string.Empty,
                                CustomServiceDisplayName = order?.CustomServiceDisplayName,
                                IsCustomServiceType = order?.ServiceType?.IsCustom == true,
                                IsDeepCleaning = order != null && HasDeepCleaningExtra(order),
                                CustomerName = order == null
                                    ? null
                                    : $"{order.ContactFirstName} {order.ContactLastName}".Trim(),
                                Amount = l.Amount,
                                IsTopUp = l.IsTopUp,
                                IsReverted = l.RevertedAt != null,
                                RevertedAt = l.RevertedAt
                            };
                        })
                        .ToList()
                };
            }).ToList();
        }

        // ── Reverting a payout ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Moves a whole payout back to unpaid. Privileged (the page is SuperAdmin-only), needs a
        /// reason, and is audit-logged per line like every payment. The payout and its lines stay
        /// in the history, marked reverted — a reversal is a new fact, not an erasure.
        /// </summary>
        public async Task<CleanerPayoutDto?> RevertPayoutAsync(int payoutId, string reason, int userId)
        {
            var trimmedReason = (reason ?? string.Empty).Trim();
            if (trimmedReason.Length < 3)
                throw new InvalidOperationException("Say why this payout is being reverted.");
            if (trimmedReason.Length > 500) trimmedReason = trimmedReason[..500];

            // Read before the transaction, so the lock is its first statement (see PayCleanersAsync).
            var orderIds = await _context.CleanerPayoutLines
                .Where(l => l.CleanerPayoutId == payoutId)
                .Select(l => l.OrderId)
                .Distinct()
                .ToListAsync();

            await using var transaction = await _context.Database.BeginTransactionAsync();
            await _ledger.LockOrdersAsync(orderIds);

            var payout = await _context.CleanerPayouts
                .Include(p => p.Lines)
                .FirstOrDefaultAsync(p => p.Id == payoutId);
            if (payout == null) return null;

            var active = payout.Lines.Where(l => l.RevertedAt == null).ToList();
            if (payout.RevertedAt != null || active.Count == 0)
                throw new InvalidOperationException("This payout has already been reverted.");

            await _ledger.RevertAsync(active, userId);

            payout.RevertedAt = DateTime.UtcNow;
            payout.RevertedByUserId = userId;
            payout.RevertReason = trimmedReason;

            await _context.SaveChangesAsync();
            await transaction.CommitAsync();

            // One audit row per line, against its order — the same shape as the payment it undoes.
            var orders = await _context.Orders.Where(o => orderIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id);
            var assignmentIds = active.Where(l => l.OrderCleanerId.HasValue).Select(l => l.OrderCleanerId!.Value).ToList();
            var paidNow = await _context.OrderCleaners.AsNoTracking()
                .Where(oc => assignmentIds.Contains(oc.Id))
                .ToDictionaryAsync(oc => oc.Id, oc => oc.PaidAmount ?? 0m);

            foreach (var line in active)
            {
                if (!orders.TryGetValue(line.OrderId, out var order)) continue;
                var remaining = line.OrderCleanerId.HasValue && paidNow.TryGetValue(line.OrderCleanerId.Value, out var v) ? v : 0m;
                await LogPayoutAsync(order, "PayoutReversed", payout.CleanerName, line.Amount, remaining,
                    payout.PaidVia, payout.Note, userId, payout.Id, payout.Reference, trimmedReason);
            }

            var reloaded = await _context.CleanerPayouts.AsNoTracking()
                .Include(p => p.Lines)
                .Include(p => p.RevertedByUser)
                .FirstAsync(p => p.Id == payoutId);
            return (await MapPayoutsAsync(new[] { reloaded })).Single();
        }
    }
}
