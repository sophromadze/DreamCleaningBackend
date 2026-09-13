using DreamCleaningBackend.Data;
using DreamCleaningBackend.Models;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DreamCleaningBackend.Services;

/// <summary>Sanitized read copies. The persisted restore snapshots are never handed to the browser.</summary>
public static class AuditDisplayProjection
{
    public record Values(string? OldValues, string? NewValues);

    public static async Task<Dictionary<long, Values>> BuildAsync(ApplicationDbContext db, IEnumerable<AuditLog> logs)
    {
        var rows = logs.Select(log => (Log: log, Old: Parse(log.OldValues), New: Parse(log.NewValues))).ToList();
        var objects = rows.SelectMany(r => new[] { r.Old, r.New }).OfType<JObject>().ToList();
        var ids = objects.SelectMany(o => o.Properties()).Where(p => p.Name.EndsWith("Id") && p.Value.Type == JTokenType.Integer)
            .Select(p => (int)p.Value).Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id))
            .Select(u => new { u.Id, Name = (u.FirstName + " " + u.LastName).Trim() }).ToDictionaryAsync(u => u.Id, u => u.Name);
        var services = await db.ServiceTypes.AsNoTracking().Where(s => ids.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => s.Name);
        var clients = await db.ContractClients.AsNoTracking().Where(c => ids.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.LegalEntityName);
        var leads = await db.Leads.AsNoTracking().Where(l => ids.Contains(l.Id))
            .Select(l => new { l.Id, Name = (l.FirstName + " " + l.LastName).Trim() }).ToDictionaryAsync(l => l.Id, l => l.Name);
        var apartments = await db.Apartments.AsNoTracking().Where(a => ids.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, a => a.Name);
        var cleaners = await db.Cleaners.AsNoTracking().Where(c => ids.Contains(c.Id))
            .Select(c => new { c.Id, Name = (c.FirstName + " " + c.LastName).Trim() }).ToDictionaryAsync(c => c.Id, c => c.Name);
        foreach (var obj in objects)
        {
            foreach (var p in obj.Properties().ToList())
            {
                if (p.Value.Type != JTokenType.Integer || !p.Name.EndsWith("Id")) continue;
                var id = (int)p.Value;
                string? name = null;
                if (p.Name is "UserId" or "CustomerUserId" or "TargetUserId" or "AdminId" or "AssignedAdminId"
                    or "CreatedByAdminId" or "CreatedByUserId" or "BookedByAdminUserId" or "PaidByUserId"
                    or "UpdatedByUserId" or "RequestedByUserId" or "HiddenByUserId" or "AssignedToAdminId" or "ClientId") users.TryGetValue(id, out name);
                else if (p.Name == "ServiceTypeId") services.TryGetValue(id, out name);
                else if (p.Name == "ContractClientId") clients.TryGetValue(id, out name);
                else if (p.Name == "LeadId") leads.TryGetValue(id, out name);
                else if (p.Name == "ApartmentId") apartments.TryGetValue(id, out name);
                else if (p.Name == "CleanerId") cleaners.TryGetValue(id, out name);
                if (!string.IsNullOrWhiteSpace(name)) p.Value = $"{name} (#{id})";
            }
        }
        return rows.ToDictionary(r => r.Log.Id, r => new Values(r.Old?.ToString(Formatting.None), r.New?.ToString(Formatting.None)));
    }

    private static JToken? Parse(string? json)
    {
        var safe = AuditDataPolicy.SanitizeJson(json);
        return string.IsNullOrWhiteSpace(safe) ? null : JToken.Parse(safe);
    }
}
