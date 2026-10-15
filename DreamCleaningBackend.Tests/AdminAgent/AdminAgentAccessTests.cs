using System.Reflection;
using System.Security.Claims;
using DreamCleaningBackend.Controllers;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services.AdminAgent;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DreamCleaningBackend.Tests.AdminAgent;

/// <summary>
/// WHO MAY USE THE ANALYTICS AGENT (2026-10): SuperAdmins, and each only their own chats.
///
/// The hidden tab is the convenience; the policy on AdminAgentController is the control. These
/// tests evaluate, for EVERY action on the controller, the authorization policy ASP.NET Core
/// would build from its attributes, using the real policy registration (the same
/// <see cref="AdminAgentController.AddPolicy"/> Program.cs calls), against principals carrying
/// each role. A new action, an [AllowAnonymous], or a per-action [Authorize(Roles = ...)] that
/// widened access fails here.
/// </summary>
public class AdminAgentAccessTests
{
    private static readonly string[] OtherRoles = { "Admin", "Moderator", "Customer", "Cleaner" };

    private static IServiceProvider AuthServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization(AdminAgentController.AddPolicy);
        return services.BuildServiceProvider();
    }

    private static ClaimsPrincipal Principal(string? role)
    {
        if (role == null) return new ClaimsPrincipal(new ClaimsIdentity()); // anonymous
        var claims = new[] { new Claim("UserId", "7"), new Claim("Role", role), new Claim(ClaimTypes.Role, role) };
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer", ClaimTypes.Name, ClaimTypes.Role));
    }

    private static IEnumerable<MethodInfo> Actions() =>
        typeof(AdminAgentController)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes<HttpMethodAttribute>().Any());

    [Fact]
    public void TheControllerHasEndpointsToCheck()
    {
        // A rename that leaves no [Http*] actions would make every other test pass vacuously.
        Assert.True(Actions().Count() >= 8, "Expected the analytics agent's endpoints on AdminAgentController.");
    }

    [Fact]
    public void NoEndpointIsAnonymous()
    {
        Assert.Null(typeof(AdminAgentController).GetCustomAttribute<AllowAnonymousAttribute>());
        var anonymous = Actions().Where(a => a.GetCustomAttribute<AllowAnonymousAttribute>() != null).Select(a => a.Name).ToList();
        Assert.True(anonymous.Count == 0, "These analytics-agent endpoints allow anonymous callers: " + string.Join(", ", anonymous));
    }

    [Fact]
    public async Task EveryEndpointAdmitsASuperAdminAndRefusesEveryoneElse()
    {
        var sp = AuthServices();
        var provider = sp.GetRequiredService<IAuthorizationPolicyProvider>();
        var authorization = sp.GetRequiredService<IAuthorizationService>();
        var classData = typeof(AdminAgentController).GetCustomAttributes<AuthorizeAttribute>(inherit: true).Cast<IAuthorizeData>().ToList();
        Assert.Contains(classData, d => d.Policy == AdminAgentController.Policy);

        var failures = new List<string>();
        foreach (var action in Actions())
        {
            var data = classData.Concat(action.GetCustomAttributes<AuthorizeAttribute>().Cast<IAuthorizeData>()).ToList();
            var policy = await AuthorizationPolicy.CombineAsync(provider, data);
            Assert.NotNull(policy);

            if (!(await authorization.AuthorizeAsync(Principal("SuperAdmin"), null, policy!)).Succeeded)
                failures.Add($"{action.Name}: refuses a SuperAdmin");
            foreach (var role in OtherRoles)
                if ((await authorization.AuthorizeAsync(Principal(role), null, policy!)).Succeeded)
                    failures.Add($"{action.Name}: admits {role}");
            if ((await authorization.AuthorizeAsync(Principal(null), null, policy!)).Succeeded)
                failures.Add($"{action.Name}: admits an anonymous caller");
        }

        Assert.True(failures.Count == 0, string.Join("; ", failures));
    }

    [Fact]
    public void StartingATurnHasItsOwnRateLimitNotThePublicChatOne()
    {
        var ask = typeof(AdminAgentController).GetMethod(nameof(AdminAgentController.Ask))!;
        var limit = ask.GetCustomAttribute<Microsoft.AspNetCore.RateLimiting.EnableRateLimitingAttribute>();
        Assert.NotNull(limit);
        Assert.Equal(AdminAgentController.RateLimitPolicy, limit!.PolicyName);
        Assert.NotEqual(ChatController.RateLimitPolicy, limit.PolicyName);
    }

    // ── Each SuperAdmin sees only their own sessions ─────────────────────────────────────────────

    private static ApplicationDbContext Db() => new(new DbContextOptionsBuilder<ApplicationDbContext>()
        .UseInMemoryDatabase($"admin-agent-access-{Guid.NewGuid()}", b => b.EnableNullChecks(false))
        .Options);

    private static AdminAgentService Service(ApplicationDbContext db, string? apiKey = "test-key")
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AdminAgent:ApiKey"] = apiKey })
            .Build();
        // No scope factory: the background answer is skipped (BackgroundWork logs and returns),
        // which is all these tests need - they check who may START a turn, not the AI call.
        return new AdminAgentService(db, config, new NoSql(), null!, NullLogger<AdminAgentService>.Instance);
    }

    private sealed class NoSql : IAgentSqlRunner
    {
        public bool IsConfigured => false;
        public Task<AgentSqlResult> RunAsync(string sql, int userId, int sessionId, long? messageId, CancellationToken ct) =>
            Task.FromResult(new AgentSqlResult { Ok = false, Error = "not configured" });
    }

    [Fact]
    public async Task ASuperAdminCannotReachAnotherSuperAdminsChat()
    {
        await using var db = Db();
        var service = Service(db);
        var mine = await service.CreateSessionAsync(1, "Mine");
        await service.StartTurnAsync(1, mine.Id, "How many orders this month?", null);

        // User 2 sees nothing of user 1's chat...
        Assert.Empty(await service.ListSessionsAsync(2));
        Assert.Null(await service.GetSessionAsync(2, mine.Id));
        var firstMessageId = (await db.AdminAgentMessages.FirstAsync()).Id;
        Assert.Null(await service.GetMessageAsync(2, mine.Id, firstMessageId));
        // ...cannot rename or delete it...
        Assert.Null(await service.RenameSessionAsync(2, mine.Id, "Hijacked"));
        Assert.False(await service.DeleteSessionAsync(2, mine.Id));
        // ...and cannot post into it.
        var ex = await Assert.ThrowsAsync<AdminAgentRequestException>(() => service.StartTurnAsync(2, mine.Id, "hello", null));
        Assert.Equal(404, ex.StatusCode);

        // The owner still has it, untouched.
        var detail = await service.GetSessionAsync(1, mine.Id);
        Assert.NotNull(detail);
        Assert.Equal("Mine", detail!.Session.Title);
        Assert.Single(await service.ListSessionsAsync(1));
    }

    [Fact]
    public async Task AMissingApiKeyIsAClearNotConfiguredAnswer_NotACrash()
    {
        await using var db = Db();
        var service = Service(db, apiKey: null);
        Assert.False(service.GetStatus().IsConfigured);
        var session = await service.CreateSessionAsync(1, null);
        var ex = await Assert.ThrowsAsync<AdminAgentRequestException>(() => service.StartTurnAsync(1, session.Id, "Revenue?", null));
        Assert.Equal(503, ex.StatusCode);
        Assert.Contains("not configured", ex.Message);
        Assert.Empty(db.AdminAgentMessages);
    }

    [Fact]
    public async Task OnlyOneAnswerRunsPerChatAtATime()
    {
        await using var db = Db();
        var service = Service(db);
        var session = await service.CreateSessionAsync(1, null);
        var started = await service.StartTurnAsync(1, session.Id, "First question", null);
        Assert.Equal(AdminAgentMessageStatuses.Running, started.AssistantMessage.Status);
        Assert.Equal("First question", started.Session.Title);

        var ex = await Assert.ThrowsAsync<AdminAgentRequestException>(() => service.StartTurnAsync(1, session.Id, "Second", null));
        Assert.Equal(409, ex.StatusCode);
    }

    [Fact]
    public async Task TheQuestionCarriesTheNewYorkDate_NotTheSystemPrompt()
    {
        await using var db = Db();
        var service = Service(db);
        var session = await service.CreateSessionAsync(1, null);
        await service.StartTurnAsync(1, session.Id, "Revenue today?", "voice");

        var user = await db.AdminAgentMessages.FirstAsync(m => m.Role == AdminAgentMessageRoles.User);
        Assert.Equal("voice", user.InputMode);
        Assert.Contains("current date and time in New York", user.ApiMessagesJson);
        // The system prompt must be byte-stable between turns (prompt cache + preserved thinking).
        Assert.Equal(AdminAgentSystemPrompt.Build(), AdminAgentSystemPrompt.Build());
        Assert.DoesNotContain(DateTime.UtcNow.ToString("yyyy-MM-dd"), AdminAgentSystemPrompt.Build());
    }
}
