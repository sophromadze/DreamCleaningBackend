using DreamCleaningBackend.Models;

namespace DreamCleaningBackend.Services.Interfaces
{
    public interface IPermissionService
    {
        bool HasPermission(UserRole role, Permission permission);
        bool CanView(UserRole role);
        bool CanCreate(UserRole role);
        bool CanUpdate(UserRole role);
        bool CanDelete(UserRole role);
        bool CanActivate(UserRole role);
        bool CanDeactivate(UserRole role);
    }
}
