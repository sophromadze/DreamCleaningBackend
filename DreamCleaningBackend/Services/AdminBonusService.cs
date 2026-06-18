using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace DreamCleaningBackend.Services
{
    public class AdminBonusService : IAdminBonusService
    {
        private readonly ApplicationDbContext _context;

        public AdminBonusService(ApplicationDbContext context)
        {
            _context = context;
        }

        public async Task<OrderAssignedAdminDto> AssignAdminAsync(int orderId, int? adminId, int byUserId)
        {
            var order = await _context.Orders.FirstOrDefaultAsync(o => o.Id == orderId);
            if (order == null)
                throw new InvalidOperationException("Order not found.");

            if (adminId.HasValue)
            {
                // Admin or SuperAdmin — the create-for-user flow auto-assigns the creating
                // staff member, and the owner (SuperAdmin) creates orders too.
                var admin = await _context.Users
                    .FirstOrDefaultAsync(u => u.Id == adminId.Value
                                              && !u.IsDeleted
                                              && u.IsActive
                                              && (u.Role == UserRole.Admin || u.Role == UserRole.SuperAdmin));
                if (admin == null)
                    throw new InvalidOperationException("Selected user is not an active admin.");
            }

            var previousAdminId = order.AssignedAdminId;
            if (previousAdminId == adminId)
            {
                // No-op — return the current state without writing history noise.
                return await BuildAssignedAdminDtoAsync(adminId);
            }

            var rate = await GetCurrentRateValueAsync();

            order.AssignedAdminId = adminId;
            order.UpdatedAt = DateTime.UtcNow;

            _context.OrderAdminAssignmentHistories.Add(new OrderAdminAssignmentHistory
            {
                OrderId = orderId,
                PreviousAdminId = previousAdminId,
                NewAdminId = adminId,
                ChangedByUserId = byUserId,
                ChangedAt = DateTime.UtcNow,
                BonusRateAtChange = rate
            });

            await _context.SaveChangesAsync();
            return await BuildAssignedAdminDtoAsync(adminId);
        }

        public async Task<AdminBonusRateDto> GetRateAsync()
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

            return new AdminBonusRateDto
            {
                RatePerOrder = setting.RatePerOrder,
                Currency = setting.Currency,
                UpdatedAt = setting.UpdatedAt,
                UpdatedByUserId = setting.UpdatedByUserId,
                UpdatedByUserName = updatedByName
            };
        }

        public async Task<AdminBonusRateDto> SetRateAsync(decimal newRate, int byUserId)
        {
            if (newRate < 0)
                throw new InvalidOperationException("Bonus rate cannot be negative.");

            var setting = await EnsureSettingAsync();
            setting.RatePerOrder = newRate;
            setting.UpdatedByUserId = byUserId;
            setting.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            return await GetRateAsync();
        }

        public async Task<List<AdminBonusSummaryDto>> GetBonusesAsync(
            DateTime from,
            DateTime to,
            int viewerUserId,
            bool viewerIsSuperAdmin,
            int? adminIdFilter = null)
        {
            var rate = await GetCurrentRateValueAsync();

            // Base admin list: all active admins, or just the viewer if not SuperAdmin.
            var adminsQuery = _context.Users
                .Where(u => !u.IsDeleted && u.IsActive && u.Role == UserRole.Admin);

            if (!viewerIsSuperAdmin)
                adminsQuery = adminsQuery.Where(u => u.Id == viewerUserId);
            else if (adminIdFilter.HasValue)
                adminsQuery = adminsQuery.Where(u => u.Id == adminIdFilter.Value);

            var admins = await adminsQuery
                .OrderBy(u => u.FirstName)
                .ThenBy(u => u.LastName)
                .Select(u => new
                {
                    u.Id,
                    u.FirstName,
                    u.LastName,
                    u.ShiftColor
                })
                .ToListAsync();

            // Bonus eligibility (per spec): Status == "Done" AND (IsPaid OR PaymentMethod != Normal).
            // Window: ServiceDate ∈ [from, to). Counts use the current AssignedAdminId.
            var ordersInWindow = _context.Orders
                .Where(o => o.AssignedAdminId.HasValue
                            && o.ServiceDate >= from
                            && o.ServiceDate < to);

            var assignedAgg = await ordersInWindow
                .GroupBy(o => o.AssignedAdminId!.Value)
                .Select(g => new
                {
                    AdminId = g.Key,
                    Assigned = g.Count(),
                    Eligible = g.Count(o => o.Status == "Done"
                                            && (o.IsPaid || o.PaymentMethod != PaymentMethod.Normal))
                })
                .ToListAsync();

            var settingCurrency = (await EnsureSettingAsync()).Currency;

            return admins.Select(a =>
            {
                var stats = assignedAgg.FirstOrDefault(x => x.AdminId == a.Id);
                var eligible = stats?.Eligible ?? 0;
                return new AdminBonusSummaryDto
                {
                    AdminId = a.Id,
                    FirstName = a.FirstName,
                    LastName = a.LastName,
                    ShiftColor = a.ShiftColor,
                    AssignedCount = stats?.Assigned ?? 0,
                    EligibleCount = eligible,
                    BonusAmount = eligible * rate,
                    RatePerOrder = rate,
                    Currency = settingCurrency
                };
            }).ToList();
        }

        public async Task<AdminBonusSummaryDto> GetSummaryForAdminAsync(int adminId, DateTime? from, DateTime? to)
        {
            var admin = await _context.Users
                .Where(u => u.Id == adminId && !u.IsDeleted)
                .Select(u => new { u.Id, u.FirstName, u.LastName, u.ShiftColor })
                .FirstOrDefaultAsync();
            if (admin == null)
                throw new InvalidOperationException("Admin not found.");

            var rate = await GetCurrentRateValueAsync();
            var currency = (await EnsureSettingAsync()).Currency;

            var q = _context.Orders.Where(o => o.AssignedAdminId == adminId);
            if (from.HasValue) q = q.Where(o => o.ServiceDate >= from.Value);
            if (to.HasValue) q = q.Where(o => o.ServiceDate < to.Value);

            var assigned = await q.CountAsync();
            var eligible = await q.CountAsync(o => o.Status == "Done"
                                                   && (o.IsPaid || o.PaymentMethod != PaymentMethod.Normal));

            return new AdminBonusSummaryDto
            {
                AdminId = admin.Id,
                FirstName = admin.FirstName,
                LastName = admin.LastName,
                ShiftColor = admin.ShiftColor,
                AssignedCount = assigned,
                EligibleCount = eligible,
                BonusAmount = eligible * rate,
                RatePerOrder = rate,
                Currency = currency
            };
        }

        // ──────────────────────────────────────────────────────────────────────────────

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
                setting = new AdminBonusSetting
                {
                    RatePerOrder = 10m,
                    Currency = "GEL",
                    UpdatedAt = DateTime.UtcNow
                };
                _context.AdminBonusSettings.Add(setting);
                await _context.SaveChangesAsync();
            }
            return setting;
        }

        private async Task<decimal> GetCurrentRateValueAsync()
        {
            var setting = await EnsureSettingAsync();
            return setting.RatePerOrder;
        }
    }
}
