using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services.Mcp;
using DreamCleaningBackend.Tests.Billing;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DreamCleaningBackend.Tests.Mcp;

/// <summary>
/// The Claude connector over HTTP against a real MariaDB (set DC_TEST_MARIADB): who may connect,
/// what ends a session, and that add/delete_expense follow the panel's rules exactly.
/// </summary>
public class McpConnectorIntegrationTests : IClassFixture<MariaDbDatabase>, IAsyncLifetime
{
    private readonly MariaDbDatabase _db;
    private McpTestHost? _host;
    private McpTestHost Host => _host!;

    public McpConnectorIntegrationTests(MariaDbDatabase db) => _db = db;

    public async ValueTask InitializeAsync()
    {
        if (_db.Available) _host = await McpTestHost.StartAsync(_db);
    }

    public async ValueTask DisposeAsync()
    {
        if (_host != null) await _host.DisposeAsync();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ── Authorize: SuperAdmin only ──────────────────────────────────────────────────────────────

    [MariaDbFact]
    public async Task NonSuperAdminsAreRefusedAtAuthorize()
    {
        var refused = new List<int>();
        foreach (var role in new[] { UserRole.Admin, UserRole.Moderator, UserRole.Customer, UserRole.Cleaner })
        {
            var user = await Host.AddUserAsync(role);
            refused.Add(user.Id);
            var pending = await Host.RegisterAndBuildRequestAsync();
            var response = await Host.DecideAsync(pending, user);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Contains("Only SuperAdmin", await response.Content.ReadAsStringAsync(Ct));
        }
        Assert.Equal(0, await Host.WithDbAsync(db => db.McpAuthorizationCodes.CountAsync(c => refused.Contains(c.UserId), Ct)));
    }

    [MariaDbFact]
    public async Task AnonymousCallersCannotDecide_AndTheConsentCheckSaysWhoMayApprove()
    {
        var pending = await Host.RegisterAndBuildRequestAsync();
        var anonymous = await Host.Client.PostAsync("/api/oauth/authorize/decision",
            McpTestHost.JsonBody(new { query = pending.Query, approve = true }), Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        var admin = await Host.AddUserAsync(UserRole.Admin);
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/oauth/authorize/check")
        {
            Content = McpTestHost.JsonBody(new { query = pending.Query, approve = false })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", McpTestHost.SiteJwt(admin));
        var check = JsonNode.Parse(await (await Host.Client.SendAsync(request, Ct)).Content.ReadAsStringAsync(Ct))!;
        Assert.False(check["canApprove"]!.GetValue<bool>());
        Assert.Equal("claude.ai", check["redirectHost"]!.GetValue<string>());
    }

    [MariaDbFact]
    public async Task AnUnregisteredRedirectIsNeverRedirectedTo()
    {
        var pending = await Host.RegisterAndBuildRequestAsync();
        var tampered = pending.Query.Replace(Uri.EscapeDataString(McpTestHost.Redirect), Uri.EscapeDataString("https://evil.example/cb"));
        var response = await Host.Client.GetAsync("/api/oauth/authorize?" + tampered, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(response.Headers.Location);

        var good = await Host.Client.GetAsync("/api/oauth/authorize?" + pending.Query, Ct);
        Assert.Equal(HttpStatusCode.Redirect, good.StatusCode);
        Assert.StartsWith(McpTestHost.BaseUrl + "/connect/authorize?", good.Headers.Location!.ToString());
    }

    // ── Tokens ──────────────────────────────────────────────────────────────────────────────────

    [MariaDbFact]
    public async Task ASuperAdminConnects_AndTokensAreStoredOnlyAsHashes()
    {
        var super = await Host.AddUserAsync(UserRole.SuperAdmin);
        var tokens = await Host.ConnectAsync(super);

        var list = await Host.McpAsync(tokens.Access, "tools/list");
        Assert.Equal(200, list.Status);
        Assert.Equal(19, list.Result!["tools"]!.AsArray().Count);

        var stored = await Host.WithDbAsync(db => db.McpTokens.Select(t => t.TokenHash).ToListAsync(Ct));
        Assert.DoesNotContain(tokens.Access, stored);
        Assert.DoesNotContain(tokens.Refresh, stored);
        Assert.Contains(McpOAuthPolicy.Hash(tokens.Access), stored);
    }

    [MariaDbFact]
    public async Task NoTokenOrABadToken_Gets401WithTheDiscoveryPointer()
    {
        var none = await Host.McpAsync(null, "tools/list");
        Assert.Equal(401, none.Status);
        Assert.Contains($"resource_metadata=\"{McpTestHost.BaseUrl}/.well-known/oauth-protected-resource/api/mcp\"", none.WwwAuthenticate);

        var bad = await Host.McpAsync("dcmcp_at_not-a-real-token", "tools/list");
        Assert.Equal(401, bad.Status);
        Assert.Contains("invalid_token", bad.WwwAuthenticate);

        // The site's own JWT is not a connector token.
        var super = await Host.AddUserAsync(UserRole.SuperAdmin);
        Assert.Equal(401, (await Host.McpAsync(McpTestHost.SiteJwt(super), "tools/list")).Status);
    }

    [MariaDbFact]
    public async Task ExpiredAccessAndRefreshTokensAreRejected()
    {
        var super = await Host.AddUserAsync(UserRole.SuperAdmin);
        var tokens = await Host.ConnectAsync(super);
        var past = DateTime.UtcNow.AddMinutes(-1);
        await Host.WithDbAsync(db => db.McpTokens.ExecuteUpdateAsync(s => s.SetProperty(t => t.ExpiresAt, past), Ct));

        Assert.Equal(401, (await Host.McpAsync(tokens.Access, "tools/list")).Status);
        var refresh = await Host.TokenAsync(new() { ["grant_type"] = "refresh_token", ["refresh_token"] = tokens.Refresh });
        Assert.Equal(HttpStatusCode.BadRequest, refresh.StatusCode);
        Assert.Contains("invalid_grant", await refresh.Content.ReadAsStringAsync(Ct));
    }

    [MariaDbFact]
    public async Task ARevokedSessionIsRejected_AndCannotRefresh()
    {
        var super = await Host.AddUserAsync(UserRole.SuperAdmin);
        var tokens = await Host.ConnectAsync(super);
        var grantId = await Host.WithDbAsync(db => db.McpGrants.Where(g => g.UserId == super.Id).Select(g => g.Id).SingleAsync(Ct));

        var revoke = new HttpRequestMessage(HttpMethod.Delete, $"/api/admin/mcp/sessions/{grantId}");
        revoke.Headers.Authorization = new AuthenticationHeaderValue("Bearer", McpTestHost.SiteJwt(super));
        Assert.Equal(HttpStatusCode.NoContent, (await Host.Client.SendAsync(revoke, Ct)).StatusCode);

        Assert.Equal(401, (await Host.McpAsync(tokens.Access, "tools/list")).Status);
        var refresh = await Host.TokenAsync(new() { ["grant_type"] = "refresh_token", ["refresh_token"] = tokens.Refresh });
        Assert.Contains("invalid_grant", await refresh.Content.ReadAsStringAsync(Ct));
    }

    [MariaDbFact]
    public async Task OnlySuperAdminsSeeTheSessionsList()
    {
        var admin = await Host.AddUserAsync(UserRole.Admin);
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/mcp/sessions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", McpTestHost.SiteJwt(admin));
        Assert.Equal(HttpStatusCode.Forbidden, (await Host.Client.SendAsync(request, Ct)).StatusCode);

        // And a connector token is not a way into the admin API.
        var super = await Host.AddUserAsync(UserRole.SuperAdmin);
        var tokens = await Host.ConnectAsync(super);
        var viaConnector = new HttpRequestMessage(HttpMethod.Get, "/api/admin/mcp/sessions");
        viaConnector.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.Access);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Host.Client.SendAsync(viaConnector, Ct)).StatusCode);
    }

    [MariaDbFact]
    public async Task ADemotedUserLosesAccessOnTheVeryNextCall()
    {
        var super = await Host.AddUserAsync(UserRole.SuperAdmin);
        var tokens = await Host.ConnectAsync(super);
        Assert.Equal(200, (await Host.CallAsync(tokens.Access, "list_expense_categories", new { })).Status);

        await Host.WithDbAsync(db => db.Users.Where(u => u.Id == super.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.Role, UserRole.Admin), Ct));

        var call = await Host.CallAsync(tokens.Access, "list_expense_categories", new { });
        Assert.Equal(401, call.Status);
        var grant = await Host.WithDbAsync(db => db.McpGrants.SingleAsync(g => g.UserId == super.Id, Ct));
        Assert.NotNull(grant.RevokedAt);

        var refresh = await Host.TokenAsync(new() { ["grant_type"] = "refresh_token", ["refresh_token"] = tokens.Refresh });
        Assert.Contains("invalid_grant", await refresh.Content.ReadAsStringAsync(Ct));
    }

    [MariaDbFact]
    public async Task SigningOutEverywhereEndsTheConnectorSessionToo()
    {
        var super = await Host.AddUserAsync(UserRole.SuperAdmin);
        var tokens = await Host.ConnectAsync(super);
        await Host.WithDbAsync(db => db.Users.Where(u => u.Id == super.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.TokenVersion, u => u.TokenVersion + 1), Ct));
        Assert.Equal(401, (await Host.McpAsync(tokens.Access, "tools/list")).Status);
    }

    [MariaDbFact]
    public async Task ABlockedAccountLosesAccess()
    {
        var super = await Host.AddUserAsync(UserRole.SuperAdmin);
        var tokens = await Host.ConnectAsync(super);
        await Host.WithDbAsync(db => db.Users.Where(u => u.Id == super.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false), Ct));
        Assert.Equal(401, (await Host.McpAsync(tokens.Access, "tools/list")).Status);
    }

    [MariaDbFact]
    public async Task CodesAreSingleUse_PkceBound_AndAReplayEndsTheSession()
    {
        var super = await Host.AddUserAsync(UserRole.SuperAdmin);
        var pending = await Host.RegisterAndBuildRequestAsync();
        var decision = JsonNode.Parse(await (await Host.DecideAsync(pending, super)).Content.ReadAsStringAsync(Ct))!;
        var code = McpTestHost.CodeFrom(decision["redirectUrl"]!.GetValue<string>());
        Dictionary<string, string> Form(string verifier) => new()
        {
            ["grant_type"] = "authorization_code", ["code"] = code, ["client_id"] = pending.ClientId,
            ["redirect_uri"] = McpTestHost.Redirect, ["code_verifier"] = verifier
        };

        var wrongVerifier = await Host.TokenAsync(Form(McpOAuthPolicy.Base64Url(new byte[32])));
        Assert.Contains("PKCE", await wrongVerifier.Content.ReadAsStringAsync(Ct));

        var ok = await Host.TokenAsync(Form(pending.Verifier));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var access = JsonNode.Parse(await ok.Content.ReadAsStringAsync(Ct))!["access_token"]!.GetValue<string>();

        var replay = await Host.TokenAsync(Form(pending.Verifier));
        Assert.Contains("invalid_grant", await replay.Content.ReadAsStringAsync(Ct));
        Assert.Equal(401, (await Host.McpAsync(access, "tools/list")).Status);
    }

    [MariaDbFact]
    public async Task RefreshRotates_AndAReusedRefreshTokenEndsTheSession()
    {
        var super = await Host.AddUserAsync(UserRole.SuperAdmin);
        var tokens = await Host.ConnectAsync(super);

        var first = await Host.TokenAsync(new() { ["grant_type"] = "refresh_token", ["refresh_token"] = tokens.Refresh, ["client_id"] = tokens.ClientId });
        var rotated = JsonNode.Parse(await first.Content.ReadAsStringAsync(Ct))!;
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.NotEqual(tokens.Refresh, rotated["refresh_token"]!.GetValue<string>());
        Assert.Equal(200, (await Host.McpAsync(rotated["access_token"]!.GetValue<string>(), "tools/list")).Status);

        var reuse = await Host.TokenAsync(new() { ["grant_type"] = "refresh_token", ["refresh_token"] = tokens.Refresh });
        Assert.Contains("invalid_grant", await reuse.Content.ReadAsStringAsync(Ct));
        Assert.Equal(401, (await Host.McpAsync(rotated["access_token"]!.GetValue<string>(), "tools/list")).Status);
    }

    // ── Expenses: the panel's validation, the connector's restrictions ──────────────────────────

    private async Task<(int Status, string Body)> PanelCreateAsync(User super, object dto)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/expenses") { Content = McpTestHost.JsonBody(dto) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", McpTestHost.SiteJwt(super));
        var response = await Host.Client.SendAsync(request, Ct);
        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    [MariaDbFact]
    public async Task AddExpenseRefusesExactlyWhatThePanelRefuses_WithTheSameMessage()
    {
        var super = await Host.AddUserAsync(UserRole.SuperAdmin);
        var tokens = await Host.ConnectAsync(super);
        const int supplies = 1, subscriptions = 0;
        var before = await Host.WithDbAsync(db => db.Expenses.CountAsync(Ct));

        var cases = new (object Panel, object Mcp, string Message)[]
        {
            (new { name = "  ", amount = 5, categoryId = supplies, startDate = "2026-10-01" },
             new { name = "  ", amount = 5, category_id = supplies, type = "one_time", start_date = "2026-10-01" },
             "name is required"),
            (new { name = "X", amount = 5, categoryId = 999, startDate = "2026-10-01" },
             new { name = "X", amount = 5, category_id = 999, type = "one_time", start_date = "2026-10-01" },
             "Selected category does not exist."),
            (new { name = "X", amount = 5, categoryId = subscriptions, startDate = "2026-10-01", isRecurring = true },
             new { name = "X", amount = 5, category_id = subscriptions, type = "recurring", start_date = "2026-10-01" },
             "Recurring expenses need a frequency in months > 0."),
            (new { name = "X", amount = 5, categoryId = subscriptions, startDate = "2026-10-01", isRecurring = true, frequencyMonths = 3, prorateByDay = true },
             new { name = "X", amount = 5, category_id = subscriptions, type = "recurring", frequency_months = 3, prorate_by_day = true, start_date = "2026-10-01" },
             "Day-based proration is only available for monthly expenses."),
            (new { name = new string('n', 201), amount = 5, categoryId = supplies, startDate = "2026-10-01" },
             new { name = new string('n', 201), amount = 5, category_id = supplies, type = "one_time", start_date = "2026-10-01" },
             "maximum length of 200"),
            (new { name = "X", amount = 5, categoryId = supplies, startDate = "2026-10-01", notes = new string('n', 1001) },
             new { name = "X", amount = 5, category_id = supplies, type = "one_time", start_date = "2026-10-01", notes = new string('n', 1001) },
             "maximum length of 1000")
        };

        foreach (var (panelDto, mcpArgs, message) in cases)
        {
            var panel = await PanelCreateAsync(super, panelDto);
            Assert.Equal(400, panel.Status);
            var mcp = await Host.CallAsync(tokens.Access, "add_expense", mcpArgs);
            Assert.True(mcp.IsToolError, mcp.Text);
            Assert.Contains(message, panel.Body, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(message, mcp.Text, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal(before, await Host.WithDbAsync(db => db.Expenses.CountAsync(Ct)));
    }

    [MariaDbFact]
    public async Task AValidExpenseIsStoredLikeThePanels_PlusTheClaudeMarker()
    {
        var super = await Host.AddUserAsync(UserRole.SuperAdmin);
        var tokens = await Host.ConnectAsync(super);

        var panel = await PanelCreateAsync(super, new { name = "Cloud host", amount = 49.99, categoryId = 0, startDate = "2026-10-01", isRecurring = true, frequencyMonths = 1, prorateByDay = true, notes = "n" });
        Assert.Equal(201, panel.Status);
        var mcp = await Host.CallAsync(tokens.Access, "add_expense", new
        {
            name = "Cloud host", amount = 49.99, category_id = 0, type = "recurring", frequency_months = 1,
            prorate_by_day = true, start_date = "2026-10-01", notes = "n", confirm_duplicate = true
        });
        Assert.False(mcp.IsToolError, mcp.Text);
        Assert.Equal(1, await Host.WithDbAsync(db => db.Expenses.CountAsync(e => e.Name == "Cloud host", Ct))); // previewed only
        await ApplyAsync(tokens.Access, mcp);

        var rows = await Host.WithDbAsync(db => db.Expenses.Where(e => e.Name == "Cloud host").OrderBy(e => e.Id).ToListAsync(Ct));
        Assert.Equal(2, rows.Count);
        var (p, c) = (rows[0], rows[1]);
        Assert.Equal((p.Name, p.Amount, p.CategoryId, p.StartDate, p.IsRecurring, p.FrequencyMonths, p.ProrateByDay, p.Notes, p.Currency, p.CreatedByUserId),
                     (c.Name, c.Amount, c.CategoryId, c.StartDate, c.IsRecurring, c.FrequencyMonths, c.ProrateByDay, c.Notes, c.Currency, c.CreatedByUserId));
        Assert.Null(p.CreatedVia);
        Assert.Equal(ExpenseOrigins.Claude, c.CreatedVia);
        Assert.NotNull(c.McpGrantId);

        // The audit row records the call without the notes.
        var log = await Host.WithDbAsync(db => db.McpToolCallLogs.OrderByDescending(l => l.Id).FirstAsync(l => l.Tool == "add_expense", Ct));
        Assert.True(log.Succeeded);
        Assert.Equal(super.Id, log.UserId);
        Assert.Contains("[omitted]", log.ArgumentsSummary);
    }

    [MariaDbFact]
    public async Task SalariesAndGoogleAdsAreRefused_AndDuplicatesNeedConfirmation()
    {
        var super = await Host.AddUserAsync(UserRole.SuperAdmin);
        var tokens = await Host.ConnectAsync(super);
        var adsId = await Host.WithDbAsync(async db =>
        {
            var row = new ExpenseCategory { Id = 90, Name = "Google Ads", DisplayOrder = 90 };
            db.ExpenseCategories.Add(row);
            await db.SaveChangesAsync(Ct);
            return row.Id;
        });

        var salary = await Host.CallAsync(tokens.Access, "add_expense", new { name = "Pay", amount = 100, category_id = 4, type = "one_time", start_date = "2026-10-01" });
        Assert.Contains("Salaries can't be added through Claude", salary.Text);
        var ads = await Host.CallAsync(tokens.Access, "add_expense", new { name = "Ads", amount = 100, category_id = adsId, type = "one_time", start_date = "2026-10-01" });
        Assert.Contains("syncs automatically", ads.Text);

        var categories = (await Host.CallAsync(tokens.Access, "list_expense_categories", new { })).Json!["categories"]!.AsArray();
        Assert.DoesNotContain(categories, c => c!["id"]!.GetValue<int>() is 4 or 90);

        var args = new { name = "Gloves", amount = 9.5, category_id = 1, type = "one_time", start_date = "2026-10-02" };
        await ApplyAsync(tokens.Access, await Host.CallAsync(tokens.Access, "add_expense", args));
        var dup = await Host.CallAsync(tokens.Access, "add_expense", args);
        Assert.True(dup.IsToolError);
        Assert.Contains("confirm_duplicate", dup.Text);
        Assert.Equal(1, await Host.WithDbAsync(db => db.Expenses.CountAsync(e => e.Name == "Gloves", Ct)));
    }

    [MariaDbFact]
    public async Task DeleteExpenseOnlyRemovesTheUsersOwnFreshClaudeRows()
    {
        var super = await Host.AddUserAsync(UserRole.SuperAdmin);
        var other = await Host.AddUserAsync(UserRole.SuperAdmin);
        var tokens = await Host.ConnectAsync(super);

        await ApplyAsync(tokens.Access, await Host.CallAsync(tokens.Access, "add_expense", new { name = "Mine", amount = 1, category_id = 1, type = "one_time", start_date = "2026-10-02" }));
        var mine = await Host.WithDbAsync(db => db.Expenses.Where(e => e.Name == "Mine" && e.CreatedByUserId == super.Id).Select(e => e.Id).SingleAsync(Ct));
        var ids = await Host.WithDbAsync(async db =>
        {
            Expense Row(string name, string? via, int by, DateTime at) => new()
            {
                Name = name, Amount = 1, CategoryId = 1, StartDate = new DateTime(2026, 10, 2), CreatedVia = via,
                CreatedByUserId = by, CreatedAt = at, UpdatedAt = at
            };
            var panel = Row("Panel row", null, super.Id, DateTime.UtcNow);
            var theirs = Row("Their Claude row", ExpenseOrigins.Claude, other.Id, DateTime.UtcNow);
            var old = Row("Old Claude row", ExpenseOrigins.Claude, super.Id, DateTime.UtcNow.AddHours(-25));
            db.Expenses.AddRange(panel, theirs, old);
            await db.SaveChangesAsync(Ct);
            return (panel.Id, theirs.Id, old.Id);
        });

        Assert.Contains("not added through Claude", (await Host.CallAsync(tokens.Access, "delete_expense", new { expense_id = ids.Item1 })).Text);
        Assert.Contains("another user", (await Host.CallAsync(tokens.Access, "delete_expense", new { expense_id = ids.Item2 })).Text);
        Assert.Contains("24 hours", (await Host.CallAsync(tokens.Access, "delete_expense", new { expense_id = ids.Item3 })).Text);
        Assert.Contains("no expense", (await Host.CallAsync(tokens.Access, "delete_expense", new { expense_id = 999999 })).Text);

        var deleted = await Host.CallAsync(tokens.Access, "delete_expense", new { expense_id = mine });
        Assert.False(deleted.IsToolError, deleted.Text);
        Assert.Contains(mine, await Host.WithDbAsync(db => db.Expenses.Select(e => e.Id).ToListAsync(Ct))); // previewed only
        await ApplyAsync(tokens.Access, deleted);
        var left = await Host.WithDbAsync(db => db.Expenses.Select(e => e.Id).ToListAsync(Ct));
        Assert.DoesNotContain(mine, left);
        Assert.Contains(ids.Item1, left);
        Assert.Contains(ids.Item2, left);
        Assert.Contains(ids.Item3, left);
    }

    [MariaDbFact]
    public async Task EveryToolCallIsAudited_FailuresIncluded()
    {
        var super = await Host.AddUserAsync(UserRole.SuperAdmin);
        var tokens = await Host.ConnectAsync(super);
        await Host.CallAsync(tokens.Access, "describe_data", new { section = "definitions" });
        await Host.CallAsync(tokens.Access, "delete_expense", new { expense_id = 424242 });

        var logs = await Host.WithDbAsync(db => db.McpToolCallLogs.Where(l => l.UserId == super.Id).OrderBy(l => l.Id).ToListAsync(Ct));
        Assert.Equal(new[] { "describe_data", "delete_expense" }, logs.Select(l => l.Tool));
        Assert.True(logs[0].Succeeded);
        Assert.False(logs[1].Succeeded);
        Assert.Contains("no expense", logs[1].Error);
    }

    /// <summary>Every connector write is a preview; this confirms it.</summary>
    private async Task ApplyAsync(string access, McpTestHost.McpReply preview)
    {
        Assert.False(preview.IsToolError, preview.Text);
        var token = preview.Json!["token"]!.GetValue<string>();
        var applied = await Host.CallAsync(access, "apply_change", new { token });
        Assert.False(applied.IsToolError, applied.Text);
    }

    [MariaDbFact]
    public async Task MaintenancePrunesOldAuditRowsAndDeadTokens()
    {
        await Host.WithDbAsync(async db =>
        {
            db.McpToolCallLogs.Add(new McpToolCallLog { UserId = 1, GrantId = 1, Tool = "old", CreatedAt = DateTime.UtcNow.AddDays(-181) });
            db.McpToolCallLogs.Add(new McpToolCallLog { UserId = 1, GrantId = 1, Tool = "recent", CreatedAt = DateTime.UtcNow.AddDays(-179) });
            await db.SaveChangesAsync(Ct);
            await McpMaintenanceService.RunOnceAsync(db, DateTime.UtcNow, Ct);
            var tools = await db.McpToolCallLogs.Select(l => l.Tool).ToListAsync(Ct);
            Assert.DoesNotContain("old", tools);
            Assert.Contains("recent", tools);
        });
    }
}
