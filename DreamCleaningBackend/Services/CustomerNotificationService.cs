using DreamCleaningBackend.Data;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace DreamCleaningBackend.Services
{
    public class CustomerNotificationService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<CustomerNotificationService> _logger;

        public CustomerNotificationService(IServiceProvider serviceProvider, ILogger<CustomerNotificationService> logger)
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
                    await SendScheduledCustomerNotifications();

                    // Check every hour
                    await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in customer notification service");
                }
            }
        }

        private async Task SendScheduledCustomerNotifications()
        {
            using var scope = _serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var emailService = scope.ServiceProvider.GetRequiredService<IEmailService>();

            var now = DateTime.Now;
            var twoDaysFromNow = now.AddDays(2);
            var twoHoursFromNow = now.AddHours(2);

            // Get orders for 2-day reminders (only for orders scheduled more than 2 days from booking)
            var twoDayReminders = await context.Orders
                .Include(o => o.ServiceType)
                .Include(o => o.User)
                .Where(o => o.ServiceDate.Date == twoDaysFromNow.Date &&
                           o.Status == "Active" &&
                           o.IsPaid &&
                           // Only send 2-day reminder if order was created more than 2 days ago
                           o.OrderDate.Date < twoDaysFromNow.AddDays(-2).Date &&
                           !context.NotificationLogs.Any(nl =>
                               nl.OrderId == o.Id &&
                               nl.CustomerId == o.UserId &&
                               nl.NotificationType == "CustomerTwoDayReminder"))
                .ToListAsync();

            // Get orders for 2-hour reminders (approximate time matching)
            var twoHourReminders = await context.Orders
                .Include(o => o.ServiceType)
                .Include(o => o.User)
                .Where(o => o.ServiceDate.Date == now.Date &&
                           o.Status == "Active" &&
                           o.IsPaid &&
                           !context.NotificationLogs.Any(nl =>
                               nl.OrderId == o.Id &&
                               nl.CustomerId == o.UserId &&
                               nl.NotificationType == "CustomerTwoHourReminder"))
                .ToListAsync();

            // Filter 2-hour reminders by time (within 30 minutes of target time)
            var filteredTwoHourReminders = new List<Order>();
            foreach (var order in twoHourReminders)
            {
                var serviceDateTime = order.ServiceDate.Date.Add(order.ServiceTime);
                var timeDiff = Math.Abs((serviceDateTime - twoHoursFromNow).TotalMinutes);
                if (timeDiff <= 30)
                {
                    filteredTwoHourReminders.Add(order);
                }
            }

            // Send 2-day reminders
            foreach (var order in twoDayReminders)
            {
                try
                {
                    await emailService.SendCustomerReminderNotificationAsync(
                        order.ContactEmail,
                        order.ContactFirstName,
                        order.ServiceDate,
                        order.ServiceTime.ToString(),
                        order.ServiceType.Name,
                        order.ServiceAddress,
                        true // isDaysBefore = true for 2-day reminder
                    );

                    // Log notification
                    context.NotificationLogs.Add(new NotificationLog
                    {
                        OrderId = order.Id,
                        CustomerId = order.UserId,
                        NotificationType = "CustomerTwoDayReminder",
                        SentAt = DateTime.Now
                    });

                    _logger.LogInformation($"Sent 2-day reminder to customer {order.ContactEmail} for order {order.Id}");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Failed to send 2-day reminder for order {order.Id} to customer {order.ContactEmail}");
                }
            }

            // Send 2-hour reminders
            foreach (var order in filteredTwoHourReminders)
            {
                try
                {
                    await emailService.SendCustomerReminderNotificationAsync(
                        order.ContactEmail,
                        order.ContactFirstName,
                        order.ServiceDate,
                        order.ServiceTime.ToString(),
                        order.ServiceType.Name,
                        order.ServiceAddress,
                        false // isDaysBefore = false for 2-hour reminder
                    );

                    // Log notification
                    context.NotificationLogs.Add(new NotificationLog
                    {
                        OrderId = order.Id,
                        CustomerId = order.UserId,
                        NotificationType = "CustomerTwoHourReminder",
                        SentAt = DateTime.Now
                    });

                    _logger.LogInformation($"Sent 2-hour reminder to customer {order.ContactEmail} for order {order.Id}");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Failed to send 2-hour reminder for order {order.Id} to customer {order.ContactEmail}");
                }
            }

            await context.SaveChangesAsync();
        }
    }
}