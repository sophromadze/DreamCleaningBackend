using DreamCleaningBackend.Data;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace DreamCleaningBackend.Services
{
    public class CleanerNotificationService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<CleanerNotificationService> _logger;
        private int _consecutiveErrors = 0;
        private const int MAX_CONSECUTIVE_ERRORS = 5;

        public CleanerNotificationService(IServiceProvider serviceProvider, ILogger<CleanerNotificationService> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Wait 30 seconds before starting to avoid startup issues
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await SendScheduledNotifications();
                    _consecutiveErrors = 0; // Reset on success
                }
                catch (Exception ex)
                {
                    _consecutiveErrors++;
                    _logger.LogError(ex, $"Error in cleaner notification service (attempt {_consecutiveErrors})");

                    if (_consecutiveErrors >= MAX_CONSECUTIVE_ERRORS)
                    {
                        _logger.LogCritical("Too many consecutive errors in CleanerNotificationService. Stopping service.");
                        break;
                    }
                }

                try
                {
                    // Use exponential backoff if errors occurred
                    var delay = _consecutiveErrors > 0
                        ? TimeSpan.FromMinutes(5 * _consecutiveErrors) // 5, 10, 15, 20, 25 minutes
                        : TimeSpan.FromHours(1); // Normal delay

                    await Task.Delay(delay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    // Expected when cancellation is requested
                    _logger.LogInformation("CleanerNotificationService cancellation requested");
                    break;
                }
            }

            _logger.LogInformation("CleanerNotificationService stopped");
        }

        private async Task SendScheduledNotifications()
        {
            using var scope = _serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var emailService = scope.ServiceProvider.GetRequiredService<IEmailService>();

            var now = DateTime.UtcNow;
            var twoDaysFromNow = now.AddDays(2);
            var fourHoursFromNow = now.AddHours(4);

            // Get orders for 2-day reminders
            var twoDayReminders = await context.OrderCleaners
                .Include(oc => oc.Order)
                    .ThenInclude(o => o.ServiceType)
                .Include(oc => oc.Cleaner)
                .Where(oc => oc.Order.ServiceDate.Date == twoDaysFromNow.Date &&
                           oc.Order.Status == "Active" &&
                           !context.NotificationLogs.Any(nl =>
                               nl.OrderId == oc.OrderId &&
                               nl.CleanerId == oc.CleanerId &&
                               nl.NotificationType == "TwoDayReminder"))
                .ToListAsync();

            // Get orders for 4-hour reminders (approximate time matching)
            var fourHourReminders = await context.OrderCleaners
                .Include(oc => oc.Order)
                    .ThenInclude(o => o.ServiceType)
                .Include(oc => oc.Cleaner)
                .Where(oc => oc.Order.ServiceDate.Date == now.Date &&
                           oc.Order.Status == "Active" &&
                           !context.NotificationLogs.Any(nl =>
                               nl.OrderId == oc.OrderId &&
                               nl.CleanerId == oc.CleanerId &&
                               nl.NotificationType == "FourHourReminder"))
                .ToListAsync();

            // Send 2-day reminders
            foreach (var orderCleaner in twoDayReminders)
            {
                try
                {
                    if (orderCleaner.Cleaner != null && orderCleaner.Order != null)
                    {
                        await emailService.SendCleanerReminderNotificationAsync(
                            orderCleaner.Cleaner.Email,
                            $"{orderCleaner.Cleaner.FirstName} {orderCleaner.Cleaner.LastName}",
                            orderCleaner.Order.ServiceDate,
                            orderCleaner.Order.ServiceTime.ToString(),
                            orderCleaner.Order.ServiceType?.Name ?? "Cleaning Service",
                            orderCleaner.Order.ServiceAddress,
                            true
                        );

                        // Log the notification
                        var log = new NotificationLog
                        {
                            OrderId = orderCleaner.OrderId,
                            CleanerId = orderCleaner.CleanerId,
                            NotificationType = "TwoDayReminder",
                            SentAt = DateTime.UtcNow
                        };
                        context.NotificationLogs.Add(log);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Failed to send 2-day reminder for Order {orderCleaner.OrderId}, Cleaner {orderCleaner.CleanerId}");
                }
            }

            // Send 4-hour reminders  
            foreach (var orderCleaner in fourHourReminders)
            {
                try
                {
                    var orderTime = orderCleaner.Order.ServiceTime;
                    var currentTime = TimeSpan.FromHours(now.Hour) + TimeSpan.FromMinutes(now.Minute);
                    var timeDifference = orderTime - currentTime;

                    if (timeDifference.TotalHours <= 4 && timeDifference.TotalHours > 0)
                    {
                        if (orderCleaner.Cleaner != null && orderCleaner.Order != null)
                        {
                            await emailService.SendCleanerReminderNotificationAsync(
                                orderCleaner.Cleaner.Email,
                                $"{orderCleaner.Cleaner.FirstName} {orderCleaner.Cleaner.LastName}",
                                orderCleaner.Order.ServiceDate,
                                orderCleaner.Order.ServiceTime.ToString(),
                                orderCleaner.Order.ServiceType?.Name ?? "Cleaning Service",
                                orderCleaner.Order.ServiceAddress,
                                false
                            );

                            // Log the notification
                            var log = new NotificationLog
                            {
                                OrderId = orderCleaner.OrderId,
                                CleanerId = orderCleaner.CleanerId,
                                NotificationType = "FourHourReminder",
                                SentAt = DateTime.UtcNow
                            };
                            context.NotificationLogs.Add(log);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Failed to send 4-hour reminder for Order {orderCleaner.OrderId}, Cleaner {orderCleaner.CleanerId}");
                }
            }

            // Save all notification logs
            if (context.ChangeTracker.HasChanges())
            {
                await context.SaveChangesAsync();
            }
        }
    }
}