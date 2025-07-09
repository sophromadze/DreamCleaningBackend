using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services.Interfaces;

namespace DreamCleaningBackend.Services
{
    public class PermissionService : IPermissionService
    {
        private readonly Dictionary<UserRole, Permission> _rolePermissions = new()
        {
            // SuperAdmin has all permissions including managing SuperAdmin roles
            { UserRole.SuperAdmin, Permission.View | Permission.Create | Permission.Update |
                                 Permission.Delete | Permission.Activate | Permission.Deactivate |
                                   Permission.ManageRoles | Permission.ManageSuperAdminRoles },
        
            // Admin has all permissions except Delete and ManageSuperAdminRoles
            { UserRole.Admin, Permission.View | Permission.Create | Permission.Update |
                           Permission.Activate | Permission.Deactivate | Permission.ManageRoles },
        
            // Moderator can only view
            { UserRole.Moderator, Permission.View },
        
            // Customer has no admin permissions
            { UserRole.Customer, 0 }
        };

        public bool HasPermission(UserRole role, Permission permission)
        {
            if (_rolePermissions.TryGetValue(role, out var rolePermissions))
            {
                return (rolePermissions & permission) == permission;
            }
            return false;
        }

        public bool CanView(UserRole role) => HasPermission(role, Permission.View);
        public bool CanCreate(UserRole role) => HasPermission(role, Permission.Create);
        public bool CanUpdate(UserRole role) => HasPermission(role, Permission.Update);
        public bool CanDelete(UserRole role) => HasPermission(role, Permission.Delete);
        public bool CanActivate(UserRole role) => HasPermission(role, Permission.Activate);
        public bool CanDeactivate(UserRole role) => HasPermission(role, Permission.Deactivate);
        public bool CanManageRoles(UserRole role) => HasPermission(role, Permission.ManageRoles);
        public bool CanManageSuperAdminRoles(UserRole role) => HasPermission(role, Permission.ManageSuperAdminRoles);
    }
}
