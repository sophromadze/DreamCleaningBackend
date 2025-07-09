using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;
using System.Security.Claims;

namespace DreamCleaningBackend.Hubs
{
    [Authorize]
    public class UserManagementHub : Hub
    {
        // Store user connections
        private static readonly ConcurrentDictionary<int, HashSet<string>> UserConnections = new();

        public override async Task OnConnectedAsync()
        {
            Console.WriteLine($"SignalR: New connection attempt - {Context.ConnectionId}");

            var userIdClaim = Context.User?.FindFirst("UserId")?.Value;
            Console.WriteLine($"SignalR: User ID claim: {userIdClaim}");

            if (int.TryParse(userIdClaim, out int userId))
            {
                Console.WriteLine($"SignalR: User {userId} connected with connection {Context.ConnectionId}");

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
                Console.WriteLine($"SignalR: User {userId} added to group User_{userId}");
            }
            else
            {
                Console.WriteLine("SignalR: Failed to parse user ID from token");
            }

            await base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            Console.WriteLine($"SignalR: Connection disconnected - {Context.ConnectionId}");

            var userIdClaim = Context.User?.FindFirst("UserId")?.Value;
            if (int.TryParse(userIdClaim, out int userId))
            {
                Console.WriteLine($"SignalR: User {userId} disconnected");

                // Remove connection from user's connection list
                if (UserConnections.TryGetValue(userId, out var connections))
                {
                    connections.Remove(Context.ConnectionId);
                    if (connections.Count == 0)
                    {
                        UserConnections.TryRemove(userId, out _);
                        Console.WriteLine($"SignalR: All connections removed for user {userId}");
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
            Console.WriteLine($"SignalR: User {userId} online status: {isOnline}");
            return isOnline;
        }

        // Method to get user's connection IDs
        public static HashSet<string>? GetUserConnections(int userId)
        {
            UserConnections.TryGetValue(userId, out var connections);
            Console.WriteLine($"SignalR: User {userId} has {connections?.Count ?? 0} connections");
            return connections;
        }
    }
}