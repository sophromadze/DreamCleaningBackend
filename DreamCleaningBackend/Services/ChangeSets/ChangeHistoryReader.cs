using System.Globalization;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DreamCleaningBackend.Services.ChangeSets
{
    public sealed class ChangeHistoryFilter
    {
        public string? EntityType { get; set; }
        public long? EntityId { get; set; }
        public string? Action { get; set; }
        public string? Source { get; set; }
        public int? ActorUserId { get; set; }
        public string? Actor { get; set; }
        public string? BatchId { get; set; }

        /// <summary>New York calendar days, inclusive.</summary>
        public DateTime? From { get; set; }
        public DateTime? To { get; set; }
        public string? Search { get; set; }
        public int Page { get; set; } = 1;
        public int PageSize { get; set; } = 20;
    }

    /// <summary>
    /// The change history of the Services catalogue, subscription plans and expenses, for the
    /// Claude connector (2026-10). Only <see cref="AuditCaptureScope"/>'s types are readable here:
    /// the rest of the audit log is about customers and orders and stays in the admin panel.
    /// </summary>
    public sealed class ChangeHistoryReader
    {
        public const int MaxPageSize = 50;

        private readonly ApplicationDbContext _db;

        public ChangeHistoryReader(ApplicationDbContext db) => _db = db;

        public async Task<object> ListAsync(ChangeHistoryFilter filter, CancellationToken ct = default)
        {
            var page = Math.Max(1, filter.Page);
            var size = Math.Clamp(filter.PageSize, 1, MaxPageSize);
            var types = AuditCaptureScope.EntityTypeNames.ToList();
            var query = _db.AuditLogs.AsNoTracking().Where(a => types.Contains(a.EntityType));

            if (!string.IsNullOrWhiteSpace(filter.EntityType))
            {
                var type = AuditCaptureScope.Resolve(NormalizeType(filter.EntityType))?.Name
                    ?? throw new ChangeSetException($"Unknown entity type '{filter.EntityType}'. Types: {string.Join(", ", types)}.");
                query = query.Where(a => a.EntityType == type);
            }
            if (filter.EntityId != null) query = query.Where(a => a.EntityId == filter.EntityId);
            if (!string.IsNullOrWhiteSpace(filter.Action))
            {
                var action = char.ToUpperInvariant(filter.Action.Trim()[0]) + filter.Action.Trim()[1..].ToLowerInvariant();
                query = query.Where(a => a.Action == action);
            }
            if (!string.IsNullOrWhiteSpace(filter.Source))
            {
                var source = filter.Source.Trim().ToLowerInvariant();
                query = source == "unknown" ? query.Where(a => a.Source == null) : query.Where(a => a.Source == source);
            }
            if (filter.ActorUserId != null) query = query.Where(a => a.UserId == filter.ActorUserId);
            if (!string.IsNullOrWhiteSpace(filter.Actor))
            {
                var actor = filter.Actor.Trim().ToLower();
                query = query.Where(a => (a.ActorName != null && a.ActorName.ToLower().Contains(actor))
                    || (a.User != null && (a.User.FirstName + " " + a.User.LastName).ToLower().Contains(actor)));
            }
            if (!string.IsNullOrWhiteSpace(filter.BatchId)) query = query.Where(a => a.BatchId == filter.BatchId.Trim());
            if (filter.From != null) query = query.Where(a => a.CreatedAt >= NyTimeHelper.ToUtc(filter.From.Value.Date));
            if (filter.To != null) query = query.Where(a => a.CreatedAt < NyTimeHelper.ToUtc(filter.To.Value.Date.AddDays(1)));
            if (!string.IsNullOrWhiteSpace(filter.Search))
            {
                var term = filter.Search.Trim().ToLower();
                query = query.Where(a =>
                    (a.NewValues != null && a.NewValues.ToLower().Contains(term)) ||
                    (a.OldValues != null && a.OldValues.ToLower().Contains(term)) ||
                    (a.Note != null && a.Note.ToLower().Contains(term)) ||
                    (a.ActorName != null && a.ActorName.ToLower().Contains(term)));
            }

            var total = await query.CountAsync(ct);
            var rows = await query.OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.Id)
                .Skip((page - 1) * size).Take(size).Include(a => a.User).ToListAsync(ct);
            var ids = rows.Select(r => r.Id).ToList();
            var revertedBy = await RevertedByAsync(ids, ct);

            return new
            {
                total,
                page,
                page_size = size,
                total_pages = (int)Math.Ceiling(total / (double)size),
                entries = rows.Select(r => Summary(r, revertedBy.GetValueOrDefault(r.Id))).ToList()
            };
        }

        public async Task<object> DetailAsync(long id, CancellationToken ct = default)
        {
            var row = await _db.AuditLogs.AsNoTracking().Include(a => a.User).FirstOrDefaultAsync(a => a.Id == id, ct);
            if (row == null || !AuditCaptureScope.IsCaptured(row.EntityType))
                throw new ChangeSetException($"There is no Services, plan or expense history entry #{id}.");

            var before = Parse(row.OldValues);
            var after = Parse(row.NewValues);
            var keys = (before?.Properties().Select(p => p.Name) ?? Enumerable.Empty<string>())
                .Union(after?.Properties().Select(p => p.Name) ?? Enumerable.Empty<string>()).ToList();
            var changed = string.IsNullOrEmpty(row.ChangedFields)
                ? (before == null || after == null ? keys : new List<string>())
                : JsonConvert.DeserializeObject<List<string>>(row.ChangedFields) ?? new();
            var revertedBy = await RevertedByAsync(new List<long> { id }, ct);
            var batchSize = row.BatchId == null ? 1 : await _db.AuditLogs.CountAsync(a => a.BatchId == row.BatchId, ct);

            return new
            {
                entry = Summary(row, revertedBy.GetValueOrDefault(id)),
                diff = keys.Select(k => new
                {
                    field = k,
                    old = ChangeSetService.Plain(before?[k]),
                    @new = ChangeSetService.Plain(after?[k]),
                    changed = changed.Contains(k)
                }).ToList(),
                batch_entries = batchSize,
                revert_hint = row.UndoneAt != null
                    ? "Undone with the old Undo; re-apply it with Redo on the Audits tab."
                    : "preview_revert with entry_id (this entry) or batch_id (the whole change) shows what reverting would do."
            };
        }

        private async Task<Dictionary<long, List<long>>> RevertedByAsync(List<long> ids, CancellationToken ct) =>
            (await _db.AuditLogs.AsNoTracking().Where(a => a.RevertOfId != null && ids.Contains(a.RevertOfId.Value))
                .Select(a => new { a.Id, Of = a.RevertOfId!.Value }).ToListAsync(ct))
            .GroupBy(x => x.Of).ToDictionary(g => g.Key, g => g.Select(x => x.Id).ToList());

        private static object Summary(AuditLog r, List<long>? revertedBy)
        {
            var before = Parse(r.OldValues);
            var after = Parse(r.NewValues);
            var changed = string.IsNullOrEmpty(r.ChangedFields) ? null : JsonConvert.DeserializeObject<List<string>>(r.ChangedFields);
            return new
            {
                id = r.Id,
                at = NyTimeHelper.ToNy(DateTime.SpecifyKind(r.CreatedAt, DateTimeKind.Utc)).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                entity_type = r.EntityType,
                entity_id = r.EntityId,
                name = ChangeSetService.NameOf(after ?? before),
                action = r.Action,
                source = r.Source ?? "not recorded",
                actor = r.ActorName ?? (r.User == null ? null : (r.User.FirstName + " " + r.User.LastName).Trim()),
                actor_user_id = r.UserId,
                batch_id = r.BatchId,
                revert_of_id = r.RevertOfId,
                reverted_by = revertedBy is { Count: > 0 } ? revertedBy : null,
                note = r.Note,
                legacy_undone_at = r.UndoneAt?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                changes = before != null && after != null && changed != null
                    ? changed.Select(f => new { field = f, old = ChangeSetService.Plain(before[f]), @new = ChangeSetService.Plain(after[f]) }).ToList()
                    : null
            };
        }

        /// <summary>Accepts the write tool's entity names too (service_type → ServiceType, included_amount → ServiceThreshold).</summary>
        public static string NormalizeType(string raw)
        {
            var t = raw.Trim();
            return t.ToLowerInvariant() switch
            {
                "included_amount" or "threshold" => nameof(ServiceThreshold),
                "rate_tier" => nameof(ServiceRateTier),
                "expense_category" => nameof(ExpenseCategory),
                _ => t.Replace("_", string.Empty)
            };
        }

        private static JObject? Parse(string? json)
        {
            var safe = AuditDataPolicy.SanitizeJson(json);
            return string.IsNullOrWhiteSpace(safe) ? null : JObject.Parse(safe);
        }
    }
}
