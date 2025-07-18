using DreamCleaningBackend.Data;
using DreamCleaningBackend.Models;
using Microsoft.EntityFrameworkCore;
using DreamCleaningBackend.Services.Interfaces;
using Newtonsoft.Json;
using System.Security.Claims;

namespace DreamCleaningBackend.Services
{
    public class AuditService : IAuditService
    {
        private readonly ApplicationDbContext _context;
        private readonly IHttpContextAccessor _httpContextAccessor;

        // ADD THIS: JSON serialization settings to handle circular references
        private readonly JsonSerializerSettings _jsonSettings = new JsonSerializerSettings
        {
            ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
            NullValueHandling = NullValueHandling.Include,
            DateFormatHandling = DateFormatHandling.IsoDateFormat,
            DateTimeZoneHandling = DateTimeZoneHandling.Utc
        };

        public AuditService(ApplicationDbContext context, IHttpContextAccessor httpContextAccessor)
        {
            _context = context;
            _httpContextAccessor = httpContextAccessor;
        }

        public async Task LogCreateAsync<T>(T entity) where T : class
        {
            try
            {
                var entityType = typeof(T).Name;
                var entityId = GetEntityId(entity);
                var userId = GetCurrentUserId();

                // Create a clean copy without navigation properties
                var cleanEntity = CreateCleanEntity(entity);

                var auditLog = new AuditLog
                {
                    EntityType = entityType,
                    EntityId = entityId,
                    Action = "Create",
                    OldValues = null,
                    NewValues = JsonConvert.SerializeObject(cleanEntity, _jsonSettings),
                    ChangedFields = JsonConvert.SerializeObject(GetAllPropertyNames(entity), _jsonSettings),
                    UserId = userId,
                    IpAddress = GetIpAddress(),
                    UserAgent = GetUserAgent(),
                    CreatedAt = DateTime.Now
                };

                _context.AuditLogs.Add(auditLog);
                await _context.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                // Log the error but don't throw - auditing shouldn't break the main operation
                Console.WriteLine($"Audit logging failed: {ex.Message}");
            }
        }

        public async Task LogUpdateAsync<T>(T originalEntity, T currentEntity) where T : class
        {
            try
            {
                var entityType = typeof(T).Name;
                var entityId = GetEntityId(currentEntity);
                var userId = GetCurrentUserId();

                var changedFields = GetChangedFields(originalEntity, currentEntity);

                // Special handling for Order entity
                if (entityType == "Order" && originalEntity is Order originalOrder && currentEntity is Order currentOrder)
                {
                    // First, log the main order changes (without navigation properties)
                    if (changedFields.Any())
                    {
                        var cleanOriginal = CreateCleanEntity(originalEntity);
                        var cleanCurrent = CreateCleanEntity(currentEntity);

                        var auditLog = new AuditLog
                        {
                            EntityType = entityType,
                            EntityId = entityId,
                            Action = "Update",
                            OldValues = JsonConvert.SerializeObject(cleanOriginal, _jsonSettings),
                            NewValues = JsonConvert.SerializeObject(cleanCurrent, _jsonSettings),
                            ChangedFields = JsonConvert.SerializeObject(changedFields, _jsonSettings),
                            UserId = userId,
                            IpAddress = GetIpAddress(),
                            UserAgent = GetUserAgent(),
                            CreatedAt = DateTime.Now
                        };

                        _context.AuditLogs.Add(auditLog);
                    }

                    // Now check if services have changed and create a separate audit entry
                    await LogOrderServiceChanges(originalOrder, currentOrder, entityId, userId);
                }
                else
                {
                    // Standard handling for other entities
                    if (changedFields.Any())
                    {
                        var cleanOriginal = CreateCleanEntity(originalEntity);
                        var cleanCurrent = CreateCleanEntity(currentEntity);

                        var auditLog = new AuditLog
                        {
                            EntityType = entityType,
                            EntityId = entityId,
                            Action = "Update",
                            OldValues = JsonConvert.SerializeObject(cleanOriginal, _jsonSettings),
                            NewValues = JsonConvert.SerializeObject(cleanCurrent, _jsonSettings),
                            ChangedFields = JsonConvert.SerializeObject(changedFields, _jsonSettings),
                            UserId = userId,
                            IpAddress = GetIpAddress(),
                            UserAgent = GetUserAgent(),
                            CreatedAt = DateTime.Now
                        };

                        _context.AuditLogs.Add(auditLog);
                    }
                }

                await _context.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Audit logging failed: {ex.Message}");
            }
        }

        public async Task LogDeleteAsync<T>(T entity) where T : class
        {
            try
            {
                var entityType = typeof(T).Name;
                var entityId = GetEntityId(entity);
                var userId = GetCurrentUserId();

                // Create a clean copy without navigation properties
                var cleanEntity = CreateCleanEntity(entity);

                var auditLog = new AuditLog
                {
                    EntityType = entityType,
                    EntityId = entityId,
                    Action = "Delete",
                    OldValues = JsonConvert.SerializeObject(cleanEntity, _jsonSettings),
                    NewValues = null,
                    ChangedFields = null,
                    UserId = userId,
                    IpAddress = GetIpAddress(),
                    UserAgent = GetUserAgent(),
                    CreatedAt = DateTime.Now
                };

                _context.AuditLogs.Add(auditLog);
                await _context.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Audit logging failed: {ex.Message}");
            }
        }

        public async Task<List<AuditLog>> GetEntityHistoryAsync(string entityType, long entityId)
        {
            return await _context.AuditLogs
                .Where(a => a.EntityType == entityType && a.EntityId == entityId)
                .OrderByDescending(a => a.CreatedAt)
                .Include(a => a.User)
                .ToListAsync();
        }

        // Helper method to create a clean entity without navigation properties
        private object CreateCleanEntity(object entity)
        {
            var entityType = entity.GetType();
            var cleanEntity = Activator.CreateInstance(entityType);

            var properties = entityType.GetProperties()
                .Where(p => p.CanRead && p.CanWrite &&
                       (p.PropertyType.IsPrimitive ||
                        p.PropertyType == typeof(string) ||
                        p.PropertyType == typeof(DateTime) ||
                        p.PropertyType == typeof(DateTime?) ||
                        p.PropertyType == typeof(TimeSpan) ||
                        p.PropertyType == typeof(TimeSpan?) ||
                        p.PropertyType == typeof(decimal) ||
                        p.PropertyType == typeof(decimal?) ||
                        p.PropertyType == typeof(int) ||
                        p.PropertyType == typeof(int?) ||
                        p.PropertyType == typeof(long) ||
                        p.PropertyType == typeof(long?) ||
                        p.PropertyType == typeof(bool) ||
                        p.PropertyType == typeof(bool?) ||
                        p.PropertyType.IsEnum));

            foreach (var property in properties)
            {
                try
                {
                    var value = property.GetValue(entity);
                    property.SetValue(cleanEntity, value);
                }
                catch
                {
                    // Skip properties that throw exceptions
                }
            }

            return cleanEntity;
        }

        private async Task LogOrderServiceChanges(Order originalOrder, Order currentOrder, long orderId, int? userId)
        {
            // Create snapshots of services - explicitly cast to object
            var beforeServices = originalOrder.OrderServices?.Select(os => (object)new
            {
                ServiceId = os.ServiceId,
                ServiceName = os.Service?.Name ?? $"Service {os.ServiceId}",
                Quantity = os.Quantity,
                Cost = os.Cost
            }).OrderBy(s => ((dynamic)s).ServiceId).ToList() ?? new List<object>();

            var afterServices = currentOrder.OrderServices?.Select(os => (object)new
            {
                ServiceId = os.ServiceId,
                ServiceName = os.Service?.Name ?? $"Service {os.ServiceId}",
                Quantity = os.Quantity,
                Cost = os.Cost
            }).OrderBy(s => ((dynamic)s).ServiceId).ToList() ?? new List<object>();

            var beforeExtraServices = originalOrder.OrderExtraServices?.Select(oes => (object)new
            {
                ExtraServiceId = oes.ExtraServiceId,
                ExtraServiceName = oes.ExtraService?.Name ?? $"Extra Service {oes.ExtraServiceId}",
                Quantity = oes.Quantity,
                Hours = oes.Hours,
                Cost = oes.Cost
            }).OrderBy(s => ((dynamic)s).ExtraServiceId).ToList() ?? new List<object>();

            var afterExtraServices = currentOrder.OrderExtraServices?.Select(oes => (object)new
            {
                ExtraServiceId = oes.ExtraServiceId,
                ExtraServiceName = oes.ExtraService?.Name ?? $"Extra Service {oes.ExtraServiceId}",
                Quantity = oes.Quantity,
                Hours = oes.Hours,
                Cost = oes.Cost
            }).OrderBy(s => ((dynamic)s).ExtraServiceId).ToList() ?? new List<object>();

            // Check for changes more reliably
            bool servicesChanged = false;
            bool extraServicesChanged = false;

            // Check regular services
            if (beforeServices.Count != afterServices.Count)
            {
                servicesChanged = true;
            }
            else
            {
                for (int i = 0; i < beforeServices.Count; i++)
                {
                    dynamic before = beforeServices[i];
                    dynamic after = afterServices[i];

                    if (before.ServiceId != after.ServiceId ||
                        before.Quantity != after.Quantity ||
                        Math.Abs((decimal)before.Cost - (decimal)after.Cost) > 0.01m)
                    {
                        servicesChanged = true;
                        break;
                    }
                }
            }

            // Check extra services
            if (beforeExtraServices.Count != afterExtraServices.Count)
            {
                extraServicesChanged = true;
            }
            else
            {
                for (int i = 0; i < beforeExtraServices.Count; i++)
                {
                    dynamic before = beforeExtraServices[i];
                    dynamic after = afterExtraServices[i];

                    if (before.ExtraServiceId != after.ExtraServiceId ||
                        before.Quantity != after.Quantity ||
                        before.Hours != after.Hours ||
                        Math.Abs((decimal)before.Cost - (decimal)after.Cost) > 0.01m)
                    {
                        extraServicesChanged = true;
                        break;
                    }
                }
            }

            if (servicesChanged || extraServicesChanged)
            {
                var serviceAuditLog = new AuditLog
                {
                    EntityType = "OrderServicesUpdate",
                    EntityId = orderId,
                    Action = "Update",
                    OldValues = JsonConvert.SerializeObject(new
                    {
                        Services = beforeServices,
                        ExtraServices = beforeExtraServices
                    }, _jsonSettings),
                    NewValues = JsonConvert.SerializeObject(new
                    {
                        Services = afterServices,
                        ExtraServices = afterExtraServices
                    }, _jsonSettings),
                    ChangedFields = JsonConvert.SerializeObject(new[] { "Services", "ExtraServices" }, _jsonSettings),
                    UserId = userId,
                    IpAddress = GetIpAddress(),
                    UserAgent = GetUserAgent(),
                    CreatedAt = DateTime.Now
                };

                _context.AuditLogs.Add(serviceAuditLog);
            }
        }

        // Rest of your helper methods remain the same...
        private long GetEntityId(object entity)
        {
            var idProperty = entity.GetType().GetProperty("Id");
            if (idProperty != null)
            {
                var value = idProperty.GetValue(entity);
                return Convert.ToInt64(value);
            }
            return 0;
        }

        private int? GetCurrentUserId()
        {
            var userIdClaim = _httpContextAccessor.HttpContext?.User?.FindFirst(ClaimTypes.NameIdentifier);
            if (userIdClaim != null && int.TryParse(userIdClaim.Value, out int userId))
            {
                return userId;
            }
            return null;
        }

        private string? GetIpAddress()
        {
            return _httpContextAccessor.HttpContext?.Connection?.RemoteIpAddress?.ToString();
        }

        private string? GetUserAgent()
        {
            return _httpContextAccessor.HttpContext?.Request?.Headers["User-Agent"].ToString();
        }

        private List<string> GetAllPropertyNames(object entity)
        {
            return entity.GetType()
                .GetProperties()
                .Where(p => p.CanRead && p.CanWrite && !p.PropertyType.IsClass ||
                           p.PropertyType == typeof(string) ||
                           p.PropertyType == typeof(DateTime) ||
                           p.PropertyType == typeof(DateTime?))
                .Select(p => p.Name)
                .ToList();
        }

        private List<string> GetChangedFields(object original, object current)
        {
            var changedFields = new List<string>();
            var properties = original.GetType().GetProperties()
                .Where(p => p.CanRead && p.CanWrite &&
                       (p.PropertyType.IsPrimitive ||
                        p.PropertyType == typeof(string) ||
                        p.PropertyType == typeof(DateTime) ||
                        p.PropertyType == typeof(DateTime?) ||
                        p.PropertyType == typeof(TimeSpan) ||
                        p.PropertyType == typeof(TimeSpan?) ||
                        p.PropertyType == typeof(decimal) ||
                        p.PropertyType == typeof(decimal?) ||
                        p.PropertyType == typeof(int) ||
                        p.PropertyType == typeof(int?) ||
                        p.PropertyType == typeof(long) ||
                        p.PropertyType == typeof(long?) ||
                        p.PropertyType == typeof(bool) ||
                        p.PropertyType == typeof(bool?) ||
                        p.PropertyType.IsEnum));

            foreach (var property in properties)
            {
                try
                {
                    var originalValue = property.GetValue(original);
                    var currentValue = property.GetValue(current);

                    if (!Equals(originalValue, currentValue))
                    {
                        changedFields.Add(property.Name);
                    }
                }
                catch
                {
                    // Skip properties that throw exceptions
                }
            }

            return changedFields;
        }

        public async Task LogCleanerAssignmentAsync(int orderId, string cleanerEmail, string action, int adminId)
        {
            try
            {
                var auditLog = new AuditLog
                {
                    EntityType = "CleanerAssignment",
                    EntityId = orderId,
                    Action = action, // "Assigned" or "Removed"
                    OldValues = null,
                    NewValues = JsonConvert.SerializeObject(new
                    {
                        CleanerEmail = cleanerEmail,
                        OrderId = orderId
                    }, _jsonSettings),
                    ChangedFields = JsonConvert.SerializeObject(new[] { "CleanerEmail" }, _jsonSettings),
                    UserId = adminId,
                    IpAddress = GetIpAddress(),
                    UserAgent = GetUserAgent(),
                    CreatedAt = DateTime.Now
                };

                _context.AuditLogs.Add(auditLog);
                await _context.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Cleaner assignment audit logging failed: {ex.Message}");
            }
        }
    }
}
