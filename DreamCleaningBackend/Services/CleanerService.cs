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

        // Minimum gap (minutes) required between the end of one job and the start of the next
        // for the same cleaner on the same day. Below this, assignment is hard-blocked.
        private const int MinGapMinutes = 60;

        public async Task<List<AvailableCleanerDto>> GetAvailableCleanersAsync(Order order)
        {
            var serviceDate = order.ServiceDate.Date;
            var serviceTimeSpan = order.ServiceTime;
            var orderCity = order.City;

            var cleaners = await _context.Cleaners
                .Where(c => c.IsActive)
                .Include(c => c.Vacations)
                .ToListAsync();

            // Pull every other Active/Pending assignment on the same calendar day in one query,
            // then evaluate the 1-hour-gap rule per cleaner in memory.
            var sameDayJobs = await _context.OrderCleaners
                .Where(oc => oc.OrderId != order.Id &&
                             oc.Order.ServiceDate.Date == serviceDate &&
                             (oc.Order.Status == "Active" || oc.Order.Status == "Pending"))
                .Select(oc => new SameDayJob
                {
                    CleanerId = oc.CleanerId,
                    Start = oc.Order.ServiceTime,
                    DurationMinutes = oc.Order.TotalDuration
                })
                .ToListAsync();

            var jobsByCleaner = sameDayJobs
                .GroupBy(j => j.CleanerId)
                .ToDictionary(g => g.Key, g => g.ToList());

            var availableCleaners = new List<AvailableCleanerDto>();

            foreach (var cleaner in cleaners)
            {
                var (isBusyDay, busyReason) = EvaluateBusyDay(cleaner, serviceDate);
                jobsByCleaner.TryGetValue(cleaner.Id, out var cleanerJobs);
                var (hasConflict, conflictReason) = EvaluateScheduleConflict(
                    cleanerJobs, serviceTimeSpan, order.TotalDuration);

                availableCleaners.Add(new AvailableCleanerDto
                {
                    Id = cleaner.Id,
                    FirstName = cleaner.FirstName,
                    LastName = cleaner.LastName,
                    Email = cleaner.Email ?? string.Empty,
                    IsAvailable = !hasConflict,
                    Location = cleaner.Location,
                    Ranking = cleaner.Ranking,
                    Experience = cleaner.Experience,
                    IsBusyDay = isBusyDay,
                    BusyDayReason = busyReason,
                    HasScheduleConflict = hasConflict,
                    ConflictReason = conflictReason,
                    CreatedAt = cleaner.CreatedAt
                });
            }

            // Suggest the best fit first: cleaners with no conflict on top, then cleaners that are
            // not marked busy that day, then borough match, then ranking (Top first) and experience.
            static string Normalize(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant();
            var city = Normalize(orderCity);

            int LocationRank(AvailableCleanerDto c) =>
                !string.IsNullOrEmpty(city) && Normalize(c.Location) == city ? 0 : 1;

            static int ExperienceRank(string? experience)
            {
                var e = Normalize(experience);
                if (e == "good") return 0;
                if (e == "normal") return 1;
                return 2;
            }

            return availableCleaners
                .OrderByDescending(c => c.IsAvailable)
                .ThenBy(c => c.IsBusyDay)
                .ThenBy(c => LocationRank(c))
                .ThenBy(c => (int)c.Ranking)
                .ThenBy(c => ExperienceRank(c.Experience))
                .ThenBy(c => c.LastName)
                .ToList();
        }

        // Lightweight projection of an existing same-day assignment used for conflict math.
        private class SameDayJob
        {
            public int CleanerId { get; set; }
            public TimeSpan Start { get; set; }
            public decimal DurationMinutes { get; set; }
        }

        // Parses the CSV of DayOfWeek integers stored on Cleaner.BusyDaysOfWeek.
        public static HashSet<DayOfWeek> ParseBusyDaysOfWeek(string? csv)
        {
            var set = new HashSet<DayOfWeek>();
            if (string.IsNullOrWhiteSpace(csv))
                return set;

            foreach (var part in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (int.TryParse(part, out var n) && n >= 0 && n <= 6)
                    set.Add((DayOfWeek)n);
            }
            return set;
        }

        // Soft "busy day" check: recurring weekday off OR a vacation range covering the date.
        private static (bool isBusy, string? reason) EvaluateBusyDay(Cleaner cleaner, DateTime date)
        {
            var reasons = new List<string>();

            if (ParseBusyDaysOfWeek(cleaner.BusyDaysOfWeek).Contains(date.DayOfWeek))
                reasons.Add($"Off on {date.DayOfWeek}s");

            var vacation = cleaner.Vacations?
                .FirstOrDefault(v => v.StartDate.Date <= date && date <= v.EndDate.Date);
            if (vacation != null)
                reasons.Add($"On vacation {vacation.StartDate:MMM d} – {vacation.EndDate:MMM d}");

            return reasons.Count == 0 ? (false, null) : (true, string.Join(" · ", reasons));
        }

        // Hard conflict check: any existing job that day whose interval is within MinGapMinutes
        // of the new job (overlapping or less than the required gap on either side).
        private static (bool hasConflict, string? reason) EvaluateScheduleConflict(
            List<SameDayJob>? sameDayJobs, TimeSpan newStart, decimal newDurationMinutes)
        {
            if (sameDayJobs == null || sameDayJobs.Count == 0)
                return (false, null);

            var newEnd = newStart + TimeSpan.FromMinutes((double)newDurationMinutes);
            var buffer = TimeSpan.FromMinutes(MinGapMinutes);

            foreach (var job in sameDayJobs.OrderBy(j => j.Start))
            {
                var existStart = job.Start;
                var existEnd = existStart + TimeSpan.FromMinutes((double)job.DurationMinutes);

                // Conflict unless one job fully ends at least `buffer` before the other starts.
                if (newStart < existEnd + buffer && existStart < newEnd + buffer)
                {
                    return (true,
                        $"Booked {FormatTime(existStart)}–{FormatTime(existEnd)} that day (needs {MinGapMinutes}-min gap)");
                }
            }
            return (false, null);
        }

        private static string FormatTime(TimeSpan t)
        {
            // Normalize into a 0–24h day for display (durations can push past midnight).
            var minutes = ((int)t.TotalMinutes % 1440 + 1440) % 1440;
            var hours24 = minutes / 60;
            var mins = minutes % 60;
            var period = hours24 >= 12 ? "PM" : "AM";
            var hours12 = hours24 % 12;
            if (hours12 == 0) hours12 = 12;
            return $"{hours12}:{mins:D2} {period}";
        }

        // Returns the display names of cleaners that cannot be assigned to this order due to the
        // 1-hour-gap rule against their other Active/Pending jobs the same day. Empty = all clear.
        private async Task<List<string>> FindScheduleConflictsAsync(Order order, IEnumerable<int> cleanerIds)
        {
            var ids = cleanerIds.Distinct().ToList();
            if (ids.Count == 0)
                return new List<string>();

            var serviceDate = order.ServiceDate.Date;

            var sameDayJobs = await _context.OrderCleaners
                .Where(oc => ids.Contains(oc.CleanerId) &&
                             oc.OrderId != order.Id &&
                             oc.Order.ServiceDate.Date == serviceDate &&
                             (oc.Order.Status == "Active" || oc.Order.Status == "Pending"))
                .Select(oc => new SameDayJob
                {
                    CleanerId = oc.CleanerId,
                    Start = oc.Order.ServiceTime,
                    DurationMinutes = oc.Order.TotalDuration
                })
                .ToListAsync();

            if (sameDayJobs.Count == 0)
                return new List<string>();

            var jobsByCleaner = sameDayJobs.GroupBy(j => j.CleanerId).ToDictionary(g => g.Key, g => g.ToList());
            var conflictingIds = ids
                .Where(id => jobsByCleaner.TryGetValue(id, out var jobs) &&
                             EvaluateScheduleConflict(jobs, order.ServiceTime, order.TotalDuration).hasConflict)
                .ToList();

            if (conflictingIds.Count == 0)
                return new List<string>();

            return await _context.Cleaners
                .Where(c => conflictingIds.Contains(c.Id))
                .Select(c => (c.FirstName + " " + c.LastName).Trim())
                .ToListAsync();
        }

        public async Task<bool> AssignCleanersToOrderAsync(AssignCleanersDto dto, int assignedBy)
        {
            using var transaction = await _context.Database.BeginTransactionAsync();

            try
            {
                var order = await _context.Orders
                    .Include(o => o.OrderServices)
                        .ThenInclude(os => os.Service)
                    .FirstOrDefaultAsync(o => o.Id == dto.OrderId);

                if (order == null)
                {
                    await transaction.RollbackAsync();
                    return false;
                }

                // HARD RULE: refuse cleaners that already have an Active/Pending job the same day
                // within 1 hour of this one. Admins MAY assign cleaners marked "busy" (weekday/vacation),
                // so that is intentionally NOT blocked here — only real schedule overlaps are.
                var conflicts = await FindScheduleConflictsAsync(order, dto.CleanerIds);
                if (conflicts.Count > 0)
                {
                    throw new CleanerAssignmentException(
                        "Cannot assign — these cleaners have another job within 1 hour the same day: "
                        + string.Join(", ", conflicts) + ".");
                }

                // Update hourly rate and recalculate total salary if provided
                if (dto.CleanerHourlyRate.HasValue)
                {
                    order.CleanerHourlyRate = dto.CleanerHourlyRate.Value;
                    bool hasCleanersService = order.OrderServices.Any(os =>
                        os.Service?.ServiceRelationType == "cleaner");
                    order.CleanerTotalSalary = OrderPricingCalculator.CalculateCleanerTotalSalary(
                        order.TotalDuration, order.MaidsCount, hasCleanersService, order.CleanerHourlyRate);
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
                                _logger.LogError(ex, "Audit logging failed for cleaner assignment");
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
            catch (CleanerAssignmentException)
            {
                await transaction.RollbackAsync();
                throw;
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
                _logger.LogError(ex, "Audit logging failed for cleaner removal");
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
                        _logger.LogError(ex, "Background removal email sending failed");
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
