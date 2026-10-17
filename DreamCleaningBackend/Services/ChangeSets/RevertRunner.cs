using System.Text.Json;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DreamCleaningBackend.Services.ChangeSets
{
    /// <summary>One record in a revert preview: what will happen to it, and why not if it can't.</summary>
    public sealed class RevertItem
    {
        public string EntityType { get; set; } = string.Empty;
        public long EntityId { get; set; }
        public string? Name { get; set; }

        /// <summary>restore (put a deleted record back), delete (undo a create), update (put fields back), deactivate (the alternative to delete).</summary>
        public string Action { get; set; } = string.Empty;

        /// <summary>ok, conflict (changed after the entry — blocked unless overridden), blocked.</summary>
        public string Status { get; set; } = "ok";
        public string? Reason { get; set; }
        public List<RevertConflict>? Conflicts { get; set; }

        /// <summary>"deactivate" when the record can't be deleted but can be switched off instead.</summary>
        public string? Alternative { get; set; }
        public List<long> EntryIds { get; set; } = new();
    }

    public sealed class RevertConflict
    {
        public string Field { get; set; } = string.Empty;

        /// <summary>The value the reverted entry left behind.</summary>
        public object? Expected { get; set; }

        /// <summary>What the record holds now.</summary>
        public object? Current { get; set; }

        /// <summary>What reverting would put back.</summary>
        public object? Restore { get; set; }
    }

    /// <summary>
    /// Reverts one history entry, or every entry of a batch, to the state before it (2026-10).
    /// Runs as an ordinary change set, so it is previewed, confirmed with a token, and itself
    /// logged as new "Revert" rows that point back at what they reverted — history is never
    /// edited.
    ///
    /// Per record, the batch's entries collapse to one net step: restore the EARLIEST entry's
    /// before-image, expecting the LATEST entry's after-image to be what is there now. When the
    /// record no longer matches that (somebody changed it since), the preview shows each field —
    /// what the entry left, what is there now, what reverting would write — and nothing applies
    /// unless <c>override_conflicts</c> is passed; an override goes through the same preview and
    /// confirm and is noted on the rows it writes.
    ///
    /// Undoing a CREATE deletes the record — refused while orders use it, with deactivating it
    /// offered instead (<c>use_deactivate_alternative</c>). Undoing a DELETE re-inserts the record
    /// with its original id, so a service type deleted with everything under it comes back whole
    /// from one batch revert.
    ///
    /// Through the Claude connector, expense history follows the connector's expense rules: no
    /// Salaries, no Google Ads, and nothing a background job (source "system") wrote. The admin
    /// panel keeps full SuperAdmin access.
    /// </summary>
    public sealed class RevertRunner : IChangeSetRunner
    {
        public const string OverrideNotePrefix = "[conflict override]";
        public const int MaxEntries = 500;

        public string Kind => ChangeSetKinds.Revert;

        private sealed class RecordPlan
        {
            public required string EntityType { get; init; }
            public required Type Clr { get; init; }
            public required long EntityId { get; init; }
            public required List<AuditLog> Entries { get; init; }
            public JObject? Restore { get; set; }
            public JObject? Expected { get; set; }
            public HashSet<string> Fields { get; } = new(StringComparer.Ordinal);
            public object? Current { get; set; }
            public JObject? CurrentValues { get; set; }
            public required RevertItem Item { get; init; }
        }

        public async Task<ChangeRunResult> RunAsync(ChangeRunContext context, CancellationToken ct)
        {
            var db = context.Db;
            var request = context.Request;
            var entryId = Long(request, "entry_id");
            var batchId = Str(request, "batch_id")?.Trim();
            var overrideConflicts = Bool(request, "override_conflicts") ?? false;
            var useDeactivate = Bool(request, "use_deactivate_alternative") ?? false;
            if ((entryId == null) == string.IsNullOrEmpty(batchId))
                throw new ChangeSetException("Give exactly one of entry_id (one history entry) or batch_id (every entry of one change).");

            var entries = entryId != null
                ? await db.AuditLogs.AsNoTracking().Where(a => a.Id == entryId).ToListAsync(ct)
                : await db.AuditLogs.AsNoTracking().Where(a => a.BatchId == batchId).OrderBy(a => a.Id).Take(MaxEntries + 1).ToListAsync(ct);
            if (entries.Count == 0)
                throw new ChangeSetException(entryId != null ? $"There is no history entry #{entryId}." : $"There is no history batch {batchId}.");
            if (entries.Count > MaxEntries)
                throw new ChangeSetException($"That batch has more than {MaxEntries} entries; revert it in parts.");

            var result = new ChangeRunResult();
            var items = new List<RevertItem>();

            // Rows of other kinds in a batch (events, uncaptured types) are reported, not reverted.
            var skipped = entries.Where(e => !AuditCaptureScope.IsCaptured(e.EntityType)).ToList();
            foreach (var s in skipped)
                result.Warnings.Add($"#{s.Id} ({s.EntityType} {s.Action}) is not a record change that Revert restores; it is left as is.");
            entries = entries.Where(e => AuditCaptureScope.IsCaptured(e.EntityType)).ToList();
            if (entries.Count == 0)
                throw new ChangeSetException(entryId != null
                    ? $"History entry #{entryId} ({skipped[0].EntityType}) is not one Revert can restore. Revert covers the Services catalogue, subscription plans and expenses."
                    : "Nothing in that batch is a change Revert can restore.");

            // ── Entry-level refusals ────────────────────────────────────────────────────────
            var entryProblems = new List<string>();
            foreach (var e in entries)
            {
                if (e.UndoneAt != null)
                    entryProblems.Add($"#{e.Id} was already undone with the old Undo on {e.UndoneAt:yyyy-MM-dd} (use Redo on the Audits tab to re-apply it)");
                else if (AuditSnapshot.HasFabricatedBeforeImage(e))
                    entryProblems.Add($"#{e.Id}: {AuditSnapshot.FabricatedBeforeImageMessage}");
                else if (e.OldValues == null && e.NewValues == null)
                    entryProblems.Add($"#{e.Id} recorded no values to restore");
                else if (context.Caller.ViaConnector && e.Source == AuditSources.System)
                    entryProblems.Add($"#{e.Id} was written by a background job (source system), which Claude can't revert — use the Audits tab");
            }

            // ── Net step per record ─────────────────────────────────────────────────────────
            var plans = new List<RecordPlan>();
            foreach (var group in entries.GroupBy(e => (e.EntityType, e.EntityId)))
            {
                var ordered = group.OrderBy(e => e.Id).ToList();
                var clr = AuditCaptureScope.Resolve(group.Key.EntityType)!;
                var plan = new RecordPlan
                {
                    EntityType = clr.Name,
                    Clr = clr,
                    EntityId = group.Key.EntityId,
                    Entries = ordered,
                    Restore = Parse(ordered[0].OldValues),
                    Expected = Parse(ordered[^1].NewValues),
                    Item = new RevertItem { EntityType = clr.Name, EntityId = group.Key.EntityId, EntryIds = ordered.Select(e => e.Id).ToList() }
                };
                foreach (var e in ordered)
                {
                    if (e.OldValues != null && e.NewValues != null && !string.IsNullOrEmpty(e.ChangedFields))
                        foreach (var f in JsonConvert.DeserializeObject<List<string>>(e.ChangedFields) ?? new()) plan.Fields.Add(f);
                }
                plan.Item.Name = ChangeSetService.NameOf(plan.Expected ?? plan.Restore);
                plan.Item.Action = plan.Restore == null ? "delete" : plan.Expected == null ? "restore" : "update";
                plans.Add(plan);
            }

            // ── Current state, conflicts and per-record refusals ────────────────────────────
            foreach (var plan in plans)
            {
                var current = await db.FindAsync(plan.Clr, new object[] { (int)plan.EntityId }, ct);
                plan.Current = current;
                plan.CurrentValues = current == null ? null : JObject.Parse(AuditCaptureScope.Serialize(AuditCaptureScope.Snapshot(current)));
                DetectConflicts(plan);

                if (context.Caller.ViaConnector)
                {
                    var refusal = await ConnectorRefusalAsync(db, plan, ct);
                    if (refusal != null) Block(plan.Item, refusal);
                }
            }

            var inserting = plans.Where(p => p.Item.Status != "blocked" && p.Restore != null && (p.Current == null))
                .Select(p => (p.EntityType, p.EntityId)).ToHashSet();
            foreach (var plan in plans.Where(p => p.Item.Status != "blocked"))
            {
                if (plan.Restore == null && plan.Current != null)
                {
                    var inUse = await InUseReasonAsync(db, plan, ct);
                    if (inUse != null)
                    {
                        var canDeactivate = plan.Clr.GetProperty("IsActive") != null;
                        if (canDeactivate && useDeactivate)
                        {
                            plan.Item.Action = "deactivate";
                            plan.Item.Reason = inUse + " — deactivating it instead, as asked.";
                        }
                        else
                        {
                            Block(plan.Item, inUse + (canDeactivate ? "." : ", and it can't be deactivated instead."));
                            if (canDeactivate) plan.Item.Alternative = "deactivate";
                        }
                    }
                }
                if (plan.Restore != null && plan.Item.Status != "blocked")
                {
                    var missing = await MissingReferenceAsync(db, plan, inserting, ct);
                    if (missing != null) Block(plan.Item, missing);
                }
            }

            items.AddRange(plans.Select(p => p.Item));
            result.Report = new
            {
                items,
                override_conflicts = overrideConflicts,
                use_deactivate_alternative = useDeactivate
            };

            var blocked = items.Where(i => i.Status == "blocked").ToList();
            var conflicted = items.Where(i => i.Status == "conflict").ToList();
            var reasons = new List<string>();
            if (entryProblems.Count > 0) reasons.Add(string.Join("; ", entryProblems) + ".");
            if (blocked.Count > 0)
            {
                reasons.Add($"{blocked.Count} record(s) can't be reverted: " +
                            string.Join("; ", blocked.Select(b => $"{b.EntityType} #{b.EntityId}: {b.Reason}")));
                if (blocked.Any(b => b.Alternative == "deactivate"))
                    reasons.Add("Preview again with use_deactivate_alternative: true to deactivate those records instead of deleting them.");
            }
            if (conflicted.Count > 0 && !overrideConflicts)
                reasons.Add($"{conflicted.Count} record(s) changed after this entry, so reverting would overwrite the later change(s) — see the conflicts. " +
                            "Preview again with override_conflicts: true to overwrite them anyway.");
            if (reasons.Count > 0)
            {
                result.BlockedReason = string.Join(" ", reasons);
                return result;
            }

            // ── Write ───────────────────────────────────────────────────────────────────────
            if (conflicted.Count > 0)
            {
                var detail = string.Join(", ", conflicted.Select(c => $"{c.EntityType} #{c.EntityId}"));
                var prefix = $"{OverrideNotePrefix} overwrote later changes to {detail}.";
                db.AuditWrite.Note = Trim500(string.IsNullOrEmpty(db.AuditWrite.Note) ? prefix : prefix + " " + db.AuditWrite.Note);
                result.Warnings.Add($"Overriding conflicts: the later changes to {detail} will be overwritten.");
            }

            foreach (var plan in plans)
                db.AuditWrite.RevertOf[(plan.EntityType, plan.EntityId)] = plan.Entries[0].Id;

            foreach (var plan in plans)
            {
                switch (plan.Item.Action)
                {
                    case "delete":
                        if (plan.Current != null) db.Remove(plan.Current);
                        break;
                    case "deactivate":
                        plan.Clr.GetProperty("IsActive")!.SetValue(plan.Current, false);
                        break;
                    case "restore":
                        if (plan.Current == null)
                        {
                            var entity = Activator.CreateInstance(plan.Clr)!;
                            Assign(entity, plan.Restore!, null);
                            db.Add(entity);
                        }
                        else
                        {
                            // Only reachable with override: a record that exists again is put back as it was.
                            Assign(plan.Current, plan.Restore!, null);
                        }
                        break;
                    case "update":
                        if (plan.Current == null)
                        {
                            // Override of "deleted since": bring it back with the restored values.
                            var entity = Activator.CreateInstance(plan.Clr)!;
                            Assign(entity, plan.Restore!, null);
                            db.Add(entity);
                        }
                        else
                        {
                            Assign(plan.Current, plan.Restore!, plan.Item.Status == "conflict" ? null : plan.Fields);
                        }
                        break;
                }
            }

            await db.SaveChangesAsync(ct);
            await CheckCatalogueKeysAsync(db, plans, ct);

            result.Result = new
            {
                reverting = entryId != null ? $"history entry #{entryId}" : $"history batch {batchId}",
                records = items.Count
            };
            return result;
        }

        // ── Conflicts ──────────────────────────────────────────────────────────────────────

        private static void DetectConflicts(RecordPlan plan)
        {
            var conflicts = new List<RevertConflict>();
            if (plan.Expected == null && plan.CurrentValues != null)
            {
                plan.Item.Reason = "it was deleted by this change but exists again";
            }
            else if (plan.Expected != null && plan.CurrentValues == null)
            {
                plan.Item.Reason = "it has been deleted since";
            }
            else if (plan.Expected != null && plan.CurrentValues != null)
            {
                // An update compares the fields it changed; a create compares the whole record.
                var fields = plan.Restore == null || plan.Fields.Count == 0
                    ? plan.Expected.Properties().Select(p => p.Name).Where(n => !AuditCaptureScope.NoiseFields.Contains(n) && n != "Id")
                    : plan.Fields.AsEnumerable();
                foreach (var f in fields)
                {
                    var expected = plan.Expected[f];
                    var current = plan.CurrentValues[f];
                    if (!SameValue(expected, current))
                        conflicts.Add(new RevertConflict
                        {
                            Field = f,
                            Expected = ChangeSetService.Plain(expected),
                            Current = ChangeSetService.Plain(current),
                            Restore = ChangeSetService.Plain(plan.Restore?[f])
                        });
                }
                if (conflicts.Count == 0) return; // still exactly as the entry left it
                plan.Item.Reason = "changed after this entry";
            }
            else
            {
                return; // expected null, current null: a delete being restored — nothing there, as expected
            }

            plan.Item.Status = "conflict";
            plan.Item.Conflicts = conflicts.Count > 0 ? conflicts : null;
        }

        private static bool SameValue(JToken? a, JToken? b)
        {
            var na = a == null || a.Type == JTokenType.Null || (a.Type == JTokenType.String && a.Value<string>() == "");
            var nb = b == null || b.Type == JTokenType.Null || (b.Type == JTokenType.String && b.Value<string>() == "");
            if (na || nb) return na && nb;
            if (a!.Type is JTokenType.Integer or JTokenType.Float && b!.Type is JTokenType.Integer or JTokenType.Float)
                return a.Value<decimal>() == b.Value<decimal>();
            return JToken.DeepEquals(a, b);
        }

        private static void Block(RevertItem item, string reason)
        {
            item.Status = "blocked";
            item.Reason = reason;
        }

        // ── Refusals ───────────────────────────────────────────────────────────────────────

        /// <summary>The connector's expense rules, applied to the history it may revert.</summary>
        private static async Task<string?> ConnectorRefusalAsync(ApplicationDbContext db, RecordPlan plan, CancellationToken ct)
        {
            if (plan.EntityType == nameof(Expense))
            {
                var categories = new[] { plan.Restore, plan.Expected, plan.CurrentValues }
                    .Select(v => v?["CategoryId"]?.Type == JTokenType.Integer ? v["CategoryId"]!.Value<int>() : (int?)null)
                    .Where(id => id != null).Select(id => id!.Value).Distinct().ToList();
                if (categories.Any(SalaryExpenseRules.IsSalaryCategory))
                    return "Salaries can't be changed through Claude — use the Expenses page or the Audits tab";
                var names = await db.ExpenseCategories.AsNoTracking().Where(c => categories.Contains(c.Id)).Select(c => c.Name).ToListAsync(ct);
                if (names.Any(IsGoogleAds) || IsGoogleAds(plan.Restore?["Name"]?.ToString()) && plan.Restore?["SourceKey"]?.Type == JTokenType.String)
                    return "Google Ads spend is synced automatically and can't be changed through Claude";
            }
            if (plan.EntityType == nameof(ExpenseCategory))
            {
                var name = (plan.Restore ?? plan.Expected ?? plan.CurrentValues)?["Name"]?.ToString();
                var id = (plan.Restore ?? plan.Expected ?? plan.CurrentValues)?["Id"]?.Value<int?>();
                if (IsGoogleAds(name) || (id != null && SalaryExpenseRules.IsSalaryCategory(id.Value)))
                    return "the Salaries and Google Ads categories can't be changed through Claude";
            }
            return null;
        }

        private static bool IsGoogleAds(string? name) =>
            string.Equals(name?.Trim(), GoogleAdsCostService.CategoryName, StringComparison.OrdinalIgnoreCase);

        /// <summary>Why a record can't be deleted (it is in use), or null.</summary>
        private static async Task<string?> InUseReasonAsync(ApplicationDbContext db, RecordPlan plan, CancellationToken ct)
        {
            var id = (int)plan.EntityId;
            switch (plan.EntityType)
            {
                case nameof(ServiceType):
                    var typeOrders = await db.Orders.CountAsync(o => o.ServiceTypeId == id, ct);
                    return typeOrders > 0 ? $"{typeOrders} order(s) use this service type" : null;
                case nameof(Service):
                    var lines = await db.OrderServices.Where(os => os.ServiceId == id).Select(os => os.OrderId).Distinct().CountAsync(ct);
                    if (lines > 0) return $"{lines} order(s) use this service";
                    var dependents = await db.ServiceThresholds.CountAsync(t => t.SourceServiceId == id && t.ServiceId != id, ct);
                    return dependents > 0 ? "another service's included amount is counted from it" : null;
                case nameof(ExtraService):
                    var extraOrders = await db.OrderExtraServices.Where(e => e.ExtraServiceId == id).Select(e => e.OrderId).Distinct().CountAsync(ct);
                    return extraOrders > 0 ? $"{extraOrders} order(s) use this extra service" : null;
                case nameof(Subscription):
                    var planOrders = await db.Orders.CountAsync(o => o.SubscriptionId == id, ct);
                    var planUsers = await db.Users.CountAsync(u => u.SubscriptionId == id, ct);
                    return planOrders + planUsers > 0 ? $"{planOrders} order(s) and {planUsers} customer(s) use this plan" : null;
                case nameof(PollQuestion):
                    var answers = await db.PollAnswers.CountAsync(a => a.PollQuestionId == id, ct);
                    return answers > 0 ? $"{answers} poll answer(s) were given to this question" : null;
                case nameof(ExpenseCategory):
                    var expenses = await db.Expenses.CountAsync(e => e.CategoryId == id, ct);
                    return expenses > 0 ? $"{expenses} expense(s) are in this category" : null;
                default:
                    return null;
            }
        }

        /// <summary>A restored record must point at rows that exist (or are being restored with it).</summary>
        private static async Task<string?> MissingReferenceAsync(ApplicationDbContext db, RecordPlan plan,
            HashSet<(string Type, long Id)> inserting, CancellationToken ct)
        {
            var entityType = db.Model.FindEntityType(plan.Clr)!;
            foreach (var fk in entityType.GetForeignKeys())
            {
                if (fk.Properties.Count != 1) continue;
                var name = fk.Properties[0].Name;
                var token = plan.Restore![name];
                if (token == null || token.Type != JTokenType.Integer) continue;
                var value = token.Value<long>();
                var principal = fk.PrincipalEntityType.ClrType;
                if (principal == plan.Clr && value == plan.EntityId) continue; // self-reference (Levels)
                if (inserting.Contains((principal.Name, value))) continue;
                var exists = await db.FindAsync(principal, new object[] { Convert.ChangeType(value, fk.PrincipalKey.Properties[0].ClrType) }, ct);
                if (exists == null)
                    return $"it points at {principal.Name} #{value} ({name}), which no longer exists — restore that first";
            }
            return null;
        }

        /// <summary>The same uniqueness rules the Services tab enforces on save.</summary>
        private static async Task CheckCatalogueKeysAsync(ApplicationDbContext db, List<RecordPlan> plans, CancellationToken ct)
        {
            foreach (var plan in plans.Where(p => p.Item.Action is "restore" or "update"))
            {
                var id = (int)plan.EntityId;
                if (plan.EntityType == nameof(Service))
                {
                    var s = await db.Services.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
                    if (s != null && await db.Services.AnyAsync(x => x.Id != id && x.ServiceTypeId == s.ServiceTypeId && x.ServiceKey == s.ServiceKey, ct))
                        throw new ChangeSetException($"Service #{id} would share the key \"{s.ServiceKey}\" with another service of the same type. Change or remove that one first.");
                }
                else if (plan.EntityType == nameof(ExtraService))
                {
                    var e = await db.ExtraServices.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
                    if (e?.ExtraServiceKey != null)
                    {
                        var holders = await db.ExtraServices.AsNoTracking().Where(x => x.Id != id && x.ExtraServiceKey == e.ExtraServiceKey).ToListAsync(ct);
                        if (holders.Any(h => ExtraServiceKeyPolicy.Conflicts(e.IsAvailableForAll, e.ServiceTypeId, h)))
                            throw new ChangeSetException($"Extra service #{id} would share the key \"{e.ExtraServiceKey}\" with another extra. Change or remove that one first.");
                    }
                }
            }
        }

        // ── Values ─────────────────────────────────────────────────────────────────────────

        private static readonly Newtonsoft.Json.JsonSerializer Reader = Newtonsoft.Json.JsonSerializer.Create(AuditCaptureScope.Json);

        /// <summary>Writes snapshot values onto an entity: <paramref name="only"/> those fields, or every column.</summary>
        private static void Assign(object entity, JObject values, IReadOnlySet<string>? only)
        {
            foreach (var column in AuditCaptureScope.Columns(entity.GetType()))
            {
                if (only != null && !only.Contains(column.Name)) continue;
                if (!values.TryGetValue(column.Name, out var token)) continue;
                var value = token.Type == JTokenType.Null ? null : token.ToObject(column.PropertyType, Reader);
                if (value == null && column.PropertyType.IsValueType && Nullable.GetUnderlyingType(column.PropertyType) == null) continue;
                column.SetValue(entity, value);
            }
        }

        private static JObject? Parse(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            return JObject.Parse(AuditDataPolicy.SanitizeJson(json)!);
        }

        private static string Trim500(string s) => s.Length > 500 ? s[..500] : s;

        private static long? Long(JsonElement a, string n) =>
            a.ValueKind == JsonValueKind.Object && a.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : null;

        private static string? Str(JsonElement a, string n) =>
            a.ValueKind == JsonValueKind.Object && a.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static bool? Bool(JsonElement a, string n) =>
            a.ValueKind == JsonValueKind.Object && a.TryGetProperty(n, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
    }
}
