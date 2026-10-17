using System.Text.Json;
using DreamCleaningBackend.Data;

namespace DreamCleaningBackend.Services.ChangeSets
{
    /// <summary>What a change set does — picks the <see cref="IChangeSetRunner"/>.</summary>
    public static class ChangeSetKinds
    {
        public const string Catalog = "catalog";
        public const string AddExpense = "add_expense";
        public const string DeleteExpense = "delete_expense";
        public const string Revert = "revert";
    }

    /// <summary>
    /// Who is previewing or applying. <see cref="McpGrantId"/> is set for the Claude connector and
    /// null for the admin panel; a token only applies through the same door it was previewed
    /// through, by the same person.
    /// </summary>
    public sealed record ChangeSetCaller(int UserId, int? McpGrantId, string Source)
    {
        public bool ViaConnector => McpGrantId != null;

        public static ChangeSetCaller Connector(int userId, int grantId) => new(userId, grantId, Models.AuditSources.Mcp);

        public static ChangeSetCaller AdminPanel(int userId) => new(userId, null, Models.AuditSources.Admin);
    }

    /// <summary>A refusal or input problem the person can act on; its message is shown as is.</summary>
    public sealed class ChangeSetException : Exception
    {
        public ChangeSetException(string message) : base(message) { }
    }

    /// <summary>What a runner gets: the dry-run (or apply) scope's context and services.</summary>
    public sealed class ChangeRunContext
    {
        public required ApplicationDbContext Db { get; init; }
        public required IServiceProvider Services { get; init; }
        public required ChangeSetCaller Caller { get; init; }
        public required JsonElement Request { get; init; }

        /// <summary>True while previewing: the writes are rolled back afterwards.</summary>
        public required bool IsPreview { get; init; }
    }

    /// <summary>What a runner reports back besides the history rows its writes produced.</summary>
    public sealed class ChangeRunResult
    {
        /// <summary>Tool-specific result (e.g. the created rows), shown with the preview and the apply.</summary>
        public object? Result { get; set; }

        public List<string> Warnings { get; } = new();

        /// <summary>When set, nothing may be applied: the preview explains why and issues no token.</summary>
        public string? BlockedReason { get; set; }

        /// <summary>Extra detail for the preview (a revert's conflicts, offered alternatives…).</summary>
        public object? Report { get; set; }
    }

    /// <summary>
    /// Performs one kind of change through the code the admin panel uses. It is run TWICE — once
    /// inside a transaction that is rolled back (the preview), then again for real (the apply) —
    /// so it must be deterministic given the same database state and must write only through the
    /// context it is handed.
    /// </summary>
    public interface IChangeSetRunner
    {
        string Kind { get; }

        Task<ChangeRunResult> RunAsync(ChangeRunContext context, CancellationToken ct);
    }
}
