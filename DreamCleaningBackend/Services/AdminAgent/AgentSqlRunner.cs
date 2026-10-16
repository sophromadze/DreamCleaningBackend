using System.Data;
using System.Diagnostics;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.Models;
using MySqlConnector;

namespace DreamCleaningBackend.Services.AdminAgent
{
    public sealed class AgentSqlResult
    {
        public bool Ok { get; init; }
        public string? Error { get; init; }
        public List<string> Columns { get; init; } = new();
        public List<object?[]> Rows { get; init; } = new();
        public bool Truncated { get; init; }
        public int DurationMs { get; init; }
        /// <summary>The statement exactly as it was executed (normalised by the validator).</summary>
        public string? ExecutedSql { get; init; }
    }

    public interface IAgentSqlRunner
    {
        bool IsConfigured { get; }

        /// <summary>
        /// Validates and runs one read-only statement for the analytics agent, and writes an
        /// <see cref="AdminAgentSqlLog"/> row whatever the outcome.
        /// </summary>
        /// <param name="maxRows">Row cap; null = <see cref="AgentSqlRunner.MaxRows"/> (the panel agent).
        /// The Claude connector passes its smaller cap.</param>
        Task<AgentSqlResult> RunAsync(string sql, int userId, int sessionId, long? messageId, CancellationToken ct, int? maxRows = null);
    }

    /// <summary>
    /// Executes the analytics agent's ad-hoc SQL. Every layer is a separate guarantee:
    /// <list type="number">
    /// <item><see cref="AgentSqlValidator"/> refuses anything that is not one plain SELECT/WITH;</item>
    /// <item>the connection is <c>ConnectionStrings:AgentReadOnly</c> ONLY — a database user that
    /// holds SELECT on the <c>agent_*</c> views and nothing else. When the key is missing the tool
    /// says so; it never falls back to the application's own connection, which can write;</item>
    /// <item>the statement runs inside <c>START TRANSACTION READ ONLY</c>, which is rolled back;</item>
    /// <item><c>SET STATEMENT max_statement_time=10 FOR</c> stops a runaway query on the server
    /// (MariaDB), and the command timeout stops it on our side;</item>
    /// <item>at most <see cref="MaxRows"/> rows are read; the model is told when there were more.</item>
    /// </list>
    /// </summary>
    public sealed class AgentSqlRunner : IAgentSqlRunner
    {
        public const string ConnectionStringName = "AgentReadOnly";
        public const int MaxRows = 1000;
        public const int MaxStatementSeconds = 10;

        private readonly string? _connectionString;
        private readonly ApplicationDbContext _context;
        private readonly ILogger<AgentSqlRunner> _logger;

        public AgentSqlRunner(IConfiguration configuration, ApplicationDbContext context, ILogger<AgentSqlRunner> logger)
        {
            _connectionString = configuration.GetConnectionString(ConnectionStringName);
            _context = context;
            _logger = logger;
        }

        public bool IsConfigured => !string.IsNullOrWhiteSpace(_connectionString);

        public async Task<AgentSqlResult> RunAsync(string sql, int userId, int sessionId, long? messageId, CancellationToken ct, int? maxRows = null)
        {
            var rowCap = Math.Clamp(maxRows ?? MaxRows, 1, MaxRows);
            var (normalized, refusal) = AgentSqlValidator.Normalize(sql);
            if (refusal != null)
            {
                await LogAsync(userId, sessionId, messageId, sql, ok: false, rejected: true, refusal, 0, 0, false);
                return new AgentSqlResult { Ok = false, Error = refusal };
            }

            if (!IsConfigured)
            {
                const string notConfigured =
                    "The read-only SQL connection is not configured (ConnectionStrings:AgentReadOnly), so ad-hoc queries are unavailable. Use the canonical tools instead.";
                await LogAsync(userId, sessionId, messageId, normalized!, ok: false, rejected: true, notConfigured, 0, 0, false);
                return new AgentSqlResult { Ok = false, Error = notConfigured };
            }

            var stopwatch = Stopwatch.StartNew();
            var columns = new List<string>();
            var rows = new List<object?[]>();
            var truncated = false;
            try
            {
                await using var connection = new MySqlConnection(_connectionString);
                await connection.OpenAsync(ct);

                await using (var begin = connection.CreateCommand())
                {
                    begin.CommandText = "START TRANSACTION READ ONLY";
                    await begin.ExecuteNonQueryAsync(ct);
                }

                await using (var command = connection.CreateCommand())
                {
                    command.CommandText = $"SET STATEMENT max_statement_time={MaxStatementSeconds} FOR {normalized}";
                    command.CommandTimeout = MaxStatementSeconds + 5;
                    await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleResult, ct);
                    for (var i = 0; i < reader.FieldCount; i++)
                        columns.Add(reader.GetName(i));

                    while (await reader.ReadAsync(ct))
                    {
                        if (rows.Count >= rowCap)
                        {
                            truncated = true;
                            break;
                        }
                        var values = new object?[reader.FieldCount];
                        for (var i = 0; i < reader.FieldCount; i++)
                            values[i] = reader.IsDBNull(i) ? null : ToJsonFriendly(reader.GetValue(i));
                        rows.Add(values);
                    }
                }

                await using (var rollback = connection.CreateCommand())
                {
                    rollback.CommandText = "ROLLBACK";
                    await rollback.ExecuteNonQueryAsync(CancellationToken.None);
                }
            }
            catch (Exception ex) when (ex is MySqlException or InvalidOperationException or TimeoutException)
            {
                stopwatch.Stop();
                // 1969 = MariaDB ER_STATEMENT_TIMEOUT (max_statement_time); 3024 = the MySQL equivalent.
                var message = ex is MySqlException { Number: 1969 or 3024 } || ex is TimeoutException
                    ? $"The query ran longer than {MaxStatementSeconds} seconds and was stopped. Narrow it (date range, fewer joins) or aggregate more."
                    : $"The database refused the query: {ex.Message}";
                _logger.LogWarning("Analytics agent SQL failed for user {UserId}, session {SessionId} after {Ms} ms: {Error}",
                    userId, sessionId, stopwatch.ElapsedMilliseconds, ex.Message);
                await LogAsync(userId, sessionId, messageId, normalized!, ok: false, rejected: false, message,
                    (int)stopwatch.ElapsedMilliseconds, 0, false);
                return new AgentSqlResult { Ok = false, Error = message, DurationMs = (int)stopwatch.ElapsedMilliseconds, ExecutedSql = normalized };
            }

            stopwatch.Stop();
            _logger.LogInformation(
                "Analytics agent SQL by user {UserId}, session {SessionId}: {Rows} rows{Truncated} in {Ms} ms",
                userId, sessionId, rows.Count, truncated ? " (truncated)" : string.Empty, stopwatch.ElapsedMilliseconds);
            await LogAsync(userId, sessionId, messageId, normalized!, ok: true, rejected: false, null,
                (int)stopwatch.ElapsedMilliseconds, rows.Count, truncated);

            return new AgentSqlResult
            {
                Ok = true,
                Columns = columns,
                Rows = rows,
                Truncated = truncated,
                DurationMs = (int)stopwatch.ElapsedMilliseconds,
                ExecutedSql = normalized
            };
        }

        private static object? ToJsonFriendly(object value) => value switch
        {
            DateTime dt => dt.TimeOfDay == TimeSpan.Zero ? dt.ToString("yyyy-MM-dd") : dt.ToString("yyyy-MM-dd HH:mm:ss"),
            DateOnly d => d.ToString("yyyy-MM-dd"),
            TimeSpan t => t.ToString(@"hh\:mm"),
            byte[] bytes => $"<{bytes.Length} bytes>",
            bool b => b,
            sbyte or byte or short or ushort or int or uint or long or ulong => Convert.ToInt64(value),
            decimal m => m,
            float or double => Convert.ToDecimal(value),
            _ => value.ToString()
        };

        private async Task LogAsync(int userId, int sessionId, long? messageId, string sql, bool ok, bool rejected,
            string? error, int durationMs, int rowCount, bool truncated)
        {
            try
            {
                _context.AdminAgentSqlLogs.Add(new AdminAgentSqlLog
                {
                    UserId = userId,
                    SessionId = sessionId,
                    MessageId = messageId,
                    Sql = sql.Length > AgentSqlValidator.MaxLength ? sql[..AgentSqlValidator.MaxLength] : sql,
                    Succeeded = ok,
                    Rejected = rejected,
                    Error = error?.Length > 1000 ? error[..1000] : error,
                    DurationMs = durationMs,
                    RowCount = rowCount,
                    Truncated = truncated,
                    CreatedAt = DateTime.UtcNow
                });
                await _context.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                // The audit row must never cost the answer, but it must never vanish silently either.
                _logger.LogError(ex, "Could not write the analytics agent SQL log for user {UserId}, session {SessionId}", userId, sessionId);
            }
        }
    }
}
