using DreamCleaningBackend.Models;
using Microsoft.AspNetCore.Mvc;

namespace DreamCleaningBackend.Controllers
{
    /// <summary>
    /// Shared base for the topic-split admin controllers (AdminCatalogController,
    /// AdminUsersController, AdminOrdersController, ...). They all serve under the
    /// same "api/admin" route prefix the original monolithic AdminController used,
    /// so every existing frontend URL keeps working unchanged.
    /// </summary>
    public abstract class AdminControllerBase : ControllerBase
    {
        protected UserRole GetCurrentUserRole()
        {
            var roleClaim = User.FindFirst("Role")?.Value;
            Enum.TryParse<UserRole>(roleClaim, out var role);
            return role;
        }

        protected int GetCurrentUserId()
        {
            return int.TryParse(User.FindFirst("UserId")?.Value, out var id) ? id : 0;
        }
    }
}
