namespace DreamCleaningBackend.Services.Interfaces
{
    public interface IUserManagementService
    {
        Task NotifyUserBlocked(int userId, string reason);
        Task NotifyUserRoleChanged(int userId, string newRole);
        Task NotifyUserUnblocked(int userId);
        Task ForceUserLogout(int userId, string reason);
    }
}