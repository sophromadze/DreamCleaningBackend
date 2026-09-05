using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace DreamCleaningBackend.Services
{
    /// <inheritdoc />
    public class CleanerPortalService : ICleanerPortalService
    {
        private readonly ApplicationDbContext _context;

        public CleanerPortalService(ApplicationDbContext context)
        {
            _context = context;
        }

        /// <summary>
        /// The one query shape both views load. Every Include here is needed to BUILD the cleaner
        /// view - the service and extra names, the service type - and nothing more is loaded than
        /// that, so the SuperAdmin list stays the same weight as the cleaner one.
        /// </summary>
        private IQueryable<Order> JobsQuery() =>
            _context.Orders
                .AsNoTracking()
                .Include(o => o.ServiceType)
                .Include(o => o.OrderServices).ThenInclude(os => os.Service)
                .Include(o => o.OrderExtraServices).ThenInclude(oes => oes.ExtraService);

        public async Task<CleanerPortalMyJobsDto> GetMyJobsAsync(int cleanerId)
        {
            // One pass over this cleaner's assignments, split in memory. Two round trips would ask
            // the same index twice for a set that is a handful of rows per person.
            // OrderCleaners is Included here and not only filtered on: the hours a cleaner is told
            // and the instructions written for them both live on the assignment row, and the
            // payroll split needs every assignment on the order, not just this one.
            var orders = await JobsQuery()
                .Include(o => o.OrderCleaners)
                .Where(o => o.OrderCleaners.Any(oc => oc.CleanerId == cleanerId))
                .OrderBy(o => o.ServiceDate).ThenBy(o => o.ServiceTime)
                .ToListAsync();

            var result = new CleanerPortalMyJobsDto();

            foreach (var order in orders)
            {
                if (CleanerJobView.IsCurrentJob(order.Status))
                {
                    result.Current.Add(BuildJob(order, cleanerId));
                }
                else if (CleanerJobView.IsPastJob(order.Status, order.StatusBeforeRefund))
                {
                    // A finished job takes its place in the calendar so the month reads as a whole -
                    // the cleaner worked it. What it stops carrying is the customer's address:
                    // that was given out to get somebody to the door, and the job is over.
                    var past = BuildJob(order, cleanerId);
                    CleanerJobView.RedactCompletedJob(past);
                    result.Past.Add(past);
                }
                // Cancelled and refunded-before-service jobs fall through deliberately: the
                // cleaning never happened, so it belongs in neither list.
            }

            // History reads newest first - the job somebody is trying to remember is the recent one.
            result.Past = result.Past.OrderByDescending(p => p.ServiceDate).ToList();

            return result;
        }

        public async Task<List<CleanerPortalAdminJobDto>> GetAllJobsAsync(DateTime? from, DateTime? to, string? search)
        {
            var query = JobsQuery()
                .Include(o => o.OrderCleaners).ThenInclude(oc => oc.Cleaner)
                .AsQueryable();

            // ServiceDate is NY wall-clock, and the caller sends plain dates picked in NY, so these
            // compare directly with no timezone conversion - the same rule the statistics pages use.
            if (from.HasValue)
                query = query.Where(o => o.ServiceDate >= from.Value.Date);
            if (to.HasValue)
                query = query.Where(o => o.ServiceDate <= to.Value.Date);

            var term = (search ?? string.Empty).Trim().ToLowerInvariant();
            if (term.Length > 0)
            {
                query = query.Where(o =>
                    o.ContactFirstName.ToLower().Contains(term) ||
                    o.ContactLastName.ToLower().Contains(term) ||
                    o.ServiceAddress.ToLower().Contains(term) ||
                    o.City.ToLower().Contains(term) ||
                    o.OrderCleaners.Any(oc => oc.Cleaner != null &&
                        (oc.Cleaner.FirstName + " " + oc.Cleaner.LastName).ToLower().Contains(term)));
            }

            var orders = await query
                .OrderByDescending(o => o.ServiceDate).ThenByDescending(o => o.ServiceTime)
                .ToListAsync();

            // A cleaning that never happened is on nobody's calendar - cancelled, and refunded
            // before service, are dropped here exactly as they are from the cleaner's own month.
            // Filtered in memory rather than in the query so it is the SAME predicate the
            // cleaner's view splits on: a SQL rewrite of it would be a second copy of the rule,
            // free to disagree with the one it mirrors, and this is the screen where such a
            // disagreement shows up as a job one audience can see and the other cannot.
            var cleanings = orders
                .Where(o => CleanerJobView.BelongsOnTheCalendar(o.Status, o.StatusBeforeRefund));

            return cleanings.Select(order =>
            {
                // Widen the cleaner's own projection rather than re-listing its fields: a field
                // added to BuildJob has to reach this list too, and a hand-copied initializer is
                // exactly where one gets forgotten.
                var job = BuildJob(order);
                return new CleanerPortalAdminJobDto
                {
                    OrderId = job.OrderId,
                    ServiceDate = job.ServiceDate,
                    ServiceTime = job.ServiceTime,
                    ServiceTypeName = job.ServiceTypeName,
                    Services = job.Services,
                    ExtraServices = job.ExtraServices,
                    CustomerName = job.CustomerName,
                    Address = job.Address,
                    BringCleaningSupplies = job.BringCleaningSupplies,
                    BringCleaningEssentials = job.BringCleaningEssentials,
                    ServiceDurationMinutes = job.ServiceDurationMinutes,
                    PropertyType = job.PropertyType,
                    LevelsQuantity = job.LevelsQuantity,
                    FloorTypes = job.FloorTypes,
                    EntryMethod = job.EntryMethod,
                    CustomerInstructions = job.CustomerInstructions,
                    // Deliberately NOT carried: the per-cleaner note is addressed to one person,
                    // and a list row covering three of them has nobody to be addressing. The
                    // detail panel is where a SuperAdmin reads what each cleaner was told.
                    CleanerInstructions = null,
                    IsCompleted = job.IsCompleted,
                    Status = order.Status,
                    MaidsCount = order.MaidsCount,
                    IsPaid = order.IsPaid || order.PaymentMethod != PaymentMethod.Normal,
                    AssignedCleaners = order.OrderCleaners
                        .Where(oc => oc.Cleaner != null)
                        .Select(oc => $"{oc.Cleaner.FirstName} {oc.Cleaner.LastName}".Trim())
                        .OrderBy(n => n)
                        .ToList()
                };
            }).ToList();
        }

        public async Task<CleanerPortalOrderDetailDto?> GetOrderDetailAsync(int orderId)
        {
            // The extra Includes here are the ones OrderDtoMapper reads that the list does not
            // need: the owning user (its no-email resolution), the subscription name, and the
            // assigned admin's display name.
            var order = await _context.Orders
                .AsNoTracking()
                .Include(o => o.User)
                .Include(o => o.ServiceType)
                .Include(o => o.Subscription)
                .Include(o => o.AssignedAdmin)
                .Include(o => o.OrderServices).ThenInclude(os => os.Service)
                .Include(o => o.OrderExtraServices).ThenInclude(oes => oes.ExtraService)
                .Include(o => o.OrderCleaners).ThenInclude(oc => oc.Cleaner)
                .FirstOrDefaultAsync(o => o.Id == orderId);

            if (order == null) return null;

            var adminNotes = await _context.AdminOrderNotes
                .AsNoTracking()
                .Where(n => n.OrderId == orderId)
                .Select(n => n.Notes)
                .FirstOrDefaultAsync();

            return new CleanerPortalOrderDetailDto
            {
                CleanerView = BuildJob(order),
                // The SHARED assembler, not a second copy: this panel shows the same order the
                // admin panel does, so a field added to OrderDtoMapper shows up here too.
                Order = OrderDtoMapper.ToOrderDto(order),
                AdminNotes = adminNotes,
                AssignedCleaners = order.OrderCleaners
                    .Where(oc => oc.Cleaner != null)
                    .OrderBy(oc => oc.AssignedAt)
                    .Select(oc => new CleanerPortalAssignedCleanerDto
                    {
                        CleanerId = oc.CleanerId,
                        Name = $"{oc.Cleaner.FirstName} {oc.Cleaner.LastName}".Trim(),
                        Phone = oc.Cleaner.Phone,
                        Email = oc.Cleaner.Email,
                        AssignedAt = oc.AssignedAt,
                        AssignmentNotificationSentAt = oc.AssignmentNotificationSentAt
                    })
                    .ToList()
            };
        }

        /// <summary>
        /// THE cleaner-facing projection, used by both views so the SuperAdmin always sees exactly
        /// what the cleaner sees, plus their extra fields - never a differently-built version of it.
        ///
        /// <paramref name="cleanerId"/> is the person being addressed, when there is one. It picks
        /// the assignment row their hours and their instructions come from; the SuperAdmin's list
        /// passes null, because a row covering three cleaners is not addressed to any of them.
        /// </summary>
        private static CleanerPortalJobDto BuildJob(Order order, int? cleanerId = null)
        {
            var assignment = cleanerId.HasValue
                ? (order.OrderCleaners ?? new List<OrderCleaner>())
                    .FirstOrDefault(oc => oc.CleanerId == cleanerId.Value)
                : null;

            return new CleanerPortalJobDto
            {
                OrderId = order.Id,
                ServiceDate = order.ServiceDate,
                ServiceTime = $"{order.ServiceTime.Hours:D2}:{order.ServiceTime.Minutes:D2}",
                // Deep / Regular, never the raw "Residential Cleaning" - see CleanerJobView.
                ServiceTypeName = CleanerJobView.ResolveCleaningTypeName(order, "Cleaning"),
                Services = (order.OrderServices ?? new List<Models.OrderService>())
                    .Where(os => os.Service != null && !string.IsNullOrWhiteSpace(os.Service.Name))
                    // Levels prices as an ordinary service row but is REPORTED by the gated chip
                    // built from Order.LevelsQuantity, so the generic loop must not print it a
                    // second time. See CleanerJobView.IsServiceLineHiddenFromCleaners.
                    .Where(os => !CleanerJobView.IsServiceLineHiddenFromCleaners(os.Service!.ServiceKey))
                    .OrderBy(os => os.Id)
                    .Select(os => new CleanerPortalServiceLineDto
                    {
                        Name = os.Service!.Name,
                        Quantity = os.Quantity,
                        ServiceKey = os.Service.ServiceKey
                    })
                    .ToList(),
                ExtraServices = (order.OrderExtraServices ?? new List<OrderExtraService>())
                    .OrderBy(oes => oes.ExtraService?.DisplayOrder ?? 0)
                    .ThenBy(oes => oes.Id)
                    .Where(oes => !CleanerJobView.IsExtraHiddenFromCleaners(oes.ExtraService?.Name))
                    .Select(oes => FormatExtra(oes))
                    .ToList(),
                CustomerName = CleanerJobView.ResolveCustomerDisplayName(order),
                Address = CleanerJobView.BuildFullAddress(order),
                BringCleaningSupplies = CleanerJobView.RequiresCleanerToBringSupplies(order),
                BringCleaningEssentials = CleanerJobView.RequiresCleanerToBringEssentials(order),
                // THEIR payroll line - the hours they were told and are paid for. Falls back to the
                // automatic per-cleaner split when nobody in particular is being addressed.
                ServiceDurationMinutes = (int)CleanerPayrollCalculator
                    .ResolveBillableMinutesForCleaner(order, assignment?.CleanerId),
                PropertyType = PropertyDetailsHelper.NormalizePropertyType(order.PropertyType),
                LevelsQuantity = PropertyDetailsHelper.IsHouse(order.PropertyType) ? order.LevelsQuantity : null,
                FloorTypes = ParseFloorTypes(order.FloorTypes, order.FloorTypeOther),
                EntryMethod = Blank(order.EntryMethod),
                CustomerInstructions = Blank(order.SpecialInstructions),
                CleanerInstructions = Blank(assignment?.TipsForCleaner),
                IsCompleted = CleanerJobView.IsPastJob(order.Status, order.StatusBeforeRefund)
            };
        }

        private static string? Blank(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        /// <summary>
        /// Order.FloorTypes is a CSV that may carry an "other:free text" entry, with
        /// Order.FloorTypeOther holding the same text separately on older rows. Both shapes are
        /// flattened to plain display strings here, because a cleaner reading "other:terrazzo"
        /// would be reading our storage format rather than their job.
        /// </summary>
        private static List<string> ParseFloorTypes(string? floorTypes, string? floorTypeOther)
        {
            var result = new List<string>();
            var other = (floorTypeOther ?? string.Empty).Trim();

            foreach (var raw in (floorTypes ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var part = raw.Trim();
                if (part.Length == 0) continue;

                if (part.StartsWith("other:", StringComparison.OrdinalIgnoreCase))
                {
                    var text = part.Substring("other:".Length).Trim();
                    if (text.Length > 0) result.Add(text);
                    continue;
                }

                if (string.Equals(part, "other", StringComparison.OrdinalIgnoreCase))
                {
                    if (other.Length > 0) result.Add(other);
                    continue;
                }

                result.Add(part);
            }

            // A row that recorded only the free-text box still has something to say.
            if (result.Count == 0 && other.Length > 0) result.Add(other);

            return result;
        }

        /// <summary>
        /// An extra named the way the assignment email names it: hours when the extra is measured
        /// in hours, a multiplier when more than one was bought, the bare name otherwise. Never a
        /// price - a cleaner is told what work is included, not what the customer paid for it.
        /// </summary>
        private static string FormatExtra(OrderExtraService orderExtra)
        {
            var name = orderExtra.ExtraService!.Name.Trim();

            if (orderExtra.ExtraService.HasHours && orderExtra.Hours > 0)
            {
                var minutes = (int)Math.Round(orderExtra.Hours * 60, MidpointRounding.AwayFromZero);
                return $"{name} ({DurationFormat(minutes)})";
            }

            if (orderExtra.ExtraService.HasQuantity && orderExtra.Quantity > 1)
                return $"{name} x {orderExtra.Quantity}";

            return name;
        }

        private static string DurationFormat(int minutes)
        {
            var hours = minutes / 60;
            var mins = minutes % 60;
            if (hours == 0) return $"{mins}m";
            return mins == 0 ? $"{hours}h" : $"{hours}h {mins}m";
        }
    }
}
