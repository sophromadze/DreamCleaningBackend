using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace DreamCleaningBackend.Services
{
    public class CleanerService : ICleanerService
    {
        private readonly ApplicationDbContext _context;
        private readonly IEmailService _emailService;
        private readonly IAuditService _auditService;
        private readonly ISmsService _smsService;
        private readonly ILogger<CleanerService> _logger;

        public CleanerService(
            ApplicationDbContext context,
            IEmailService emailService,
            IAuditService auditService,
            ISmsService smsService,
            ILogger<CleanerService> logger)
        {
            _context = context;
            _emailService = emailService;
            _auditService = auditService;
            _smsService = smsService;
            _logger = logger;
        }

        // Dispatch a cleaner assignment notification. Cleaners with an email get the full HTML
        // email (preferred — richer formatting, no segmentation cost). Cleaners with only a phone
        // get the same data as a compact SMS body. Returns true if anything was sent.
        private async Task<bool> NotifyCleanerOfAssignmentAsync(Cleaner cleaner, int orderId)
        {
            if (!string.IsNullOrWhiteSpace(cleaner.Email))
            {
                await _emailService.SendCleanerAssignmentNotificationAsync(
                    cleaner.Email,
                    cleaner.FirstName,
                    orderId,
                    sendCopyToAdmin: false);
                return true;
            }

            if (!string.IsNullOrWhiteSpace(cleaner.Phone) && _smsService.IsSmsEnabled())
            {
                try
                {
                    var smsBody = await _emailService.BuildCleanerAssignmentSmsBodyAsync(cleaner, orderId);
                    if (!string.IsNullOrEmpty(smsBody))
                    {
                        await _smsService.SendSmsAsync(cleaner.Phone, smsBody);
                        return true;
                    }
                }
                catch (InvalidPhoneNumberException)
                {
                    _logger.LogWarning("Cleaner {CleanerId} has no email and an invalid phone — assignment notification not delivered", cleaner.Id);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to send cleaner assignment SMS to cleaner {CleanerId} for order {OrderId}", cleaner.Id, orderId);
                }
            }

            return false;
        }

        public async Task<List<AvailableCleanerDto>> GetAvailableCleanersAsync(DateTime serviceDate, string serviceTime)
        {
            var cleaners = await _context.Cleaners
                .Where(c => c.IsActive)
                .ToListAsync();

            var availableCleaners = new List<AvailableCleanerDto>();

            // Parse the time string to TimeSpan for comparison
            TimeSpan.TryParse(serviceTime, out var serviceTimeSpan);

            foreach (var cleaner in cleaners)
            {
                // Check if cleaner is already assigned to another order at the same time
                var isAvailable = !await _context.OrderCleaners
                    .AnyAsync(oc => oc.CleanerId == cleaner.Id &&
                                   oc.Order.ServiceDate.Date == serviceDate.Date &&
                                   oc.Order.ServiceTime == serviceTimeSpan &&
                                   oc.Order.Status == "Active");

                availableCleaners.Add(new AvailableCleanerDto
                {
                    Id = cleaner.Id,
                    FirstName = cleaner.FirstName,
                    LastName = cleaner.LastName,
                    Email = cleaner.Email ?? string.Empty,
                    IsAvailable = isAvailable
                });
            }

            return availableCleaners.OrderBy(c => c.LastName).ToList();
        }

        public async Task<bool> AssignCleanersToOrderAsync(AssignCleanersDto dto, int assignedBy)
        {
            using var transaction = await _context.Database.BeginTransactionAsync();

            try
            {
                // Update hourly rate and recalculate total salary if provided
                if (dto.CleanerHourlyRate.HasValue)
                {
                    var order = await _context.Orders
                        .Include(o => o.OrderServices)
                            .ThenInclude(os => os.Service)
                        .FirstOrDefaultAsync(o => o.Id == dto.OrderId);
                    if (order != null)
                    {
                        order.CleanerHourlyRate = dto.CleanerHourlyRate.Value;
                        // For cleaner-hours service type, TotalDuration is per cleaner; for regular, it's total across all
                        bool hasCleanersService = order.OrderServices.Any(os =>
                            os.Service?.ServiceRelationType == "cleaner");
                        var perCleanerDuration = hasCleanersService
                            ? order.TotalDuration
                            : (order.MaidsCount > 1 ? order.TotalDuration / order.MaidsCount : order.TotalDuration);
                        var roundedPerCleaner = (decimal)((int)Math.Round((double)perCleanerDuration / 15.0) * 15);
                        order.CleanerTotalSalary = Math.Round(roundedPerCleaner / 60m * order.MaidsCount * order.CleanerHourlyRate, 2);
                    }
                }

                // DON'T remove existing assignments - just add new ones or update existing ones
                foreach (var cleanerId in dto.CleanerIds)
                {
                    // Check if this cleaner is already assigned
                    var existingAssignment = await _context.OrderCleaners
                        .FirstOrDefaultAsync(oc => oc.OrderId == dto.OrderId && oc.CleanerId == cleanerId);

                    // Get cleaner details for audit logging
                    var cleaner = await _context.Cleaners
                        .FirstOrDefaultAsync(c => c.Id == cleanerId);

                    if (existingAssignment == null)
                    {
                        // Add new assignment
                        var orderCleaner = new OrderCleaner
                        {
                            OrderId = dto.OrderId,
                            CleanerId = cleanerId,
                            AssignedBy = assignedBy,
                            TipsForCleaner = dto.TipsForCleaner
                        };

                        _context.OrderCleaners.Add(orderCleaner);

                        // LOG CLEANER ASSIGNMENT TO AUDIT
                        if (cleaner != null)
                        {
                            try
                            {
                                await _auditService.LogCleanerAssignmentAsync(
                                    orderId: dto.OrderId,
                                    cleanerEmail: cleaner.Email ?? string.Empty,
                                    action: "Assigned",
                                    adminId: assignedBy
                                );
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"Audit logging failed for cleaner assignment: {ex.Message}");
                            }
                        }

                    }
                    else
                    {
                        // Update existing assignment with new tips
                        existingAssignment.TipsForCleaner = dto.TipsForCleaner;
                        existingAssignment.AssignedAt = DateTime.UtcNow;
                        existingAssignment.AssignedBy = assignedBy;
                    }
                }

                await _context.SaveChangesAsync();
                await transaction.CommitAsync();

                return true;
            }
            catch
            {
                await transaction.RollbackAsync();
                return false;
            }
        }

        public async Task<bool> UnassignCleanerFromOrderAsync(int orderId, int cleanerId, int removedBy)
        {
            var assignment = await _context.OrderCleaners
                .Include(oc => oc.Order)
                    .ThenInclude(o => o.ServiceType)
                .Include(oc => oc.Cleaner)
                .FirstOrDefaultAsync(oc => oc.OrderId == orderId && oc.CleanerId == cleanerId);

            if (assignment == null)
                return false;

            // Store cleaner info before removing
            var cleanerEmail = assignment.Cleaner.Email;
            var cleanerFirstName = assignment.Cleaner.FirstName;
            var serviceDate = assignment.Order.ServiceDate;
            var serviceTime = assignment.Order.ServiceTime.ToString();
            var serviceTypeName = assignment.Order.ServiceType.Name;

            // LOG CLEANER REMOVAL TO AUDIT BEFORE REMOVING
            try
            {
                await _auditService.LogCleanerAssignmentAsync(
                    orderId: orderId,
                    cleanerEmail: assignment.Cleaner.Email ?? string.Empty,
                    action: "Removed",
                    adminId: removedBy
                );
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Audit logging failed for cleaner removal: {ex.Message}");
            }

            _context.OrderCleaners.Remove(assignment);

            // Clean up NotificationLog entries for the removed cleaner on this order
            // so they won't receive any further reminders and can get fresh notifications if re-assigned
            var staleNotifications = await _context.NotificationLogs
                .Where(nl => nl.OrderId == orderId && nl.CleanerId == cleanerId)
                .ToListAsync();
            if (staleNotifications.Any())
            {
                _context.NotificationLogs.RemoveRange(staleNotifications);
            }

            await _context.SaveChangesAsync();

            // OPTIMIZED: Send removal notification in background (fire and forget)
            if (!string.IsNullOrWhiteSpace(cleanerEmail))
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _emailService.SendCleanerRemovalNotificationAsync(
                            cleanerEmail,
                            cleanerFirstName,
                            serviceDate,
                            serviceTime,
                            serviceTypeName
                        );
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Background removal email sending failed: {ex.Message}");
                    }
                });
            }

            return true;
        }

        public async Task<SendCleanerAssignmentMailsResultDto?> SendPendingCleanerAssignmentMailsAsync(int orderId)
        {
            var orderExists = await _context.Orders.AnyAsync(o => o.Id == orderId);
            if (!orderExists)
                return null;

            var pendingAssignments = await _context.OrderCleaners
                .Where(oc => oc.OrderId == orderId && oc.AssignmentNotificationSentAt == null)
                .Include(oc => oc.Cleaner)
                .ToListAsync();

            if (!pendingAssignments.Any())
            {
                return new SendCleanerAssignmentMailsResultDto
                {
                    EmailsSent = 0,
                    Message = "All assigned cleaners already received the assignment email."
                };
            }

            var sent = 0;
            foreach (var assignment in pendingAssignments)
            {
                if (assignment.Cleaner == null) continue;
                if (string.IsNullOrWhiteSpace(assignment.Cleaner.Email) &&
                    string.IsNullOrWhiteSpace(assignment.Cleaner.Phone))
                    continue;

                var delivered = await NotifyCleanerOfAssignmentAsync(assignment.Cleaner, orderId);
                if (!delivered) continue;

                assignment.AssignmentNotificationSentAt = DateTime.UtcNow;
                sent++;
            }

            await _context.SaveChangesAsync();

            if (sent == 0)
            {
                return new SendCleanerAssignmentMailsResultDto
                {
                    EmailsSent = 0,
                    Message = "No cleaners with a valid email or phone number needed an assignment notification."
                };
            }

            return new SendCleanerAssignmentMailsResultDto
            {
                EmailsSent = sent,
                Message = $"Assignment notification sent to {sent} cleaner(s)."
            };
        }

        public async Task<SendCleanerAssignmentMailsResultDto?> ResendCleanerAssignmentMailAsync(int orderId, int cleanerId)
        {
            var assignment = await _context.OrderCleaners
                .Include(oc => oc.Cleaner)
                .FirstOrDefaultAsync(oc => oc.OrderId == orderId && oc.CleanerId == cleanerId);

            if (assignment == null)
            {
                var orderExists = await _context.Orders.AnyAsync(o => o.Id == orderId);
                if (!orderExists)
                    return null;

                return new SendCleanerAssignmentMailsResultDto
                {
                    EmailsSent = 0,
                    Message = "Cleaner is not assigned to this order."
                };
            }

            if (assignment.Cleaner == null ||
                (string.IsNullOrWhiteSpace(assignment.Cleaner.Email) &&
                 string.IsNullOrWhiteSpace(assignment.Cleaner.Phone)))
            {
                return new SendCleanerAssignmentMailsResultDto
                {
                    EmailsSent = 0,
                    Message = "Cleaner has no email or phone number on file."
                };
            }

            // Restart reminder flow for this specific cleaner by removing previous reminder logs.
            // After the assignment notification is resent, reminder service will schedule exactly one fresh cycle.
            var staleReminderLogs = await _context.NotificationLogs
                .Where(nl => nl.OrderId == orderId
                    && nl.CleanerId == cleanerId
                    && (nl.NotificationType == "TwoDayReminder" || nl.NotificationType == "FourHourReminder"))
                .ToListAsync();

            if (staleReminderLogs.Any())
            {
                _context.NotificationLogs.RemoveRange(staleReminderLogs);
            }

            var delivered = await NotifyCleanerOfAssignmentAsync(assignment.Cleaner, orderId);
            if (!delivered)
            {
                return new SendCleanerAssignmentMailsResultDto
                {
                    EmailsSent = 0,
                    Message = "Assignment notification could not be delivered (invalid phone or SMS disabled)."
                };
            }

            assignment.AssignmentNotificationSentAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            var channel = !string.IsNullOrWhiteSpace(assignment.Cleaner.Email) ? "Email" : "SMS";
            return new SendCleanerAssignmentMailsResultDto
            {
                EmailsSent = 1,
                Message = $"{channel} assignment notification re-sent to {assignment.Cleaner.FirstName}."
            };
        }
    }
}
