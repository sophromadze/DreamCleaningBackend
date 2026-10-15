using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DreamCleaningBackend.Models
{
    /// <summary>
    /// One conversation with the SuperAdmin analytics agent (2026-10). Owned by exactly one
    /// SuperAdmin: every read and write is filtered by <see cref="OwnerUserId"/>, so another
    /// SuperAdmin never sees it. Entirely separate from the public chat agent's
    /// <see cref="ChatAgentSession"/> — different audience, tools, prompt and tables.
    /// </summary>
    public class AdminAgentSession
    {
        public int Id { get; set; }

        public int OwnerUserId { get; set; }
        public virtual User? Owner { get; set; }

        [StringLength(200)]
        public string Title { get; set; } = "New chat";

        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }

        /// <summary>Running token totals for the session, kept in step with its messages.</summary>
        public long InputTokens { get; set; }
        public long OutputTokens { get; set; }
        public long CacheReadTokens { get; set; }
        public long CacheWriteTokens { get; set; }

        /// <summary>
        /// Estimated USD, priced with the AdminAgent:Price* settings in force when each turn ran.
        /// Null while no price is configured — an estimate from guessed prices would be worse
        /// than none.
        /// </summary>
        [Column(TypeName = "decimal(18,6)")]
        public decimal? EstimatedCostUsd { get; set; }

        public virtual ICollection<AdminAgentMessage> Messages { get; set; } = new List<AdminAgentMessage>();
    }

    public static class AdminAgentMessageRoles
    {
        public const string User = "user";
        public const string Assistant = "assistant";
    }

    public static class AdminAgentMessageStatuses
    {
        /// <summary>The assistant is still working on this turn.</summary>
        public const string Running = "running";
        public const string Complete = "complete";
        public const string Error = "error";
    }

    /// <summary>
    /// One message of an analytics-agent session: either the SuperAdmin's question or the
    /// agent's answer. An answer carries everything needed to show how it was reached.
    /// </summary>
    public class AdminAgentMessage
    {
        public long Id { get; set; }

        public int SessionId { get; set; }
        public virtual AdminAgentSession? Session { get; set; }

        [StringLength(20)]
        public string Role { get; set; } = AdminAgentMessageRoles.User;

        /// <summary>The question, or the answer's markdown text.</summary>
        public string Content { get; set; } = string.Empty;

        /// <summary>
        /// How a question was entered: "text" today. Kept so a later voice front end can send a
        /// transcript through the very same endpoint and still be told apart.
        /// </summary>
        [StringLength(10)]
        public string? InputMode { get; set; }

        [StringLength(20)]
        public string Status { get; set; } = AdminAgentMessageStatuses.Complete;

        /// <summary>Progress label of a running turn ("Running query…"), shown on reload.</summary>
        [StringLength(200)]
        public string? ProgressLabel { get; set; }

        [StringLength(1000)]
        public string? ErrorMessage { get; set; }

        /// <summary>Charts and tables of an answer, in order: a JSON array of parts.</summary>
        public string? PartsJson { get; set; }

        /// <summary>
        /// "How this was calculated": every tool the turn called with its inputs, the filters and
        /// date range it applied, and each SQL statement with its row count and timing.
        /// </summary>
        public string? ToolCallsJson { get; set; }

        /// <summary>
        /// The exact Messages API content this message contributed to the conversation, replayed
        /// verbatim on later turns. Append-only on purpose: the model's thinking blocks stay
        /// valid only while every earlier message is unchanged.
        /// </summary>
        public string? ApiMessagesJson { get; set; }

        [StringLength(60)]
        public string? Model { get; set; }

        public int InputTokens { get; set; }
        public int OutputTokens { get; set; }
        public int CacheReadTokens { get; set; }
        public int CacheWriteTokens { get; set; }

        [Column(TypeName = "decimal(18,6)")]
        public decimal? CostUsd { get; set; }

        public DateTime CreatedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
    }

    /// <summary>
    /// Audit row for every statement the agent's run_sql tool executed (or refused), with who
    /// asked, in which session, how long it took and how many rows it returned.
    /// </summary>
    public class AdminAgentSqlLog
    {
        public long Id { get; set; }

        public int UserId { get; set; }
        public int SessionId { get; set; }
        public long? MessageId { get; set; }

        public string Sql { get; set; } = string.Empty;

        public bool Succeeded { get; set; }

        /// <summary>Refused by the validator before reaching the database.</summary>
        public bool Rejected { get; set; }

        [StringLength(1000)]
        public string? Error { get; set; }

        public int DurationMs { get; set; }
        public int RowCount { get; set; }
        public bool Truncated { get; set; }

        public DateTime CreatedAt { get; set; }
    }
}
