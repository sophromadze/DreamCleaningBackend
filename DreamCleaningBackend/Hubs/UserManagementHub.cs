using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging;

namespace DreamCleaningBackend.Hubs
{
    [Authorize]
    public class UserManagementHub : Hub
    {
        private readonly ILogger<UserManagementHub> _logger;
        
        // Store user connections
        private static readonly ConcurrentDictionary<int, HashSet<string>> UserConnections = new();

        public UserManagementHub(ILogger<UserManagementHub> logger)
        {
            _logger = logger;
        }

        public override async Task OnConnectedAsync()
        {
            _logger.LogInformation($"SignalR: New connection attempt - {Context.ConnectionId}");

            var userIdClaim = Context.User?.FindFirst("UserId")?.Value;
            _logger.LogInformation($"SignalR: User ID claim: {userIdClaim}");

            if (int.TryParse(userIdClaim, out int userId))
            {
                _logger.LogInformation($"SignalR: User {userId} connected with connection {Context.ConnectionId}");

                // Add connection to user's connection list
                UserConnections.AddOrUpdate(userId,
                    new HashSet<string> { Context.ConnectionId },
                    (key, connections) =>
                    {
                        connections.Add(Context.ConnectionId);
                        return connections;
                    });

                // Join user to their personal group
                await Groups.AddToGroupAsync(Context.ConnectionId, $"User_{userId}");
                _logger.LogInformation($"SignalR: User {userId} added to group User_{userId}");
            }
            else
            {
                _logger.LogWarning("SignalR: Failed to parse user ID from token");
            }

            await base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            _logger.LogInformation($"SignalR: Connection disconnected - {Context.ConnectionId}");

            var userIdClaim = Context.User?.FindFirst("UserId")?.Value;
            if (int.TryParse(userIdClaim, out int userId))
            {
                _logger.LogInformation($"SignalR: User {userId} disconnected");

                // Remove connection from user's connection list
                if (UserConnections.TryGetValue(userId, out var connections))
                {
                    connections.Remove(Context.ConnectionId);
                    if (connections.Count == 0)
                    {
                        UserConnections.TryRemove(userId, out _);
                        _logger.LogInformation($"SignalR: User {userId} has no more connections");
                    }
                }

                // Remove from personal group
                await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"User_{userId}");
            }

            await base.OnDisconnectedAsync(exception);
        }

        // Method to check if user is online
        public static bool IsUserOnline(int userId)
        {
            var isOnline = UserConnections.ContainsKey(userId) && UserConnections[userId].Count > 0;
            return isOnline;
        }

        // Method to get user's connection IDs
        public static HashSet<string>? GetUserConnections(int userId)
        {
            UserConnections.TryGetValue(userId, out var connections);
            return connections;
        }
    }
}