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

        public CleanerNotificationService(IServiceProvider serviceProvider, ILogger<CleanerNotificationService> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await SendScheduledNotifications();

                    // Check every hour
                    await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in cleaner notification service");
                }
            }
        }

        private async Task SendScheduledNotifications()
        {
            using var scope = _serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var emailService = scope.ServiceProvider.GetRequiredService<IEmailService>();

            var now = DateTime.Now;
            var twoDaysFromNow = now.AddDays(2);
            var fourHoursFromNow = now.AddHours(4);

            // Get orders for 2-day reminders
            var twoDayReminders = await context.OrderCleaners
                .Include(oc => oc.Order)
                    .ThenInclude(o => o.ServiceType)
                .Include(oc => oc.Cleaner)
                .Where(oc => oc.Order.ServiceDate.Date == twoDaysFromNow.Date &&
                           oc.Order.Status == "Active" &&  // Use string comparison
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
                           oc.Order.Status == "Active" &&  // Use string comparison
                           !context.NotificationLogs.Any(nl =>
                               nl.OrderId == oc.OrderId &&
                               nl.CleanerId == oc.CleanerId &&
                               nl.NotificationType == "FourHourReminder"))
                .ToListAsync();

            // Filter 4-hour reminders by time (within 30 minutes of target time)
            var filteredFourHourReminders = new List<OrderCleaner>();
            foreach (var oc in fourHourReminders)
            {
                var serviceDateTime = oc.Order.ServiceDate.Date.Add(oc.Order.ServiceTime);
                var timeDiff = Math.Abs((serviceDateTime - fourHoursFromNow).TotalMinutes);
                if (timeDiff <= 30)
                {
                    filteredFourHourReminders.Add(oc);
                }
            }

            // Send 2-day reminders
            foreach (var orderCleaner in twoDayReminders)
            {
                try
                {
                    await emailService.SendCleanerReminderNotificationAsync(
                        orderCleaner.Cleaner.Email,
                        orderCleaner.Cleaner.FirstName,
                        orderCleaner.Order.ServiceDate,
                        orderCleaner.Order.ServiceTime.ToString(),
                        orderCleaner.Order.ServiceType.Name,
                        orderCleaner.Order.ApartmentName ?? "Address provided separately",
                        true
                    );

                    // Log notification
                    context.NotificationLogs.Add(new NotificationLog
                    {
                        OrderId = orderCleaner.OrderId,
                        CleanerId = orderCleaner.CleanerId,
                        NotificationType = "TwoDayReminder",
                        SentAt = DateTime.Now
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Failed to send 2-day reminder for order {orderCleaner.OrderId} to cleaner {orderCleaner.CleanerId}");
                }
            }

            // Send 4-hour reminders
            foreach (var orderCleaner in filteredFourHourReminders)
            {
                try
                {
                    await emailService.SendCleanerReminderNotificationAsync(
                        orderCleaner.Cleaner.Email,
                        orderCleaner.Cleaner.FirstName,
                        orderCleaner.Order.ServiceDate,
                        orderCleaner.Order.ServiceTime.ToString(),
                        orderCleaner.Order.ServiceType.Name,
                        orderCleaner.Order.ApartmentName ?? "Address provided separately",
                        false
                    );

                    // Log notification
                    context.NotificationLogs.Add(new NotificationLog
                    {
                        OrderId = orderCleaner.OrderId,
                        CleanerId = orderCleaner.CleanerId,
                        NotificationType = "FourHourReminder",
                        SentAt = DateTime.Now
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Failed to send 4-hour reminder for order {orderCleaner.OrderId} to cleaner {orderCleaner.CleanerId}");
                }
            }

            await context.SaveChangesAsync();
        }
    }
}