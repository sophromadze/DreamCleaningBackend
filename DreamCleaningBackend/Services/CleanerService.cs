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

        public CleanerService(ApplicationDbContext context, IEmailService emailService, IAuditService auditService)
        {
            _context = context;
            _emailService = emailService;
            _auditService = auditService;
        }

        public async Task<List<CleanerCalendarDto>> GetCleanerCalendarAsync(int cleanerId)
        {
            var startDate = DateTime.Today;
            var endDate = startDate.AddDays(30);

            // Get ALL active orders in the date range
            var allOrders = await _context.Orders
                .Where(o => o.ServiceDate >= startDate && o.ServiceDate <= endDate)
                .Where(o => o.Status == "Active")
                .Include(o => o.ServiceType)
                .Include(o => o.OrderCleaners)
                .ToListAsync(); // Execute query first

            // Then map to DTOs in memory where we can use null propagating operators
            var result = allOrders.Select(o => new CleanerCalendarDto
            {
                OrderId = o.Id,
                ClientName = $"{o.ContactFirstName} {o.ContactLastName}",
                ServiceDate = o.ServiceDate,
                ServiceTime = o.ServiceTime.ToString(),
                ServiceAddress = o.ApartmentName ?? "Address not provided",
                ServiceTypeName = o.ServiceType.Name,
                TotalDuration = o.TotalDuration,
                TipsForCleaner = o.OrderCleaners.FirstOrDefault(oc => oc.CleanerId == cleanerId)?.TipsForCleaner,
                IsAssignedToCleaner = o.OrderCleaners.Any(oc => oc.CleanerId == cleanerId)
            })
            .OrderBy(o => o.ServiceDate)
            .ThenBy(o => o.ServiceTime)
            .ToList();

            return result;
        }

        public async Task<CleanerOrderDetailDto> GetOrderDetailsForCleanerAsync(int orderId, int cleanerId)
        {
            var order = await _context.Orders
                .Include(o => o.ServiceType)
                .Include(o => o.OrderServices)
                    .ThenInclude(os => os.Service)
                .Include(o => o.OrderExtraServices)
                    .ThenInclude(oes => oes.ExtraService)
                .Include(o => o.OrderCleaners)
                    .ThenInclude(oc => oc.Cleaner)
                .FirstOrDefaultAsync(o => o.Id == orderId);

            if (order == null)
                return null;

            // Get additional tips from OrderCleaner if any
            var cleanerAssignment = order.OrderCleaners.FirstOrDefault(oc => oc.CleanerId == cleanerId);

            var assignedCleaners = order.OrderCleaners
                .Select(oc => $"{oc.Cleaner.FirstName} {oc.Cleaner.LastName}")
                .ToList();

            return new CleanerOrderDetailDto
            {
                OrderId = order.Id,
                ClientName = $"{order.ContactFirstName} {order.ContactLastName}",
                ClientEmail = order.ContactEmail,
                ClientPhone = order.ContactPhone,
                ServiceDate = order.ServiceDate,
                ServiceTime = order.ServiceTime.ToString(),
                ServiceAddress = order.ServiceAddress,
                AptSuite = order.AptSuite,
                City = order.City,
                State = order.State,
                ZipCode = order.ZipCode,
                ServiceTypeName = order.ServiceType.Name,
                Services = order.OrderServices.Select(os => $"{os.Service.Name} (x{os.Quantity})").ToList(),
                ExtraServices = order.OrderExtraServices.Select(oes =>
                {
                    if (oes.Hours > 0)
                        return $"{oes.ExtraService.Name} (x{oes.Quantity}, {oes.Hours:0.#}h)";
                    else
                        return $"{oes.ExtraService.Name} (x{oes.Quantity})";
                }).ToList(),
                TotalDuration = order.TotalDuration,
                MaidsCount = order.MaidsCount,
                EntryMethod = order.EntryMethod,
                SpecialInstructions = order.SpecialInstructions,
                Status = order.Status,
                // FIX: Use Order.Tips (the actual tips amount) and additional instructions
                TipsAmount = order.Tips, // Add this field
                TipsForCleaner = cleanerAssignment?.TipsForCleaner, // Additional instructions from admin
                AssignedCleaners = assignedCleaners
            };
        }

        public async Task<List<AvailableCleanerDto>> GetAvailableCleanersAsync(DateTime serviceDate, string serviceTime)
        {
            var cleaners = await _context.Users
                .Where(u => u.Role == UserRole.Cleaner && u.IsActive)
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
                                   oc.Order.Status == "Active");  // Use string comparison

                availableCleaners.Add(new AvailableCleanerDto
                {
                    Id = cleaner.Id,
                    FirstName = cleaner.FirstName,
                    LastName = cleaner.LastName,
                    Email = cleaner.Email,
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
                var assignedCleanerEmails = new List<string>();
                var newlyAssignedCleanerIds = new List<int>();

                // DON'T remove existing assignments - just add new ones or update existing ones
                foreach (var cleanerId in dto.CleanerIds)
                {
                    // Check if this cleaner is already assigned
                    var existingAssignment = await _context.OrderCleaners
                        .FirstOrDefaultAsync(oc => oc.OrderId == dto.OrderId && oc.CleanerId == cleanerId);

                    // Get cleaner details for audit logging
                    var cleaner = await _context.Users
                        .FirstOrDefaultAsync(u => u.Id == cleanerId);

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
                        newlyAssignedCleanerIds.Add(cleanerId); // Track newly assigned cleaners

                        // LOG CLEANER ASSIGNMENT TO AUDIT
                        if (cleaner != null)
                        {
                            try
                            {
                                await _auditService.LogCleanerAssignmentAsync(
                                    orderId: dto.OrderId,
                                    cleanerEmail: cleaner.Email,
                                    action: "Assigned",
                                    adminId: assignedBy
                                );
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"Audit logging failed for cleaner assignment: {ex.Message}");
                            }
                        }

                        if (cleaner != null)
                        {
                            assignedCleanerEmails.Add(cleaner.Email);
                        }
                    }
                    else
                    {
                        // Update existing assignment with new tips
                        existingAssignment.TipsForCleaner = dto.TipsForCleaner;
                        existingAssignment.AssignedAt = DateTime.Now;
                        existingAssignment.AssignedBy = assignedBy;
                    }
                }

                await _context.SaveChangesAsync();
                await transaction.CommitAsync();

                // KEEP ASSIGNMENT EMAILS SYNCHRONOUS (only newly assigned cleaners)
                if (newlyAssignedCleanerIds.Any())
                {
                    await SendCleanerAssignmentNotifications(dto.OrderId, newlyAssignedCleanerIds);
                }

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
                    cleanerEmail: assignment.Cleaner.Email,
                    action: "Removed",
                    adminId: removedBy
                );
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Audit logging failed for cleaner removal: {ex.Message}");
            }

            _context.OrderCleaners.Remove(assignment);
            await _context.SaveChangesAsync();

            // OPTIMIZED: Send removal notification in background (fire and forget)
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

            return true;
        }

        private async Task SendCleanerAssignmentNotifications(int orderId, List<int> cleanerIds)
        {
            var order = await _context.Orders
                .Include(o => o.ServiceType)
                .Include(o => o.OrderCleaners)
                .FirstOrDefaultAsync(o => o.Id == orderId);

            if (order == null) return;

            var cleaners = await _context.Users
                .Where(u => cleanerIds.Contains(u.Id))
                .ToListAsync();

            foreach (var cleaner in cleaners)
            {
                await _emailService.SendCleanerAssignmentNotificationAsync(
                    cleaner.Email,
                    cleaner.FirstName,
                    order.ServiceDate,
                    order.ServiceTime.ToString(),
                    order.ServiceType.Name,
                    order.ApartmentName ?? "Address provided separately"
                );
            }
        }
    }
}