using System.Security.Claims;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Newtonsoft.Json;

namespace DreamCleaningBackend.Services.ChangeSets
{
    /// <summary>
    /// Per-context settings and bookkeeping for <see cref="AuditCaptureInterceptor"/>. Lives on
    /// <see cref="ApplicationDbContext.AuditWrite"/>, so it is scoped exactly like the context.
    /// </summary>
    public sealed class AuditWriteState
    {
        /// <summary>Overrides the source the request would imply (an apply stamps "mcp" or "admin").</summary>
        public string? Source { get; set; }
        public int? ActorUserId { get; set; }
        public string? ActorName { get; set; }

        /// <summary>Every history row written while this is set carries it.</summary>
        public string? BatchId { get; set; }
        public string? Note { get; set; }

        /// <summary>(EntityType, EntityId) → the history row being reverted. A row written for one of these is a "Revert".</summary>
        public Dictionary<(string Type, long Id), long> RevertOf { get; } = new();

        /// <summary>
        /// When true, every history row this context writes is also kept in <see cref="Written"/>,
        /// in order — a change set reads its own dry run from there. Off by default so a long-lived
        /// context (a background job) does not accumulate rows in memory.
        /// </summary>
        public bool KeepWritten { get; set; }

        public List<AuditLog> Written { get; } = new();

        internal bool InsideAuditSave;
        internal List<PendingCapture>? Pending;
        internal IDbContextTransaction? OwnedTransaction;

        /// <summary>Clears the per-change settings (not <see cref="Written"/>).</summary>
        public void ResetChangeContext()
        {
            Source = null;
            ActorUserId = null;
            ActorName = null;
            BatchId = null;
            Note = null;
            RevertOf.Clear();
        }
    }

    internal sealed class PendingCapture
    {
        public required EntityEntry Entry { get; init; }
        public required string Action { get; init; }
        public required string EntityType { get; init; }
        public long EntityId { get; set; }
        public Dictionary<string, object?>? Before { get; init; }
        public Dictionary<string, object?>? After { get; set; }
        public List<string>? ChangedFields { get; init; }
    }

    /// <summary>
    /// Writes the change history for <see cref="AuditCaptureScope"/>'s entity types at the data
    /// layer (2026-10), so a change made from the admin panel, through the Claude connector or by
    /// a background job is recorded the same way and can never skip its row.
    ///
    /// How it holds together:
    /// <list type="bullet">
    /// <item><b>Same transaction as the change.</b> Before/after images are read from EF's tracked
    /// values while the save is being prepared. A new row's id only exists after the INSERT, so
    /// the history rows are written right after the save — inside a transaction this interceptor
    /// opens when the caller has none, and commits only once the history is written. If writing
    /// the history fails, the change is rolled back with it.</item>
    /// <item><b>Cascades are captured.</b> Deleting a service type takes its services, their
    /// included amounts and rate tiers, its poll questions, and detaches its extras. The database
    /// would do most of that by itself, invisibly; the interceptor loads those dependents first
    /// so EF performs the cascade and every row gets its own full snapshot. That is what lets
    /// one batch Revert put the whole service type back.</item>
    /// <item><b>Who and from where.</b> <see cref="AuditWriteState"/> overrides win; otherwise a
    /// request under <c>/api/mcp</c> is "mcp", any other signed-in request is "admin", and no
    /// request at all (a background job such as the Google Ads sync) is "system".</item>
    /// </list>
    /// </summary>
    public sealed class AuditCaptureInterceptor : SaveChangesInterceptor
    {
        public static readonly AuditCaptureInterceptor Instance = new();

        private static readonly IHttpContextAccessor Http = new HttpContextAccessor();

        private AuditCaptureInterceptor() { }

        // ── Before the save ─────────────────────────────────────────────────────────────────

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context is ApplicationDbContext context && !context.AuditWrite.InsideAuditSave)
            {
                await ExpandCascadesAsync(context, cancellationToken);
                if (Collect(context))
                    context.AuditWrite.OwnedTransaction = await BeginIfNeededAsync(context, cancellationToken);
            }
            return await base.SavingChangesAsync(eventData, result, cancellationToken);
        }

        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        {
            if (eventData.Context is ApplicationDbContext context && !context.AuditWrite.InsideAuditSave)
            {
                ExpandCascadesAsync(context, CancellationToken.None, sync: true).GetAwaiter().GetResult();
                if (Collect(context) && SupportsTransactions(context) && context.Database.CurrentTransaction == null)
                    context.AuditWrite.OwnedTransaction = context.Database.BeginTransaction();
            }
            return base.SavingChanges(eventData, result);
        }

        // ── After the save ──────────────────────────────────────────────────────────────────

        public override async ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context is ApplicationDbContext context && context.AuditWrite.Pending != null && !context.AuditWrite.InsideAuditSave)
            {
                var state = context.AuditWrite;
                try
                {
                    var rows = await BuildRowsAsync(context, cancellationToken);
                    context.AuditLogs.AddRange(rows);
                    state.InsideAuditSave = true;
                    try { await context.SaveChangesAsync(cancellationToken); }
                    finally { state.InsideAuditSave = false; }
                    if (state.KeepWritten) state.Written.AddRange(rows);
                    if (state.OwnedTransaction != null) await state.OwnedTransaction.CommitAsync(cancellationToken);
                }
                catch
                {
                    if (state.OwnedTransaction != null) await state.OwnedTransaction.RollbackAsync(CancellationToken.None);
                    throw;
                }
                finally
                {
                    await DisposeOwnedAsync(state);
                    state.Pending = null;
                }
            }
            return await base.SavedChangesAsync(eventData, result, cancellationToken);
        }

        public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
        {
            if (eventData.Context is ApplicationDbContext context && context.AuditWrite.Pending != null && !context.AuditWrite.InsideAuditSave)
            {
                var state = context.AuditWrite;
                try
                {
                    var rows = BuildRowsAsync(context, CancellationToken.None, sync: true).GetAwaiter().GetResult();
                    context.AuditLogs.AddRange(rows);
                    state.InsideAuditSave = true;
                    try { context.SaveChanges(); }
                    finally { state.InsideAuditSave = false; }
                    if (state.KeepWritten) state.Written.AddRange(rows);
                    state.OwnedTransaction?.Commit();
                }
                catch
                {
                    state.OwnedTransaction?.Rollback();
                    throw;
                }
                finally
                {
                    state.OwnedTransaction?.Dispose();
                    state.OwnedTransaction = null;
                    state.Pending = null;
                }
            }
            return base.SavedChanges(eventData, result);
        }

        public override async Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
        {
            if (eventData.Context is ApplicationDbContext context && !context.AuditWrite.InsideAuditSave)
            {
                var state = context.AuditWrite;
                if (state.OwnedTransaction != null)
                {
                    try { await state.OwnedTransaction.RollbackAsync(CancellationToken.None); } catch { /* already gone */ }
                    await DisposeOwnedAsync(state);
                }
                state.Pending = null;
            }
            await base.SaveChangesFailedAsync(eventData, cancellationToken);
        }

        public override void SaveChangesFailed(DbContextErrorEventData eventData)
        {
            if (eventData.Context is ApplicationDbContext context && !context.AuditWrite.InsideAuditSave)
            {
                var state = context.AuditWrite;
                if (state.OwnedTransaction != null)
                {
                    try { state.OwnedTransaction.Rollback(); } catch { /* already gone */ }
                    state.OwnedTransaction.Dispose();
                    state.OwnedTransaction = null;
                }
                state.Pending = null;
            }
            base.SaveChangesFailed(eventData);
        }

        // ── Pieces ──────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Loads the captured dependents of every captured row being deleted and applies the
        /// DATABASE's own cascade to them in the tracker — delete for ON DELETE CASCADE, a null
        /// foreign key for ON DELETE SET NULL (an extra service detached from its type) — until
        /// nothing new turns up. Only those two: they are what MariaDB would do invisibly anyway.
        /// Restrict and client-only behaviours are left alone, so the database refuses those
        /// deletes exactly as before.
        ///
        /// The states are set by hand rather than through ChangeTracker.CascadeChanges, which also
        /// walks the restrict-side navigations (an included amount's SourceService) and throws
        /// "association severed" for a row the ServiceId cascade is about to delete anyway.
        /// </summary>
        private static async Task ExpandCascadesAsync(ApplicationDbContext context, CancellationToken ct, bool sync = false)
        {
            // EF ran DetectChanges just before this hook; enumerating entries must not run it again
            // on every save in the application.
            var tracker = context.ChangeTracker;
            var auto = tracker.AutoDetectChangesEnabled;
            tracker.AutoDetectChangesEnabled = false;
            try { await ExpandCascadesCoreAsync(context, ct, sync); }
            finally { tracker.AutoDetectChangesEnabled = auto; }
        }

        private static async Task ExpandCascadesCoreAsync(ApplicationDbContext context, CancellationToken ct, bool sync)
        {
            var expanded = new HashSet<(object Entity, string Navigation)>();
            for (var round = 0; round < 6; round++)
            {
                var loads = context.ChangeTracker.Entries()
                    .Where(e => e.State == EntityState.Deleted && AuditCaptureScope.IsCaptured(e.Metadata.ClrType))
                    .SelectMany(e => e.Navigations.OfType<CollectionEntry>().Select(n => (Entry: e, Nav: n)))
                    .Where(x => x.Nav.Metadata is INavigation nav
                        && AuditCaptureScope.IsCaptured(nav.TargetEntityType.ClrType)
                        && nav.ForeignKey.DeleteBehavior is DeleteBehavior.Cascade or DeleteBehavior.SetNull
                        && expanded.Add((x.Entry.Entity, nav.Name)))
                    .ToList();
                if (loads.Count == 0) return;

                foreach (var (_, nav) in loads)
                {
                    if (sync) nav.Load();
                    else await nav.LoadAsync(ct);

                    var fk = ((INavigation)nav.Metadata).ForeignKey;
                    foreach (var dependent in nav.CurrentValue?.Cast<object>().ToList() ?? new List<object>())
                    {
                        var entry = context.Entry(dependent);
                        if (entry.State is EntityState.Deleted or EntityState.Detached) continue;
                        if (fk.DeleteBehavior == DeleteBehavior.Cascade)
                            entry.State = EntityState.Deleted;
                        else
                            foreach (var p in fk.Properties) entry.Property(p.Name).CurrentValue = null;
                    }
                }
            }
        }

        /// <summary>Reads before-images for updates and deletes now; new rows are finished after the INSERT.</summary>
        private static bool Collect(ApplicationDbContext context)
        {
            var tracker = context.ChangeTracker;
            var auto = tracker.AutoDetectChangesEnabled;
            tracker.AutoDetectChangesEnabled = false; // see ExpandCascadesAsync
            try { return CollectCore(context); }
            finally { tracker.AutoDetectChangesEnabled = auto; }
        }

        private static bool CollectCore(ApplicationDbContext context)
        {
            var pending = new List<PendingCapture>();
            foreach (var entry in context.ChangeTracker.Entries())
            {
                var type = entry.Metadata.ClrType;
                if (!AuditCaptureScope.IsCaptured(type)) continue;

                switch (entry.State)
                {
                    case EntityState.Added:
                        pending.Add(new PendingCapture { Entry = entry, Action = "Create", EntityType = type.Name });
                        break;
                    case EntityState.Modified:
                    {
                        var before = AuditCaptureScope.Snapshot(entry, original: true);
                        var after = AuditCaptureScope.Snapshot(entry, original: false);
                        var changed = AuditCaptureScope.ChangedFields(before, after);
                        if (changed.Count == 0) break; // a housekeeping-only touch is not a change
                        pending.Add(new PendingCapture
                        {
                            Entry = entry, Action = "Update", EntityType = type.Name, EntityId = KeyOf(entry),
                            Before = before, After = after, ChangedFields = changed
                        });
                        break;
                    }
                    case EntityState.Deleted:
                        pending.Add(new PendingCapture
                        {
                            Entry = entry, Action = "Delete", EntityType = type.Name, EntityId = KeyOf(entry),
                            Before = AuditCaptureScope.Snapshot(entry, original: true)
                        });
                        break;
                }
            }

            if (pending.Count == 0) return false;
            context.AuditWrite.Pending = pending;
            return true;
        }

        private static async Task<List<AuditLog>> BuildRowsAsync(ApplicationDbContext context, CancellationToken ct, bool sync = false)
        {
            var state = context.AuditWrite;
            var http = Http.HttpContext;
            var userId = state.ActorUserId ?? ClaimUserId(http);
            var actorName = state.ActorName;
            if (actorName == null && userId != null)
            {
                var query = context.Users.AsNoTracking().Where(u => u.Id == userId)
                    .Select(u => (u.FirstName + " " + u.LastName).Trim());
                actorName = sync ? query.FirstOrDefault() : await query.FirstOrDefaultAsync(ct);
            }

            var source = state.Source ?? ResolveSource(http);
            // One admin click = one batch: every history row a request writes shares an id, so a
            // cascade (a service type and everything under it) is reverted as a unit. A change set
            // sets its own; a background job gets one per save.
            var batchId = state.BatchId ?? RequestBatchId(http) ?? Guid.NewGuid().ToString();
            var ip = http?.Connection.RemoteIpAddress?.ToString();
            var agent = http?.Request.Headers.UserAgent.ToString();
            if (agent?.Length > 500) agent = agent[..500];
            var now = DateTime.UtcNow;

            var rows = new List<AuditLog>();
            foreach (var capture in state.Pending!)
            {
                if (capture.Action == "Create")
                {
                    capture.EntityId = KeyOf(capture.Entry);
                    capture.After = AuditCaptureScope.Snapshot(capture.Entry, original: false);
                }

                var isRevert = state.RevertOf.TryGetValue((capture.EntityType, capture.EntityId), out var revertOf);
                rows.Add(new AuditLog
                {
                    EntityType = capture.EntityType,
                    EntityId = capture.EntityId,
                    Action = isRevert ? "Revert" : capture.Action,
                    OldValues = capture.Before == null ? null : AuditCaptureScope.Serialize(capture.Before),
                    NewValues = capture.After == null ? null : AuditCaptureScope.Serialize(capture.After),
                    ChangedFields = capture.ChangedFields == null ? null : JsonConvert.SerializeObject(capture.ChangedFields),
                    UserId = userId,
                    ActorName = actorName?.Length > 200 ? actorName[..200] : actorName,
                    Source = source,
                    BatchId = batchId,
                    RevertOfId = isRevert ? revertOf : null,
                    Note = state.Note,
                    IpAddress = string.IsNullOrEmpty(ip) ? null : ip,
                    UserAgent = string.IsNullOrEmpty(agent) ? null : agent,
                    CreatedAt = now
                });
            }
            return rows;
        }

        private const string RequestBatchKey = "AuditCapture.BatchId";

        private static string? RequestBatchId(HttpContext? http)
        {
            if (http == null) return null;
            if (http.Items.TryGetValue(RequestBatchKey, out var existing) && existing is string id) return id;
            var created = Guid.NewGuid().ToString();
            http.Items[RequestBatchKey] = created;
            return created;
        }

        public static string ResolveSource(HttpContext? http)
        {
            if (http == null || http.User?.Identity?.IsAuthenticated != true) return AuditSources.System;
            return http.Request.Path.StartsWithSegments(Mcp.McpOAuthPolicy.McpPath) ? AuditSources.Mcp : AuditSources.Admin;
        }

        private static int? ClaimUserId(HttpContext? http)
        {
            var claim = http?.User?.FindFirst(ClaimTypes.NameIdentifier) ?? http?.User?.FindFirst("UserId");
            return claim != null && int.TryParse(claim.Value, out var id) ? id : null;
        }

        private static long KeyOf(EntityEntry entry)
        {
            var key = entry.Metadata.FindPrimaryKey()!.Properties.Single();
            return Convert.ToInt64(entry.Property(key.Name).CurrentValue);
        }

        private static bool SupportsTransactions(ApplicationDbContext context) =>
            !string.Equals(context.Database.ProviderName, "Microsoft.EntityFrameworkCore.InMemory", StringComparison.Ordinal);

        private static async Task<IDbContextTransaction?> BeginIfNeededAsync(ApplicationDbContext context, CancellationToken ct) =>
            SupportsTransactions(context) && context.Database.CurrentTransaction == null
                ? await context.Database.BeginTransactionAsync(ct)
                : null;

        private static async Task DisposeOwnedAsync(AuditWriteState state)
        {
            if (state.OwnedTransaction == null) return;
            await state.OwnedTransaction.DisposeAsync();
            state.OwnedTransaction = null;
        }
    }
}
