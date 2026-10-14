using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace DreamCleaningBackend.Services
{
    public class AdminBonusService : IAdminBonusService
    {
        private readonly ApplicationDbContext _context;

        // GEL per USD for a month, used to put the percentage bonus (earned on dollar prices) on
        // the same GEL footing as the fixed bonuses. Resolved LAZILY from the container:
        // FinancialRateService takes this service in its constructor, so a constructor dependency
        // the other way round would be a cycle DI refuses to build.
        private readonly Func<int, int, Task<decimal>> _usdPerGel;

        public AdminBonusService(ApplicationDbContext context, IServiceProvider services)
            : this(context, async (year, month) =>
                (await services.GetRequiredService<IFinancialRateService>().GetOrCreateAsync(year, month)).UsdPerGel)
        {
        }

        /// <summary>Tests: a fixed exchange rate instead of the snapshot table.</summary>
        public AdminBonusService(ApplicationDbContext context, Func<int, int, Task<decimal>> usdPerGel)
        {
            _context = context;
            _usdPerGel = usdPerGel;
        }

        public async Task<OrderAssignedAdminDto> AssignAdminAsync(int orderId, int? adminId, int byUserId)
        {
            var order = await _context.Orders.FirstOrDefaultAsync(o => o.Id == orderId);
            if (order == null)
                throw new InvalidOperationException("Order not found.");

            // Position and manager are read here so the attribution can be snapshotted onto the
            // order alongside the assignment itself — see the comment on Order.BonusBookerId.
            AdminPosition position = AdminPosition.Administrator;
            int? assigneeManagerId = null;
            var assigneeIsSuperAdmin = false;

            if (adminId.HasValue)
            {
                // Admin or SuperAdmin — the create-for-user flow auto-assigns the creating
                // staff member, and the owner (SuperAdmin) creates orders too.
                var admin = await _context.Users
                    .Where(u => u.Id == adminId.Value
                                && !u.IsDeleted
                                && u.IsActive
                                && (u.Role == UserRole.Admin || u.Role == UserRole.SuperAdmin))
                    .Select(u => new { u.Id, u.AdminPosition, u.ManagerId, u.Role })
                    .FirstOrDefaultAsync();
                if (admin == null)
                    throw new InvalidOperationException("Selected user is not an active admin.");

                position = admin.AdminPosition;
                assigneeManagerId = admin.ManagerId;
                assigneeIsSuperAdmin = admin.Role == UserRole.SuperAdmin;
            }

            var previousAdminId = order.AssignedAdminId;
            if (previousAdminId == adminId)
            {
                // No-op — return the current state without writing history noise. The stored
                // attribution is deliberately left ALONE here rather than re-resolved: re-selecting
                // the same person is not a reason to move money that was already earned, and
                // re-resolving would let a later promotion or manager change rewrite it.
                return await BuildAssignedAdminDtoAsync(adminId);
            }

            // A SuperAdmin is never paid a staff bonus (owner's rule, 2026-10): they stay the
            // order's assigned admin — the pill, the responsibility — but nobody earns on it, so it
            // costs nothing in Statistics and Finances either. Migration
            // ClearSuperAdminBonusAttribution applied the same rule to orders already held.
            var attribution = adminId.HasValue && !assigneeIsSuperAdmin
                ? AdminBonusAttribution.Resolve(position, adminId.Value, assigneeManagerId)
                : BonusAttribution.None;

            order.AssignedAdminId = adminId;
            order.BonusBookerId = attribution.BookerId;
            order.BonusBookerPosition = attribution.BookerPosition;
            order.BonusManagerId = attribution.ManagerId;
            order.UpdatedAt = DateTime.UtcNow;

            _context.OrderAdminAssignmentHistories.Add(new OrderAdminAssignmentHistory
            {
                OrderId = orderId,
                PreviousAdminId = previousAdminId,
                NewAdminId = adminId,
                ChangedByUserId = byUserId,
                ChangedAt = DateTime.UtcNow,
                // What THIS person earns for THIS order, at today's rates — the one number a
                // dispute about this assignment would need. Zero when the assignment was cleared.
                BonusRateAtChange = adminId.HasValue && !assigneeIsSuperAdmin
                    ? await ResolveBookerRateForOrderAsync(adminId.Value, position, order.IsNewCustomerOrder)
                    : 0m
            });

            await _context.SaveChangesAsync();
            return await BuildAssignedAdminDtoAsync(adminId);
        }

        public async Task<AdminBonusRatesDto> GetRatesAsync()
        {
            var setting = await EnsureSettingAsync();
            string? updatedByName = null;
            if (setting.UpdatedByUserId.HasValue)
            {
                updatedByName = await _context.Users
                    .Where(u => u.Id == setting.UpdatedByUserId.Value)
                    .Select(u => u.FirstName + " " + u.LastName)
                    .FirstOrDefaultAsync();
            }

            return new AdminBonusRatesDto
            {
                AdministratorNewCustomerRate = setting.AdministratorNewCustomerRate,
                AdministratorExistingCustomerRate = setting.AdministratorExistingCustomerRate,
                ManagerOwnBookingNewCustomerRate = setting.ManagerOwnBookingNewCustomerRate,
                ManagerOwnBookingExistingCustomerRate = setting.ManagerOwnBookingExistingCustomerRate,
                ManagerTeamNewCustomerRate = setting.ManagerTeamNewCustomerRate,
                ManagerTeamExistingCustomerRate = setting.ManagerTeamExistingCustomerRate,
                Currency = setting.Currency,
                UpdatedAt = setting.UpdatedAt,
                UpdatedByUserId = setting.UpdatedByUserId,
                UpdatedByUserName = updatedByName
            };
        }

        public async Task<AdminBonusRatesDto> SetRatesAsync(SetAdminBonusRatesDto dto, int byUserId)
        {
            var values = new[]
            {
                dto.AdministratorNewCustomerRate,
                dto.AdministratorExistingCustomerRate,
                dto.ManagerOwnBookingNewCustomerRate,
                dto.ManagerOwnBookingExistingCustomerRate,
                dto.ManagerTeamNewCustomerRate,
                dto.ManagerTeamExistingCustomerRate
            };
            if (values.Any(v => v < 0))
                throw new InvalidOperationException("Bonus rates cannot be negative.");

            var setting = await EnsureSettingAsync();
            setting.AdministratorNewCustomerRate = dto.AdministratorNewCustomerRate;
            setting.AdministratorExistingCustomerRate = dto.AdministratorExistingCustomerRate;
            setting.ManagerOwnBookingNewCustomerRate = dto.ManagerOwnBookingNewCustomerRate;
            setting.ManagerOwnBookingExistingCustomerRate = dto.ManagerOwnBookingExistingCustomerRate;
            setting.ManagerTeamNewCustomerRate = dto.ManagerTeamNewCustomerRate;
            setting.ManagerTeamExistingCustomerRate = dto.ManagerTeamExistingCustomerRate;
            setting.UpdatedByUserId = byUserId;
            setting.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            return await GetRatesAsync();
        }

        /// <summary>Applies the override and returns what it replaced, for the audit entry.</summary>
        public async Task<SetAdminBonusOverrideDto> SetRateOverrideAsync(
            int adminId, SetAdminBonusOverrideDto dto, int byUserId)
        {
            var submitted = new[]
            {
                dto.OwnBookingNewCustomerRate,
                dto.OwnBookingExistingCustomerRate,
                dto.TeamBookingNewCustomerRate,
                dto.TeamBookingExistingCustomerRate
            };
            if (submitted.Any(v => v < 0))
                throw new InvalidOperationException("Bonus rates cannot be negative.");

            var exists = await _context.Users.AnyAsync(u => u.Id == adminId && !u.IsDeleted);
            if (!exists)
                throw new InvalidOperationException("Admin not found.");

            var existing = await _context.AdminBonusRateOverrides
                .FirstOrDefaultAsync(o => o.UserId == adminId);

            // Captured before anything moves. Nulls here mean the person was following the company
            // defaults, which is exactly what the audit trail needs to distinguish from a rate that
            // happened to equal them.
            var previous = new SetAdminBonusOverrideDto
            {
                OwnBookingNewCustomerRate = existing?.OwnBookingNewCustomerRate,
                OwnBookingExistingCustomerRate = existing?.OwnBookingExistingCustomerRate,
                TeamBookingNewCustomerRate = existing?.TeamBookingNewCustomerRate,
                TeamBookingExistingCustomerRate = existing?.TeamBookingExistingCustomerRate
            };

            // Every field null means "follow the company defaults again" — the row is removed rather
            // than left holding four nulls, so the panel's "own rate" marker and the row's existence
            // can never disagree.
            if (submitted.All(v => v == null))
            {
                if (existing != null)
                {
                    _context.AdminBonusRateOverrides.Remove(existing);
                    await _context.SaveChangesAsync();
                }
                return previous;
            }

            if (existing == null)
            {
                existing = new AdminBonusRateOverride { UserId = adminId };
                _context.AdminBonusRateOverrides.Add(existing);
            }

            existing.OwnBookingNewCustomerRate = dto.OwnBookingNewCustomerRate;
            existing.OwnBookingExistingCustomerRate = dto.OwnBookingExistingCustomerRate;
            existing.TeamBookingNewCustomerRate = dto.TeamBookingNewCustomerRate;
            existing.TeamBookingExistingCustomerRate = dto.TeamBookingExistingCustomerRate;
            existing.UpdatedByUserId = byUserId;
            existing.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
            return previous;
        }

        public async Task<List<AdminBonusSummaryDto>> GetBonusesAsync(
            DateTime from,
            DateTime to,
            int viewerUserId,
            bool viewerIsSuperAdmin,
            int? adminIdFilter = null)
        {
            // Base admin list: all active admins, or just the viewer if not SuperAdmin.
            var adminsQuery = _context.Users
                .Where(u => !u.IsDeleted && u.IsActive && u.Role == UserRole.Admin);

            if (!viewerIsSuperAdmin)
                adminsQuery = adminsQuery.Where(u => u.Id == viewerUserId);
            else if (adminIdFilter.HasValue)
                adminsQuery = adminsQuery.Where(u => u.Id == adminIdFilter.Value);

            var admins = await adminsQuery
                // Managers first, then by name — the panel reads as a manager followed by the
                // administrators whose work they earn a share of.
                .OrderByDescending(u => u.AdminPosition == AdminPosition.Manager)
                .ThenBy(u => u.FirstName)
                .ThenBy(u => u.LastName)
                .Select(u => new AdminRow
                {
                    Id = u.Id,
                    FirstName = u.FirstName,
                    LastName = u.LastName,
                    ShiftColor = u.ShiftColor,
                    Position = u.AdminPosition,
                    ManagerId = u.ManagerId,
                    ManagerName = u.Manager != null ? u.Manager.FirstName + " " + u.Manager.LastName : null
                })
                .ToListAsync();

            var window = _context.Orders.Where(o => o.ServiceDate >= from && o.ServiceDate < to);
            var counts = await AggregateAsync(window);

            var teamSizes = await _context.Users
                .Where(u => !u.IsDeleted && u.IsActive && u.Role == UserRole.Admin && u.ManagerId != null)
                .GroupBy(u => u.ManagerId.Value)
                .Select(g => new { ManagerId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.ManagerId, x => x.Count);

            var setting = await EnsureSettingAsync();
            var adminIds = admins.Select(a => a.Id).ToList();
            var overrides = await LoadOverridesAsync(adminIds);
            var percent = await PercentByAdminAsync(from, to, adminIds);

            return admins
                .Select(a => BuildSummary(a, counts, setting, overrides, teamSizes, percent))
                .ToList();
        }

        public async Task<AdminBonusSummaryDto> GetSummaryForAdminAsync(int adminId, DateTime? from, DateTime? to)
        {
            var admin = await _context.Users
                .Where(u => u.Id == adminId && !u.IsDeleted)
                .Select(u => new AdminRow
                {
                    Id = u.Id,
                    FirstName = u.FirstName,
                    LastName = u.LastName,
                    ShiftColor = u.ShiftColor,
                    Position = u.AdminPosition,
                    ManagerId = u.ManagerId,
                    ManagerName = u.Manager != null ? u.Manager.FirstName + " " + u.Manager.LastName : null
                })
                .FirstOrDefaultAsync();
            if (admin == null)
                throw new InvalidOperationException("Admin not found.");

            var window = _context.Orders.AsQueryable();
            if (from.HasValue) window = window.Where(o => o.ServiceDate >= from.Value);
            if (to.HasValue) window = window.Where(o => o.ServiceDate < to.Value);
            // Narrowed to this one person BEFORE aggregating — the all-time variant this backs
            // (the user-profile stat sends no dates) would otherwise group the whole Orders table.
            window = window.Where(o => o.BonusBookerId == adminId || o.BonusManagerId == adminId);

            var counts = await AggregateAsync(window);
            var setting = await EnsureSettingAsync();
            var overrides = await LoadOverridesAsync(new List<int> { adminId });

            var teamSize = await _context.Users
                .CountAsync(u => !u.IsDeleted && u.IsActive && u.Role == UserRole.Admin && u.ManagerId == adminId);
            var percent = await PercentByAdminAsync(from, to, new List<int> { adminId });

            return BuildSummary(admin, counts, setting, overrides,
                new Dictionary<int, int> { [adminId] = teamSize }, percent);
        }

        public async Task<Dictionary<int, decimal>> GetOrderBonusCostsGelAsync(
            DateTime? from, DateTime? to, bool includeUnfinished = false)
        {
            var query = _context.Orders
                .Where(o => o.BonusBookerId != null || o.BonusManagerId != null)
                .Where(includeUnfinished
                    ? AdminBonusAttribution.BonusEligibleOrProjected
                    : AdminBonusAttribution.BonusEligible);
            if (from.HasValue) query = query.Where(o => o.ServiceDate >= from.Value);
            if (to.HasValue) query = query.Where(o => o.ServiceDate < to.Value);

            var orders = await query
                .Select(o => new FixedBonusInput(
                    o.Id, o.BonusBookerId, o.BonusBookerPosition, o.BonusManagerId, o.IsNewCustomerOrder))
                .ToListAsync();
            if (orders.Count == 0)
                return new Dictionary<int, decimal>();

            var costs = await FixedCostsGelAsync(orders);

            // The percentage bonus is the SAME kind of cost — money paid to staff because of an
            // order — so it rides in the same per-order figure. That is what makes it reach
            // Statistics, Finances, the shifts panel and the salary payout once each, through the
            // paths those already take, with nothing added at any of them. Its orders are always a
            // subset of the ones above (same window, same eligibility, booker set).
            foreach (var line in await ComputePercentBonusesAsync(from, to, includeUnfinished, onlyUsers: null))
                costs[line.OrderId] = (costs.TryGetValue(line.OrderId, out var c) ? c : 0m) + line.Gel;

            return costs;
        }

        // ── Percentage bonuses (2026-10) ───────────────────────────────────────────────

        public async Task<List<AdminPercentBonusRuleDto>> GetPercentRulesAsync(int adminId)
        {
            var rules = await _context.AdminPercentBonusRules
                .Where(r => r.UserId == adminId)
                .Include(r => r.CreatedByUser)
                .ToListAsync();
            return ToRuleDtos(rules);
        }

        public async Task<AdminPercentBonusRuleDto> AddPercentRuleAsync(int adminId, SetAdminPercentBonusDto dto, int byUserId)
        {
            if (dto.NewCustomerPercent < 0m || dto.NewCustomerPercent > 100m
                || dto.ExistingCustomerPercent < 0m || dto.ExistingCustomerPercent > 100m)
                throw new InvalidOperationException("Each percentage must be between 0 and 100.");
            if (!Enum.TryParse<AdminPercentBonusBasis>(dto.Basis, ignoreCase: true, out var basis)
                || !Enum.IsDefined(basis)
                || int.TryParse(dto.Basis, out _))
                throw new InvalidOperationException("Pick what the percentage is taken from.");

            // Admins only — the same people the fixed bonus pays. A SuperAdmin earns no bonus at
            // all (owner's rule), and a rule parked on them would read as money owed.
            var isAdmin = await _context.Users.AnyAsync(u => u.Id == adminId && !u.IsDeleted && u.Role == UserRole.Admin);
            if (!isAdmin)
                throw new InvalidOperationException("Percentage bonuses can only be given to admins.");

            var rule = new AdminPercentBonusRule
            {
                UserId = adminId,
                Basis = basis,
                NewCustomerPercent = decimal.Round(dto.NewCustomerPercent, 2),
                ExistingCustomerPercent = decimal.Round(dto.ExistingCustomerPercent, 2),
                StartDate = (dto.StartDate ?? NyTimeHelper.NowNy).Date,
                CreatedByUserId = byUserId,
                CreatedAt = DateTime.UtcNow
            };
            _context.AdminPercentBonusRules.Add(rule);
            await _context.SaveChangesAsync();
            await _context.Entry(rule).Reference(r => r.CreatedByUser).LoadAsync();
            return ToRuleDtos(new List<AdminPercentBonusRule> { rule }).Single();
        }

        public async Task<AdminPercentBonusRule?> DeletePercentRuleAsync(int adminId, int ruleId)
        {
            var rule = await _context.AdminPercentBonusRules
                .FirstOrDefaultAsync(r => r.Id == ruleId && r.UserId == adminId);
            if (rule == null) return null;
            _context.AdminPercentBonusRules.Remove(rule);
            await _context.SaveChangesAsync();
            return rule;
        }

        /// <summary>
        /// The percentage bonus on every qualifying order in the window: the order's BOOKER, a rule
        /// in force on its service date, and the same eligibility the fixed bonus uses. One line
        /// per order, in USD (what it was computed on) and GEL (what bonuses are paid and reported
        /// in, at the order month's rate). GEL is left unrounded so the reports, which convert it
        /// back at that same rate, land on the dollar figure.
        /// </summary>
        private async Task<List<PercentBonusLine>> ComputePercentBonusesAsync(
            DateTime? from, DateTime? to, bool includeUnfinished, IReadOnlyCollection<int>? onlyUsers)
        {
            var ruleQuery = _context.AdminPercentBonusRules.AsQueryable();
            if (onlyUsers != null)
                ruleQuery = ruleQuery.Where(r => onlyUsers.Contains(r.UserId));
            var rules = await ruleQuery.ToListAsync();
            // Nothing set anywhere is the normal case — no order query at all.
            if (!rules.Any(r => r.PaysAnything))
                return new List<PercentBonusLine>();

            var rulesByUser = rules.GroupBy(r => r.UserId).ToDictionary(g => g.Key, g => g.ToList());
            var userIds = rulesByUser.Keys.ToList();
            var earliest = rules.Min(r => r.StartDate.Date);

            var query = _context.Orders
                .Where(o => o.BonusBookerId != null && userIds.Contains(o.BonusBookerId.Value))
                .Where(includeUnfinished
                    ? AdminBonusAttribution.BonusEligibleOrProjected
                    : AdminBonusAttribution.BonusEligible)
                .Where(o => o.ServiceDate >= earliest);
            if (from.HasValue) query = query.Where(o => o.ServiceDate >= from.Value);
            if (to.HasValue) query = query.Where(o => o.ServiceDate < to.Value);

            var orders = await query
                .Select(o => new
                {
                    o.Id, o.BonusBookerId, o.BonusBookerPosition, o.BonusManagerId, o.IsNewCustomerOrder,
                    o.ServiceDate, o.SubTotal, o.DiscountAmount, o.SubscriptionDiscountAmount,
                    o.LoyaltyDiscountAmount, o.Tax, o.Tips, o.CompanyDevelopmentTips,
                    o.TotalRefundedAmount, o.CleanerTotalSalary
                })
                .ToListAsync();
            if (orders.Count == 0)
                return new List<PercentBonusLine>();

            // The "net" basis has the order's FIXED bonuses taken off (both slots, the manager's
            // share included) — but not this percentage, which cannot be a share of itself.
            var fixedGel = await FixedCostsGelAsync(orders
                .Select(o => new FixedBonusInput(o.Id, o.BonusBookerId, o.BonusBookerPosition, o.BonusManagerId, o.IsNewCustomerOrder))
                .ToList());

            var fx = new Dictionary<int, decimal>();
            var lines = new List<PercentBonusLine>();
            foreach (var o in orders)
            {
                var rule = AdminOrderEarnings.RuleOn(rulesByUser[o.BonusBookerId!.Value], o.ServiceDate);
                if (rule == null || !rule.PaysAnything) continue;

                var key = o.ServiceDate.Year * 100 + o.ServiceDate.Month;
                if (!fx.TryGetValue(key, out var usdPerGel))
                    fx[key] = usdPerGel = await _usdPerGel(o.ServiceDate.Year, o.ServiceDate.Month);
                if (usdPerGel <= 0m) continue;

                var figures = AdminOrderEarnings.Figures(
                    o.SubTotal, o.DiscountAmount, o.SubscriptionDiscountAmount, o.LoyaltyDiscountAmount,
                    o.Tax, o.Tips, o.CompanyDevelopmentTips, o.TotalRefundedAmount);
                var fixedUsd = (fixedGel.TryGetValue(o.Id, out var g) ? g : 0m) * usdPerGel;
                var netBefore = AdminOrderEarnings.Net(figures.Revenue, o.CleanerTotalSalary, fixedUsd);

                var usd = AdminOrderEarnings.PercentBonusUsd(rule, o.IsNewCustomerOrder, figures.OrderTotal, netBefore);
                if (usd <= 0m) continue;

                lines.Add(new PercentBonusLine(o.Id, o.BonusBookerId.Value, usd, usd / usdPerGel));
            }
            return lines;
        }

        private sealed record PercentBonusLine(int OrderId, int UserId, decimal Usd, decimal Gel);

        private sealed record FixedBonusInput(
            int Id, int? BonusBookerId, AdminPosition BonusBookerPosition, int? BonusManagerId, bool IsNewCustomerOrder);

        /// <summary>The fixed per-order GEL bonuses (both slots), keyed by order id.</summary>
        private async Task<Dictionary<int, decimal>> FixedCostsGelAsync(List<FixedBonusInput> orders)
        {
            if (orders.Count == 0)
                return new Dictionary<int, decimal>();

            var earnerIds = orders.Select(o => o.BonusBookerId)
                .Concat(orders.Select(o => o.BonusManagerId))
                .Where(id => id.HasValue)
                .Select(id => id!.Value)
                .Distinct()
                .ToList();

            var setting = await EnsureSettingAsync();
            var overrides = await LoadOverridesAsync(earnerIds);

            // Rates resolved once per person (and per position, for the booker slot), not once per
            // order — a year-wide statistics window walks thousands of orders through this.
            var bookerCache = new Dictionary<(int UserId, AdminPosition Position), BonusRates>();
            BonusRates BookerRatesFor(int userId, AdminPosition position)
            {
                var key = (userId, position);
                if (bookerCache.TryGetValue(key, out var cached))
                    return cached;
                overrides.TryGetValue(userId, out var personal);
                var resolved = AdminBonusAttribution.ResolveOwnBookingRates(position, setting, personal);
                bookerCache[key] = resolved;
                return resolved;
            }

            var teamCache = new Dictionary<int, BonusRates>();
            BonusRates TeamRatesFor(int userId)
            {
                if (teamCache.TryGetValue(userId, out var cached))
                    return cached;
                overrides.TryGetValue(userId, out var personal);
                var resolved = AdminBonusAttribution.ResolveTeamRates(setting, personal);
                teamCache[userId] = resolved;
                return resolved;
            }

            var costs = new Dictionary<int, decimal>(orders.Count);
            foreach (var o in orders)
            {
                decimal cost = 0m;
                if (o.BonusBookerId.HasValue)
                {
                    var r = BookerRatesFor(o.BonusBookerId.Value, o.BonusBookerPosition);
                    cost += o.IsNewCustomerOrder ? r.NewCustomer : r.ExistingCustomer;
                }
                if (o.BonusManagerId.HasValue)
                {
                    var r = TeamRatesFor(o.BonusManagerId.Value);
                    cost += o.IsNewCustomerOrder ? r.NewCustomer : r.ExistingCustomer;
                }
                costs[o.Id] = cost;
            }
            return costs;
        }

        private static List<AdminPercentBonusRuleDto> ToRuleDtos(List<AdminPercentBonusRule> rules)
        {
            var today = NyTimeHelper.NowNy.Date;
            var current = AdminOrderEarnings.RuleOn(rules, today);
            return rules
                .OrderByDescending(r => r.StartDate).ThenByDescending(r => r.Id)
                .Select(r => new AdminPercentBonusRuleDto
                {
                    Id = r.Id,
                    Basis = r.Basis.ToString(),
                    NewCustomerPercent = r.NewCustomerPercent,
                    ExistingCustomerPercent = r.ExistingCustomerPercent,
                    StartDate = r.StartDate.Date,
                    IsCurrent = current != null && current.Id == r.Id,
                    IsUpcoming = r.StartDate.Date > today,
                    CreatedAt = r.CreatedAt,
                    CreatedByName = r.CreatedByUser == null
                        ? null
                        : $"{r.CreatedByUser.FirstName} {r.CreatedByUser.LastName}".Trim()
                })
                .ToList();
        }

        // ──────────────────────────────────────────────────────────────────────────────

        private sealed class AdminRow
        {
            public int Id { get; set; }
            public string FirstName { get; set; } = string.Empty;
            public string LastName { get; set; } = string.Empty;
            public string? ShiftColor { get; set; }
            public AdminPosition Position { get; set; }
            public int? ManagerId { get; set; }
            public string? ManagerName { get; set; }
        }

        /// <summary>
        /// Own-slot counts for ONE person, split by the position they held when they took each
        /// booking. The split is what lets a promoted person's older orders keep paying at the
        /// administrator rate while their newer ones pay at the manager own-booking rate.
        /// </summary>
        private sealed class OwnCounts
        {
            public int Assigned { get; set; }
            public int AsAdministratorNew { get; set; }
            public int AsAdministratorExisting { get; set; }
            public int AsManagerNew { get; set; }
            public int AsManagerExisting { get; set; }

            public int TotalNew => AsAdministratorNew + AsManagerNew;
            public int TotalExisting => AsAdministratorExisting + AsManagerExisting;
        }

        private sealed class TeamCounts
        {
            public int Assigned { get; set; }
            public int NewCustomer { get; set; }
            public int ExistingCustomer { get; set; }
        }

        private sealed class BonusCounts
        {
            public Dictionary<int, OwnCounts> Own { get; set; } = new();
            public Dictionary<int, TeamCounts> Team { get; set; } = new();
        }

        /// <summary>
        /// Both slots, aggregated in SQL rather than by pulling the window into memory — the
        /// all-time profile summary runs through here too. The assigned total and the eligible
        /// new/returning split are separate passes so the eligibility rule stays the one reusable
        /// Expression (AdminBonusAttribution.BonusEligible) instead of being retyped inside a
        /// Count() lambda, where it would be free to drift.
        /// </summary>
        private async Task<BonusCounts> AggregateAsync(IQueryable<Order> window)
        {
            var result = new BonusCounts();

            var bookerSide = window.Where(o => o.BonusBookerId != null);

            var bookedAssigned = await bookerSide
                .GroupBy(o => o.BonusBookerId.Value)
                .Select(g => new { UserId = g.Key, Count = g.Count() })
                .ToListAsync();

            var bookedEligible = await bookerSide
                .Where(AdminBonusAttribution.BonusEligible)
                .GroupBy(o => new { UserId = o.BonusBookerId.Value, o.BonusBookerPosition })
                .Select(g => new
                {
                    g.Key.UserId,
                    g.Key.BonusBookerPosition,
                    NewCustomer = g.Count(o => o.IsNewCustomerOrder),
                    ExistingCustomer = g.Count(o => !o.IsNewCustomerOrder)
                })
                .ToListAsync();

            foreach (var a in bookedAssigned)
                result.Own[a.UserId] = new OwnCounts { Assigned = a.Count };
            foreach (var e in bookedEligible)
            {
                if (!result.Own.TryGetValue(e.UserId, out var row))
                    result.Own[e.UserId] = row = new OwnCounts();
                if (e.BonusBookerPosition == AdminPosition.Manager)
                {
                    row.AsManagerNew = e.NewCustomer;
                    row.AsManagerExisting = e.ExistingCustomer;
                }
                else
                {
                    row.AsAdministratorNew = e.NewCustomer;
                    row.AsAdministratorExisting = e.ExistingCustomer;
                }
            }

            var managerSide = window.Where(o => o.BonusManagerId != null);

            var teamAssigned = await managerSide
                .GroupBy(o => o.BonusManagerId.Value)
                .Select(g => new { UserId = g.Key, Count = g.Count() })
                .ToListAsync();

            var teamEligible = await managerSide
                .Where(AdminBonusAttribution.BonusEligible)
                .GroupBy(o => o.BonusManagerId.Value)
                .Select(g => new
                {
                    UserId = g.Key,
                    NewCustomer = g.Count(o => o.IsNewCustomerOrder),
                    ExistingCustomer = g.Count(o => !o.IsNewCustomerOrder)
                })
                .ToListAsync();

            foreach (var a in teamAssigned)
                result.Team[a.UserId] = new TeamCounts { Assigned = a.Count };
            foreach (var e in teamEligible)
            {
                if (!result.Team.TryGetValue(e.UserId, out var row))
                    result.Team[e.UserId] = row = new TeamCounts();
                row.NewCustomer = e.NewCustomer;
                row.ExistingCustomer = e.ExistingCustomer;
            }

            return result;
        }

        private async Task<Dictionary<int, AdminBonusRateOverride>> LoadOverridesAsync(List<int> userIds)
        {
            return await _context.AdminBonusRateOverrides
                .Where(o => userIds.Contains(o.UserId))
                .ToDictionaryAsync(o => o.UserId, o => o);
        }

        /// <summary>Each person's percentage bonus over the window, with their rule history.</summary>
        private async Task<Dictionary<int, PercentSummary>> PercentByAdminAsync(
            DateTime? from, DateTime? to, List<int> adminIds)
        {
            var lines = await ComputePercentBonusesAsync(from, to, includeUnfinished: false, onlyUsers: adminIds);
            var rules = await _context.AdminPercentBonusRules
                .Where(r => adminIds.Contains(r.UserId))
                .Include(r => r.CreatedByUser)
                .ToListAsync();

            return adminIds.ToDictionary(id => id, id =>
            {
                var mine = lines.Where(l => l.UserId == id).ToList();
                return new PercentSummary(
                    decimal.Round(mine.Sum(l => l.Gel), 2),
                    mine.Sum(l => l.Usd),
                    mine.Count,
                    ToRuleDtos(rules.Where(r => r.UserId == id).ToList()));
            });
        }

        private sealed record PercentSummary(decimal Gel, decimal Usd, int OrderCount, List<AdminPercentBonusRuleDto> Rules);

        private static AdminBonusSummaryDto BuildSummary(
            AdminRow admin,
            BonusCounts counts,
            AdminBonusSetting setting,
            Dictionary<int, AdminBonusRateOverride> overrides,
            Dictionary<int, int> teamSizes,
            Dictionary<int, PercentSummary> percentByAdmin)
        {
            overrides.TryGetValue(admin.Id, out var personal);

            counts.Own.TryGetValue(admin.Id, out var own);
            counts.Team.TryGetValue(admin.Id, out var team);
            own ??= new OwnCounts();
            team ??= new TeamCounts();

            // Paid at the rate for the position held WHEN each booking was taken, which is why the
            // own counts arrive already split. Somebody promoted mid-month is paid the
            // administrator rate for what they booked before and the manager rate for what they
            // booked after — the displayed rate below is today's, so for that one person the label
            // describes what they earn from now on rather than every order in the count.
            var asAdministrator = AdminBonusAttribution.ResolveOwnBookingRates(
                AdminPosition.Administrator, setting, personal);
            var asManager = AdminBonusAttribution.ResolveOwnBookingRates(
                AdminPosition.Manager, setting, personal);
            var teamRates = AdminBonusAttribution.ResolveTeamRates(setting, personal);

            var bonus =
                AdminBonusAttribution.ComputeBonus(own.AsAdministratorNew, own.AsAdministratorExisting, asAdministrator)
                + AdminBonusAttribution.ComputeBonus(own.AsManagerNew, own.AsManagerExisting, asManager)
                + AdminBonusAttribution.ComputeBonus(team.NewCustomer, team.ExistingCustomer, teamRates);

            var displayedOwnRates = admin.Position == AdminPosition.Manager ? asManager : asAdministrator;
            percentByAdmin.TryGetValue(admin.Id, out var percent);

            return new AdminBonusSummaryDto
            {
                AdminId = admin.Id,
                FirstName = admin.FirstName,
                LastName = admin.LastName,
                ShiftColor = admin.ShiftColor,
                Position = admin.Position.ToString(),
                ManagerId = admin.ManagerId,
                ManagerName = admin.ManagerName,
                TeamSize = teamSizes.TryGetValue(admin.Id, out var size) ? size : 0,
                AssignedCount = own.Assigned + team.Assigned,
                EligibleCount = own.TotalNew + own.TotalExisting + team.NewCustomer + team.ExistingCustomer,
                OwnNewCustomerCount = own.TotalNew,
                OwnExistingCustomerCount = own.TotalExisting,
                TeamNewCustomerCount = team.NewCustomer,
                TeamExistingCustomerCount = team.ExistingCustomer,
                OwnNewCustomerRate = displayedOwnRates.NewCustomer,
                OwnExistingCustomerRate = displayedOwnRates.ExistingCustomer,
                OwnNewCustomerRateIsCustom = displayedOwnRates.NewCustomerIsCustom,
                OwnExistingCustomerRateIsCustom = displayedOwnRates.ExistingCustomerIsCustom,
                TeamNewCustomerRate = teamRates.NewCustomer,
                TeamExistingCustomerRate = teamRates.ExistingCustomer,
                TeamNewCustomerRateIsCustom = teamRates.NewCustomerIsCustom,
                TeamExistingCustomerRateIsCustom = teamRates.ExistingCustomerIsCustom,
                FixedBonusAmount = bonus,
                PercentBonusAmount = percent?.Gel ?? 0m,
                PercentBonusUsd = percent?.Usd ?? 0m,
                PercentBonusOrderCount = percent?.OrderCount ?? 0,
                PercentRules = percent?.Rules ?? new List<AdminPercentBonusRuleDto>(),
                // Everything this person is owed in bonuses — what the salary payout adds to the
                // second instalment. GEL, like the fixed rates.
                BonusAmount = bonus + (percent?.Gel ?? 0m),
                Currency = setting.Currency
            };
        }

        private async Task<decimal> ResolveBookerRateForOrderAsync(
            int adminId, AdminPosition position, bool isNewCustomerOrder)
        {
            var setting = await EnsureSettingAsync();
            var personal = await _context.AdminBonusRateOverrides.FirstOrDefaultAsync(o => o.UserId == adminId);
            var rates = AdminBonusAttribution.ResolveOwnBookingRates(position, setting, personal);
            return isNewCustomerOrder ? rates.NewCustomer : rates.ExistingCustomer;
        }

        private async Task<OrderAssignedAdminDto> BuildAssignedAdminDtoAsync(int? adminId)
        {
            if (!adminId.HasValue)
                return new OrderAssignedAdminDto { AdminId = null };

            var admin = await _context.Users
                .Where(u => u.Id == adminId.Value)
                .Select(u => new { u.Id, u.FirstName, u.LastName })
                .FirstOrDefaultAsync();

            if (admin == null)
                return new OrderAssignedAdminDto { AdminId = null };

            return new OrderAssignedAdminDto
            {
                AdminId = admin.Id,
                FirstName = admin.FirstName,
                LastName = admin.LastName,
                DisplayName = FormatDisplayName(admin.FirstName, admin.LastName)
            };
        }

        // "F. LastName" — first initial uppercased + dot + space + full last name.
        // Centralised here so the same format flows everywhere assigned-admin appears.
        public static string FormatDisplayName(string firstName, string lastName)
        {
            var initial = string.IsNullOrWhiteSpace(firstName) ? "" : char.ToUpper(firstName.Trim()[0]) + ".";
            var last = (lastName ?? string.Empty).Trim();
            return string.IsNullOrEmpty(initial) ? last : $"{initial} {last}".Trim();
        }

        private async Task<AdminBonusSetting> EnsureSettingAsync()
        {
            var setting = await _context.AdminBonusSettings.FirstOrDefaultAsync();
            if (setting == null)
            {
                // Defaults match the seeded row — see AdminBonusSetting for the three slots.
                setting = new AdminBonusSetting
                {
                    AdministratorNewCustomerRate = 10m,
                    AdministratorExistingCustomerRate = 10m,
                    ManagerOwnBookingNewCustomerRate = 15m,
                    ManagerOwnBookingExistingCustomerRate = 25m,
                    ManagerTeamNewCustomerRate = 5m,
                    ManagerTeamExistingCustomerRate = 15m,
                    Currency = "GEL",
                    UpdatedAt = DateTime.UtcNow
                };
                _context.AdminBonusSettings.Add(setting);
                await _context.SaveChangesAsync();
            }
            return setting;
        }
    }
}
