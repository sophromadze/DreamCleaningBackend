using System.Reflection;
using DreamCleaningBackend.Models;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Newtonsoft.Json;

namespace DreamCleaningBackend.Services.ChangeSets
{
    /// <summary>
    /// Which entity types the change history captures at the data layer (2026-10), and how a row
    /// of one is turned into the JSON snapshot an AuditLog stores.
    ///
    /// These are the Services catalogue (everything on the admin Services tab), subscription
    /// plans, and expenses. For them the history is written by <see cref="AuditCaptureInterceptor"/>
    /// inside the same database transaction as the change, whoever made it — the admin panel, the
    /// Claude connector or a background job — and <see cref="AuditService"/>'s explicit
    /// LogCreate/Update/Delete calls write nothing for them, so a change is never recorded twice.
    /// Their rows are kept forever (<see cref="AuditLogCleanupService"/> skips them) and are the
    /// ones the Revert flow can restore.
    /// </summary>
    public static class AuditCaptureScope
    {
        private static readonly Dictionary<string, Type> TypesByName = new Type[]
        {
            typeof(ServiceType), typeof(Service), typeof(ServiceThreshold), typeof(ServiceRateTier),
            typeof(ExtraService), typeof(PollQuestion), typeof(Subscription),
            typeof(Expense), typeof(ExpenseCategory)
        }.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);

        /// <summary>The AuditLog.EntityType names of the captured types.</summary>
        public static IReadOnlyCollection<string> EntityTypeNames { get; } = TypesByName.Keys.ToList();

        /// <summary>The Services-tab half (everything except subscriptions and expenses).</summary>
        public static IReadOnlySet<string> CatalogueTypeNames { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            nameof(ServiceType), nameof(Service), nameof(ServiceThreshold), nameof(ServiceRateTier),
            nameof(ExtraService), nameof(PollQuestion)
        };

        public static IReadOnlySet<string> ExpenseTypeNames { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            nameof(Expense), nameof(ExpenseCategory)
        };

        public static bool IsCaptured(Type type) => TypesByName.ContainsKey(type.Name) && TypesByName[type.Name] == type;

        public static bool IsCaptured(string? entityType) => entityType != null && TypesByName.ContainsKey(entityType);

        public static Type? Resolve(string? entityType) =>
            entityType != null && TypesByName.TryGetValue(entityType, out var t) ? t : null;

        public const string UndoReplacedMessage =
            "Changes to the Services catalogue, plans and expenses are reverted with Revert, which previews the change and checks nothing has moved since.";

        /// <summary>Bookkeeping columns: never a reason to write an Update row, never compared by a fingerprint check.</summary>
        public static readonly IReadOnlySet<string> NoiseFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "UpdatedAt"
        };

        /// <summary>The same serializer settings AuditService has always written rows with.</summary>
        public static readonly JsonSerializerSettings Json = new()
        {
            ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
            NullValueHandling = NullValueHandling.Include,
            DateFormatHandling = DateFormatHandling.IsoDateFormat,
            DateTimeZoneHandling = DateTimeZoneHandling.Utc
        };

        /// <summary>
        /// The restorable scalar columns of <paramref name="type"/> in declaration order — the same
        /// set AuditService has always snapshotted (navigation properties are never included).
        /// </summary>
        public static IReadOnlyList<PropertyInfo> Columns(Type type) =>
            ColumnCache.GetOrAdd(type, t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead && p.CanWrite && IsRevertableScalar(p.PropertyType) && !AuditDataPolicy.IsSecret(p.Name))
                .ToList());

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, IReadOnlyList<PropertyInfo>> ColumnCache = new();

        /// <summary>A column snapshot read from EF's tracked values (current or original).</summary>
        public static Dictionary<string, object?> Snapshot(EntityEntry entry, bool original)
        {
            var values = original ? entry.OriginalValues : entry.CurrentValues;
            var result = new Dictionary<string, object?>();
            foreach (var column in Columns(entry.Metadata.ClrType))
            {
                var property = entry.Metadata.FindProperty(column.Name);
                if (property == null) continue;
                result[column.Name] = AsStored(values[property]);
            }
            return result;
        }

        /// <summary>A column snapshot of a plain (detached or tracked) entity object.</summary>
        public static Dictionary<string, object?> Snapshot(object entity)
        {
            var result = new Dictionary<string, object?>();
            foreach (var column in Columns(entity.GetType()))
                result[column.Name] = AsStored(column.GetValue(entity));
            return result;
        }

        /// <summary>
        /// A timestamp as the database keeps it: datetime(6) holds microseconds, while a value set
        /// in memory (DateTime.UtcNow) carries 100 ns ticks. Without this a row written a moment
        /// ago would not match itself read back.
        /// </summary>
        private static object? AsStored(object? value) => value is DateTime d
            ? new DateTime(d.Ticks - d.Ticks % 10, d.Kind)
            : value;

        public static string Serialize(Dictionary<string, object?> snapshot) =>
            JsonConvert.SerializeObject(snapshot, Json);

        /// <summary>The fields whose values differ between two snapshots, noise excluded.</summary>
        public static List<string> ChangedFields(Dictionary<string, object?> before, Dictionary<string, object?> after)
        {
            var changed = new List<string>();
            foreach (var key in before.Keys.Union(after.Keys))
            {
                if (NoiseFields.Contains(key)) continue;
                before.TryGetValue(key, out var a);
                after.TryGetValue(key, out var b);
                if (a is null or "" && b is null or "") continue;
                if (!Equals(a, b)) changed.Add(key);
            }
            return changed;
        }

        public static bool IsRevertableScalar(Type t)
        {
            if (t.IsPrimitive || t.IsEnum) return true;
            if (t == typeof(string) || t == typeof(decimal) || t == typeof(decimal?)) return true;
            if (t == typeof(DateTime) || t == typeof(DateTime?)) return true;
            if (t == typeof(TimeSpan) || t == typeof(TimeSpan?)) return true;
            if (t == typeof(Guid) || t == typeof(Guid?)) return true;
            var underlying = Nullable.GetUnderlyingType(t);
            return underlying != null && (underlying.IsPrimitive || underlying.IsEnum);
        }
    }
}
