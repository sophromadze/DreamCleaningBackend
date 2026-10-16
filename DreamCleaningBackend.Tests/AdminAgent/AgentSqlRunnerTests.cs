using DreamCleaningBackend.Data;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services.AdminAgent;
using DreamCleaningBackend.Tests.Billing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Xunit;

namespace DreamCleaningBackend.Tests.AdminAgent;

/// <summary>Runner behaviour that needs no database server.</summary>
public class AgentSqlRunnerTests
{
    private static ApplicationDbContext Db() => new(new DbContextOptionsBuilder<ApplicationDbContext>()
        .UseInMemoryDatabase($"agent-sql-{Guid.NewGuid()}", b => b.EnableNullChecks(false))
        .Options);

    [Fact]
    public async Task WithoutTheReadOnlyConnectionItSaysNotConfigured_AndNeverFallsBackToTheMainOne()
    {
        await using var db = Db();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // The application's own (writable) connection is present - and must NOT be used.
                ["ConnectionStrings:DefaultConnection"] = "Server=localhost;Database=whatever;User=root;Password=x"
            })
            .Build();
        var runner = new AgentSqlRunner(config, db, NullLogger<AgentSqlRunner>.Instance);

        Assert.False(runner.IsConfigured);
        var result = await runner.RunAsync("SELECT COUNT(*) FROM agent_orders", 1, 1, null, CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Contains("not configured", result.Error);
        Assert.Contains("ConnectionStrings:AgentReadOnly", result.Error);

        var log = Assert.Single(db.AdminAgentSqlLogs);
        Assert.False(log.Succeeded);
        Assert.True(log.Rejected);
    }

    [Fact]
    public async Task ARefusedStatementIsLoggedAndNeverExecuted()
    {
        await using var db = Db();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Points at nothing that listens: reaching it would throw, so a refusal proves the
                // statement stopped at the validator.
                ["ConnectionStrings:AgentReadOnly"] = "Server=127.0.0.1;Port=9;Database=none;User=x;Password=y;Connection Timeout=1"
            })
            .Build();
        var runner = new AgentSqlRunner(config, db, NullLogger<AgentSqlRunner>.Instance);

        var result = await runner.RunAsync("SELECT 1; DROP TABLE Users", 5, 9, 42, CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Contains("one statement", result.Error);

        var log = Assert.Single(db.AdminAgentSqlLogs);
        Assert.Equal(5, log.UserId);
        Assert.Equal(9, log.SessionId);
        Assert.Equal(42, log.MessageId);
        Assert.True(log.Rejected);
    }
}

/// <summary>
/// Against a REAL MariaDB (set DC_TEST_MARIADB): the generated views compile, and a user holding
/// exactly the generated grants can read the views and nothing else.
/// </summary>
public class AgentSqlRunnerMariaDbTests : IClassFixture<MariaDbDatabase>, IAsyncLifetime
{
    private readonly MariaDbDatabase _db;
    private readonly string _user = "agent_it_" + Guid.NewGuid().ToString("N")[..8];
    private const string Password = "It-Only-Pass-2026!";
    private string _readOnlyConnection = string.Empty;

    public AgentSqlRunnerMariaDbTests(MariaDbDatabase db) => _db = db;

    public async ValueTask InitializeAsync()
    {
        if (!_db.Available) return;
        await using var ctx = _db.CreateContext();
        foreach (var view in AgentViewCatalog.Views)
            await ctx.Database.ExecuteSqlRawAsync(AgentViewCatalog.CreateViewSql(view));

        // Runs once per test (xUnit builds a new instance per test), so seed only once.
        if (!await ctx.ServiceTypes.AnyAsync(t => t.ServiceKey == "agent-it-type"))
        {
            ctx.ServiceTypes.Add(new ServiceType { Name = "Agent IT type", ServiceKey = "agent-it-type", BasePrice = 90m });
            await ctx.SaveChangesAsync();
        }

        // '%' because a Windows TCP client is not always seen as 'localhost'. The server is the
        // local throwaway one DC_TEST_MARIADB names, and the user is dropped again below.
        await ctx.Database.ExecuteSqlRawAsync($"CREATE USER '{_user}'@'%' IDENTIFIED BY '{Password}'");
        foreach (var grant in AgentViewCatalog.GrantSql(_db.DatabaseName, _user, "%"))
            await ctx.Database.ExecuteSqlRawAsync(grant);

        var builder = new MySqlConnectionStringBuilder(_db.ConnectionString) { UserID = _user, Password = Password };
        _readOnlyConnection = builder.ConnectionString;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_db.Available) return;
        await using var ctx = _db.CreateContext();
        await ctx.Database.ExecuteSqlRawAsync($"DROP USER IF EXISTS '{_user}'@'%'");
    }

    private AgentSqlRunner Runner(ApplicationDbContext ctx) => new(
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:AgentReadOnly"] = _readOnlyConnection })
            .Build(),
        ctx, NullLogger<AgentSqlRunner>.Instance);

    [MariaDbFact]
    public async Task EveryViewIsReadableByTheReadOnlyUser()
    {
        await using var ctx = _db.CreateContext();
        var runner = Runner(ctx);
        foreach (var view in AgentViewCatalog.Views)
        {
            var result = await runner.RunAsync($"SELECT * FROM {view.Name} LIMIT 1", 1, 1, null, TestContext.Current.CancellationToken);
            Assert.True(result.Ok, $"{view.Name}: {result.Error}");
            Assert.Equal(view.Columns.Select(c => c.Name), result.Columns);
        }

        var types = await runner.RunAsync("SELECT name, service_type_key FROM agent_service_types", 1, 1, null, TestContext.Current.CancellationToken);
        Assert.True(types.Ok, types.Error);
        Assert.Contains(types.Rows, r => (string?)r[1] == "agent-it-type");
    }

    [MariaDbFact]
    public async Task ArrangedCleaningKindIsTheStoredLabel_AndNullWhenNotSet()
    {
        await using var ctx = _db.CreateContext();
        var user = new User
        {
            FirstName = "Agent", LastName = "Kind", Email = $"agent.kind.{Guid.NewGuid():N}@example.com",
            Role = UserRole.Customer, IsActive = true, CreatedAt = DateTime.UtcNow
        };
        ctx.Users.Add(user);
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
        var typeId = await ctx.ServiceTypes.Where(t => t.ServiceKey == "agent-it-type").Select(t => t.Id).SingleAsync(TestContext.Current.CancellationToken);

        Order NewOrder(string? kind) => new()
        {
            UserId = user.Id, ServiceTypeId = typeId, OrderDate = DateTime.UtcNow, ServiceDate = DateTime.UtcNow.Date,
            ServiceTime = TimeSpan.FromHours(10), ContactFirstName = "A", ContactLastName = "K", ContactEmail = "k@example.com",
            ContactPhone = "2125550100", ServiceAddress = "1 Test St", City = "Manhattan", State = "New York", ZipCode = "10001",
            Status = "Pending", TotalDuration = 120, CreatedAt = DateTime.UtcNow, CustomServiceDisplayName = kind
        };
        var orders = new[] { NewOrder("Move In/Out"), NewOrder(" Deep "), NewOrder(null), NewOrder("  ") };
        ctx.Orders.AddRange(orders);
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await Runner(ctx).RunAsync(
            $"SELECT order_id, arranged_cleaning_kind FROM agent_orders WHERE customer_id = {user.Id} ORDER BY order_id",
            1, 1, null, TestContext.Current.CancellationToken);
        Assert.True(result.Ok, result.Error);
        Assert.Equal(new string?[] { "Move In/Out", "Deep", null, null }, result.Rows.Select(r => (string?)r[1]));
    }

    [MariaDbFact]
    public async Task TheDatabaseItselfRefusesTheTablesBehindTheViews()
    {
        await using var ctx = _db.CreateContext();
        var runner = Runner(ctx);
        // A plain SELECT passes the validator - the GRANTS are what stop it.
        var result = await runner.RunAsync("SELECT PasswordHash FROM Users", 1, 1, null, TestContext.Current.CancellationToken);
        Assert.False(result.Ok);
        Assert.Contains("refused", result.Error);

        // And the user cannot write even when going around the validator entirely.
        await using var raw = new MySqlConnection(_readOnlyConnection);
        await raw.OpenAsync(TestContext.Current.CancellationToken);
        await using var cmd = raw.CreateCommand();
        cmd.CommandText = "CREATE TABLE should_not_exist (id INT)";
        await Assert.ThrowsAsync<MySqlException>(() => cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
    }

    [MariaDbFact]
    public async Task ResultsStopAtTheRowCapAndSayTheyWereTruncated()
    {
        await using var ctx = _db.CreateContext();
        var runner = Runner(ctx);
        var result = await runner.RunAsync(
            "WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 1500) SELECT i FROM n",
            1, 1, null, TestContext.Current.CancellationToken);
        Assert.True(result.Ok, result.Error);
        Assert.Equal(AgentSqlRunner.MaxRows, result.Rows.Count);
        Assert.True(result.Truncated);

        var log = await ctx.AdminAgentSqlLogs.OrderByDescending(l => l.Id).FirstAsync(TestContext.Current.CancellationToken);
        Assert.True(log.Succeeded);
        Assert.True(log.Truncated);
        Assert.Equal(AgentSqlRunner.MaxRows, log.RowCount);
    }
}
