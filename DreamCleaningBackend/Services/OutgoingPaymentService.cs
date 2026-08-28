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
    /// Which orders qualify: <see cref="OrderStatuses.WasPerformed"/> — Done, plus an order that
    /// was Done and later fully refunded. The customer's money going back does not un-work the
    /// cleaner's day, and that pay is still owed.
    /// </summary>
    public class OutgoingPaymentService : IOutgoingPaymentService
    {
        private readonly ApplicationDbContext _context;

        public OutgoingPaymentService(ApplicationDbContext context)
        {
            _context = context;
        }

        public async Task<OutgoingPaymentListDto> GetAsync(OutgoingPaymentQuery query)
        {
            var orders = await LoadPerformedOrdersAsync(query);

            var rows = orders.Select(BuildOrderRow).ToList();

            // Paid-state filtering happens after the rows are built because "unpaid" is a fact
            // about the cleaner lines, not a column on the order.
            var filtered = query.PaidStatus switch
            {
                OutgoingPaymentPaidStatus.Unpaid => rows.Where(r => !r.IsFullyPaid).ToList(),
                OutgoingPaymentPaidStatus.Paid => rows.Where(r => r.IsFullyPaid && r.Cleaners.Count > 0).ToList(),
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

            // A paid line is a historical record. Editing it would move a figure that has already
            // been handed over, so it is refused here as well as disabled in the UI.
            if (assignment.IsPaid)
                throw new InvalidOperationException("This cleaner has already been paid for this order. Undo the payment first to change their rate or hours.");

            if (dto.UpdateHourlyRate)
                assignment.SalaryHourlyRate = dto.HourlyRate;

            if (dto.UpdateBillableMinutes)
                assignment.SalaryBillableMinutes = dto.BillableMinutes;

            ApplyOrderTotalSalary(order);
            order.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();
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

            foreach (var assignment in order.OrderCleaners)
            {
                if (assignment.IsPaid && assignment.SalaryHourlyRate == null)
                    assignment.SalaryHourlyRate = previousRate;
            }

            order.CleanerHourlyRate = OrderPricingCalculator.Round2(hourlyRate);

            ApplyOrderTotalSalary(order);
            order.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();
            return BuildOrderRow(order);
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
            MarkPaid(assignment, line, dto.PaidVia, dto.PaymentNote, paidByUserId);

            order.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
            return BuildOrderRow(order);
        }

        public async Task<OutgoingPaymentOrderDto?> MarkOrderPaidAsync(
            int orderId, MarkOrderPaidDto dto, int paidByUserId)
        {
            var order = await PerformedOrdersWithIncludes()
                .FirstOrDefaultAsync(o => o.Id == orderId);
            if (order == null) return null;

            ApplyOrderTotalSalary(order);
            var payroll = BuildPayroll(order);

            foreach (var assignment in order.OrderCleaners.Where(oc => !oc.IsPaid))
            {
                var line = payroll.Lines.FirstOrDefault(l => l.OrderCleanerId == assignment.Id);
                // Each cleaner is paid via their OWN saved method — a "pay all" is one action,
                // not one payment channel.
                MarkPaid(assignment, line, null, dto.PaymentNote, paidByUserId);
            }

            order.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
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

            assignment.IsPaid = false;
            assignment.PaidAmount = null;
            assignment.PaidVia = null;
            assignment.PaidAt = null;
            assignment.PaidByUserId = null;
            assignment.PaymentNote = null;

            order.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
            return BuildOrderRow(order);
        }

        // ===== internals =====

        private static void MarkPaid(
            OrderCleaner assignment,
            CleanerPayrollCalculator.CleanerLine? line,
            CleanerPaymentMethod? paidVia,
            string? note,
            int paidByUserId)
        {
            if (assignment.IsPaid) return;

            assignment.IsPaid = true;
            // Salary + tips: the cleaner is handed one figure, and that is what we record.
            assignment.PaidAmount = line?.Payout ?? 0m;
            assignment.PaidVia = paidVia ?? assignment.Cleaner?.PaymentMethod;
            assignment.PaidAt = DateTime.UtcNow;
            assignment.PaidByUserId = paidByUserId;
            assignment.PaymentNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
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
                .Where(o => o.Status == OrderStatuses.Done
                            || (o.Status == OrderStatuses.Refunded && o.StatusBeforeRefund == OrderStatuses.Done));

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

        private static bool HasCleanerService(Order order) =>
            order.OrderServices.Any(os => os.Service?.ServiceRelationType == "cleaner");

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
                    PaymentNote = assignment.PaymentNote
                };
            }).ToList();

            var totalSalary = payroll.TotalSalary;
            var totalPayout = OrderPricingCalculator.Round2(cleaners.Sum(c => c.Payout));

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
                OrderHourlyRate = order.CleanerHourlyRate,
                ExpectedHourlyRate = expectedRate,
                TotalSalary = totalSalary,
                TotalPayout = totalPayout,
                Cleaners = cleaners,
                IsFullyPaid = cleaners.Count > 0 && cleaners.All(c => c.IsPaid),
                IsPartiallyPaid = cleaners.Any(c => c.IsPaid) && cleaners.Any(c => !c.IsPaid)
            };

            row.Warnings = BuildWarnings(order, row, expectedRate);
            return row;
        }

        /// <summary>
        /// Everything a SuperAdmin should look at before paying. These NEVER block a payout — the
        /// job happened and the cleaner is owed either way; the warning exists so a mistake is
        /// noticed at the moment somebody is looking at the money, which is the one moment it
        /// reliably gets fixed.
        /// </summary>
        private static List<string> BuildWarnings(Order order, OutgoingPaymentOrderDto row, decimal expectedRate)
        {
            var warnings = new List<string>();

            if (row.Cleaners.Count == 0)
            {
                warnings.Add("No cleaners are assigned to this order, so nobody can be paid for it. "
                    + $"The reported labour cost is still the estimate for {Math.Max(1, order.MaidsCount)} cleaner(s).");
                return warnings;
            }

            // The rate warning is per DISTINCT rate: with mixed rates on one job, naming each one
            // is what tells the reader whether the odd one out was deliberate.
            var offRates = row.Cleaners
                .Where(c => c.HourlyRate != expectedRate)
                .Select(c => c.HourlyRate)
                .Distinct()
                .OrderBy(r => r)
                .ToList();

            if (offRates.Count > 0)
            {
                var listed = string.Join(", ", offRates.Select(r => $"${r:0.##}/hr"));
                warnings.Add($"Hourly rate is {listed}, but {row.ServiceTypeName} should default to ${expectedRate:0.##}/hr. "
                    + "Check whether this was intentional before paying.");
            }

            if (row.Cleaners.Count != order.MaidsCount)
            {
                warnings.Add($"{row.Cleaners.Count} cleaner(s) assigned but the order was priced for {order.MaidsCount}. "
                    + "Pay is split across the assigned cleaners, so the per-cleaner hours differ from the booking.");
            }

            if (order.TotalDuration <= 0)
                warnings.Add("This order has no duration recorded, so every cleaner's pay calculates to $0.");

            if (!row.IsPaidByCustomer)
                warnings.Add("The customer has not paid for this order yet.");

            return warnings;
        }

        private static OutgoingPaymentSummaryDto BuildSummary(List<OutgoingPaymentOrderDto> rows)
        {
            var lines = rows.SelectMany(r => r.Cleaners).ToList();

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
                UnpaidPayout = OrderPricingCalculator.Round2(lines.Where(l => !l.IsPaid).Sum(l => l.Payout)),
                UnpaidCleanerCount = lines.Count(l => !l.IsPaid),
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
    }
}
