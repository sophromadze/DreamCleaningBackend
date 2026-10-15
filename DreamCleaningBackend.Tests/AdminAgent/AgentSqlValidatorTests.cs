using DreamCleaningBackend.Services.AdminAgent;
using Xunit;

namespace DreamCleaningBackend.Tests.AdminAgent;

/// <summary>
/// The analytics agent's run_sql guard (2026-10). Defence in depth: the real boundary is the
/// AgentReadOnly database user (SELECT on agent_* views only), but this is what refuses a bad
/// statement with a reason the model can act on, and it must never let a write or a side channel
/// through even if that user were mis-granted.
/// </summary>
public class AgentSqlValidatorTests
{
    [Theory]
    [InlineData("SELECT COUNT(*) FROM agent_orders")]
    [InlineData("select status, count(*) from agent_orders group by status")]
    [InlineData("  SELECT 1  ")]
    [InlineData("SELECT order_id FROM agent_orders;")]
    [InlineData("SELECT order_id FROM agent_orders ;  ")]
    [InlineData("WITH w AS (SELECT service_date, total FROM agent_orders) SELECT SUM(total) FROM w")]
    [InlineData("(SELECT 1) UNION ALL (SELECT 2)")]
    [InlineData("SELECT DATE_FORMAT(service_date, '%Y-%m') m, SUM(subtotal) FROM agent_orders GROUP BY m")]
    // Keywords INSIDE literals and quoted identifiers are data, not SQL.
    [InlineData("SELECT * FROM agent_orders WHERE status = 'Update; DROP TABLE x -- into outfile'")]
    [InlineData("SELECT `order_id` AS `delete` FROM agent_orders")]
    [InlineData("SELECT 'it''s fine' AS note")]
    [InlineData("SELECT 'back\\'slash' AS note")]
    [InlineData("SELECT \"double; quoted\" AS note")]
    // Columns whose names merely CONTAIN a keyword.
    [InlineData("SELECT start_date, updated_at, is_deleted, set_count FROM agent_expenses")]
    [InlineData("SELECT REPLACE(name, 'a', 'b') FROM agent_staff")]
    public void AllowsPlainReadQueries(string sql)
    {
        Assert.Null(AgentSqlValidator.Validate(sql));
    }

    [Theory]
    [InlineData(null, "empty")]
    [InlineData("", "empty")]
    [InlineData("   ", "empty")]
    [InlineData("SHOW TABLES", "must start with SELECT or WITH")]
    [InlineData("DESCRIBE agent_orders", "must start with SELECT or WITH")]
    [InlineData("EXPLAIN SELECT 1", "must start with SELECT or WITH")]
    [InlineData("UPDATE agent_orders SET total = 0", "must start with SELECT or WITH")]
    [InlineData("DELETE FROM agent_orders", "must start with SELECT or WITH")]
    [InlineData("INSERT INTO agent_orders VALUES (1)", "must start with SELECT or WITH")]
    [InlineData("DROP VIEW agent_orders", "must start with SELECT or WITH")]
    // Chained statements.
    [InlineData("SELECT 1; SELECT 2", "Only one statement")]
    [InlineData("SELECT 1; DROP TABLE Users", "Only one statement")]
    [InlineData("SELECT 1;;", "Only one statement")]
    // File and timing side channels.
    [InlineData("SELECT * FROM agent_orders INTO OUTFILE '/tmp/x'", "INTO")]
    [InlineData("SELECT * FROM agent_orders INTO DUMPFILE '/tmp/x'", "INTO")]
    [InlineData("SELECT total INTO @t FROM agent_orders", "INTO")]
    [InlineData("SELECT LOAD_FILE('/etc/passwd')", "LOAD_FILE")]
    [InlineData("SELECT load_file('/etc/passwd')", "LOAD_FILE")]
    [InlineData("SELECT SLEEP(5)", "SLEEP")]
    [InlineData("SELECT sleep (5)", "SLEEP")]
    [InlineData("SELECT BENCHMARK(1000000, MD5('a'))", "BENCHMARK")]
    // Locking.
    [InlineData("SELECT * FROM agent_orders FOR UPDATE", "FOR UPDATE")]
    [InlineData("SELECT * FROM agent_orders LOCK IN SHARE MODE", "LOCK IN SHARE MODE")]
    [InlineData("SELECT GET_LOCK('x', 10)", "Lock and wait")]
    // Comments, including MariaDB's executable /*! */ form.
    [InlineData("SELECT 1 /*! ; DROP TABLE Users */", "Comments")]
    [InlineData("SELECT 1 -- note", "Comments")]
    [InlineData("SELECT 1 # note", "Comments")]
    [InlineData("SELECT/**/1", "Comments")]
    // Writes or session statements hidden in a SELECT/WITH.
    [InlineData("WITH x AS (SELECT 1) DELETE FROM agent_orders", "Only read queries")]
    [InlineData("SELECT 1 FROM DUAL WHERE 1 = (SELECT 1) AND SET", "Only read queries")]
    // System schemas and variables.
    [InlineData("SELECT * FROM information_schema.tables", "System schemas")]
    [InlineData("SELECT user FROM mysql.user", "System schemas")]
    [InlineData("SELECT @@version", "variables")]
    [InlineData("SELECT @x", "variables")]
    // Malformed.
    [InlineData("SELECT 'unterminated", "unterminated quote")]
    public void RefusesEverythingElse(string? sql, string reasonFragment)
    {
        var reason = AgentSqlValidator.Validate(sql);
        Assert.NotNull(reason);
        Assert.Contains(reasonFragment, reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RefusesOverlongQueries()
    {
        var sql = "SELECT " + new string('1', AgentSqlValidator.MaxLength);
        Assert.Contains("longer than", AgentSqlValidator.Validate(sql)!);
    }

    [Fact]
    public void NormalizeStripsOneTrailingSemicolonAndKeepsLiterals()
    {
        var (sql, error) = AgentSqlValidator.Normalize("  SELECT 'a;b' AS x ;  ");
        Assert.Null(error);
        Assert.Equal("SELECT 'a;b' AS x", sql);
    }
}
