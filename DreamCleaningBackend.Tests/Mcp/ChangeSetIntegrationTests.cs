using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services.ChangeSets;
using DreamCleaningBackend.Tests.Billing;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DreamCleaningBackend.Tests.Mcp;

/// <summary>
/// Preview → apply change sets, the change history captured at the data layer, and Revert
/// (2026-10) — over HTTP, through the Claude connector and the admin panel, on a real MariaDB
/// (set DC_TEST_MARIADB): the dry run, the row locks and the conditional token claim need it.
/// </summary>
public class ChangeSetIntegrationTests : IClassFixture<MariaDbDatabase>, IAsyncLifetime
{
    private readonly MariaDbDatabase _db;
    private McpTestHost? _host;
    private McpTestHost Host => _host!;

    public ChangeSetIntegrationTests(MariaDbDatabase db) => _db = db;

    public async ValueTask InitializeAsync()
    {
        if (_db.Available) _host = await McpTestHost.StartAsync(_db);
    }

    public async ValueTask DisposeAsync()
    {
        if (_host != null) await _host.DisposeAsync();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────

    private sealed record Connected(User User, string Access);

    private async Task<Connected> ConnectSuperAsync()
    {
        var user = await Host.AddUserAsync(UserRole.SuperAdmin);
        return new Connected(user, (await Host.ConnectAsync(user)).Access);
    }

    private async Task<JsonNode> PreviewOkAsync(Connected c, string tool, object args)
    {
        var reply = await Host.CallAsync(c.Access, tool, args);
        Assert.False(reply.IsToolError, reply.Text);
        var json = reply.Json!;
        Assert.True(json["can_apply"]!.GetValue<bool>(), reply.Text);
        return json;
    }

    private async Task<McpTestHost.McpReply> ApplyAsync(Connected c, string token, string? note = null) =>
        await Host.CallAsync(c.Access, "apply_change", note == null ? new { token } : new { token, note });

    private async Task<JsonNode> ApplyOkAsync(Connected c, JsonNode preview, string? note = null)
    {
        var reply = await ApplyAsync(c, preview["token"]!.GetValue<string>(), note);
        Assert.False(reply.IsToolError, reply.Text);
        return reply.Json!;
    }

    private async Task<(int Status, JsonNode? Body)> PanelAsync(User user, HttpMethod method, string url, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (body != null) request.Content = McpTestHost.JsonBody(body);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", McpTestHost.SiteJwt(user));
        var response = await Host.Client.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        return ((int)response.StatusCode, string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text));
    }

    private sealed record Catalogue(int TypeId, int BedroomsId, int SqftId, int LevelsId, int ExtraId, int UniversalExtraId);

    /// <summary>A service type with bedrooms, sq.ft (included amount from bedrooms + a rate tier), levels (self rule), a poll question and its own extra.</summary>
    private async Task<Catalogue> SeedCatalogueAsync() => await Host.WithDbAsync(async db =>
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        var type = new ServiceType { Name = $"Type {tag}", BasePrice = 90, TimeDuration = 120, MinimumPrice = 100, IsActive = true, HasPoll = true, DisplayOrder = 50, CreatedAt = DateTime.UtcNow };
        db.ServiceTypes.Add(type);
        await db.SaveChangesAsync(Ct);
        var bedrooms = new Service { Name = "Bedrooms", ServiceKey = "bedrooms", Cost = 22.5m, TimeDuration = 30, ServiceTypeId = type.Id, IsActive = true, DisplayOrder = 1, CreatedAt = DateTime.UtcNow };
        var sqft = new Service { Name = "Square Feet", ServiceKey = "sqft", Cost = 0.05m, TimeDuration = 1, ServiceTypeId = type.Id, InputType = "slider", IsRangeInput = true, ChargeAboveThreshold = true, IsActive = true, DisplayOrder = 2, CreatedAt = DateTime.UtcNow };
        var levels = new Service { Name = "Levels", ServiceKey = "levels", Cost = 35, TimeDuration = 25, ServiceTypeId = type.Id, MinValue = 1, MaxValue = 4, ChargeAboveThreshold = true, IsActive = true, DisplayOrder = 3, CreatedAt = DateTime.UtcNow };
        db.Services.AddRange(bedrooms, sqft, levels);
        await db.SaveChangesAsync(Ct);
        db.ServiceThresholds.AddRange(
            new ServiceThreshold { ServiceId = sqft.Id, SourceServiceId = bedrooms.Id, SourceQuantity = 2, IncludedQuantity = 850, CreatedAt = DateTime.UtcNow },
            new ServiceThreshold { ServiceId = levels.Id, SourceServiceId = levels.Id, SourceQuantity = 1, IncludedQuantity = 1, CreatedAt = DateTime.UtcNow });
        db.ServiceRateTiers.Add(new ServiceRateTier { ServiceId = sqft.Id, FromQuantity = 2000, Cost = 0.04m, TimeDuration = 1, DisplayOrder = 1, CreatedAt = DateTime.UtcNow });
        db.PollQuestions.Add(new PollQuestion { ServiceTypeId = type.Id, Question = "Pets?", QuestionType = "radio", Options = "[\"Yes\",\"No\"]", IsRequired = true, DisplayOrder = 1, IsActive = true, CreatedAt = DateTime.UtcNow });
        var own = new ExtraService { Name = $"Inside fridge {tag}", Price = 40, Duration = 30, ServiceTypeId = type.Id, IsAvailableForAll = false, IsActive = true, DisplayOrder = 7, CreatedAt = DateTime.UtcNow };
        var universal = new ExtraService { Name = $"Windows {tag}", Price = 25, Duration = 20, IsAvailableForAll = true, IsActive = true, DisplayOrder = 8, CreatedAt = DateTime.UtcNow };
        db.ExtraServices.AddRange(own, universal);
        await db.SaveChangesAsync(Ct);
        return new Catalogue(type.Id, bedrooms.Id, sqft.Id, levels.Id, own.Id, universal.Id);
    });

    /// <summary>Every column of every catalogue row that belongs to the type (and every extra), as JSON.</summary>
    private async Task<List<string>> SnapshotAsync(Catalogue c) => await Host.WithDbAsync(async db =>
    {
        var rows = new List<object>();
        rows.AddRange(await db.ServiceTypes.AsNoTracking().Where(t => t.Id == c.TypeId).ToListAsync(Ct));
        var services = await db.Services.AsNoTracking().Where(s => s.ServiceTypeId == c.TypeId).OrderBy(s => s.Id).ToListAsync(Ct);
        rows.AddRange(services);
        var ids = services.Select(s => s.Id).ToList();
        rows.AddRange(await db.ServiceThresholds.AsNoTracking().Where(t => ids.Contains(t.ServiceId)).OrderBy(t => t.Id).ToListAsync(Ct));
        rows.AddRange(await db.ServiceRateTiers.AsNoTracking().Where(t => ids.Contains(t.ServiceId)).OrderBy(t => t.Id).ToListAsync(Ct));
        rows.AddRange(await db.PollQuestions.AsNoTracking().Where(p => p.ServiceTypeId == c.TypeId).OrderBy(p => p.Id).ToListAsync(Ct));
        rows.AddRange(await db.ExtraServices.AsNoTracking().Where(e => e.Id == c.ExtraId || e.Id == c.UniversalExtraId).OrderBy(e => e.Id).ToListAsync(Ct));
        return rows.Select(r => r.GetType().Name + ":" + AuditCaptureScope.Serialize(AuditCaptureScope.Snapshot(r))).ToList();
    });

    private Task<decimal> CostAsync(int serviceId) =>
        Host.WithDbAsync(db => db.Services.AsNoTracking().Where(s => s.Id == serviceId).Select(s => s.Cost).FirstAsync(Ct));

    private Task<List<AuditLog>> HistoryAsync(string type, long id) =>
        Host.WithDbAsync(db => db.AuditLogs.AsNoTracking().Where(a => a.EntityType == type && a.EntityId == id).OrderBy(a => a.Id).ToListAsync(Ct));

    // ── Preview / apply ─────────────────────────────────────────────────────────────────────────

    [MariaDbFact]
    public async Task APreviewWritesNothing_AndApplyWritesExactlyIt_AsOneLoggedBatch()
    {
        var c = await ConnectSuperAsync();
        var cat = await SeedCatalogueAsync();
        var before = await HistoryAsync(nameof(Service), cat.BedroomsId);

        var preview = await PreviewOkAsync(c, "preview_catalog_changes", new
        {
            operations = new object[]
            {
                new { entity = "service", op = "update", id = cat.BedroomsId, fields = new { cost = 25 } },
                new { entity = "service", op = "update", id = cat.LevelsId, fields = new { cost = 40, time_duration = 30 } }
            }
        });
        var changes = preview["changes"]!.AsArray();
        Assert.Equal(2, changes.Count);
        var bedrooms = changes.Single(x => x!["entity_id"]!.GetValue<long>() == cat.BedroomsId)!;
        Assert.Equal("Cost", bedrooms["fields"]![0]!["field"]!.GetValue<string>());
        Assert.Equal(22.5m, bedrooms["fields"]![0]!["old"]!.GetValue<decimal>());
        Assert.Equal(25m, bedrooms["fields"]![0]!["new"]!.GetValue<decimal>());

        // Nothing happened yet.
        Assert.Equal(22.5m, await CostAsync(cat.BedroomsId));
        Assert.Equal(before.Count, (await HistoryAsync(nameof(Service), cat.BedroomsId)).Count);

        var applied = await ApplyOkAsync(c, preview, "October prices");
        Assert.Equal(2, applied["changes_written"]!.GetValue<int>());
        Assert.Equal(25m, await CostAsync(cat.BedroomsId));
        Assert.Equal(40m, await CostAsync(cat.LevelsId));

        var batch = preview["batch_id"]!.GetValue<string>();
        var rows = await Host.WithDbAsync(db => db.AuditLogs.AsNoTracking().Where(a => a.BatchId == batch).ToListAsync(Ct));
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r =>
        {
            Assert.Equal(AuditSources.Mcp, r.Source);
            Assert.Equal(c.User.Id, r.UserId);
            Assert.Equal("SuperAdmin Tester", r.ActorName);
            Assert.Equal("October prices", r.Note);
            Assert.Equal("Update", r.Action);
        });
    }

    [MariaDbFact]
    public async Task AStaleTokenIsRejected_UsedUp_AndNothingIsWritten()
    {
        var c = await ConnectSuperAsync();
        var cat = await SeedCatalogueAsync();
        var preview = await PreviewOkAsync(c, "preview_catalog_changes", new
        {
            operations = new[] { new { entity = "service", op = "update", id = cat.BedroomsId, fields = new { cost = 26 } } }
        });

        // Somebody changes the same record between the preview and the confirm.
        await Host.WithDbAsync(async db =>
        {
            var s = await db.Services.FirstAsync(x => x.Id == cat.BedroomsId, Ct);
            s.Cost = 27;
            await db.SaveChangesAsync(Ct);
        });

        var token = preview["token"]!.GetValue<string>();
        var stale = await ApplyAsync(c, token);
        Assert.True(stale.IsToolError);
        Assert.Contains("changed after the preview", stale.Text);
        Assert.Contains("used up", stale.Text);
        Assert.Contains("preview again", stale.Text);
        Assert.Equal(27m, await CostAsync(cat.BedroomsId));

        var again = await ApplyAsync(c, token);
        Assert.Contains("already used", again.Text);
        var outcome = await Host.WithDbAsync(db => db.PendingChangeSets.AsNoTracking().Where(p => p.BatchId == preview["batch_id"]!.GetValue<string>()).Select(p => p.Outcome).FirstAsync(Ct));
        Assert.StartsWith("rejected:", outcome);
    }

    [MariaDbFact]
    public async Task TokensAreSingleUse_Expire_AndOnlyWorkForWhoeverPreviewedThem()
    {
        var a = await ConnectSuperAsync();
        var b = await ConnectSuperAsync();
        var cat = await SeedCatalogueAsync();
        object Op(decimal cost) => new { operations = new[] { new { entity = "service", op = "update", id = cat.SqftId, fields = new { cost } } } };

        // Single use.
        var first = await PreviewOkAsync(a, "preview_catalog_changes", Op(0.06m));
        await ApplyOkAsync(a, first);
        Assert.Contains("already used", (await ApplyAsync(a, first["token"]!.GetValue<string>())).Text);

        // Someone else's token, through the connector or the panel, reads as unknown — and does not burn it.
        var mine = await PreviewOkAsync(a, "preview_catalog_changes", Op(0.07m));
        Assert.Contains("not valid here", (await ApplyAsync(b, mine["token"]!.GetValue<string>())).Text);
        var panel = await PanelAsync(a.User, HttpMethod.Post, "/api/admin/audit-logs/revert/apply", new { token = mine["token"]!.GetValue<string>() });
        Assert.Equal(400, panel.Status);
        Assert.Contains("not valid here", panel.Body!["message"]!.GetValue<string>());
        await ApplyOkAsync(a, mine);
        Assert.Equal(0.07m, await CostAsync(cat.SqftId));

        // Expired.
        var late = await PreviewOkAsync(a, "preview_catalog_changes", Op(0.08m));
        await Host.WithDbAsync(db => db.PendingChangeSets.Where(p => p.BatchId == late["batch_id"]!.GetValue<string>())
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.ExpiresAt, DateTime.UtcNow.AddMinutes(-1)), Ct));
        Assert.Contains("expired", (await ApplyAsync(a, late["token"]!.GetValue<string>())).Text);
        Assert.Equal(0.07m, await CostAsync(cat.SqftId));

        // Garbage.
        Assert.Contains("not a change token", (await ApplyAsync(a, "hello")).Text);
    }

    [MariaDbFact]
    public async Task ABatchIsAllOrNothing_AndCanReferToARecordItCreates()
    {
        var c = await ConnectSuperAsync();
        var cat = await SeedCatalogueAsync();

        // The second operation is refused (duplicate key in the same type): the first must not land.
        var refused = await Host.CallAsync(c.Access, "preview_catalog_changes", new
        {
            operations = new object[]
            {
                new { entity = "service", op = "update", id = cat.BedroomsId, fields = new { cost = 99 } },
                new { entity = "service", op = "update", id = cat.LevelsId, fields = new { service_key = "bedrooms" } }
            }
        });
        Assert.True(refused.IsToolError);
        Assert.Contains("Operation 1", refused.Text);
        Assert.Equal(22.5m, await CostAsync(cat.BedroomsId));

        var preview = await PreviewOkAsync(c, "preview_catalog_changes", new
        {
            operations = new object[]
            {
                new { entity = "service", op = "create", fields = new { name = "Bathrooms", service_key = "bathrooms", cost = 22.5, time_duration = 30, service_type_id = cat.TypeId } },
                new { entity = "rate_tier", op = "create", service_id = "@0", fields = new { from_quantity = 0, cost = 20, time_duration = 25, display_order = 1 } }
            }
        });
        Assert.Contains(preview["changes"]!.AsArray(), x => x!["action"]!.GetValue<string>() == "Create" && x["entity_type"]!.GetValue<string>() == "ServiceRateTier");
        await ApplyOkAsync(c, preview);

        var created = await Host.WithDbAsync(db => db.Services.AsNoTracking().Include(s => s.RateTiers).FirstAsync(s => s.ServiceTypeId == cat.TypeId && s.ServiceKey == "bathrooms", Ct));
        Assert.Single(created.RateTiers);
        Assert.Equal(20m, created.RateTiers.Single().Cost);
    }

    // ── Audit capture ───────────────────────────────────────────────────────────────────────────

    [MariaDbFact]
    public async Task HistoryIsCapturedOnceForThePanel_TheConnector_AndBackgroundWork()
    {
        var c = await ConnectSuperAsync();
        var cat = await SeedCatalogueAsync();

        // Admin panel: the Services tab's own PUT.
        var panel = await PanelAsync(c.User, HttpMethod.Put, $"/api/admin/services/{cat.BedroomsId}", new
        {
            name = "Bedrooms", serviceKey = "bedrooms", cost = 23.5, timeDuration = 30, serviceTypeId = cat.TypeId,
            inputType = "dropdown", displayOrder = 1
        });
        Assert.Equal(200, panel.Status);
        var panelRows = (await HistoryAsync(nameof(Service), cat.BedroomsId)).Where(r => r.Action == "Update").ToList();
        var panelRow = Assert.Single(panelRows); // the old explicit audit call no longer writes a second row
        Assert.Equal(AuditSources.Admin, panelRow.Source);
        Assert.Equal(c.User.Id, panelRow.UserId);
        Assert.Equal("SuperAdmin Tester", panelRow.ActorName);
        Assert.Contains("Cost", panelRow.ChangedFields);
        Assert.NotNull(panelRow.BatchId);

        // Connector.
        await ApplyOkAsync(c, await PreviewOkAsync(c, "preview_catalog_changes", new
        {
            operations = new[] { new { entity = "service", op = "update", id = cat.BedroomsId, fields = new { cost = 24.5 } } }
        }));
        var mcpRow = (await HistoryAsync(nameof(Service), cat.BedroomsId)).Last();
        Assert.Equal(AuditSources.Mcp, mcpRow.Source);

        // Background work (no request at all).
        await Host.WithDbAsync(async db =>
        {
            var s = await db.Services.FirstAsync(x => x.Id == cat.BedroomsId, Ct);
            s.Cost = 25.5m;
            await db.SaveChangesAsync(Ct);
        });
        var systemRow = (await HistoryAsync(nameof(Service), cat.BedroomsId)).Last();
        Assert.Equal(AuditSources.System, systemRow.Source);
        Assert.Null(systemRow.UserId);

        // A housekeeping-only touch is not a change.
        var count = (await HistoryAsync(nameof(Service), cat.BedroomsId)).Count;
        await Host.WithDbAsync(async db =>
        {
            var s = await db.Services.FirstAsync(x => x.Id == cat.BedroomsId, Ct);
            s.UpdatedAt = DateTime.UtcNow.AddMinutes(5);
            await db.SaveChangesAsync(Ct);
        });
        Assert.Equal(count, (await HistoryAsync(nameof(Service), cat.BedroomsId)).Count);

        // And the connector can read it back, filtered.
        var history = (await Host.CallAsync(c.Access, "get_change_history", new { entity_type = "service", entity_id = cat.BedroomsId, source = "admin" })).Json!;
        Assert.Equal(1, history["total"]!.GetValue<int>());
        Assert.Equal(panelRow.Id, history["entries"]![0]!["id"]!.GetValue<long>());
    }

    // ── Revert ──────────────────────────────────────────────────────────────────────────────────

    [MariaDbFact]
    public async Task RevertRestoresTheOldValues_AsANewEntry_AndBlocksOnConflictsUntilOverridden()
    {
        var c = await ConnectSuperAsync();
        var cat = await SeedCatalogueAsync();
        Task<decimal> Price() => Host.WithDbAsync(db => db.ExtraServices.AsNoTracking().Where(e => e.Id == cat.ExtraId).Select(e => e.Price).FirstAsync(Ct));
        object Update(decimal price) => new { operations = new[] { new { entity = "extra_service", op = "update", id = cat.ExtraId, fields = new { price } } } };

        await ApplyOkAsync(c, await PreviewOkAsync(c, "preview_catalog_changes", Update(45)));
        var entry = (await HistoryAsync(nameof(ExtraService), cat.ExtraId)).Last();

        var detail = (await Host.CallAsync(c.Access, "get_change_detail", new { entry_id = entry.Id })).Json!;
        var price = detail["diff"]!.AsArray().Single(d => d!["field"]!.GetValue<string>() == "Price")!;
        Assert.True(price["changed"]!.GetValue<bool>());
        Assert.Equal(40m, price["old"]!.GetValue<decimal>());
        Assert.Equal(45m, price["new"]!.GetValue<decimal>());

        var revert = await PreviewOkAsync(c, "preview_revert", new { entry_id = entry.Id });
        Assert.Equal(45m, await Price());
        await ApplyOkAsync(c, revert);
        Assert.Equal(40m, await Price());

        var rows = await HistoryAsync(nameof(ExtraService), cat.ExtraId);
        var revertRow = rows.Last();
        Assert.Equal("Revert", revertRow.Action);
        Assert.Equal(entry.Id, revertRow.RevertOfId);
        var original = rows.Single(r => r.Id == entry.Id);
        Assert.Equal(entry.NewValues, original.NewValues); // history is never edited
        Assert.Null(original.UndoneAt);

        // A later change conflicts.
        await ApplyOkAsync(c, await PreviewOkAsync(c, "preview_catalog_changes", Update(50)));
        var second = (await HistoryAsync(nameof(ExtraService), cat.ExtraId)).Last();
        await Host.WithDbAsync(async db =>
        {
            var e = await db.ExtraServices.FirstAsync(x => x.Id == cat.ExtraId, Ct);
            e.Price = 55;
            await db.SaveChangesAsync(Ct);
        });

        var blocked = (await Host.CallAsync(c.Access, "preview_revert", new { entry_id = second.Id })).Json!;
        Assert.False(blocked["can_apply"]!.GetValue<bool>());
        Assert.Null(blocked["token"]);
        Assert.Contains("override_conflicts", blocked["blocked_reason"]!.GetValue<string>());
        var item = blocked["report"]!["items"]![0]!;
        Assert.Equal("conflict", item["status"]!.GetValue<string>());
        var conflict = item["conflicts"]![0]!;
        Assert.Equal("Price", conflict["field"]!.GetValue<string>());
        Assert.Equal(50m, conflict["expected"]!.GetValue<decimal>());
        Assert.Equal(55m, conflict["current"]!.GetValue<decimal>());
        Assert.Equal(40m, conflict["restore"]!.GetValue<decimal>());
        Assert.Equal(55m, await Price());

        var forced = await PreviewOkAsync(c, "preview_revert", new { entry_id = second.Id, override_conflicts = true });
        await ApplyOkAsync(c, forced, "owner asked");
        Assert.Equal(40m, await Price());
        var forcedRow = (await HistoryAsync(nameof(ExtraService), cat.ExtraId)).Last();
        Assert.Equal("Revert", forcedRow.Action);
        Assert.StartsWith(RevertRunner.OverrideNotePrefix, forcedRow.Note);
        Assert.EndsWith("owner asked", forcedRow.Note);
    }

    [MariaDbFact]
    public async Task DeletingAServiceType_ThenRevertingTheBatch_RestoresTheIdenticalState()
    {
        var super = await Host.AddUserAsync(UserRole.SuperAdmin);
        var cat = await SeedCatalogueAsync();
        var before = await SnapshotAsync(cat);

        var deleted = await PanelAsync(super, HttpMethod.Delete, $"/api/admin/service-types/{cat.TypeId}");
        Assert.Equal(200, deleted.Status);
        Assert.Equal(0, await Host.WithDbAsync(db => db.Services.CountAsync(s => s.ServiceTypeId == cat.TypeId, Ct)));
        Assert.Null(await Host.WithDbAsync(db => db.ExtraServices.AsNoTracking().Where(e => e.Id == cat.ExtraId).Select(e => e.ServiceTypeId).FirstAsync(Ct)));

        var typeRow = (await HistoryAsync(nameof(ServiceType), cat.TypeId)).Single(r => r.Action == "Delete");
        var batch = await Host.WithDbAsync(db => db.AuditLogs.AsNoTracking().Where(a => a.BatchId == typeRow.BatchId).ToListAsync(Ct));
        // The type, 3 services, 2 included amounts, 1 rate tier, 1 poll question deleted; 1 extra detached.
        Assert.Equal(9, batch.Count);
        Assert.Equal(8, batch.Count(r => r.Action == "Delete"));
        Assert.Contains(batch, r => r.EntityType == nameof(ExtraService) && r.Action == "Update");
        Assert.All(batch, r => Assert.Equal(AuditSources.Admin, r.Source));

        // One batch revert, from the panel, brings all of it back exactly as it was.
        var preview = await PanelAsync(super, HttpMethod.Post, "/api/admin/audit-logs/revert/preview", new { batchId = typeRow.BatchId });
        Assert.Equal(200, preview.Status);
        Assert.True(preview.Body!["canApply"]!.GetValue<bool>(), preview.Body.ToJsonString());
        var applied = await PanelAsync(super, HttpMethod.Post, "/api/admin/audit-logs/revert/apply", new { token = preview.Body["token"]!.GetValue<string>() });
        Assert.Equal(200, applied.Status);

        Assert.Equal(before, await SnapshotAsync(cat));
        var reverts = await Host.WithDbAsync(db => db.AuditLogs.AsNoTracking().Where(a => a.BatchId == preview.Body["batchId"]!.GetValue<string>()).ToListAsync(Ct));
        Assert.Equal(9, reverts.Count);
        Assert.All(reverts, r => Assert.Equal("Revert", r.Action));
        Assert.All(reverts, r => Assert.Contains(batch, b => b.Id == r.RevertOfId));
    }

    [MariaDbFact]
    public async Task RevertingACreateThatOrdersUse_IsRefused_AndOffersDeactivating()
    {
        var c = await ConnectSuperAsync();
        var cat = await SeedCatalogueAsync();
        await ApplyOkAsync(c, await PreviewOkAsync(c, "preview_catalog_changes", new
        {
            operations = new[] { new { entity = "extra_service", op = "create", fields = new { name = "Balcony " + Guid.NewGuid().ToString("N")[..6], price = 30, duration = 20, is_available_for_all = true } } }
        }));
        var created = await Host.WithDbAsync(db => db.AuditLogs.AsNoTracking().Where(a => a.EntityType == nameof(ExtraService) && a.Action == "Create" && a.Source == AuditSources.Mcp)
            .OrderByDescending(a => a.Id).FirstAsync(Ct));

        await Host.WithDbAsync(async db =>
        {
            var customer = new User { FirstName = "C", LastName = "U", Email = $"c.{Guid.NewGuid():N}@example.com", Phone = "2125550111", Role = UserRole.Customer, IsActive = true, CreatedAt = DateTime.UtcNow };
            db.Users.Add(customer);
            await db.SaveChangesAsync(Ct);
            var order = NewOrder(customer.Id, cat.TypeId);
            db.Orders.Add(order);
            await db.SaveChangesAsync(Ct);
            db.OrderExtraServices.Add(new OrderExtraService { OrderId = order.Id, ExtraServiceId = (int)created.EntityId, Quantity = 1, Cost = 30, Duration = 20, CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync(Ct);
        });

        var blocked = (await Host.CallAsync(c.Access, "preview_revert", new { entry_id = created.Id })).Json!;
        Assert.False(blocked["can_apply"]!.GetValue<bool>());
        Assert.Contains("use_deactivate_alternative", blocked["blocked_reason"]!.GetValue<string>());
        var item = blocked["report"]!["items"]![0]!;
        Assert.Equal("blocked", item["status"]!.GetValue<string>());
        Assert.Equal("deactivate", item["alternative"]!.GetValue<string>());

        var deactivate = await PreviewOkAsync(c, "preview_revert", new { entry_id = created.Id, use_deactivate_alternative = true });
        await ApplyOkAsync(c, deactivate);
        var row = await Host.WithDbAsync(db => db.ExtraServices.AsNoTracking().FirstAsync(e => e.Id == created.EntityId, Ct));
        Assert.False(row.IsActive);
        Assert.Equal("Revert", (await HistoryAsync(nameof(ExtraService), created.EntityId)).Last().Action);
    }

    [MariaDbFact]
    public async Task ExpenseReverts_FollowTheConnectorsExpenseRules_WhileThePanelKeepsFullAccess()
    {
        var c = await ConnectSuperAsync();

        // Written by a background job: source system.
        var synced = await Host.WithDbAsync(async db =>
        {
            var e = new Expense { Name = "Synced row", Amount = 12, CategoryId = 1, StartDate = new DateTime(2026, 10, 3), CreatedByUserId = c.User.Id, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            db.Expenses.Add(e);
            await db.SaveChangesAsync(Ct);
            return e.Id;
        });
        // A salary entered in the panel.
        var salary = await Host.WithDbAsync(async db =>
        {
            db.AuditWrite.Source = AuditSources.Admin;
            db.AuditWrite.ActorUserId = c.User.Id;
            var e = new Expense { Name = "Salary", Amount = 900, CategoryId = 4, StartDate = new DateTime(2026, 10, 3), CreatedByUserId = c.User.Id, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            db.Expenses.Add(e);
            await db.SaveChangesAsync(Ct);
            return e.Id;
        });
        var syncedEntry = (await HistoryAsync(nameof(Expense), synced)).Single();
        var salaryEntry = (await HistoryAsync(nameof(Expense), salary)).Single();
        Assert.Equal(AuditSources.System, syncedEntry.Source);

        var system = (await Host.CallAsync(c.Access, "preview_revert", new { entry_id = syncedEntry.Id })).Json!;
        Assert.False(system["can_apply"]!.GetValue<bool>());
        Assert.Contains("background job", system["blocked_reason"]!.GetValue<string>());
        var pay = (await Host.CallAsync(c.Access, "preview_revert", new { entry_id = salaryEntry.Id })).Json!;
        Assert.False(pay["can_apply"]!.GetValue<bool>());
        Assert.Contains("Salaries", pay["blocked_reason"]!.GetValue<string>());

        // The panel may revert both (a revert of a create removes the row).
        foreach (var entry in new[] { syncedEntry, salaryEntry })
        {
            var preview = await PanelAsync(c.User, HttpMethod.Post, "/api/admin/audit-logs/revert/preview", new { entryId = entry.Id });
            Assert.True(preview.Body!["canApply"]!.GetValue<bool>(), preview.Body.ToJsonString());
            var applied = await PanelAsync(c.User, HttpMethod.Post, "/api/admin/audit-logs/revert/apply", new { token = preview.Body["token"]!.GetValue<string>() });
            Assert.Equal(200, applied.Status);
        }
        Assert.Equal(0, await Host.WithDbAsync(db => db.Expenses.CountAsync(e => e.Id == synced || e.Id == salary, Ct)));
    }

    [MariaDbFact]
    public async Task AddAndDeleteExpense_PreviewFirst_ThenApply()
    {
        var c = await ConnectSuperAsync();
        var preview = await PreviewOkAsync(c, "add_expense", new { name = "Mop heads", amount = 18.4, category_id = 1, type = "one_time", start_date = "2026-10-05" });
        Assert.Equal(0, await Host.WithDbAsync(db => db.Expenses.CountAsync(e => e.Name == "Mop heads", Ct)));
        Assert.Equal("Create", preview["changes"]![0]!["action"]!.GetValue<string>());

        await ApplyOkAsync(c, preview);
        var id = await Host.WithDbAsync(db => db.Expenses.Where(e => e.Name == "Mop heads").Select(e => e.Id).SingleAsync(Ct));

        var delete = await PreviewOkAsync(c, "delete_expense", new { expense_id = id });
        Assert.Equal(1, await Host.WithDbAsync(db => db.Expenses.CountAsync(e => e.Id == id, Ct)));
        await ApplyOkAsync(c, delete);
        Assert.Equal(0, await Host.WithDbAsync(db => db.Expenses.CountAsync(e => e.Id == id, Ct)));

        // The delete keeps a full snapshot, so it can be restored.
        var gone = (await HistoryAsync(nameof(Expense), id)).Last();
        Assert.Equal("Delete", gone.Action);
        Assert.Contains("Mop heads", gone.OldValues);
    }

    // ── Read tools ──────────────────────────────────────────────────────────────────────────────

    [MariaDbFact]
    public async Task TheCatalogueReadShowsEveryRowWithWhatUsesIt()
    {
        var c = await ConnectSuperAsync();
        var cat = await SeedCatalogueAsync();
        await Host.WithDbAsync(async db =>
        {
            var customer = new User { FirstName = "C", LastName = "U", Email = $"c.{Guid.NewGuid():N}@example.com", Phone = "2125550112", Role = UserRole.Customer, IsActive = true, CreatedAt = DateTime.UtcNow };
            db.Users.Add(customer);
            await db.SaveChangesAsync(Ct);
            var past = NewOrder(customer.Id, cat.TypeId, DateTime.UtcNow.Date.AddDays(-20), "Done");
            var future = NewOrder(customer.Id, cat.TypeId, DateTime.UtcNow.Date.AddDays(10), "Active");
            db.Orders.AddRange(past, future);
            await db.SaveChangesAsync(Ct);
            db.OrderServices.AddRange(
                new OrderService { OrderId = past.Id, ServiceId = cat.BedroomsId, Quantity = 2, Cost = 45, Duration = 60, CreatedAt = DateTime.UtcNow },
                new OrderService { OrderId = future.Id, ServiceId = cat.BedroomsId, Quantity = 3, Cost = 67.5m, Duration = 90, CreatedAt = DateTime.UtcNow });
            db.RecurringOrderSeries.Add(new RecurringOrderSeries { UserId = customer.Id, TemplateOrderId = future.Id, AnchorDate = future.ServiceDate, ServiceTime = TimeSpan.FromHours(10), IsActive = true, CreatedByUserId = customer.Id });
            await db.SaveChangesAsync(Ct);
        });

        var json = (await Host.CallAsync(c.Access, "get_services_catalog", new { })).Json!;
        var type = json["service_types"]!.AsArray().Single(t => t!["id"]!.GetValue<int>() == cat.TypeId)!;
        Assert.Equal(2, type["usage"]!["orders"]!.GetValue<int>());
        Assert.Equal(1, type["usage"]!["future_orders"]!.GetValue<int>());
        Assert.Equal(1, type["usage"]!["active_recurring_plans"]!.GetValue<int>());
        var bedrooms = type["services"]!.AsArray().Single(s => s!["id"]!.GetValue<int>() == cat.BedroomsId)!;
        Assert.Equal(22.5m, bedrooms["cost"]!.GetValue<decimal>());
        Assert.Equal(2, bedrooms["usage"]!["orders"]!.GetValue<int>());
        Assert.Equal(1, bedrooms["usage"]!["future_orders"]!.GetValue<int>());
        Assert.Equal(1, bedrooms["usage"]!["active_recurring_plans"]!.GetValue<int>());
        var sqft = type["services"]!.AsArray().Single(s => s!["id"]!.GetValue<int>() == cat.SqftId)!;
        Assert.Single(sqft["included_amounts"]!.AsArray());
        Assert.Single(sqft["rate_tiers"]!.AsArray());
        Assert.Single(type["poll_questions"]!.AsArray());
        Assert.Contains(cat.ExtraId, type["extra_service_ids"]!.AsArray().Select(x => x!.GetValue<int>()));
        Assert.NotNull(json["subscriptions"]);
        Assert.Contains("future_orders", json["usage_definitions"]!.GetValue<string>());
    }

    private static Order NewOrder(int userId, int serviceTypeId, DateTime? serviceDate = null, string status = "Pending") => new()
    {
        UserId = userId,
        ServiceTypeId = serviceTypeId,
        OrderDate = DateTime.UtcNow,
        ServiceDate = serviceDate ?? DateTime.UtcNow.Date.AddDays(3),
        ServiceTime = TimeSpan.FromHours(10),
        ContactFirstName = "Test",
        ContactLastName = "Customer",
        ContactEmail = "contact@example.com",
        ContactPhone = "2125550100",
        ServiceAddress = "1 Test St",
        City = "Manhattan",
        State = "New York",
        ZipCode = "10001",
        SubTotal = 100,
        Tax = 8.88m,
        Total = 108.88m,
        Status = status,
        PaymentMethod = DreamCleaningBackend.Models.PaymentMethod.Normal,
        TotalDuration = 180,
        CreatedAt = DateTime.UtcNow
    };
}
