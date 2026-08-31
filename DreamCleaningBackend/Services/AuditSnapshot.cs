using System.Reflection;
using DreamCleaningBackend.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DreamCleaningBackend.Services
{
    /// <summary>
    /// Builds the detached "before" image that <see cref="AuditService.LogUpdateAsync{T}"/>
    /// measures a change against.
    ///
    /// SINGLE implementation, and it exists because hand-rolling one is a data-loss bug (found
    /// 2026-08-31). Several call sites used to build a PARTIAL snapshot — <c>new Order { Id,
    /// Status, IsHidden }</c> — and hand it to LogUpdateAsync alongside the REAL, fully populated
    /// entity. GetChangedFields reflects over every scalar, so every field the snapshot did not
    /// copy sat at its CLR default and was recorded as a change: hiding an order wrote an audit
    /// row claiming <c>SubTotal 0 → 289.50</c>, <c>ServiceDate 0001-01-01 → 2026-08-14</c>,
    /// <c>CleanerTotalSalary 0 → 175.00</c> and about forty more.
    ///
    /// That is not merely noisy. <see cref="AuditService.UndoAsync"/> replays OldValues for every
    /// field named in ChangedFields, so one Undo click on such a row would write those zeros onto
    /// the live order. <see cref="AuditService.HasFabricatedBeforeImage"/> refuses to replay the
    /// rows already in the database; this type is what stops new ones being written.
    ///
    /// Copies SCALARS ONLY, by an explicit type whitelist that is a deliberate SUPERSET of the one
    /// GetChangedFields compares. A field the diff compares but the snapshot skips is exactly the
    /// bug above; a field the snapshot copies but the diff ignores is harmless. Navigation
    /// properties are excluded by the whitelist rather than by an <c>IsVirtual</c> test, so the
    /// result holds no reference to a tracked entity regardless of how the model declares them.
    /// Order-line changes are audited separately by AuditService.LogOrderServiceChanges.
    ///
    /// This type also owns <see cref="HasFabricatedBeforeImage"/>, the read side of the same
    /// problem: the rows written before it existed are still in the database, and Undo/Redo must
    /// refuse to replay them.
    /// </summary>
    public static class AuditSnapshot
    {
        /// <summary>
        /// A detached copy of <paramref name="entity"/>'s scalar state. Take it BEFORE mutating,
        /// and pass the live entity as the "after" side.
        /// </summary>
        public static T Of<T>(T entity) where T : class, new()
        {
            var copy = new T();
            if (entity == null) return copy;

            foreach (var p in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!p.CanRead || !p.CanWrite) continue;
                if (!IsSnapshotScalar(p.PropertyType)) continue;

                try { p.SetValue(copy, p.GetValue(entity)); }
                catch { /* a property that throws on read is not worth failing an audit over */ }
            }

            return copy;
        }

        /// <summary>
        /// Per entity type, the fields that can NEVER legitimately hold their CLR default on a
        /// persisted row. When one of these is listed as changed and its "before" value IS the
        /// default, the before-image was never a real record — it came from a call site that
        /// handed LogUpdateAsync a PARTIAL snapshot alongside the fully populated live entity, so
        /// every uncopied field was recorded as a change from zero.
        ///
        /// An entity type absent from this map is NOT guarded, exactly as before. Subscription and
        /// SpecialOffer are deliberately absent: their snapshots were already complete (9/9 and
        /// 16/16), so they never produced a fabricated row and guarding them could only cost
        /// false refusals.
        ///
        /// THE GUARD IS FAIL-CLOSED (owner's call, 2026-08-31). Refusing a valid undo costs an
        /// admin one manual edit; allowing a corrupt one silently zeroes money columns, blanks a
        /// password hash, or writes nulls over a service's pricing configuration. Do NOT relax a
        /// set to make an edge case undoable — make the correction on the record instead. Two
        /// known and accepted false positives:
        ///
        ///  - Order.SubTotal / Order.Total: a fully-discounted or $0 custom order legitimately
        ///    carries 0, so an undo on a row where one of those genuinely moved off zero is
        ///    refused.
        ///  - User.PasswordHash: a row recording a user setting a password for the FIRST time
        ///    reads as null then hash, the same shape as a snapshot that omitted the column, and
        ///    is refused. No endpoint currently writes such a row (AuthService's email-change
        ///    handler is the only User LogUpdateAsync), but one added later would inherit this.
        ///
        /// Why each set is what it is:
        ///
        ///  - Order: ServiceDate alone catches both corrupt writers (hide/unhide, order
        ///    transfer); the rest are belt-and-braces for any sparser snapshot.
        ///  - User: CreatedAt catches the six AdminUsersController shapes. PasswordHash is what
        ///    catches AuthService's email-change handler, which DID copy CreatedAt and would
        ///    otherwise slip through; it is null on both sides for an OAuth account, so the
        ///    both-sides-default exemption below clears those correctly.
        ///  - The five catalogue types: CreatedAt only, and that is deliberate rather than
        ///    minimal by accident. It is assigned once at insert and never written by any
        ///    endpoint, it is non-nullable, and it was omitted by every one of the eight partial
        ///    snapshots, so it catches 100% of the rows they wrote. Extra sentinels
        ///    (GiftCard.OriginalAmount, ServiceType.MinimumPrice, Service.Name) would add no
        ///    coverage and only false-positive surface.
        ///
        /// NOT sentinels, and the reason matters: Service.ZeroQuantityCost and
        /// ZeroQuantityDuration. Their NULL is legitimate and mandatory on the levels row, so a
        /// default "before" value cannot distinguish an omitted field from a real null; they
        /// would only fire on a subset of the rows CreatedAt already catches; and a legitimate
        /// edit setting one from null to a value would be refused. CreatedAt already covers the
        /// hazard those columns represent - replaying a fabricated null over a populated
        /// ZeroQuantityCost breaks the bedrooms-keyed studio pricing rule.
        /// </summary>
        public static readonly IReadOnlyDictionary<string, string[]> FabricationSentinelFields =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["Order"] = new[] { "ServiceDate", "OrderDate", "ServiceTypeId", "UserId", "SubTotal", "Total" },
                ["User"] = new[] { "CreatedAt", "PasswordHash", "Email", "Role" },
                ["GiftCard"] = new[] { "CreatedAt" },
                ["Service"] = new[] { "CreatedAt" },
                ["ServiceType"] = new[] { "CreatedAt" },
                ["ExtraService"] = new[] { "CreatedAt" },
                ["PromoCode"] = new[] { "CreatedAt" }
            };

        /// <summary>What an admin is told when Undo/Redo refuses a row. It names the next step,
        /// because "this is corrupt" on its own leaves them with nothing to do.</summary>
        public const string FabricatedBeforeImageMessage =
            "This audit row's before-image is incomplete, so replaying it would overwrite live " +
            "data with empty values. Make the correction directly on the record instead.";

        /// <summary>
        /// True when this row's OldValues is a fabricated before-image, so replaying it would
        /// write CLR defaults over live data. <see cref="AuditService.UndoAsync"/> and
        /// <see cref="AuditService.RedoAsync"/> refuse such rows.
        ///
        /// Applies to Update rows of the entity types in <see cref="FabricationSentinelFields"/>,
        /// and tests ONLY against fields the row actually claims changed — which is what separates
        /// the corrupt writers from every legitimate one. The hide/unhide and order-transfer rows
        /// list ServiceDate (before 0001-01-01) among their changes; a genuine full-update row
        /// lists it only when it really moved, and then carries a real date; and the
        /// minimal-snapshot rows (custom service name, booked-by-admin, status, payment method,
        /// cancel) name no sentinel field at all, so undo still works for them.
        /// </summary>
        public static bool HasFabricatedBeforeImage(AuditLog log)
        {
            if (log == null) return false;
            if (!string.Equals(log.Action, "Update", StringComparison.OrdinalIgnoreCase)) return false;
            if (string.IsNullOrWhiteSpace(log.EntityType)) return false;
            if (!FabricationSentinelFields.TryGetValue(log.EntityType, out var sentinels)) return false;
            if (string.IsNullOrWhiteSpace(log.OldValues) || string.IsNullOrWhiteSpace(log.ChangedFields)) return false;

            List<string>? changed;
            JObject? oldValues;
            JObject? newValues;
            try
            {
                changed = JsonConvert.DeserializeObject<List<string>>(log.ChangedFields);
                oldValues = JsonConvert.DeserializeObject<JObject>(log.OldValues);
                newValues = string.IsNullOrWhiteSpace(log.NewValues)
                    ? null
                    : JsonConvert.DeserializeObject<JObject>(log.NewValues);
            }
            catch
            {
                // An unreadable row is not evidence of fabrication; the undo will fail on its own.
                return false;
            }

            if (changed == null || oldValues == null) return false;

            foreach (var field in sentinels)
            {
                if (!changed.Contains(field, StringComparer.OrdinalIgnoreCase)) continue;

                var oldToken = oldValues[field];
                if (oldToken == null || !IsClrDefaultToken(oldToken)) continue;

                // Default on BOTH sides is a real (if odd) no-op, not a partial snapshot.
                var newToken = newValues?[field];
                if (newToken != null && IsClrDefaultToken(newToken)) continue;

                return true;
            }

            return false;
        }

        /// <summary>Is this serialized value the CLR default for its column — 0, or 0001-01-01?</summary>
        private static bool IsClrDefaultToken(JToken token)
        {
            switch (token.Type)
            {
                case JTokenType.Null:
                    return true;
                case JTokenType.Integer:
                case JTokenType.Float:
                    return token.Value<decimal>() == 0m;
                case JTokenType.Date:
                    return token.Value<DateTime>() == default;
                case JTokenType.String:
                    // DateTimeZoneHandling.Utc serializes default(DateTime) as "0001-01-01T00:00:00Z";
                    // an empty string is a legitimate cleared value, not a default marker.
                    var raw = token.Value<string>();
                    return !string.IsNullOrEmpty(raw) && raw.StartsWith("0001-01-01", StringComparison.Ordinal);
                default:
                    return false;
            }
        }

        /// <summary>
        /// The scalar whitelist. Mirrors AuditService.GetChangedFields' predicate and widens it
        /// with nullable enums — see the class comment for why the superset direction is the safe
        /// one. Keep it a superset if GetChangedFields ever grows.
        /// </summary>
        private static bool IsSnapshotScalar(Type t)
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
