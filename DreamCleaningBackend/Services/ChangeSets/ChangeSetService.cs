using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services.Mcp;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Newtonsoft.Json.Linq;

namespace DreamCleaningBackend.Services.ChangeSets
{
    public sealed class ChangeSetPreviewDto
    {
        public string Kind { get; set; } = string.Empty;
        public bool CanApply { get; set; }

        /// <summary>Pass to apply_change (or the panel's confirm) to make exactly this change. Null when nothing can be applied.</summary>
        public string? Token { get; set; }
        public DateTime? ExpiresAt { get; set; }
        public string BatchId { get; set; } = string.Empty;
        public string? BlockedReason { get; set; }
        public string Summary { get; set; } = string.Empty;
        public List<ChangePreviewItem> Changes { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
        public object? Result { get; set; }
        public object? Report { get; set; }
        public string? NextStep { get; set; }
    }

    public sealed class ChangePreviewItem
    {
        public string EntityType { get; set; } = string.Empty;

        /// <summary>Null for a record the change would create (its id is assigned on apply).</summary>
        public long? EntityId { get; set; }
        public string Action { get; set; } = string.Empty;
        public string? Name { get; set; }

        /// <summary>Update: each changed field, old → new.</summary>
        public List<FieldChange>? Fields { get; set; }

        /// <summary>Create / delete: the record's values.</summary>
        public Dictionary<string, object?>? Values { get; set; }
    }

    public sealed class FieldChange
    {
        public string Field { get; set; } = string.Empty;
        public object? Old { get; set; }
        public object? New { get; set; }
    }

    public sealed class ChangeSetApplyResultDto
    {
        public bool Applied { get; set; }
        public string BatchId { get; set; } = string.Empty;
        public int ChangesWritten { get; set; }
        public List<long> HistoryIds { get; set; } = new();
        public object? Result { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    /// <summary>
    /// Preview → confirm for every write the Claude connector makes and for every Revert (2026-10).
    ///
    /// <b>Preview</b> runs the change for real inside a transaction and rolls it back. Whatever the
    /// history capture wrote during that dry run IS the preview — every record the change touches
    /// with its old and new values, including knock-on effects such as other services shifting
    /// position when one is moved. Nothing persists except a <see cref="PendingChangeSet"/> holding
    /// the request, a fingerprint of each record's state, and the expected changes.
    ///
    /// <b>Apply</b> takes the token, which must be unexpired (<see cref="TokenLifetime"/>), unused,
    /// and presented by the same person through the same door (connector grant or panel). It is
    /// claimed FIRST, so it is single-use whatever happens next. Then, in one transaction: lock the
    /// affected rows, compare each against its fingerprint, run the same request again, and check it
    /// produced exactly the previewed changes. Any difference rolls everything back and the person
    /// must preview again — the token is gone.
    /// </summary>
    public sealed class ChangeSetService
    {
        public static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(15);
        public const int MaxNoteLength = 500;
        public const string TokenPrefix = "chg_";

        private static readonly HashSet<string> IgnoredInComparison = new(StringComparer.OrdinalIgnoreCase) { "CreatedAt", "UpdatedAt" };

        private readonly ApplicationDbContext _db;
        private readonly IServiceScopeFactory _scopes;
        private readonly ILogger<ChangeSetService> _logger;

        public ChangeSetService(ApplicationDbContext db, IServiceScopeFactory scopes, ILogger<ChangeSetService> logger)
        {
            _db = db;
            _scopes = scopes;
            _logger = logger;
        }

        // ── Preview ─────────────────────────────────────────────────────────────────────────

        public async Task<ChangeSetPreviewDto> PreviewAsync(ChangeSetCaller caller, string kind, JsonElement request, CancellationToken ct = default)
        {
            var batchId = Guid.NewGuid().ToString();
            var run = await RunAsync(caller, kind, request, batchId, note: null, verify: null, preview: true, ct);

            var preview = new ChangeSetPreviewDto
            {
                Kind = kind,
                BatchId = batchId,
                Changes = run.Rows.Select(ToPreviewItem).ToList(),
                Warnings = run.Result.Warnings,
                Result = run.Result.Result,
                Report = run.Result.Report,
                Summary = Summarize(run.Rows)
            };

            if (run.Result.BlockedReason != null)
            {
                preview.BlockedReason = run.Result.BlockedReason;
                preview.NextStep = "Nothing can be applied as previewed. Resolve the problem above, then preview again.";
                return preview;
            }

            if (run.Rows.Count == 0)
            {
                preview.Summary = "Nothing would change: every value is already as requested.";
                return preview;
            }

            var token = TokenPrefix + McpOAuthPolicy.Base64Url(RandomNumberGenerator.GetBytes(32));
            var now = DateTime.UtcNow;
            var stored = new StoredPreview
            {
                Fingerprints = Fingerprints(run.Rows),
                Expected = Normalize(run.Rows, _db.Model)
            };
            _db.PendingChangeSets.Add(new PendingChangeSet
            {
                TokenHash = Hash(token),
                UserId = caller.UserId,
                McpGrantId = caller.McpGrantId,
                Kind = kind,
                RequestJson = request.ValueKind == JsonValueKind.Undefined ? "{}" : request.GetRawText(),
                PreviewJson = JsonSerializer.Serialize(stored),
                BatchId = batchId,
                CreatedAt = now,
                ExpiresAt = now + TokenLifetime
            });
            await _db.SaveChangesAsync(ct);

            preview.CanApply = true;
            preview.Token = token;
            preview.ExpiresAt = now + TokenLifetime;
            preview.NextStep = $"Nothing has been changed yet. Show this to the user; apply only after they confirm. The token is single-use and expires in {TokenLifetime.TotalMinutes:0} minutes.";
            return preview;
        }

        // ── Apply ───────────────────────────────────────────────────────────────────────────

        public async Task<ChangeSetApplyResultDto> ApplyAsync(ChangeSetCaller caller, string? token, string? note, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(token) || !token.StartsWith(TokenPrefix, StringComparison.Ordinal))
                throw new ChangeSetException("That is not a change token. Run the preview again and use the token it returns.");
            note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
            if (note?.Length > MaxNoteLength) note = note[..MaxNoteLength];

            var hash = Hash(token.Trim());
            var row = await _db.PendingChangeSets.AsNoTracking().FirstOrDefaultAsync(p => p.TokenHash == hash, ct);
            // Someone else's token reads exactly like an unknown one: nothing to learn from it.
            if (row == null || row.UserId != caller.UserId || row.McpGrantId != caller.McpGrantId)
                throw new ChangeSetException("This change token is not valid here. Run the preview again.");
            if (row.ConsumedAt != null)
                throw new ChangeSetException("This change token was already used. Each preview can be applied once — run the preview again.");

            var now = DateTime.UtcNow;
            if (row.ExpiresAt < now)
            {
                await ClaimAsync(row.Id, now, "rejected: expired", ct);
                throw new ChangeSetException($"This change token expired (tokens last {TokenLifetime.TotalMinutes:0} minutes). Run the preview again.");
            }

            if (!await ClaimAsync(row.Id, now, null, ct))
                throw new ChangeSetException("This change token was already used. Each preview can be applied once — run the preview again.");

            var stored = JsonSerializer.Deserialize<StoredPreview>(row.PreviewJson) ?? new StoredPreview();
            using var request = JsonDocument.Parse(row.RequestJson);

            RunOutcome run;
            try
            {
                run = await RunAsync(caller, row.Kind, request.RootElement, row.BatchId, note, stored, preview: false, ct);
            }
            catch (ChangeSetException ex)
            {
                await SetOutcomeAsync(row.Id, "rejected: " + ex.Message, ct);
                throw new ChangeSetException($"Not applied: {ex.Message} This token is now used up — run the preview again.");
            }

            await SetOutcomeAsync(row.Id, $"applied: {run.Rows.Count} change(s)", ct);
            return new ChangeSetApplyResultDto
            {
                Applied = true,
                BatchId = row.BatchId,
                ChangesWritten = run.Rows.Count,
                HistoryIds = run.Rows.Select(r => r.Id).ToList(),
                Result = run.Result.Result,
                Message = $"Applied {run.Rows.Count} change(s) as previewed. History batch {row.BatchId}."
            };
        }

        // ── The run ─────────────────────────────────────────────────────────────────────────

        private sealed record RunOutcome(ChangeRunResult Result, List<AuditLog> Rows);

        private async Task<RunOutcome> RunAsync(ChangeSetCaller caller, string kind, JsonElement request, string batchId,
            string? note, StoredPreview? verify, bool preview, CancellationToken ct)
        {
            // A fresh scope per run: the dry run's tracked entities and rolled-back ids never leak
            // into the request's own context, and the apply starts from what the database holds.
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var runner = scope.ServiceProvider.GetServices<IChangeSetRunner>().FirstOrDefault(r => r.Kind == kind)
                ?? throw new ChangeSetException($"Unknown change kind '{kind}'.");

            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            try
            {
                if (verify != null)
                    await LockAndVerifyAsync(db, verify.Fingerprints, ct);

                db.AuditWrite.KeepWritten = true;
                db.AuditWrite.Source = caller.Source;
                db.AuditWrite.ActorUserId = caller.UserId;
                db.AuditWrite.BatchId = batchId;
                db.AuditWrite.Note = note;

                ChangeRunResult result;
                try
                {
                    result = await runner.RunAsync(new ChangeRunContext
                    {
                        Db = db, Services = scope.ServiceProvider, Caller = caller, Request = request, IsPreview = preview
                    }, ct);
                }
                catch (McpToolException ex) { throw new ChangeSetException(ex.Message); }
                catch (AdminAgent.ToolInputException ex) { throw new ChangeSetException(ex.Message); }
                catch (DbUpdateException ex)
                {
                    _logger.LogWarning(ex, "Change set {Kind} refused by the database", kind);
                    throw new ChangeSetException("The database refused this change (a duplicate, or a record something else still depends on).");
                }

                var rows = db.AuditWrite.Written.ToList();

                if (preview || result.BlockedReason != null)
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                    if (!preview) throw new ChangeSetException(result.BlockedReason!);
                    return new RunOutcome(result, rows);
                }

                var expected = verify!.Expected;
                var actual = Normalize(rows, db.Model);
                if (!expected.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(actual.OrderBy(x => x, StringComparer.Ordinal), StringComparer.Ordinal))
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                    throw new ChangeSetException("Running it now would change something different from what the preview showed (the data moved in between).");
                }

                await transaction.CommitAsync(ct);
                return new RunOutcome(result, rows);
            }
            catch (ChangeSetException) { throw; }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Change set {Kind} failed", kind);
                throw new ChangeSetException("The change failed on the server and nothing was saved.");
            }
        }

        /// <summary>
        /// Takes a row lock on every record the preview fingerprinted (so nothing can slip in
        /// between the check and the write), then compares each with its fingerprint.
        /// </summary>
        private static async Task LockAndVerifyAsync(ApplicationDbContext db, List<RecordFingerprint> fingerprints, CancellationToken ct)
        {
            foreach (var group in fingerprints.GroupBy(f => f.EntityType))
            {
                var clr = AuditCaptureScope.Resolve(group.Key) ?? throw new ChangeSetException($"Unknown record type {group.Key}.");
                var table = db.Model.FindEntityType(clr)!.GetTableName();
                var ids = string.Join(",", group.Select(f => f.EntityId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
#pragma warning disable EF1002 // table name from the model, ids are integers
                await db.Database.ExecuteSqlRawAsync($"SELECT `Id` FROM `{table}` WHERE `Id` IN ({ids}) FOR UPDATE", ct);
#pragma warning restore EF1002
            }

            var moved = new List<string>();
            foreach (var f in fingerprints)
            {
                var clr = AuditCaptureScope.Resolve(f.EntityType)!;
                var entity = await db.FindAsync(clr, new object[] { (int)f.EntityId }, ct);
                string? current = null;
                if (entity != null)
                {
                    current = FingerprintOf(AuditCaptureScope.Serialize(AuditCaptureScope.Snapshot(entity)));
                    db.Entry(entity).State = EntityState.Detached;
                }

                if (f.Hash == null ? entity != null : current != f.Hash)
                    moved.Add(entity == null ? $"{f.EntityType} #{f.EntityId} (deleted since)" :
                        f.Hash == null ? $"{f.EntityType} #{f.EntityId} (exists again)" : $"{f.EntityType} #{f.EntityId}");
            }

            if (moved.Count > 0)
                throw new ChangeSetException($"{string.Join(", ", moved)} changed after the preview.");
        }

        private async Task<bool> ClaimAsync(long id, DateTime now, string? outcome, CancellationToken ct) =>
            await _db.PendingChangeSets
                .Where(p => p.Id == id && p.ConsumedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.ConsumedAt, now).SetProperty(p => p.Outcome, outcome), ct) == 1;

        private async Task SetOutcomeAsync(long id, string outcome, CancellationToken ct)
        {
            try
            {
                var text = outcome.Length > 500 ? outcome[..500] : outcome;
                await _db.PendingChangeSets.Where(p => p.Id == id)
                    .ExecuteUpdateAsync(s => s.SetProperty(p => p.Outcome, text), CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not record the outcome of change set {Id}", id);
            }
        }

        // ── Fingerprints and the expected-change list ───────────────────────────────────────

        public sealed class StoredPreview
        {
            public List<RecordFingerprint> Fingerprints { get; set; } = new();
            public List<string> Expected { get; set; } = new();
        }

        /// <summary>Hash = null means "the record must NOT exist" (a revert re-inserting it).</summary>
        public sealed record RecordFingerprint(string EntityType, long EntityId, string? Hash);

        /// <summary>The state each existing record was in before the change touched it — its first row's before-image.</summary>
        private static List<RecordFingerprint> Fingerprints(List<AuditLog> rows) =>
            rows.Where(r => r.Action != "Create")
                .GroupBy(r => (r.EntityType, r.EntityId))
                .Select(g =>
                {
                    var first = g.OrderBy(r => r.Id).First();
                    return new RecordFingerprint(g.Key.EntityType, g.Key.EntityId,
                        first.OldValues == null ? null : FingerprintOf(first.OldValues));
                })
                .ToList();

        public static string FingerprintOf(string snapshotJson)
        {
            var obj = JObject.Parse(snapshotJson);
            var sorted = new JObject(obj.Properties()
                .Where(p => !AuditCaptureScope.NoiseFields.Contains(p.Name))
                .OrderBy(p => p.Name, StringComparer.Ordinal));
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sorted.ToString(Newtonsoft.Json.Formatting.None))));
        }

        /// <summary>
        /// One comparable line per history row. A new record's id is replaced by its position among
        /// the records of its type created (the dry run's ids were rolled back), and so is a foreign
        /// key pointing at one — only a real foreign key to THAT type, so an unrelated number that
        /// happens to be equal (a connector grant id, a quantity) is never mistaken for it.
        /// Timestamps are left out.
        /// </summary>
        public static List<string> Normalize(List<AuditLog> rows, Microsoft.EntityFrameworkCore.Metadata.IModel model)
        {
            var created = new Dictionary<(string Type, long Id), string>();
            var perType = new Dictionary<string, int>();
            foreach (var r in rows.Where(r => r.Action == "Create"))
            {
                perType[r.EntityType] = perType.GetValueOrDefault(r.EntityType) + 1;
                created[(r.EntityType, r.EntityId)] = $"new:{r.EntityType}:{perType[r.EntityType]}";
            }

            string? PrincipalOf(string entityType, string property)
            {
                var clr = AuditCaptureScope.Resolve(entityType);
                var fk = clr == null ? null : model.FindEntityType(clr)?.GetForeignKeys()
                    .FirstOrDefault(k => k.Properties.Count == 1 && k.Properties[0].Name == property);
                return fk?.PrincipalEntityType.ClrType.Name;
            }

            string Value(string entityType, string name, JToken token)
            {
                if (token.Type == JTokenType.Integer && PrincipalOf(entityType, name) is { } principal
                    && created.TryGetValue((principal, token.Value<long>()), out var placeholder))
                    return placeholder;
                return token.ToString(Newtonsoft.Json.Formatting.None);
            }

            var lines = new List<string>();
            foreach (var r in rows)
            {
                var id = r.Action == "Create" ? created[(r.EntityType, r.EntityId)] : r.EntityId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var after = r.NewValues == null ? null : JObject.Parse(r.NewValues);
                IEnumerable<string> fields = r.Action == "Create" || (r.OldValues == null && after != null)
                    ? after!.Properties().Select(p => p.Name).Where(n => n != "Id")
                    : string.IsNullOrEmpty(r.ChangedFields)
                        ? Enumerable.Empty<string>()
                        : Newtonsoft.Json.JsonConvert.DeserializeObject<List<string>>(r.ChangedFields)!;
                var parts = fields.Where(f => !IgnoredInComparison.Contains(f)).OrderBy(f => f, StringComparer.Ordinal)
                    .Select(f => $"{f}={(after?[f] is { } v ? Value(r.EntityType, f, v) : "∅")}");
                lines.Add($"{r.EntityType}|{r.Action}|{id}|{string.Join(";", parts)}");
            }
            return lines;
        }

        // ── Presentation ────────────────────────────────────────────────────────────────────

        private static ChangePreviewItem ToPreviewItem(AuditLog row)
        {
            var before = row.OldValues == null ? null : JObject.Parse(row.OldValues);
            var after = row.NewValues == null ? null : JObject.Parse(row.NewValues);
            var item = new ChangePreviewItem
            {
                EntityType = row.EntityType,
                EntityId = row.Action == "Create" ? null : row.EntityId,
                Action = row.Action,
                Name = NameOf(after ?? before)
            };

            if (before != null && after != null)
            {
                var fields = string.IsNullOrEmpty(row.ChangedFields)
                    ? new List<string>()
                    : Newtonsoft.Json.JsonConvert.DeserializeObject<List<string>>(row.ChangedFields)!;
                item.Fields = fields.Select(f => new FieldChange { Field = f, Old = Plain(before[f]), New = Plain(after[f]) }).ToList();
            }
            else
            {
                item.Values = (after ?? before)!.Properties()
                    .Where(p => p.Name is not ("CreatedAt" or "UpdatedAt") && p.Value.Type != JTokenType.Null)
                    .ToDictionary(p => p.Name, p => Plain(p.Value));
            }
            return item;
        }

        public static string? NameOf(JObject? values) =>
            values?["Name"]?.Type == JTokenType.String ? values["Name"]!.Value<string>()
            : values?["Question"]?.Type == JTokenType.String ? values["Question"]!.Value<string>()
            : null;

        public static object? Plain(JToken? token) => token switch
        {
            null => null,
            JValue v => v.Value,
            _ => token.ToString(Newtonsoft.Json.Formatting.None)
        };

        private static string Summarize(List<AuditLog> rows)
        {
            if (rows.Count == 0) return "No changes.";
            var parts = rows.GroupBy(r => r.Action)
                .Select(g => $"{g.Count()} {g.Key.ToLowerInvariant()}{(g.Count() == 1 ? "" : "s")}");
            var records = rows.Select(r => (r.EntityType, r.EntityId)).Distinct().Count();
            return $"{rows.Count} change(s) to {records} record(s): {string.Join(", ", parts)}.";
        }

        public static string Hash(string token) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
    }
}
