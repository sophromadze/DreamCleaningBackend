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
        private readonly ILogger<AuditService> _logger;

        // ADD THIS: JSON serialization settings to handle circular references
        private readonly JsonSerializerSettings _jsonSettings = new JsonSerializerSettings
        {
            ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
            NullValueHandling = NullValueHandling.Include,
            DateFormatHandling = DateFormatHandling.IsoDateFormat,
            DateTimeZoneHandling = DateTimeZoneHandling.Utc
        };

        public AuditService(ApplicationDbContext context, IHttpContextAccessor httpContextAccessor, ILogger<AuditService> logger)
        {
            _context = context;
            _httpContextAccessor = httpContextAccessor;
            _logger = logger;
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
                    CreatedAt = DateTime.UtcNow
                };

                _context.AuditLogs.Add(auditLog);
                await _context.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                // Log the error but don't throw - auditing shouldn't break the main operation
                _logger.LogError(ex, "Audit logging failed");
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
                            CreatedAt = DateTime.UtcNow
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
                            CreatedAt = DateTime.UtcNow
                        };

                        _context.AuditLogs.Add(auditLog);
                    }
                }

                await _context.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Audit logging failed");
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
                    CreatedAt = DateTime.UtcNow
                };

                _context.AuditLogs.Add(auditLog);
                await _context.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Audit logging failed");
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
                    CreatedAt = DateTime.UtcNow
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

        public async Task LogBubblePointsAdjustmentAsync(int targetUserId, string targetUserName, int points, string? reason, int adminId, string adminName)
        {
            try
            {
                var auditLog = new AuditLog
                {
                    EntityType = "BubblePointsAdjustment",
                    EntityId = targetUserId,
                    Action = points >= 0 ? "PointsAdded" : "PointsDeducted",
                    OldValues = null,
                    NewValues = JsonConvert.SerializeObject(new
                    {
                        TargetUserId = targetUserId,
                        TargetUserName = targetUserName,
                        Points = points,
                        Reason = reason ?? "No reason provided",
                        AdjustedBy = adminName,
                        AdjustedByAdminId = adminId
                    }, _jsonSettings),
                    ChangedFields = JsonConvert.SerializeObject(new[] { "BubblePoints" }, _jsonSettings),
                    UserId = adminId,
                    IpAddress = GetIpAddress(),
                    UserAgent = GetUserAgent(),
                    CreatedAt = DateTime.UtcNow
                };

                _context.AuditLogs.Add(auditLog);
                await _context.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Bubble points audit logging failed");
            }
        }

        // ─── Undo / redo ───────────────────────────────────────────────────────────────────
        //
        // Audit rows store enough information to revert / re-apply a Create / Update / Delete on
        // any concrete entity in the Models namespace. We deliberately exclude entity types that
        // (a) have external side effects (payments, webhooks, sent SMS / email) or (b) are virtual
        // audit-only types ("BubblePointsAdjustment", "CleanerAssignment", "OrderServicesUpdate")
        // that don't map to a single DbSet row. Trying to undo one of those returns an explicit
        // error rather than silently doing the wrong thing.
        //
        // For Update: we apply only the fields listed in ChangedFields so a later, partially-
        //   overlapping update isn't clobbered on unrelated columns.
        // For Create: we hard-delete the row on undo and re-insert (with its original PK) on redo.
        // For Delete: we re-insert the row on undo (preserving the original PK) and delete on redo.
        //
        // Side effects already emitted at the time of the original change (emails sent, Stripe
        // charges captured, etc.) are NOT reversed; this only changes database state. Admins
        // should be aware of that — it's the explicit scope of this feature.

        private static readonly HashSet<string> UndoBlockedEntityTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            // Audit/event logs — undoing these would lie about history.
            "AuditLog", "BubblePointsAdjustment", "CleanerAssignment", "OrderServicesUpdate",
            // Payment + external side effects.
            "PaymentHistory", "WebhookEvent",
            // Notification + scheduling logs.
            "NotificationLog", "ScheduledMail", "ScheduledSms",
            // Order-update history rows track money owed; reverting these breaks payment accounting.
            "OrderUpdateHistory",
            // NOTE: UserLoyaltyDiscount is intentionally NOT in this list. It's a virtual entity
            // type that scopes loyalty fields on the User row; the generic reflection dispatcher
            // can't resolve it, so UndoAsync / RedoAsync handle it explicitly below.
        };

        public async Task UndoAsync(long auditLogId)
        {
            var log = await _context.AuditLogs.FirstOrDefaultAsync(a => a.Id == auditLogId)
                ?? throw new InvalidOperationException("Audit log not found");
            if (log.UndoneAt.HasValue)
                throw new InvalidOperationException("This change has already been undone");
            if (UndoBlockedEntityTypes.Contains(log.EntityType))
                throw new InvalidOperationException($"'{log.EntityType}' changes cannot be undone (side-effect or audit-only entity)");

            // Special path for the virtual loyalty entity type — see ApplyLoyaltyAuditAsync for
            // the field-by-field logic. The standard reflection dispatcher would throw on this
            // because there's no UserLoyaltyDiscount class in Models.
            if (string.Equals(log.EntityType, "UserLoyaltyDiscount", StringComparison.OrdinalIgnoreCase))
            {
                await ApplyLoyaltyAuditAsync(log, useOldValues: true);
                log.UndoneAt = DateTime.UtcNow;
                await _context.SaveChangesAsync();
                return;
            }

            var clrType = ResolveEntityType(log.EntityType);

            switch (log.Action)
            {
                case "Create":
                    await DeleteByPrimaryKeyAsync(clrType, log.EntityId);
                    break;
                case "Update":
                    await ApplyJsonValuesAsync(clrType, log.EntityId, log.OldValues, log.ChangedFields);
                    break;
                case "Delete":
                    ReinsertFromJson(clrType, log.OldValues, log.EntityId, await FindByPrimaryKeyAsync(clrType, log.EntityId));
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported action '{log.Action}' for undo");
            }

            log.UndoneAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
        }

        public async Task RedoAsync(long auditLogId)
        {
            var log = await _context.AuditLogs.FirstOrDefaultAsync(a => a.Id == auditLogId)
                ?? throw new InvalidOperationException("Audit log not found");
            if (!log.UndoneAt.HasValue)
                throw new InvalidOperationException("This change is currently applied; nothing to redo");
            if (UndoBlockedEntityTypes.Contains(log.EntityType))
                throw new InvalidOperationException($"'{log.EntityType}' changes cannot be redone");

            if (string.Equals(log.EntityType, "UserLoyaltyDiscount", StringComparison.OrdinalIgnoreCase))
            {
                await ApplyLoyaltyAuditAsync(log, useOldValues: false);
                log.UndoneAt = null;
                await _context.SaveChangesAsync();
                return;
            }

            var clrType = ResolveEntityType(log.EntityType);

            switch (log.Action)
            {
                case "Create":
                    ReinsertFromJson(clrType, log.NewValues, log.EntityId, await FindByPrimaryKeyAsync(clrType, log.EntityId));
                    break;
                case "Update":
                    await ApplyJsonValuesAsync(clrType, log.EntityId, log.NewValues, log.ChangedFields);
                    break;
                case "Delete":
                    await DeleteByPrimaryKeyAsync(clrType, log.EntityId);
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported action '{log.Action}' for redo");
            }

            log.UndoneAt = null;
            await _context.SaveChangesAsync();
        }

        // Applies a UserLoyaltyDiscount audit row's snapshot to the underlying User entity.
        // The audit row stores Percentage / IsManualOverride / ActivatedAt / LastUsedAt in
        // OldValues + NewValues JSON; undo replays OldValues, redo replays NewValues.
        //
        // What this does NOT do:
        //   - Recreate the LoyaltyReminder30/60/90 NotificationLog rows that LoyaltyUsed
        //     deleted on consumption. Undoing a 'Used' restores the discount on the account
        //     but the cron's next pass may re-send a reminder (acceptable — the discount IS
        //     active again and the customer hasn't been told about this round yet).
        //   - Cascade to any Order.LoyaltyDiscount* snapshot. Order-level history is already
        //     covered by the Order audit rows.
        private async Task ApplyLoyaltyAuditAsync(AuditLog log, bool useOldValues)
        {
            var json = useOldValues ? log.OldValues : log.NewValues;
            if (string.IsNullOrWhiteSpace(json))
                throw new InvalidOperationException("Loyalty audit row has no values to apply");

            // EntityId is the target user id — same convention as LogLoyaltyDiscountChangeAsync.
            var userId = (int)log.EntityId;
            var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId)
                ?? throw new InvalidOperationException($"User #{userId} not found");

            var snapshot = JsonConvert.DeserializeAnonymousType(json, new
            {
                Percentage = (decimal?)null,
                IsManualOverride = (bool?)null,
                ActivatedAt = (DateTime?)null,
                LastUsedAt = (DateTime?)null,
            }, _jsonSettings);

            if (snapshot == null)
                throw new InvalidOperationException("Loyalty audit snapshot could not be deserialized");

            if (snapshot.Percentage.HasValue)
                user.LoyaltyDiscountPercentage = snapshot.Percentage.Value;
            if (snapshot.IsManualOverride.HasValue)
                user.LoyaltyDiscountIsManualOverride = snapshot.IsManualOverride.Value;
            // ActivatedAt/LastUsedAt are explicitly nullable on the User entity. The audit
            // bundle's null carries meaning ("cleared"), so we mirror without HasValue guards.
            user.LoyaltyDiscountActivatedAt = snapshot.ActivatedAt;
            user.LoyaltyDiscountLastUsedAt = snapshot.LastUsedAt;
        }

        // Look up a CLR Type by short class name in the Models namespace and verify it's mapped
        // by EF. Requiring an EF-mapped entity avoids accidentally targeting a DTO/POCO with the
        // same name.
        private Type ResolveEntityType(string entityTypeName)
        {
            var clrType = typeof(AuditLog).Assembly
                .GetTypes()
                .FirstOrDefault(t =>
                    t.IsClass && !t.IsAbstract &&
                    t.Namespace == "DreamCleaningBackend.Models" &&
                    string.Equals(t.Name, entityTypeName, StringComparison.OrdinalIgnoreCase));
            if (clrType == null)
                throw new InvalidOperationException($"No model type found for '{entityTypeName}'");

            if (_context.Model.FindEntityType(clrType) == null)
                throw new InvalidOperationException($"'{entityTypeName}' is not an EF-mapped entity");

            return clrType;
        }

        private async Task<object?> FindByPrimaryKeyAsync(Type clrType, long entityId)
        {
            // Use the EF-detected PK type to cast the long-stored EntityId correctly (some PKs
            // are int, others long).
            var efType = _context.Model.FindEntityType(clrType)!;
            var pk = efType.FindPrimaryKey()!.Properties.Single();
            object keyValue = pk.ClrType == typeof(int) ? (object)(int)entityId : entityId;
            return await _context.FindAsync(clrType, keyValue);
        }

        private async Task DeleteByPrimaryKeyAsync(Type clrType, long entityId)
        {
            var entity = await FindByPrimaryKeyAsync(clrType, entityId);
            if (entity == null)
                throw new InvalidOperationException($"{clrType.Name} #{entityId} not found — already deleted?");
            _context.Remove(entity);
        }

        // Applies a subset of fields from a JSON snapshot to an existing entity. Only writeable,
        // primitive-typed properties named in 'changedFieldsJson' are touched; Id and navigation
        // properties are skipped. Missing entity = error (don't silently no-op).
        private async Task ApplyJsonValuesAsync(Type clrType, long entityId, string? valuesJson, string? changedFieldsJson)
        {
            if (string.IsNullOrWhiteSpace(valuesJson))
                throw new InvalidOperationException("Audit row has no values to apply");

            var entity = await FindByPrimaryKeyAsync(clrType, entityId)
                ?? throw new InvalidOperationException($"{clrType.Name} #{entityId} not found");

            var snapshot = JsonConvert.DeserializeObject(valuesJson, clrType, _jsonSettings)
                ?? throw new InvalidOperationException("Could not deserialize audit snapshot");

            var fieldNames = string.IsNullOrWhiteSpace(changedFieldsJson)
                ? null
                : JsonConvert.DeserializeObject<List<string>>(changedFieldsJson);

            foreach (var prop in clrType.GetProperties())
            {
                if (!prop.CanRead || !prop.CanWrite) continue;
                if (prop.Name == "Id") continue;
                if (fieldNames != null && !fieldNames.Contains(prop.Name, StringComparer.OrdinalIgnoreCase)) continue;
                if (!IsRevertableScalar(prop.PropertyType)) continue;

                var value = prop.GetValue(snapshot);
                prop.SetValue(entity, value);
            }
        }

        // Re-insert a previously-deleted entity (or a previously-undone create's original row)
        // from its JSON snapshot, preserving its original primary key so foreign-key references
        // elsewhere still resolve. The caller has already looked up whether a row with this PK
        // currently exists so we don't duplicate work.
        private void ReinsertFromJson(Type clrType, string? valuesJson, long entityId, object? existingRow)
        {
            if (string.IsNullOrWhiteSpace(valuesJson))
                throw new InvalidOperationException("Audit row has no values to reinsert");
            if (existingRow != null)
                throw new InvalidOperationException($"{clrType.Name} #{entityId} still exists; nothing to reinsert");

            var entity = JsonConvert.DeserializeObject(valuesJson, clrType, _jsonSettings)
                ?? throw new InvalidOperationException("Could not deserialize audit snapshot");

            // Force the original PK so any foreign-key references that survived still resolve.
            var idProp = clrType.GetProperty("Id");
            if (idProp != null && idProp.CanWrite)
            {
                idProp.SetValue(entity, idProp.PropertyType == typeof(int) ? (object)(int)entityId : entityId);
            }

            _context.Add(entity);
        }

        private static bool IsRevertableScalar(Type t)
        {
            if (t.IsPrimitive || t.IsEnum) return true;
            if (t == typeof(string) || t == typeof(decimal) || t == typeof(decimal?)) return true;
            if (t == typeof(DateTime) || t == typeof(DateTime?)) return true;
            if (t == typeof(TimeSpan) || t == typeof(TimeSpan?)) return true;
            if (t == typeof(Guid) || t == typeof(Guid?)) return true;
            // Nullable primitives.
            var underlying = Nullable.GetUnderlyingType(t);
            return underlying != null && (underlying.IsPrimitive || underlying.IsEnum);
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
                    CreatedAt = DateTime.UtcNow
                };

                _context.AuditLogs.Add(auditLog);
                await _context.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Cleaner assignment audit logging failed");
            }
        }

        public async Task LogLoyaltyDiscountChangeAsync(
            int targetUserId,
            string action,
            decimal oldPercentage, bool oldIsManualOverride, DateTime? oldActivatedAt, DateTime? oldLastUsedAt,
            decimal newPercentage, bool newIsManualOverride, DateTime? newActivatedAt, DateTime? newLastUsedAt,
            int? adminUserId)
        {
            try
            {
                var changedFields = new List<string>();
                if (oldPercentage != newPercentage) changedFields.Add(nameof(User.LoyaltyDiscountPercentage));
                if (oldIsManualOverride != newIsManualOverride) changedFields.Add(nameof(User.LoyaltyDiscountIsManualOverride));
                if (oldActivatedAt != newActivatedAt) changedFields.Add(nameof(User.LoyaltyDiscountActivatedAt));
                if (oldLastUsedAt != newLastUsedAt) changedFields.Add(nameof(User.LoyaltyDiscountLastUsedAt));

                var auditLog = new AuditLog
                {
                    EntityType = "UserLoyaltyDiscount",
                    EntityId = targetUserId,
                    Action = action,
                    OldValues = JsonConvert.SerializeObject(new
                    {
                        Percentage = oldPercentage,
                        IsManualOverride = oldIsManualOverride,
                        ActivatedAt = oldActivatedAt,
                        LastUsedAt = oldLastUsedAt
                    }, _jsonSettings),
                    NewValues = JsonConvert.SerializeObject(new
                    {
                        Percentage = newPercentage,
                        IsManualOverride = newIsManualOverride,
                        ActivatedAt = newActivatedAt,
                        LastUsedAt = newLastUsedAt
                    }, _jsonSettings),
                    ChangedFields = JsonConvert.SerializeObject(changedFields, _jsonSettings),
                    // adminUserId is null for background-service writes (LoyaltyAutoActivated,
                    // LoyaltyAutoUpgraded, LoyaltyUsed, LoyaltyReversed). The admin UI displays
                    // these as "System".
                    UserId = adminUserId,
                    IpAddress = GetIpAddress(),
                    UserAgent = GetUserAgent(),
                    CreatedAt = DateTime.UtcNow
                };

                _context.AuditLogs.Add(auditLog);
                await _context.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Loyalty discount audit logging failed");
            }
        }
    }
}
