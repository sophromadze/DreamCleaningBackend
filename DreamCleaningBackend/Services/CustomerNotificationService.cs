using DreamCleaningBackend.Data;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace DreamCleaningBackend.Services
{
    public class CustomerNotificationService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<CustomerNotificationService> _logger;
        private int _consecutiveErrors = 0;
        private const int MAX_CONSECUTIVE_ERRORS = 5;

        public CustomerNotificationService(IServiceProvider serviceProvider, ILogger<CustomerNotificationService> logger)
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
                    await SendScheduledCustomerNotifications();
                    _consecutiveErrors = 0; // Reset on success
                }
                catch (Exception ex)
                {
                    _consecutiveErrors++;
                    _logger.LogError(ex, $"Error in customer notification service (attempt {_consecutiveErrors})");

                    if (_consecutiveErrors >= MAX_CONSECUTIVE_ERRORS)
                    {
                        _logger.LogCritical("Too many consecutive errors in CustomerNotificationService. Stopping service.");
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
                    _logger.LogInformation("CustomerNotificationService cancellation requested");
                    break;
                }
            }

            _logger.LogInformation("CustomerNotificationService stopped");
        }

        private async Task SendScheduledCustomerNotifications()
        {
            using var scope = _serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var emailService = scope.ServiceProvider.GetRequiredService<IEmailService>();

            // ServiceDate/ServiceTime are NY wall-clock values, so "today" and the
            // hours-until-cleaning math must run on NY time. Comparing against UTC
            // made the 2-hour reminder fire ~4-5 hours early (UTC offset ahead of NY).
            var now = NyTimeHelper.NowNy;
            var todayNy = now.Date;
            var twoDaysFromNow = todayNy.AddDays(2);

            // Get orders for 2-day customer reminders
            var twoDayReminders = await context.Orders
                .Include(o => o.ServiceType)
                .Include(o => o.User)
                .Where(o => o.ServiceDate.Date == twoDaysFromNow &&
                           o.Status == "Active" &&
                           o.User != null &&
                           !context.NotificationLogs.Any(nl =>
                               nl.OrderId == o.Id &&
                               nl.NotificationType == "CustomerTwoDayReminder"))
                .ToListAsync();

            // Get orders for 2-hour customer reminders
            var twoHourReminders = await context.Orders
                .Include(o => o.ServiceType)
                .Include(o => o.User)
                .Where(o => o.ServiceDate.Date == todayNy &&
                           o.Status == "Active" &&
                           o.User != null &&
                           !context.NotificationLogs.Any(nl =>
                               nl.OrderId == o.Id &&
                               nl.NotificationType == "CustomerTwoHourReminder"))
                .ToListAsync();

            // Send 2-day customer reminders
            foreach (var order in twoDayReminders)
            {
                try
                {
                    if (order.User != null)
                    {
                        // Send customer reminder email. Target the LIVE user-account email so an
                        // admin who corrected a mistyped address sees reminders follow the fix —
                        // not the contact frozen on the order at booking time. (Query already
                        // filters User != null; ContactEmail is only a defensive fallback.)
                        await emailService.SendCustomerReminderNotificationAsync(
                            order.User.Email ?? order.ContactEmail,
                            $"{order.ContactFirstName} {order.ContactLastName}",
                            order.ServiceDate,
                            order.ServiceTime.ToString(),
                            order.ServiceType?.Name ?? "Cleaning Service",
                            order.ServiceAddress,
                            true // This is a 2-day reminder
                        );

                        // Log the notification
                        var log = new NotificationLog
                        {
                            OrderId = order.Id,
                            NotificationType = "CustomerTwoDayReminder",
                            SentAt = DateTime.UtcNow
                        };
                        context.NotificationLogs.Add(log);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Failed to send customer reminder for Order {order.Id}");
                }
            }

            // Send 2-hour customer reminders
            foreach (var order in twoHourReminders)
            {
                try
                {
                    var orderTime = order.ServiceTime;
                    // now is NY wall-clock, matching how ServiceTime is stored.
                    var timeDifference = orderTime - now.TimeOfDay;

                    // Check if it's within 2 hours window
                    if (timeDifference.TotalHours <= 2 && timeDifference.TotalHours > 0)
                    {
                        if (order.User != null)
                        {
                            // Send customer reminder email to the LIVE user-account email so a
                            // corrected address is honored (see 2-day reminder note above).
                            await emailService.SendCustomerReminderNotificationAsync(
                                order.User.Email ?? order.ContactEmail,
                                $"{order.ContactFirstName} {order.ContactLastName}",
                                order.ServiceDate,
                                order.ServiceTime.ToString(),
                                order.ServiceType?.Name ?? "Cleaning Service",
                                order.ServiceAddress,
                                false // This is a 2-hour (same day) reminder
                            );

                            // Log the notification
                            var log = new NotificationLog
                            {
                                OrderId = order.Id,
                                NotificationType = "CustomerTwoHourReminder",
                                SentAt = DateTime.UtcNow
                            };
                            context.NotificationLogs.Add(log);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Failed to send 2-hour customer reminder for Order {order.Id}");
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