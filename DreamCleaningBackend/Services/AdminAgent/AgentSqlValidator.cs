using System.Text;
using System.Text.RegularExpressions;

namespace DreamCleaningBackend.Services.AdminAgent
{
    /// <summary>
    /// Checks a statement from the analytics agent's run_sql tool BEFORE it reaches the database.
    ///
    /// This is defence in depth, not the boundary. The boundary is the database user behind
    /// <c>ConnectionStrings:AgentReadOnly</c>, which holds SELECT on the <c>agent_*</c> views and
    /// nothing else, plus the read-only transaction and the statement timeout the runner adds.
    /// The validator exists so an obviously wrong statement is refused with a reason the model can
    /// act on, and so a mis-granted user is not the only thing between a model and a write.
    ///
    /// Rules (each a refusal with its own message):
    /// <list type="bullet">
    /// <item>exactly one statement, beginning with SELECT or WITH (a single trailing ';' is allowed);</item>
    /// <item>no comments of any kind — MariaDB runs the body of <c>/*! ... */</c> as SQL, so a
    /// comment is not inert;</item>
    /// <item>no INTO (OUTFILE / DUMPFILE / variables), LOAD_FILE, SLEEP, BENCHMARK, locking reads,
    /// user-level locks, write or DDL keywords, or references to the system schemas.</item>
    /// </list>
    /// Keywords are matched OUTSIDE string literals and quoted identifiers only, so
    /// <c>WHERE status = 'Update requested'</c> is fine.
    /// </summary>
    public static class AgentSqlValidator
    {
        public const int MaxLength = 20000;

        private static readonly (Regex Pattern, string Reason)[] Forbidden =
        {
            (Word("INTO"), "INTO is not allowed (no OUTFILE, DUMPFILE or variables) - just SELECT the rows."),
            (Word("LOAD_FILE"), "LOAD_FILE is not allowed."),
            (Word("SLEEP"), "SLEEP is not allowed."),
            (Word("BENCHMARK"), "BENCHMARK is not allowed."),
            (new Regex(@"\bFOR\s+UPDATE\b", RegexOptions.IgnoreCase), "Locking reads (FOR UPDATE) are not allowed."),
            (new Regex(@"\bLOCK\s+IN\s+SHARE\s+MODE\b", RegexOptions.IgnoreCase), "Locking reads (LOCK IN SHARE MODE) are not allowed."),
            (new Regex(@"\b(GET_LOCK|RELEASE_LOCK|RELEASE_ALL_LOCKS|IS_FREE_LOCK|IS_USED_LOCK|MASTER_POS_WAIT|MASTER_GTID_WAIT)\b", RegexOptions.IgnoreCase), "Lock and wait functions are not allowed."),
            (new Regex(@"\b(INSERT|UPDATE|DELETE|DROP|ALTER|CREATE|TRUNCATE|RENAME|GRANT|REVOKE|CALL|HANDLER|EXECUTE|PREPARE|DEALLOCATE|SET|DO|LOCK|UNLOCK|KILL|SHUTDOWN|FLUSH|RESET|INSTALL|UNINSTALL|ANALYZE|OPTIMIZE|REPAIR|CHECKSUM|BACKUP|RESTORE|PURGE|CHANGE|START|STOP|COMMIT|ROLLBACK|SAVEPOINT|XA)\b", RegexOptions.IgnoreCase), "Only read queries are allowed - write, DDL and session statements are refused."),
            (new Regex(@"\b(information_schema|performance_schema|mysql|sys)\s*\.", RegexOptions.IgnoreCase), "System schemas are not available - query the agent_* views."),
            (new Regex(@"@@?\w", RegexOptions.IgnoreCase), "User and system variables are not allowed."),
        };

        private static Regex Word(string word) => new($@"\b{word}\b", RegexOptions.IgnoreCase);

        /// <summary>Null when the statement may run; otherwise why it was refused.</summary>
        public static string? Validate(string? sql) => Normalize(sql).Error;

        /// <summary>
        /// The statement as it will be executed (trimmed, one trailing ';' removed) or the reason it
        /// was refused.
        /// </summary>
        public static (string? Sql, string? Error) Normalize(string? sql)
        {
            if (string.IsNullOrWhiteSpace(sql))
                return (null, "The query is empty.");
            if (sql.Length > MaxLength)
                return (null, $"The query is longer than {MaxLength} characters.");

            var text = sql.Trim();
            var (code, scanError) = StripLiterals(text);
            if (scanError != null)
                return (null, scanError);

            // One statement: a single trailing semicolon is tolerated, any other one is a second
            // statement.
            var codeTrimmed = code.TrimEnd();
            if (codeTrimmed.EndsWith(';'))
            {
                codeTrimmed = codeTrimmed[..^1].TrimEnd();
                text = text.TrimEnd();
                text = text[..^1].TrimEnd();
            }
            if (codeTrimmed.Contains(';'))
                return (null, "Only one statement is allowed - no semicolons between statements.");

            if (!Regex.IsMatch(codeTrimmed, @"^\(*\s*(SELECT|WITH)\b", RegexOptions.IgnoreCase))
                return (null, "The query must start with SELECT or WITH.");

            foreach (var (pattern, reason) in Forbidden)
                if (pattern.IsMatch(codeTrimmed))
                    return (null, reason);

            return (text, null);
        }

        /// <summary>
        /// Replaces the contents of string literals and quoted identifiers with spaces (keeping the
        /// quotes) so keyword checks only ever see SQL code. Refuses comments and unterminated
        /// quotes outright.
        /// </summary>
        private static (string Code, string? Error) StripLiterals(string sql)
        {
            var sb = new StringBuilder(sql.Length);
            var i = 0;
            while (i < sql.Length)
            {
                var ch = sql[i];
                var next = i + 1 < sql.Length ? sql[i + 1] : '\0';

                if (ch == '#' || (ch == '-' && next == '-') || (ch == '/' && next == '*'))
                    return (string.Empty, "Comments are not allowed in the query.");

                if (ch is '\'' or '"' or '`')
                {
                    var quote = ch;
                    sb.Append(quote);
                    i++;
                    var closed = false;
                    while (i < sql.Length)
                    {
                        var c = sql[i];
                        if (c == '\\' && quote != '`' && i + 1 < sql.Length)
                        {
                            sb.Append("  ");
                            i += 2;
                            continue;
                        }
                        if (c == quote)
                        {
                            // A doubled quote is an escaped quote inside the literal.
                            if (i + 1 < sql.Length && sql[i + 1] == quote)
                            {
                                sb.Append("  ");
                                i += 2;
                                continue;
                            }
                            sb.Append(quote);
                            i++;
                            closed = true;
                            break;
                        }
                        sb.Append(c == '\n' ? '\n' : ' ');
                        i++;
                    }
                    if (!closed)
                        return (string.Empty, "The query has an unterminated quote.");
                    continue;
                }

                sb.Append(ch);
                i++;
            }
            return (sb.ToString(), null);
        }
    }
}
