namespace DreamCleaningBackend.Services.Interfaces
{
    public interface IUserManagementService
    {
        Task NotifyUserBlocked(int userId, string reason);
        Task NotifyUserRoleChanged(int userId, string newRole);
        Task NotifyUserAccountUpdated(int userId, string title, string message);
        Task NotifyUserUnblocked(int userId);
        Task ForceUserLogout(int userId, string reason);
    }
}