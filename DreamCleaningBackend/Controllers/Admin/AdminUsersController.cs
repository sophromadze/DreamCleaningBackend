using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services.Interfaces;
using DreamCleaningBackend.Attributes;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Hubs;
using System.Linq;
using Newtonsoft.Json;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;
using DreamCleaningBackend.Services;
using Microsoft.AspNetCore.SignalR;
using System.Security.Claims;

namespace DreamCleaningBackend.Controllers
{
    /// <summary>User management: accounts, roles, profiles, apartments, history, loyalty discounts.
    /// Split out of the monolithic AdminController; same api/admin route prefix, so URLs are unchanged.</summary>
    [Route("api/admin")]
    [ApiController]
    [Authorize(Roles = "Admin,SuperAdmin,Moderator")]
    public class AdminUsersController : AdminControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IPermissionService _permissionService;
        private readonly IAuditService _auditService;
        private readonly IConfiguration _configuration;
        private readonly ISpecialOfferService _specialOfferService;
        private readonly IProfileService _profileService;
        private readonly IEmailService _emailService;
        private readonly ILoyaltyDiscountService _loyaltyDiscountService;
        private readonly IBubbleRewardsSettingsService _bubbleRewardsSettingsService;
        private readonly IPageAccessService _pageAccessService;
        private readonly ILogger<AdminUsersController> _logger;

        public AdminUsersController(ApplicationDbContext context,
            IPermissionService permissionService,
            IAuditService auditService,
            IConfiguration configuration,
            ISpecialOfferService specialOfferService,
            IProfileService profileService,
            IEmailService emailService,
            ILoyaltyDiscountService loyaltyDiscountService,
            IBubbleRewardsSettingsService bubbleRewardsSettingsService,
            IPageAccessService pageAccessService,
            ILogger<AdminUsersController> logger)
        {
            _logger = logger;
            _context = context;
            _permissionService = permissionService;
            _auditService = auditService;
            _configuration = configuration;
            _specialOfferService = specialOfferService;
            _profileService = profileService;
            _emailService = emailService;
            _loyaltyDiscountService = loyaltyDiscountService;
            _bubbleRewardsSettingsService = bubbleRewardsSettingsService;
            _pageAccessService = pageAccessService;
        }

        // Users Management
        [HttpGet("users")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<List<UserAdminDto>>> GetUsers()
        {
            var currentUserRole = GetCurrentUserRole();

            var users = await _context.Users
                .Include(u => u.Subscription)
                .Select(u => new UserAdminDto
                {
                    Id = u.Id,
                    FirstName = u.FirstName,
                    LastName = u.LastName,
                    Email = u.IsNoEmailUser ? "" : u.Email,
                    IsNoEmailUser = u.IsNoEmailUser,
                    Phone = u.Phone,
                    Role = u.Role.ToString(),
                    AuthProvider = u.AuthProvider,
                    SubscriptionName = u.Subscription != null ? u.Subscription.Name : null,
                    FirstTimeOrder = u.FirstTimeOrder,
                    IsActive = u.IsActive,
                    CreatedAt = u.CreatedAt,
                    CanReceiveCommunications = u.CanReceiveCommunications,
                    CanReceiveEmails = u.CanReceiveEmails,
                    CanReceiveMessages = u.CanReceiveMessages,
                    AdminNotes = null
                })
                .ToListAsync();

            var userIds = users.Select(u => u.Id).ToList();
            var notesDict = new Dictionary<int, string?>();
            try
            {
                notesDict = await _context.AdminUserNotes
                    .Where(n => userIds.Contains(n.UserId))
                    .ToDictionaryAsync(n => n.UserId, n => n.Notes);
            }
            catch (MySqlConnector.MySqlException ex) when (ex.Message.Contains("doesn't exist"))
            {
                try
                {
                    await EnsureAdminUserNotesTableExistsAsync();
                    notesDict = await _context.AdminUserNotes
                        .Where(n => userIds.Contains(n.UserId))
                        .ToDictionaryAsync(n => n.UserId, n => n.Notes);
                }
                catch
                {
                    notesDict = new Dictionary<int, string?>();
                }
            }
            foreach (var u in users)
                u.AdminNotes = notesDict.TryGetValue(u.Id, out var notes) ? notes : null;

            // Page-view grants (Admin-role users only) — parsed from the User.ViewablePages JSON column.
            var viewablePagesDict = await _context.Users
                .Where(u => userIds.Contains(u.Id) && u.ViewablePages != null)
                .Select(u => new { u.Id, u.ViewablePages })
                .ToDictionaryAsync(x => x.Id, x => x.ViewablePages);
            foreach (var u in users)
                u.ViewablePages = viewablePagesDict.TryGetValue(u.Id, out var vp)
                    ? _pageAccessService.ParsePages(vp)
                    : new List<string>();

            foreach (var u in users)
                u.IsOnline = UserManagementHub.IsUserOnline(u.Id);

            // ── Customer-care snapshot: last cleaning + total orders
            // We fetch the last (most recent) non-cancelled order per user via a single query.
            var lastOrders = await _context.Orders
                .Where(o => userIds.Contains(o.UserId) && o.Status != "Cancelled")
                .GroupBy(o => o.UserId)
                .Select(g => g
                    .OrderByDescending(o => o.ServiceDate)
                    .Select(o => new
                    {
                        o.UserId,
                        o.ServiceDate,
                        ServiceTypeName = o.ServiceType != null && o.ServiceType.IsCustom && o.CustomServiceDisplayName != null && o.CustomServiceDisplayName != ""
                            ? o.CustomServiceDisplayName + " Cleaning"
                            : (o.ServiceType != null ? o.ServiceType.Name : ""),
                        o.BedroomsQuantity,
                        o.BathroomsQuantity
                    })
                    .First())
                .ToListAsync();

            var lastOrderByUser = lastOrders.ToDictionary(o => o.UserId);

            var orderCounts = await _context.Orders
                .Where(o => userIds.Contains(o.UserId) && o.Status != "Cancelled")
                .GroupBy(o => o.UserId)
                .Select(g => new { UserId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(g => g.UserId, g => g.Count);

            foreach (var u in users)
            {
                if (lastOrderByUser.TryGetValue(u.Id, out var lo))
                {
                    u.LastCleaningDate = lo.ServiceDate;
                    u.LastCleaningServiceType = lo.ServiceTypeName;
                    u.LastBedrooms = lo.BedroomsQuantity;
                    u.LastBathrooms = lo.BathroomsQuantity;
                }
                u.TotalOrdersCount = orderCounts.TryGetValue(u.Id, out var cnt) ? cnt : 0;
            }

            // Include current user role in response for frontend to use
            return Ok(new
            {
                users = users,
                currentUserRole = currentUserRole.ToString()
            });

        }

        /// <summary>SuperAdmin-only: export the users list to an .xlsx file. The body's Columns list
        /// controls which columns are written; an empty/missing list exports all columns.</summary>
        [HttpPost("users/export")]
        [Authorize(Roles = "SuperAdmin")]
        public async Task<IActionResult> ExportUsers([FromBody] UsersExportRequestDto? dto)
        {
            var requested = dto?.Columns?
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Select(c => c.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
                ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // All available columns, in the order they should appear in the spreadsheet.
            var allColumns = new List<(string Key, string Header)>
            {
                ("userId",         "ID"),
                ("fullName",       "Full Name"),
                ("phone",          "Phone"),
                ("email",          "Email"),
                ("lastServiceType","Service Type"),
                ("lastServiceAt",  "Date & Time"),
                ("lastAddress",    "Address"),
                ("lastBorough",    "Borough"),
                ("lastZip",        "Zip"),
                ("lastBedsBaths",  "Rooms"),
                ("lastSquareFeet", "Sq.Ft"),
                ("totalSpent",     "Total Spent")
            };

            // If caller didn't list any columns, default to all (matches the UI's "all checked" default).
            var includeAll = requested.Count == 0;
            var columns = allColumns.Where(c => includeAll || requested.Contains(c.Key)).ToList();
            if (columns.Count == 0)
                return BadRequest(new { message = "No columns selected for export." });

            // Customers only — staff roles (Admin/SuperAdmin/Moderator) are excluded from the export.
            var users = await _context.Users
                .Where(u => u.Role == UserRole.Customer)
                .OrderBy(u => u.Id)
                .Select(u => new
                {
                    u.Id,
                    u.FirstName,
                    u.LastName,
                    u.Email,
                    u.Phone
                })
                .ToListAsync();

            // Last non-cancelled order per user, with the bits we need for service-type detection,
            // address, borough (City), zip, bedrooms/bathrooms.
            var lastOrders = await _context.Orders
                .Where(o => o.Status != "Cancelled")
                .GroupBy(o => o.UserId)
                .Select(g => g
                    .OrderByDescending(o => o.ServiceDate)
                    .ThenByDescending(o => o.Id)
                    .Select(o => new
                    {
                        o.Id,
                        o.UserId,
                        o.ServiceDate,
                        o.ServiceTime,
                        ServiceTypeName = o.ServiceType != null && o.ServiceType.IsCustom && o.CustomServiceDisplayName != null && o.CustomServiceDisplayName != ""
                            ? o.CustomServiceDisplayName + " Cleaning"
                            : (o.ServiceType != null ? o.ServiceType.Name : ""),
                        o.ServiceAddress,
                        o.AptSuite,
                        o.City,
                        o.ZipCode,
                        o.BedroomsQuantity,
                        o.BathroomsQuantity
                    })
                    .First())
                .ToDictionaryAsync(o => o.UserId);

            // Deep-cleaning detection: any extra service on the last order whose ExtraService.IsDeepCleaning == true
            // (and not IsSuperDeepCleaning — keeps "Deep" distinct from "Super Deep" if that ever ships).
            var lastOrderIds = lastOrders.Values.Select(o => o.Id).ToList();
            var deepOrderIds = await _context.OrderExtraServices
                .Where(oes => lastOrderIds.Contains(oes.OrderId)
                              && oes.ExtraService.IsDeepCleaning
                              && !oes.ExtraService.IsSuperDeepCleaning)
                .Select(oes => oes.OrderId)
                .Distinct()
                .ToListAsync();
            var deepOrderIdSet = new HashSet<int>(deepOrderIds);

            // Square feet: stored as a quantity on the OrderServices row whose Service.ServiceKey == "sqft".
            var sqftByOrder = await _context.OrderServices
                .Where(os => lastOrderIds.Contains(os.OrderId) && os.Service.ServiceKey == "sqft")
                .Select(os => new { os.OrderId, os.Quantity })
                .ToDictionaryAsync(x => x.OrderId, x => x.Quantity);

            // Total $ spent per user, across all non-cancelled orders.
            var totalSpentByUser = await _context.Orders
                .Where(o => o.Status != "Cancelled")
                .GroupBy(o => o.UserId)
                .Select(g => new { UserId = g.Key, Total = g.Sum(o => o.Total) })
                .ToDictionaryAsync(g => g.UserId, g => g.Total);

            using var workbook = new ClosedXML.Excel.XLWorkbook();
            var ws = workbook.Worksheets.Add("Users");

            // Header row
            for (int i = 0; i < columns.Count; i++)
            {
                var cell = ws.Cell(1, i + 1);
                cell.Value = columns[i].Header;
                cell.Style.Font.Bold = true;
                cell.Style.Fill.BackgroundColor = ClosedXML.Excel.XLColor.FromHtml("#F1F5F9");
                cell.Style.Border.BottomBorder = ClosedXML.Excel.XLBorderStyleValues.Thin;
            }

            int row = 2;
            foreach (var u in users)
            {
                lastOrders.TryGetValue(u.Id, out var lo);

                string serviceTypeLabel = "";
                if (lo != null)
                {
                    var st = (lo.ServiceTypeName ?? "").Trim();
                    var stLower = st.ToLowerInvariant();
                    if (stLower.Contains("residential"))
                    {
                        serviceTypeLabel = deepOrderIdSet.Contains(lo.Id) ? "Deep" : "Regular";
                    }
                    else
                    {
                        // Strip "Cleaning" (case-insensitive) from non-residential service-type names.
                        // E.g. "Move In/Out Cleaning" → "Move In/Out", "Office Cleaning" → "Office".
                        serviceTypeLabel = System.Text.RegularExpressions.Regex
                            .Replace(st, @"\s*\bcleaning\b\s*", " ", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                            .Trim();
                    }
                }

                string serviceAt = "";
                if (lo != null)
                {
                    var combined = lo.ServiceDate.Date + lo.ServiceTime;
                    serviceAt = combined.ToString("yyyy-MM-dd HH:mm");
                }

                string address = "";
                if (lo != null)
                {
                    // Trim the autocomplete tail ("Brooklyn, NY 11201, USA") off the stored street
                    // address — borough and zip already live in their own columns. The street portion
                    // is the substring before the first comma.
                    var rawStreet = (lo.ServiceAddress ?? "").Trim();
                    var commaIdx = rawStreet.IndexOf(',');
                    var street = commaIdx >= 0 ? rawStreet.Substring(0, commaIdx).Trim() : rawStreet;
                    address = string.IsNullOrWhiteSpace(lo.AptSuite)
                        ? street
                        : $"{street}, {lo.AptSuite}";
                }

                string bedsBaths = "";
                if (lo != null && (lo.BedroomsQuantity.HasValue || lo.BathroomsQuantity.HasValue))
                {
                    // Bedrooms = 0 means a studio (no separate bedroom), so render "Studio" instead of "0 bd".
                    var bd = lo.BedroomsQuantity.HasValue
                        ? (lo.BedroomsQuantity.Value == 0 ? "Studio" : $"{lo.BedroomsQuantity.Value} bd")
                        : "—";
                    var bt = lo.BathroomsQuantity.HasValue ? $"{lo.BathroomsQuantity.Value} ba" : "—";
                    bedsBaths = $"{bd} / {bt}";
                }

                int? sqft = lo != null && sqftByOrder.TryGetValue(lo.Id, out var sf) ? sf : (int?)null;
                decimal totalSpent = totalSpentByUser.TryGetValue(u.Id, out var ts) ? ts : 0m;

                for (int i = 0; i < columns.Count; i++)
                {
                    var col = i + 1;
                    var key = columns[i].Key;
                    switch (key)
                    {
                        case "userId":
                            ws.Cell(row, col).Value = u.Id;
                            break;
                        case "fullName":
                            ws.Cell(row, col).Value = ($"{u.FirstName} {u.LastName}").Trim();
                            break;
                        case "phone":
                            ws.Cell(row, col).Value = u.Phone ?? "";
                            break;
                        case "email":
                            ws.Cell(row, col).Value = NoEmailHelper.IsPlaceholder(u.Email) ? "" : (u.Email ?? "");
                            break;
                        case "lastServiceType":
                            ws.Cell(row, col).Value = serviceTypeLabel;
                            break;
                        case "lastServiceAt":
                            ws.Cell(row, col).Value = serviceAt;
                            break;
                        case "lastAddress":
                            ws.Cell(row, col).Value = address;
                            break;
                        case "lastBorough":
                            ws.Cell(row, col).Value = lo?.City ?? "";
                            break;
                        case "lastZip":
                            ws.Cell(row, col).Value = lo?.ZipCode ?? "";
                            break;
                        case "lastBedsBaths":
                            ws.Cell(row, col).Value = bedsBaths;
                            break;
                        case "lastSquareFeet":
                            if (sqft.HasValue) ws.Cell(row, col).Value = sqft.Value;
                            else ws.Cell(row, col).Value = "";
                            break;
                        case "totalSpent":
                            ws.Cell(row, col).Value = totalSpent;
                            ws.Cell(row, col).Style.NumberFormat.Format = "$#,##0.00";
                            break;
                    }
                }
                row++;
            }

            ws.Columns().AdjustToContents();
            // AdjustToContents can produce extremely wide columns (long addresses); clamp the upper bound.
            foreach (var c in ws.ColumnsUsed())
            {
                if (c.Width > 50) c.Width = 50;
            }
            ws.SheetView.FreezeRows(1);

            using var ms = new MemoryStream();
            workbook.SaveAs(ms);
            var bytes = ms.ToArray();

            var fileName = $"users-export-{DateTime.UtcNow:yyyyMMdd-HHmm}.xlsx";
            return File(
                bytes,
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                fileName);
        }

        [HttpPost("users/register")]
        [RequirePermission(Permission.Create)]
        public async Task<ActionResult<object>> RegisterUser([FromBody] AdminRegisterUserDto dto)
        {
            string emailLower;
            if (dto.NoEmail)
            {
                // Cash customer without any email: phone is the only way to reach them.
                if (string.IsNullOrWhiteSpace(dto.Phone))
                    return BadRequest(new { message = "Phone is required for customers without an email." });
                emailLower = NoEmailHelper.GeneratePlaceholder();
            }
            else
            {
                if (string.IsNullOrWhiteSpace(dto.Email))
                    return BadRequest(new { message = "Email is required." });
                emailLower = dto.Email.Trim().ToLowerInvariant();
                var existing = await _context.Users.AnyAsync(u => u.Email.ToLower() == emailLower);
                if (existing)
                    return StatusCode(409, new { message = "A user with this email already exists." });
            }

            var user = new User
            {
                FirstName = dto.FirstName.Trim(),
                LastName = dto.LastName.Trim(),
                Email = emailLower,
                Phone = string.IsNullOrWhiteSpace(dto.Phone) ? null : dto.Phone.Trim(),
                AuthProvider = "Admin",
                PasswordHash = null,
                PasswordSalt = null,
                IsEmailVerified = true,
                IsActive = true,
                Role = UserRole.Customer,
                FirstTimeOrder = true,
                CreatedAt = DateTime.UtcNow,
                RequiresRealEmail = false,
                IsNoEmailUser = dto.NoEmail,
                CanReceiveEmails = !dto.NoEmail
            };

            _context.Users.Add(user);
            await _context.SaveChangesAsync();

            try
            {
                await _specialOfferService.GrantAllActiveOffersToNewUser(user.Id);
            }
            catch (Exception ex)
            {
                // Log but don't fail registration
                _logger.LogError(ex, $"Failed to grant special offers to user {user.Id}");
            }

            if (!user.IsNoEmailUser)
            {
                try
                {
                    var frontendUrl = _configuration["Frontend:Url"] ?? "https://dreamcleaningnyc.com";
                    var loginUrl = $"{frontendUrl}/login";
                    await _emailService.SendAdminWelcomeEmailAsync(user.Email, user.FirstName, loginUrl);
                }
                catch (Exception ex)
                {
                    // Admin confirmed identity by phone; keep the user even if email fails
                    _logger.LogError(ex, $"Failed to send admin welcome email to {user.Email}");
                }
            }

            try
            {
                await _auditService.LogCreateAsync(user);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Audit log failed for user {user.Id}");
            }

            return Ok(new
            {
                id = user.Id,
                firstName = user.FirstName,
                lastName = user.LastName,
                email = user.IsNoEmailUser ? null : user.Email,
                phone = user.Phone,
                role = user.Role.ToString(),
                authProvider = user.AuthProvider,
                isNoEmailUser = user.IsNoEmailUser
            });
        }

        [HttpPut("users/{id}/role")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult> UpdateUserRole(int id, UpdateUserRoleDto dto)
        {
            _logger.LogInformation($"Admin: Updating user {id} role to {dto.Role}");

            var currentUserRole = GetCurrentUserRole();
            var targetUser = await _context.Users.FindAsync(id);
            if (targetUser == null)
                return NotFound();

            // Create audit copy
            var originalUser = new User
            {
                Id = targetUser.Id,
                FirstName = targetUser.FirstName,
                LastName = targetUser.LastName,
                Email = targetUser.Email,
                Phone = targetUser.Phone,
                Role = targetUser.Role,
                IsActive = targetUser.IsActive,
                AuthProvider = targetUser.AuthProvider,
                FirstTimeOrder = targetUser.FirstTimeOrder
            };

            // Cleaner role is deprecated — cleaners are managed via the standalone Cleaners table/dashboard.
            if (string.Equals(dto.Role, "Cleaner", StringComparison.OrdinalIgnoreCase))
                return BadRequest(new { message = "The Cleaner role is no longer assignable. Add cleaners via the Cleaner Dashboard instead." });

            if (!Enum.TryParse<UserRole>(dto.Role, out var newRole))
                return BadRequest("Invalid role");

            var validationResult = ValidateRoleChange(currentUserRole, targetUser.Role, newRole);
            if (!validationResult.IsValid)
                return BadRequest(new { message = validationResult.ErrorMessage });

            targetUser.Role = newRole;
            targetUser.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            // Log audit
            try
            {
                await _auditService.LogUpdateAsync(originalUser, targetUser);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Audit logging failed");
            }

            // Send notification and ensure it's delivered
            try
            {
                var userManagementService = HttpContext.RequestServices.GetRequiredService<IUserManagementService>();
                await userManagementService.NotifyUserRoleChanged(id, newRole.ToString());

                // Give time for the notification to be delivered via SignalR
                await Task.Delay(1000);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to send role change notification to user {id}");
            }

            return Ok(new { message = "Role updated successfully" });
        }

        // Grants/revokes a regular Admin read-only ("view") access to restricted admin pages
        // (Statistics, Expenses, Bubble Rewards). SuperAdmin-only. The API enforces these grants
        // live on every request, so changes here take effect immediately.
        [HttpPut("users/{id}/viewable-pages")]
        [Authorize(Roles = "SuperAdmin")]
        public async Task<ActionResult> UpdateViewablePages(int id, UpdateViewablePagesDto dto)
        {
            var targetUser = await _context.Users.FindAsync(id);
            if (targetUser == null)
                return NotFound();

            // Audit copy (captures the ViewablePages diff via the generic User update log).
            var originalUser = new User
            {
                Id = targetUser.Id,
                FirstName = targetUser.FirstName,
                LastName = targetUser.LastName,
                Email = targetUser.Email,
                Role = targetUser.Role,
                ViewablePages = targetUser.ViewablePages
            };

            List<string> saved;
            try
            {
                saved = await _pageAccessService.SetGrantedPagesAsync(id, dto.Pages ?? new List<string>());
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }

            try
            {
                await _context.Entry(targetUser).ReloadAsync();
                await _auditService.LogUpdateAsync(originalUser, targetUser);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Audit logging failed for viewable-pages update");
            }

            return Ok(new { message = "Page access updated", pages = saved });
        }

        [HttpGet("users/{id}/online-status")]
        [RequirePermission(Permission.View)]
        public ActionResult<bool> GetUserOnlineStatus(int id)
        {
            var isOnline = UserManagementHub.IsUserOnline(id);
            return Ok(new { userId = id, isOnline = isOnline });
        }

        private (bool IsValid, string ErrorMessage) ValidateRoleChange(UserRole currentUserRole, UserRole targetCurrentRole, UserRole newRole)
        {
            // Moderators cannot change roles at all (they don't have Update permission, but double-check)
            if (currentUserRole == UserRole.Moderator)
                return (false, "Moderators cannot change user roles");

            // Admins cannot assign SuperAdmin role
            if (currentUserRole == UserRole.Admin && newRole == UserRole.SuperAdmin)
                return (false, "Admins cannot assign SuperAdmin role");

            // Admins cannot remove SuperAdmin role from a SuperAdmin
            if (currentUserRole == UserRole.Admin && targetCurrentRole == UserRole.SuperAdmin)
                return (false, "Admins cannot modify SuperAdmin users");

            // Users cannot demote themselves from SuperAdmin (optional safety check)
            var currentUserId = User.FindFirst("UserId")?.Value;
            if (currentUserId != null && targetCurrentRole == UserRole.SuperAdmin && newRole != UserRole.SuperAdmin)
            {
                // Check if user is trying to demote themselves
                // This is optional - you may want to allow SuperAdmins to demote themselves
                // return (false, "Cannot remove your own SuperAdmin role");
            }

            return (true, string.Empty);
        }

        [HttpPut("users/{id}/status")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult> UpdateUserStatus(int id, UpdateUserStatusDto dto)
        {
            _logger.LogInformation($"Admin: Updating user {id} status to {dto.IsActive}");

            var targetUser = await _context.Users.FindAsync(id);
            if (targetUser == null)
                return NotFound();

            // Create audit copy
            var originalUser = new User
            {
                Id = targetUser.Id,
                FirstName = targetUser.FirstName,
                LastName = targetUser.LastName,
                Email = targetUser.Email,
                Phone = targetUser.Phone,
                Role = targetUser.Role,
                IsActive = targetUser.IsActive,
                AuthProvider = targetUser.AuthProvider,
                FirstTimeOrder = targetUser.FirstTimeOrder
            };

            var currentUserRole = GetCurrentUserRole();
            var targetUserRole = targetUser.Role;

            if (currentUserRole == UserRole.Admin && targetUserRole == UserRole.SuperAdmin)
            {
                return BadRequest(new { message = "Admins cannot modify SuperAdmin status" });
            }

            var currentUserId = int.Parse(User.FindFirst("UserId")?.Value ?? "0");
            if (currentUserId == id && !dto.IsActive)
            {
                return BadRequest(new { message = "You cannot deactivate yourself" });
            }

            targetUser.IsActive = dto.IsActive;
            targetUser.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            // Log audit
            try
            {
                await _auditService.LogUpdateAsync(originalUser, targetUser);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Audit logging failed");
            }

            // Send notification
            try
            {
                var userManagementService = HttpContext.RequestServices.GetRequiredService<IUserManagementService>();

                if (!dto.IsActive)
                {
                    // User is being blocked
                    await userManagementService.NotifyUserBlocked(id, "Your account has been blocked by an administrator.");
                    // Give more time for block notification
                    await Task.Delay(2000);
                }
                else
                {
                    // User is being unblocked
                    await userManagementService.NotifyUserUnblocked(id);
                    await Task.Delay(1000);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to send status change notification to user {id}");
            }

            return Ok(new { message = $"User {(dto.IsActive ? "activated" : "deactivated")} successfully" });
        }

        /// <summary>Admin/SuperAdmin: edit user fields. Admins cannot modify SuperAdmin users or assign the SuperAdmin role. All changes are audit-logged.</summary>
        [HttpPut("users/{id}/superadmin-full-update")]
        [Authorize(Roles = "Admin,SuperAdmin")]
        public async Task<ActionResult> SuperAdminFullUpdateUser(int id, [FromBody] SuperAdminUpdateUserDto dto)
        {
            var currentUserRole = GetCurrentUserRole();
            if (currentUserRole != UserRole.SuperAdmin && currentUserRole != UserRole.Admin)
                return Forbid();

            var targetUser = await _context.Users.FindAsync(id);
            if (targetUser == null)
                return NotFound();

            if (currentUserRole == UserRole.Admin && targetUser.Role == UserRole.SuperAdmin)
                return BadRequest(new { message = "Admins cannot modify SuperAdmin users" });

            var originalUser = new User
            {
                Id = targetUser.Id,
                FirstName = targetUser.FirstName,
                LastName = targetUser.LastName,
                Email = targetUser.Email,
                Phone = targetUser.Phone,
                Role = targetUser.Role,
                IsActive = targetUser.IsActive,
                AuthProvider = targetUser.AuthProvider,
                FirstTimeOrder = targetUser.FirstTimeOrder,
                CanReceiveCommunications = targetUser.CanReceiveCommunications,
                CanReceiveEmails = targetUser.CanReceiveEmails,
                CanReceiveMessages = targetUser.CanReceiveMessages
            };

            if (!Enum.TryParse<UserRole>(dto.Role, out var newRole))
                return BadRequest("Invalid role");

            if (currentUserRole == UserRole.Admin && newRole == UserRole.SuperAdmin)
                return BadRequest(new { message = "Admins cannot assign SuperAdmin role" });

            targetUser.FirstName = dto.FirstName;
            targetUser.LastName = dto.LastName;
            // No-email accounts keep their hidden placeholder unless the admin types a real address;
            // once a real email lands, the account becomes a normal one (flag clears, mail allowed).
            var becameEmailUser = false;
            var newEmail = dto.Email?.Trim().ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(newEmail) && !NoEmailHelper.IsPlaceholder(newEmail) && newEmail != targetUser.Email.ToLowerInvariant())
            {
                var emailTaken = await _context.Users.AnyAsync(u => u.Id != id && u.Email.ToLower() == newEmail);
                if (emailTaken)
                    return StatusCode(409, new { message = "Another user already uses this email." });

                targetUser.Email = newEmail;
                if (targetUser.IsNoEmailUser)
                {
                    targetUser.IsNoEmailUser = false;
                    targetUser.IsEmailVerified = true; // admin vouched for the address
                    becameEmailUser = true;
                }
            }
            else if (!targetUser.IsNoEmailUser && !string.IsNullOrWhiteSpace(dto.Email))
            {
                targetUser.Email = dto.Email;
            }
            targetUser.Phone = dto.Phone ?? targetUser.Phone;
            targetUser.Role = newRole;
            targetUser.IsActive = dto.IsActive;
            targetUser.FirstTimeOrder = dto.FirstTimeOrder;
            targetUser.CanReceiveCommunications = dto.CanReceiveCommunications;
            // The dto carries the pre-edit value; a freshly emailed account should start receiving mail.
            targetUser.CanReceiveEmails = becameEmailUser || dto.CanReceiveEmails;
            targetUser.CanReceiveMessages = dto.CanReceiveMessages;
            targetUser.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            try
            {
                await _auditService.LogUpdateAsync(originalUser, targetUser);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Audit logging failed");
            }

            try
            {
                var userManagementService = HttpContext.RequestServices.GetRequiredService<IUserManagementService>();
                var changes = new List<string>();
                if (originalUser.FirstName != targetUser.FirstName || originalUser.LastName != targetUser.LastName)
                    changes.Add("name");
                if (originalUser.Email != targetUser.Email)
                    changes.Add("email");
                if (originalUser.Phone != targetUser.Phone)
                    changes.Add("phone number");
                if (originalUser.Role != targetUser.Role)
                    changes.Add("role");
                if (originalUser.IsActive != targetUser.IsActive)
                    changes.Add("account status");
                if (changes.Count > 0)
                {
                    var title = changes.Count == 1 ? "Account Updated" : "Account Updated";
                    var what = changes.Count == 1
                        ? changes[0]
                        : string.Join(", ", changes.Take(changes.Count - 1)) + " and " + changes[changes.Count - 1];
                    var message = $"Your {what} was updated. Please log in again to continue.";
                    if (changes.Count > 1)
                        message = $"Your {what} were updated. Please log in again to continue.";
                    await userManagementService.NotifyUserAccountUpdated(id, title, message);
                    await Task.Delay(500);
                }
            }
            catch { /* ignore */ }

            return Ok(new { message = "User updated successfully" });
        }

        /// <summary>SuperAdmin-only: permanently delete a user and all related data from the database.</summary>
        [HttpDelete("users/{id}")]
        [Authorize(Roles = "SuperAdmin")]
        public async Task<ActionResult> DeleteUser(int id)
        {
            if (GetCurrentUserRole() != UserRole.SuperAdmin)
                return Forbid();

            var currentUserId = int.Parse(User.FindFirst("UserId")?.Value ?? "0");
            if (id == currentUserId)
                return BadRequest(new { message = "You cannot delete your own account." });

            var user = await _context.Users.FindAsync(id);
            if (user == null)
                return NotFound();

            try
            {
                var userManagementService = HttpContext.RequestServices.GetRequiredService<IUserManagementService>();
                await userManagementService.NotifyUserDeleted(id, "Your account has been permanently deleted by an administrator.");
                await Task.Delay(1500);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to notify user {id} before delete");
            }

            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                await _context.AccountMergeRequests.Where(r => r.NewAccountId == id || r.OldAccountId == id).ExecuteDeleteAsync();
                await _context.OrderCleaners.Where(oc => oc.CleanerId == id || oc.AssignedBy == id).ExecuteDeleteAsync();
                await _context.OrderUpdateHistories.Where(ouh => ouh.UpdatedByUserId == id).ExecuteDeleteAsync();
                await _context.PollSubmissions.Where(ps => ps.UserId == id).ExecuteDeleteAsync();
                await _context.GiftCardUsages.Where(g => g.UserId == id).ExecuteDeleteAsync();
                await _context.NotificationLogs.Where(n => n.CustomerId == id).ExecuteDeleteAsync();
                await _context.AuditLogs.Where(a => a.UserId == id).ExecuteDeleteAsync();
                await _context.OrderTransfers.Where(t => t.FromUserId == id || t.ToUserId == id).ExecuteDeleteAsync();
                await _context.Orders.Where(o => o.UserId == id).ExecuteDeleteAsync();
                _context.Users.Remove(user);
                await _context.SaveChangesAsync();
                await transaction.CommitAsync();
                return Ok(new { message = "User deleted successfully." });
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                var msg = ex.InnerException?.Message ?? ex.Message;
                if (msg.Contains("foreign key") || msg.Contains("REFERENCE"))
                    return BadRequest(new { message = "Cannot delete user: they have linked data (orders, assignments, etc.). Use Block instead." });
                throw;
            }
        }

        /// <summary>Admin or SuperAdmin: update a user's communication preference (emails/SMS). Requires canUpdate.</summary>
        [HttpPatch("users/{id}/communication-preference")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult> UpdateUserCommunicationPreference(int id, [FromBody] CommunicationPreferenceDto dto)
        {
            var targetUser = await _context.Users.FindAsync(id);
            if (targetUser == null)
                return NotFound();

            if (dto.CanReceiveEmails.HasValue)
                targetUser.CanReceiveEmails = dto.CanReceiveEmails.Value;
            if (dto.CanReceiveMessages.HasValue)
                targetUser.CanReceiveMessages = dto.CanReceiveMessages.Value;
            if (!dto.CanReceiveEmails.HasValue && !dto.CanReceiveMessages.HasValue)
            {
                targetUser.CanReceiveCommunications = dto.CanReceiveCommunications;
                targetUser.CanReceiveEmails = dto.CanReceiveCommunications;
                targetUser.CanReceiveMessages = dto.CanReceiveCommunications;
            }
            targetUser.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            return Ok(new { canReceiveEmails = targetUser.CanReceiveEmails, canReceiveMessages = targetUser.CanReceiveMessages, message = "Communication preference updated." });
        }

        /// <summary>Admin or SuperAdmin: update admin notes for a user. Requires canUpdate.</summary>
        [HttpPut("users/{id}/admin-notes")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult> UpdateUserAdminNotes(int id, [FromBody] UpdateUserAdminNotesDto dto)
        {
            var userExists = await _context.Users.AnyAsync(u => u.Id == id);
            if (!userExists)
                return NotFound();

            try
            {
                await EnsureAdminUserNotesTableExistsAsync();
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Could not create admin notes table. " + ex.Message });
            }

            var note = await _context.AdminUserNotes.FindAsync(id);
            if (note == null)
            {
                note = new AdminUserNote { UserId = id, Notes = dto.AdminNotes };
                _context.AdminUserNotes.Add(note);
            }
            else
                note.Notes = dto.AdminNotes;
            await _context.SaveChangesAsync();

            return Ok(new { adminNotes = note.Notes, message = "Admin notes updated." });
        }

        private async Task EnsureAdminUserNotesTableExistsAsync()
        {
            await _context.Database.ExecuteSqlRawAsync(@"
                CREATE TABLE IF NOT EXISTS AdminUserNotes (
                    UserId INT NOT NULL,
                    Notes VARCHAR(2000) NULL,
                    PRIMARY KEY (UserId),
                    CONSTRAINT FK_AdminUserNotes_Users_UserId FOREIGN KEY (UserId) REFERENCES Users (Id) ON DELETE CASCADE
                )");
        }

        [HttpGet("permissions")]
        [Authorize]
        public ActionResult<object> GetUserPermissions()
        {
            var roleClaim = User.FindFirst("Role")?.Value;
            if (!Enum.TryParse<UserRole>(roleClaim, out var userRole))
            {
                return BadRequest("Invalid role");
            }

            return Ok(new
            {
                role = userRole.ToString(),
                permissions = new
                {
                    canView = _permissionService.CanView(userRole),
                    canCreate = _permissionService.CanCreate(userRole),
                    canUpdate = _permissionService.CanUpdate(userRole),
                    canDelete = _permissionService.CanDelete(userRole),
                    canActivate = _permissionService.CanActivate(userRole),
                    canDeactivate = _permissionService.CanDeactivate(userRole)
                }
            });
        }

        [HttpGet("users/{userId}/orders")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<List<OrderListDto>>> GetUserOrders(int userId)
        {
            try
            {
                var userExists = await _context.Users.AnyAsync(u => u.Id == userId);
                if (!userExists)
                    return NotFound(new { message = "User not found" });

                var orders = await _context.Orders
                    .Include(o => o.ServiceType)
                    .Where(o => o.UserId == userId && o.Status != "Cancelled")
                    .OrderByDescending(o => o.OrderDate)
                    .Select(o => new OrderListDto
                    {
                        Id = o.Id,
                        UserId = o.UserId,
                        ContactEmail = o.ContactEmail,
                        ContactFirstName = o.ContactFirstName,
                        ContactLastName = o.ContactLastName,
                        ServiceTypeName = o.ServiceType != null && o.ServiceType.IsCustom && o.CustomServiceDisplayName != null && o.CustomServiceDisplayName != ""
                            ? o.CustomServiceDisplayName + " Cleaning"
                            : (o.ServiceType != null ? o.ServiceType.Name : ""),
                        IsCustomServiceType = o.ServiceType != null && o.ServiceType.IsCustom,
                        CustomServiceDisplayName = o.CustomServiceDisplayName,
                        ServiceDate = o.ServiceDate,
                        ServiceTime = o.ServiceTime,
                        Status = o.Status,
                        Total = o.Total,
                        ServiceAddress = o.ServiceAddress + (string.IsNullOrEmpty(o.AptSuite) ? "" : $", {o.AptSuite}"),
                        OrderDate = o.OrderDate,
                        TotalDuration = o.TotalDuration,
                        Tips = o.Tips,
                        CompanyDevelopmentTips = o.CompanyDevelopmentTips,
                        IsPaid = o.IsPaid,
                        PaidAt = o.PaidAt,
                        CancellationReason = o.CancellationReason,
                        IsLateCancellation = o.IsLateCancellation,
                        PointsRedeemed = o.PointsRedeemed,
                        PointsRedeemedDiscount = o.PointsRedeemedDiscount,
                        RewardBalanceUsed = o.RewardBalanceUsed,
                        PointsEarned = _context.BubblePointsHistories
                            .Where(h => h.OrderId == o.Id && h.UserId == userId && h.Points > 0)
                            .Sum(h => (int?)h.Points) ?? 0,
                        LoyaltyDiscountAmount = o.LoyaltyDiscountAmount,
                        LoyaltyDiscountPercentage = o.LoyaltyDiscountPercentage,
                        PaymentMethod = o.PaymentMethod.ToString(),
                        PaymentReference = o.PaymentReference,
                        PaymentNotes = o.PaymentNotes
                    })
                    .ToListAsync();

                return Ok(orders);
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpGet("users/{userId}/apartments")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<List<ApartmentDto>>> GetUserApartments(int userId)
        {
            try
            {
                var userExists = await _context.Users.AnyAsync(u => u.Id == userId);
                if (!userExists)
                    return NotFound(new { message = "User not found" });

                var apartments = await _context.Apartments
                    .Where(a => a.UserId == userId && a.IsActive)
                    .OrderBy(a => a.Name)
                    .Select(a => new ApartmentDto
                    {
                        Id = a.Id,
                        Name = a.Name,
                        Address = a.Address,
                        AptSuite = a.AptSuite,
                        City = a.City,
                        State = a.State,
                        PostalCode = a.PostalCode,
                        SpecialInstructions = a.SpecialInstructions
                    })
                    .ToListAsync();

                return Ok(apartments);
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        /// <summary>Admin/SuperAdmin: add an address (apartment) for another user. Admins cannot modify SuperAdmin users.</summary>
        [HttpPost("users/{userId}/apartments")]
        [Authorize(Roles = "Admin,SuperAdmin")]
        public async Task<ActionResult<ApartmentDto>> AddUserApartment(int userId, [FromBody] CreateApartmentDto dto)
        {
            var currentUserRole = GetCurrentUserRole();
            if (currentUserRole != UserRole.SuperAdmin && currentUserRole != UserRole.Admin)
                return Forbid();
            var targetUser = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId);
            if (targetUser == null)
                return NotFound(new { message = "User not found" });
            if (currentUserRole == UserRole.Admin && targetUser.Role == UserRole.SuperAdmin)
                return BadRequest(new { message = "Admins cannot modify SuperAdmin users" });
            try
            {
                var apartment = await _profileService.AddApartment(userId, dto);
                return Ok(apartment);
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        /// <summary>Admin/SuperAdmin: update an address (apartment) for another user. Admins cannot modify SuperAdmin users.</summary>
        [HttpPut("users/{userId}/apartments/{apartmentId}")]
        [Authorize(Roles = "Admin,SuperAdmin")]
        public async Task<ActionResult<ApartmentDto>> UpdateUserApartment(int userId, int apartmentId, [FromBody] ApartmentDto dto)
        {
            var currentUserRole = GetCurrentUserRole();
            if (currentUserRole != UserRole.SuperAdmin && currentUserRole != UserRole.Admin)
                return Forbid();
            var targetUser = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId);
            if (targetUser == null)
                return NotFound(new { message = "User not found" });
            if (currentUserRole == UserRole.Admin && targetUser.Role == UserRole.SuperAdmin)
                return BadRequest(new { message = "Admins cannot modify SuperAdmin users" });
            if (dto.Id != 0 && dto.Id != apartmentId)
                return BadRequest(new { message = "Apartment ID mismatch" });
            try
            {
                var apartment = await _profileService.UpdateApartment(userId, apartmentId, dto);
                return Ok(apartment);
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        /// <summary>Admin/SuperAdmin: delete an address (apartment) for another user. Admins cannot modify SuperAdmin users.</summary>
        [HttpDelete("users/{userId}/apartments/{apartmentId}")]
        [Authorize(Roles = "Admin,SuperAdmin")]
        public async Task<ActionResult> DeleteUserApartment(int userId, int apartmentId)
        {
            var currentUserRole = GetCurrentUserRole();
            if (currentUserRole != UserRole.SuperAdmin && currentUserRole != UserRole.Admin)
                return Forbid();
            var targetUser = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId);
            if (targetUser == null)
                return NotFound(new { message = "User not found" });
            if (currentUserRole == UserRole.Admin && targetUser.Role == UserRole.SuperAdmin)
                return BadRequest(new { message = "Admins cannot modify SuperAdmin users" });
            try
            {
                await _profileService.DeleteApartment(userId, apartmentId);
                return Ok(new { message = "Address deleted successfully" });
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpGet("users/{userId}/profile")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<UserDetailDto>> GetUserProfile(int userId)
        {
            try
            {
                var user = await _context.Users
                    .Include(u => u.Subscription)
                    .Include(u => u.Apartments.Where(a => a.IsActive))
                    .FirstOrDefaultAsync(u => u.Id == userId);

                if (user == null)
                    return NotFound(new { message = "User not found" });

                // Calculate user statistics from Orders table (excluding cancelled orders)
                var userOrders = await _context.Orders
                    .Where(o => o.UserId == userId && o.Status != "Cancelled")
                    .ToListAsync();

                var totalOrders = userOrders.Count;
                var totalSpent = userOrders.Sum(o => o.Total);
                var lastOrderDate = userOrders.OrderByDescending(o => o.OrderDate).FirstOrDefault()?.OrderDate;

                var userDetail = new UserDetailDto
                {
                    Id = user.Id,
                    FirstName = user.FirstName,
                    LastName = user.LastName,
                    Email = user.Email,
                    Phone = user.Phone,
                    Role = user.Role.ToString(),
                    AuthProvider = user.AuthProvider,
                    IsActive = user.IsActive,
                    FirstTimeOrder = user.FirstTimeOrder,
                    SubscriptionId = user.SubscriptionId,
                    SubscriptionName = user.Subscription?.Name,
                    SubscriptionExpiryDate = user.SubscriptionExpiryDate,
                    CreatedAt = user.CreatedAt,
                    CanReceiveCommunications = user.CanReceiveCommunications,
                    CanReceiveEmails = user.CanReceiveEmails,
                    CanReceiveMessages = user.CanReceiveMessages,
                    TotalOrders = totalOrders,
                    TotalSpent = totalSpent,
                    LastOrderDate = lastOrderDate,
                    ApartmentCount = user.Apartments.Count
                };

                return Ok(userDetail);
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpGet("users/{userId}/special-offers")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<List<UserSpecialOfferDto>>> GetUserSpecialOffers(int userId)
        {
            try
            {
                var userExists = await _context.Users.AnyAsync(u => u.Id == userId);
                if (!userExists)
                    return NotFound(new { message = "User not found" });

                var offers = await _specialOfferService.GetUserAvailableOffers(userId);
                return Ok(offers);
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpGet("users/{userId}/history")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult> GetUserCompleteHistory(int userId)
        {
            // Get all audit logs related to this user
            var userLogs = await _auditService.GetEntityHistoryAsync("User", userId);

            // Get all orders by this user and their audit logs
            var userOrders = await _context.Orders
                .Where(o => o.UserId == userId)
                .Select(o => o.Id)
                .ToListAsync();

            var orderLogs = new List<AuditLog>();
            foreach (var orderId in userOrders)
            {
                var logs = await _auditService.GetEntityHistoryAsync("Order", orderId);
                orderLogs.AddRange(logs);
            }

            // Combine and format
            var allLogs = userLogs.Concat(orderLogs)
                .OrderByDescending(l => l.CreatedAt)
                .Select(log => new
                {
                    log.Id,
                    log.EntityType,
                    log.EntityId,
                    log.Action,
                    log.CreatedAt,
                    ChangedBy = log.User?.FirstName + " " + log.User?.LastName,
                    OldValues = string.IsNullOrEmpty(log.OldValues) ? null : JsonConvert.DeserializeObject(log.OldValues),
                    NewValues = string.IsNullOrEmpty(log.NewValues) ? null : JsonConvert.DeserializeObject(log.NewValues),
                    ChangedFields = string.IsNullOrEmpty(log.ChangedFields) ? null : JsonConvert.DeserializeObject<List<string>>(log.ChangedFields)
                });

            return Ok(allLogs);
        }


        // ───── Loyalty Discount (re-engagement system) ─────
        //
        // User-scoped endpoints sit under /admin/users/{userId}/loyalty-discount and require
        // Permission.View for read, Permission.Update for write. The Moderator role has only
        // View by default, which matches the spec (read-only). Settings endpoints sit under
        // /admin/loyalty-discount-settings and are hidden from Moderator (Update-only writes,
        // View-gated reads).

        [HttpGet("users/{userId}/loyalty-discount")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<LoyaltyDiscountDto>> GetUserLoyaltyDiscount(int userId)
        {
            try
            {
                var dto = await _loyaltyDiscountService.GetForUserAsync(userId);
                return Ok(dto);
            }
            catch (InvalidOperationException ex)
            {
                return NotFound(new { message = ex.Message });
            }
        }

        [HttpPut("users/{userId}/loyalty-discount")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult<LoyaltyDiscountDto>> SetUserLoyaltyDiscount(int userId, [FromBody] SetLoyaltyDiscountDto body)
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            var adminUserId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "0");
            if (adminUserId == 0) return Unauthorized();

            try
            {
                var dto = await _loyaltyDiscountService.SetManualAsync(userId, body.Percentage, adminUserId);
                return Ok(dto);
            }
            catch (ArgumentOutOfRangeException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return NotFound(new { message = ex.Message });
            }
        }

        [HttpDelete("users/{userId}/loyalty-discount")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult<LoyaltyDiscountDto>> ClearUserLoyaltyDiscount(int userId)
        {
            var adminUserId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "0");
            if (adminUserId == 0) return Unauthorized();

            try
            {
                var dto = await _loyaltyDiscountService.ClearAsync(userId, adminUserId);
                return Ok(dto);
            }
            catch (InvalidOperationException ex)
            {
                return NotFound(new { message = ex.Message });
            }
        }

        [HttpGet("loyalty-discount-settings")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<LoyaltyDiscountSettingsDto>> GetLoyaltyDiscountSettings()
        {
            // BubbleRewardsSettingsService is a generic key/value store. The 7 loyalty keys all
            // live under category "LoyaltyDiscount" — seeded in ApplicationDbContext. We project
            // them into a typed DTO for the admin UI rather than returning the raw rows.
            var dto = new LoyaltyDiscountSettingsDto
            {
                LoyaltyDiscountEnabled = await _bubbleRewardsSettingsService.GetSetting<bool>("LoyaltyDiscountEnabled", true),
                LoyaltyDay60Percentage = await _bubbleRewardsSettingsService.GetSetting<decimal>("LoyaltyDay60Percentage", 10m),
                LoyaltyDay90Percentage = await _bubbleRewardsSettingsService.GetSetting<decimal>("LoyaltyDay90Percentage", 15m),
                DaysUntilFirstReminder = await _bubbleRewardsSettingsService.GetSetting<int>("DaysUntilFirstReminder", 30),
                DaysUntilDiscountActivation = await _bubbleRewardsSettingsService.GetSetting<int>("DaysUntilDiscountActivation", 60),
                DaysUntilDiscountUpgrade = await _bubbleRewardsSettingsService.GetSetting<int>("DaysUntilDiscountUpgrade", 90),
                MinDaysFromLastUseBeforeReActivation = await _bubbleRewardsSettingsService.GetSetting<int>("MinDaysFromLastUseBeforeReActivation", 30),
            };
            return Ok(dto);
        }

        [HttpPut("loyalty-discount-settings")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult<LoyaltyDiscountSettingsDto>> UpdateLoyaltyDiscountSettings([FromBody] LoyaltyDiscountSettingsDto body)
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            // Cross-field invariants. The 90-day percentage must not be below the 60-day one,
            // otherwise the "upgrade" path becomes a downgrade. The day thresholds must be
            // strictly increasing for the same reason.
            if (body.LoyaltyDay90Percentage < body.LoyaltyDay60Percentage)
                return BadRequest(new { message = "Day-90 percentage must be >= Day-60 percentage" });
            if (body.DaysUntilDiscountActivation <= body.DaysUntilFirstReminder)
                return BadRequest(new { message = "DaysUntilDiscountActivation must be > DaysUntilFirstReminder" });
            if (body.DaysUntilDiscountUpgrade <= body.DaysUntilDiscountActivation)
                return BadRequest(new { message = "DaysUntilDiscountUpgrade must be > DaysUntilDiscountActivation" });

            var updates = new List<BulkUpdateSettingDto>
            {
                new() { Key = "LoyaltyDiscountEnabled", Value = body.LoyaltyDiscountEnabled ? "true" : "false" },
                new() { Key = "LoyaltyDay60Percentage", Value = body.LoyaltyDay60Percentage.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                new() { Key = "LoyaltyDay90Percentage", Value = body.LoyaltyDay90Percentage.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                new() { Key = "DaysUntilFirstReminder", Value = body.DaysUntilFirstReminder.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                new() { Key = "DaysUntilDiscountActivation", Value = body.DaysUntilDiscountActivation.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                new() { Key = "DaysUntilDiscountUpgrade", Value = body.DaysUntilDiscountUpgrade.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                new() { Key = "MinDaysFromLastUseBeforeReActivation", Value = body.MinDaysFromLastUseBeforeReActivation.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            };

            await _bubbleRewardsSettingsService.BulkUpdateSettings(updates);

            // Re-read so the response reflects the persisted values (including any normalisation
            // BulkUpdateSettings might do).
            return await GetLoyaltyDiscountSettings();
        }
    }
}
