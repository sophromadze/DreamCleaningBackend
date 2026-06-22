using DreamCleaningBackend.Attributes;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace DreamCleaningBackend.Controllers.Crm
{
    /// <summary>
    /// CRM Customer 360 + Segments. A computed layer over existing Users/Orders/Subscriptions —
    /// it does NOT duplicate customer data, it derives lifecycle stage, segments, and lifetime
    /// value on the fly. Manual <see cref="CustomerTag"/>s are the only data this owns.
    /// Per-customer notes/communications continue to live in AdminUserCareController.
    /// </summary>
    [Route("api/crm/customers")]
    [ApiController]
    [Authorize(Roles = "Admin,SuperAdmin,Moderator")]
    public class CrmCustomersController : ControllerBase
    {
        private readonly ApplicationDbContext _context;

        // Segment thresholds. Kept as named constants so the rules are auditable in one place.
        private const int NewDays = 30;
        private const int ActiveDays = 60;
        private const int AtRiskDays = 180;
        private const decimal VipMinSpent = 1000m;
        private const int VipMinOrders = 5;

        public CrmCustomersController(ApplicationDbContext context)
        {
            _context = context;
        }

        // ─────────────────────────────────────────────────────────
        //  CUSTOMER LIST (with computed segments)
        // ─────────────────────────────────────────────────────────

        [HttpGet]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<object>> GetCustomers(
            [FromQuery] string? search,
            [FromQuery] string? segment,
            [FromQuery] string sort = "recent",
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 25)
        {
            var computed = await BuildComputedCustomers(search);

            if (!string.IsNullOrWhiteSpace(segment))
                computed = computed.Where(c => c.Segments.Contains(segment)).ToList();

            computed = (sort switch
            {
                "value" => computed.OrderByDescending(c => c.LifetimeValue),
                "orders" => computed.OrderByDescending(c => c.OrderCount),
                "name" => computed.OrderBy(c => c.FullName),
                "oldest" => computed.OrderBy(c => c.CreatedAt),
                _ => computed.OrderByDescending(c => c.LastOrderDate ?? c.CreatedAt)
            }).ToList();

            var total = computed.Count;
            var pageItems = computed
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToList();

            return Ok(new
            {
                total,
                page,
                pageSize,
                items = pageItems
            });
        }

        // ─────────────────────────────────────────────────────────
        //  SEGMENTS (card counts for the Segments tab)
        // ─────────────────────────────────────────────────────────

        [HttpGet("segments")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<List<CrmSegmentDto>>> GetSegments()
        {
            var computed = await BuildComputedCustomers(null);

            var defs = new (string Key, string Label, string Description)[]
            {
                ("new",       "New",            $"Acquired in the last {NewDays} days"),
                ("active",    "Active",         $"Ordered within {ActiveDays} days"),
                ("recurring", "Recurring",      "On an active subscription"),
                ("vip",       "VIP",            $"≥ {VipMinOrders} orders or ${VipMinSpent:N0}+ spent"),
                ("one_time",  "One-time",       "Has ordered but no subscription"),
                ("at_risk",   "At-risk",        $"No order in {ActiveDays}–{AtRiskDays} days"),
                ("churned",   "Churned",        $"No order in {AtRiskDays}+ days"),
                ("prospect",  "Prospect",       "Registered, no orders yet")
            };

            var result = defs.Select(d =>
            {
                var members = computed.Where(c => c.Segments.Contains(d.Key)).ToList();
                return new CrmSegmentDto
                {
                    Key = d.Key,
                    Label = d.Label,
                    Description = d.Description,
                    Count = members.Count,
                    TotalValue = members.Sum(m => m.LifetimeValue)
                };
            }).ToList();

            return Ok(result);
        }

        // ─────────────────────────────────────────────────────────
        //  CUSTOMER 360 DETAIL
        // ─────────────────────────────────────────────────────────

        [HttpGet("{id}")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<CrmCustomerDetailDto>> GetCustomer(int id)
        {
            var user = await _context.Users
                .Include(u => u.Subscription)
                .FirstOrDefaultAsync(u => u.Id == id && !u.IsDeleted);

            if (user == null) return NotFound(new { message = "Customer not found" });

            var orders = await _context.Orders
                .Where(o => o.UserId == id)
                .Include(o => o.ServiceType)
                .OrderByDescending(o => o.ServiceDate)
                .ToListAsync();

            var tags = await GetTagsForUsers(new[] { id });

            // Source of truth is the Orders table — see BuildComputedCustomers. The denormalized
            // User fields are only a fallback when the customer has no non-cancelled orders.
            var nonCancelled = orders.Where(o => o.Status != "cancelled").ToList();
            var orderCount = nonCancelled.Count;
            var ltv = nonCancelled.Count > 0 ? nonCancelled.Sum(o => o.Total) : user.TotalSpentAmount;
            var lastOrder = nonCancelled.Count > 0 ? nonCancelled.Max(o => (DateTime?)o.ServiceDate) : user.LastOrderDate;
            var firstOrder = nonCancelled.Count > 0 ? nonCancelled.Min(o => (DateTime?)o.ServiceDate) : null;
            var isSubscribed = IsSubscribed(user);

            var lifecycle = ComputeLifecycle(orderCount, user.CreatedAt, lastOrder);
            var segments = ComputeSegments(lifecycle, isSubscribed, ltv, orderCount);

            var detail = new CrmCustomerDetailDto
            {
                Id = user.Id,
                FirstName = user.FirstName,
                LastName = user.LastName,
                FullName = $"{user.FirstName} {user.LastName}".Trim(),
                Email = user.Email,
                Phone = user.Phone,
                LifetimeValue = ltv,
                OrderCount = orderCount,
                LastOrderDate = lastOrder,
                FirstOrderDate = firstOrder,
                CreatedAt = user.CreatedAt,
                IsSubscribed = isSubscribed,
                SubscriptionName = isSubscribed ? user.Subscription?.Name : null,
                BubblePoints = user.BubblePoints,
                BubbleCredits = user.BubbleCredits,
                ConsecutiveOrderCount = user.ConsecutiveOrderCount,
                LoyaltyDiscountPercentage = user.LoyaltyDiscountPercentage,
                CanReceiveEmails = user.CanReceiveEmails,
                CanReceiveMessages = user.CanReceiveMessages,
                AverageOrderValue = orderCount > 0 ? Math.Round(ltv / orderCount, 2) : 0,
                LifecycleStage = lifecycle,
                Segments = segments,
                Tags = tags.TryGetValue(id, out var t) ? t : new List<CustomerTagDto>(),
                RecentOrders = orders.Take(5).Select(o => new CrmCustomerOrderDto
                {
                    Id = o.Id,
                    ServiceDate = o.ServiceDate,
                    Total = o.Total,
                    Status = o.Status,
                    ServiceTypeName = o.GetDisplayServiceTypeName(),
                    ServiceAddress = o.ServiceAddress
                }).ToList()
            };

            return Ok(detail);
        }

        // ─────────────────────────────────────────────────────────
        //  TAGS
        // ─────────────────────────────────────────────────────────

        /// <summary>Distinct tag labels already in use — drives the add-tag suggestions.</summary>
        [HttpGet("tags/suggestions")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<List<string>>> GetTagSuggestions()
        {
            var labels = await _context.CustomerTags
                .Select(t => t.Label)
                .Distinct()
                .OrderBy(l => l)
                .Take(50)
                .ToListAsync();
            return Ok(labels);
        }

        [HttpPost("{id}/tags")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult<CustomerTagDto>> AddTag(int id, [FromBody] CreateCustomerTagDto dto)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);

            var userExists = await _context.Users.AnyAsync(u => u.Id == id && !u.IsDeleted);
            if (!userExists) return NotFound(new { message = "Customer not found" });

            var label = dto.Label.Trim();
            var dup = await _context.CustomerTags.AnyAsync(t => t.UserId == id && t.Label == label);
            if (dup) return Conflict(new { message = "This customer already has that tag." });

            var tag = new CustomerTag
            {
                UserId = id,
                Label = label,
                Color = string.IsNullOrWhiteSpace(dto.Color) ? null : dto.Color.Trim(),
                CreatedByAdminId = GetUserId(),
                CreatedByAdminName = GetUserDisplayName(),
                CreatedAt = DateTime.UtcNow
            };
            _context.CustomerTags.Add(tag);
            await _context.SaveChangesAsync();

            return Ok(MapTag(tag));
        }

        [HttpDelete("tags/{tagId}")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult> DeleteTag(int tagId)
        {
            var tag = await _context.CustomerTags.FirstOrDefaultAsync(t => t.Id == tagId);
            if (tag == null) return NotFound(new { message = "Tag not found" });
            _context.CustomerTags.Remove(tag);
            await _context.SaveChangesAsync();
            return Ok(new { message = "Tag removed" });
        }

        // ─────────────────────────────────────────────────────────
        //  Computation
        // ─────────────────────────────────────────────────────────

        /// <summary>
        /// Loads customers (optionally search-filtered) and decorates each with order aggregates,
        /// lifecycle stage, segments and tags. Runs three bounded queries (users, order aggregates,
        /// tags) then composes in memory — fine for the customer volumes this business sees.
        /// </summary>
        private async Task<List<CrmCustomerListItemDto>> BuildComputedCustomers(string? search)
        {
            var usersQuery = _context.Users
                .Where(u => u.Role == UserRole.Customer && !u.IsDeleted);

            if (!string.IsNullOrWhiteSpace(search))
            {
                var q = search.Trim().ToLower();
                usersQuery = usersQuery.Where(u =>
                    u.FirstName.ToLower().Contains(q) ||
                    u.LastName.ToLower().Contains(q) ||
                    u.Email.ToLower().Contains(q) ||
                    (u.Phone != null && u.Phone.Contains(q)));
            }

            var users = await usersQuery
                .Select(u => new
                {
                    u.Id, u.FirstName, u.LastName, u.Email, u.Phone, u.CreatedAt,
                    u.TotalSpentAmount, u.LastOrderDate, u.BubblePoints,
                    u.SubscriptionId, SubName = u.Subscription != null ? u.Subscription.Name : null,
                    SubDays = u.Subscription != null ? u.Subscription.SubscriptionDays : 0,
                    u.SubscriptionExpiryDate
                })
                .ToListAsync();

            var ids = users.Select(u => u.Id).ToList();

            // Order aggregates (exclude cancelled) grouped by user.
            var aggregates = await _context.Orders
                .Where(o => ids.Contains(o.UserId) && o.Status != "cancelled")
                .GroupBy(o => o.UserId)
                .Select(g => new
                {
                    UserId = g.Key,
                    Count = g.Count(),
                    Sum = g.Sum(o => o.Total),
                    MaxDate = g.Max(o => o.ServiceDate)
                })
                .ToListAsync();
            var aggMap = aggregates.ToDictionary(a => a.UserId);

            var tagMap = await GetTagsForUsers(ids);
            var now = DateTime.UtcNow;

            var result = new List<CrmCustomerListItemDto>(users.Count);
            foreach (var u in users)
            {
                aggMap.TryGetValue(u.Id, out var agg);
                var orderCount = agg?.Count ?? 0;
                // Source of truth is the Orders table. The denormalized User.TotalSpentAmount /
                // LastOrderDate fields can be stale or partial, so they're only a fallback for
                // customers who have no (non-cancelled) orders to aggregate.
                var ltv = agg != null ? agg.Sum : u.TotalSpentAmount;
                var lastOrder = agg?.MaxDate ?? u.LastOrderDate;
                var isSubscribed = u.SubscriptionId != null && u.SubDays > 0 &&
                    (u.SubscriptionExpiryDate == null || u.SubscriptionExpiryDate >= now);

                var lifecycle = ComputeLifecycle(orderCount, u.CreatedAt, lastOrder);
                var segments = ComputeSegments(lifecycle, isSubscribed, ltv, orderCount);

                result.Add(new CrmCustomerListItemDto
                {
                    Id = u.Id,
                    FirstName = u.FirstName,
                    LastName = u.LastName,
                    FullName = $"{u.FirstName} {u.LastName}".Trim(),
                    Email = u.Email,
                    Phone = u.Phone,
                    LifetimeValue = ltv,
                    OrderCount = orderCount,
                    LastOrderDate = lastOrder,
                    CreatedAt = u.CreatedAt,
                    IsSubscribed = isSubscribed,
                    SubscriptionName = isSubscribed ? u.SubName : null,
                    BubblePoints = u.BubblePoints,
                    LifecycleStage = lifecycle,
                    Segments = segments,
                    Tags = tagMap.TryGetValue(u.Id, out var t) ? t : new List<CustomerTagDto>()
                });
            }

            return result;
        }

        private static bool IsSubscribed(User u)
        {
            if (u.SubscriptionId == null || u.Subscription == null || u.Subscription.SubscriptionDays <= 0)
                return false;
            return u.SubscriptionExpiryDate == null || u.SubscriptionExpiryDate >= DateTime.UtcNow;
        }

        /// <summary>Single funnel stage. Prospect (no orders) → New → Active → AtRisk → Churned.</summary>
        private static string ComputeLifecycle(int orderCount, DateTime createdAt, DateTime? lastOrder)
        {
            var now = DateTime.UtcNow;
            if (orderCount == 0) return "Prospect";

            if ((now - createdAt).TotalDays <= NewDays && orderCount <= 1) return "New";

            if (lastOrder.HasValue)
            {
                var days = (now - lastOrder.Value).TotalDays;
                if (days <= ActiveDays) return "Active";
                if (days <= AtRiskDays) return "AtRisk";
                return "Churned";
            }
            return "Active";
        }

        private static List<string> ComputeSegments(string lifecycle, bool isSubscribed, decimal ltv, int orderCount)
        {
            var segs = new List<string>();
            switch (lifecycle)
            {
                case "Prospect": segs.Add("prospect"); break;
                case "New": segs.Add("new"); break;
                case "Active": segs.Add("active"); break;
                case "AtRisk": segs.Add("at_risk"); break;
                case "Churned": segs.Add("churned"); break;
            }
            if (isSubscribed) segs.Add("recurring");
            if (ltv >= VipMinSpent || orderCount >= VipMinOrders) segs.Add("vip");
            if (orderCount >= 1 && !isSubscribed) segs.Add("one_time");
            return segs;
        }

        private async Task<Dictionary<int, List<CustomerTagDto>>> GetTagsForUsers(IEnumerable<int> userIds)
        {
            var ids = userIds.ToList();
            if (ids.Count == 0) return new();

            var tags = await _context.CustomerTags
                .Where(t => ids.Contains(t.UserId))
                .OrderBy(t => t.Label)
                .ToListAsync();

            return tags
                .GroupBy(t => t.UserId)
                .ToDictionary(g => g.Key, g => g.Select(MapTag).ToList());
        }

        private static CustomerTagDto MapTag(CustomerTag t) => new()
        {
            Id = t.Id,
            UserId = t.UserId,
            Label = t.Label,
            Color = t.Color,
            CreatedByAdminName = t.CreatedByAdminName,
            CreatedAt = t.CreatedAt
        };

        private int GetUserId()
        {
            var id = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            return int.TryParse(id, out var parsed) ? parsed : 0;
        }

        private string GetUserDisplayName()
        {
            var first = User.FindFirst(ClaimTypes.GivenName)?.Value ?? User.FindFirst("FirstName")?.Value;
            var last = User.FindFirst(ClaimTypes.Surname)?.Value ?? User.FindFirst("LastName")?.Value;
            var combined = $"{first} {last}".Trim();
            if (!string.IsNullOrWhiteSpace(combined)) return combined;
            return User.FindFirst(ClaimTypes.Name)?.Value
                ?? User.FindFirst(ClaimTypes.Email)?.Value
                ?? "Admin";
        }
    }
}
