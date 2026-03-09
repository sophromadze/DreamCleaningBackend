namespace DreamCleaningBackend.Services.Interfaces
{
    public interface IUserManagementService
    {
        Task NotifyUserBlocked(int userId, string reason);
        Task NotifyUserRoleChanged(int userId, string newRole);
        Task NotifyUserAccountUpdated(int userId, string title, string message);
        Task NotifyUserUnblocked(int userId);
        Task ForceUserLogout(int userId, string reason);
        /// <summary>Notify user that their account has been permanently deleted (e.g. so they see the message on site and get logged out).</summary>
        Task NotifyUserDeleted(int userId, string message);
    }
}