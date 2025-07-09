using DreamCleaningBackend.Hubs;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.AspNetCore.SignalR;

namespace DreamCleaningBackend.Services
{
    public class UserManagementService : IUserManagementService
    {
        private readonly IHubContext<UserManagementHub> _hubContext;

        public UserManagementService(IHubContext<UserManagementHub> hubContext)
        {
            _hubContext = hubContext;
        }

        public async Task NotifyUserBlocked(int userId, string reason)
        {
            await _hubContext.Clients.Group($"User_{userId}")
                .SendAsync("UserBlocked", new
                {
                    message = reason,
                    timestamp = DateTime.Now,
                    shouldLogout = true
                });
        }

        public async Task NotifyUserRoleChanged(int userId, string newRole)
        {
            await _hubContext.Clients.Group($"User_{userId}")
                .SendAsync("RoleChanged", new
                {
                    newRole = newRole,
                    message = $"Your role has been updated to {newRole}. Please refresh to see the changes.",
                    timestamp = DateTime.Now,
                    shouldRefresh = true
                });
        }

        public async Task NotifyUserUnblocked(int userId)
        {
            await _hubContext.Clients.Group($"User_{userId}")
                .SendAsync("UserUnblocked", new
                {
                    message = "Your account has been unblocked. You can now use the system normally.",
                    timestamp = DateTime.Now
                });
        }

        public async Task ForceUserLogout(int userId, string reason)
        {
            await _hubContext.Clients.Group($"User_{userId}")
                .SendAsync("ForceLogout", new
                {
                    reason = reason,
                    timestamp = DateTime.Now
                });
        }
    }
}