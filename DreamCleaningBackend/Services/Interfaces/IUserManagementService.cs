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

        /// <summary>
        /// Some of this account's sessions were just ended server-side (a trusted device removed,
        /// "sign out other devices", a password change). Deliberately NOT a logout order: the
        /// browser that asked for it is in the same group and was re-issued a session, so every
        /// open browser re-checks its own session instead, and only the ones that were ended leave.
        /// </summary>
        Task NotifySessionsEnded(int userId);
    }
}