using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text.Json;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DreamCleaningBackend.Services.ChangeSets
{
    /// <summary>
    /// Reads what a <see cref="CatalogAdminOperations"/> method answered — the same action result
    /// the admin panel receives — as success (with its value) or a refusal message.
    /// </summary>
    public static class CatalogOperationResult
    {
        public static object? Read<T>(ActionResult<T> result, string what) =>
            result.Result == null ? result.Value : Read(result.Result, what);

        public static object? Read(IActionResult result, string what)
        {
            switch (result)
            {
                case OkResult:
                    return null;
                case OkObjectResult ok:
                    return ok.Value;
                case NotFoundResult:
                    throw new ChangeSetException($"{what}: not found.");
                case ObjectResult obj when (obj.StatusCode ?? 200) >= 400:
                    throw new ChangeSetException($"{what}: {MessageOf(obj.Value) ?? "refused"}");
                case ObjectResult obj:
                    return obj.Value;
                case StatusCodeResult code when code.StatusCode >= 400:
                    throw new ChangeSetException($"{what}: refused ({code.StatusCode}).");
                default:
                    return null;
            }
        }

        private static string? MessageOf(object? value)
        {
            if (value == null) return null;
            if (value is string s) return s;
            var element = JsonSerializer.SerializeToElement(value);
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var name in new[] { "message", "Message" })
                    if (element.TryGetProperty(name, out var m) && m.ValueKind == JsonValueKind.String) return m.GetString();
            }
            return element.ToString();
        }
    }

    /// <summary>
    /// The Claude connector's Services-tab writes (2026-10): a list of operations run in order,
    /// each through the very <see cref="CatalogAdminOperations"/> method the admin panel's button
    /// calls, with the same DTO (and its data annotations, which MVC checks for the panel).
    ///
    /// An update names only the fields that change: the rest are filled from the record's current
    /// values, exactly as the panel's edit form posts them. A later operation can point at a record
    /// an earlier one creates with <c>"@&lt;index&gt;"</c> (e.g. <c>"service_type_id": "@0"</c>).
    /// The whole list is one change set: all of it is applied or none of it.
    /// </summary>
    public sealed class CatalogChangeRunner : IChangeSetRunner
    {
        public const int MaxOperations = 50;

        public string Kind => ChangeSetKinds.Catalog;

        public static readonly IReadOnlyDictionary<string, string[]> OperationsByEntity = new Dictionary<string, string[]>
        {
            ["service_type"] = new[] { "create", "update", "activate", "deactivate", "delete" },
            ["service"] = new[] { "create", "update", "copy", "activate", "deactivate", "delete" },
            ["included_amount"] = new[] { "create", "update", "delete" },
            ["rate_tier"] = new[] { "create", "update", "delete" },
            ["extra_service"] = new[] { "create", "update", "copy", "activate", "deactivate", "delete" },
            ["subscription"] = new[] { "create", "update", "activate", "deactivate", "delete" },
            ["poll_question"] = new[] { "create", "update", "delete" },
            ["pricing_configuration"] = new[] { "import" }
        };

        public async Task<ChangeRunResult> RunAsync(ChangeRunContext context, CancellationToken ct)
        {
            if (context.Request.ValueKind != JsonValueKind.Object
                || !context.Request.TryGetProperty("operations", out var list) || list.ValueKind != JsonValueKind.Array)
                throw new ChangeSetException("operations is required: a list of {entity, op, id?, fields?}.");
            var count = list.GetArrayLength();
            if (count == 0) throw new ChangeSetException("operations is empty.");
            if (count > MaxOperations) throw new ChangeSetException($"At most {MaxOperations} operations per change.");

            var ops = context.Services.GetRequiredService<CatalogAdminOperations>();
            var created = new List<int?>();
            var results = new List<object?>();
            var index = 0;
            foreach (var op in list.EnumerateArray())
            {
                var outcome = await RunOneAsync(context, ops, op, index, created, ct);
                created.Add(outcome.CreatedId);
                results.Add(new { index, entity = outcome.Entity, op = outcome.Op, id = outcome.CreatedId ?? outcome.Id });
                index++;
            }

            var result = new ChangeRunResult { Result = new { operations = results } };
            if (context.IsPreview && created.Any(id => id != null))
                result.Warnings.Add("Ids of records to be created are assigned when the change is applied.");
            return result;
        }

        private sealed record OneOutcome(string Entity, string Op, int? Id, int? CreatedId);

        private static async Task<OneOutcome> RunOneAsync(ChangeRunContext context, CatalogAdminOperations ops, JsonElement op,
            int index, List<int?> created, CancellationToken ct)
        {
            if (op.ValueKind != JsonValueKind.Object) throw new ChangeSetException($"Operation {index}: must be an object.");
            var entity = Str(op, "entity")?.Trim().ToLowerInvariant() ?? throw new ChangeSetException($"Operation {index}: entity is required.");
            var verb = Str(op, "op")?.Trim().ToLowerInvariant() ?? throw new ChangeSetException($"Operation {index}: op is required.");
            if (!OperationsByEntity.TryGetValue(entity, out var allowed))
                throw new ChangeSetException($"Operation {index}: unknown entity '{entity}'. Entities: {string.Join(", ", OperationsByEntity.Keys)}.");
            if (!allowed.Contains(verb))
                throw new ChangeSetException($"Operation {index}: {entity} supports {string.Join(", ", allowed)}, not '{verb}'.");

            var fields = op.TryGetProperty("fields", out var f) && f.ValueKind == JsonValueKind.Object ? f : default;
            int RequireId() => Ref(op, "id", created, index) ?? throw new ChangeSetException($"Operation {index}: id is required for {verb}.");
            var what = $"Operation {index} ({verb} {entity})";
            var db = context.Db;

            switch (entity, verb)
            {
                // ── Service types ────────────────────────────────────────────────────────────
                case ("service_type", "create"):
                    return Created(entity, verb, CatalogOperationResult.Read(await ops.CreateServiceType(Build<CreateServiceTypeDto>(fields, created, index)), what));
                case ("service_type", "update"):
                {
                    var id = RequireId();
                    var current = await db.ServiceTypes.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw NotFound(what, id);
                    CatalogOperationResult.Read(await ops.UpdateServiceType(id, Merge<UpdateServiceTypeDto>(current, fields, created, index)), what);
                    return new(entity, verb, id, null);
                }
                case ("service_type", "activate"):
                    return await DoneAsync(entity, verb, RequireId(), id => ops.ActivateServiceType(id), what);
                case ("service_type", "deactivate"):
                    return await DoneAsync(entity, verb, RequireId(), id => ops.DeactivateServiceType(id), what);
                case ("service_type", "delete"):
                    return await DoneAsync(entity, verb, RequireId(), id => ops.DeleteServiceType(id), what);

                // ── Services (the home lines: bedrooms, bathrooms, sq.ft, cleaners, hours, levels…) ──
                case ("service", "create"):
                    return Created(entity, verb, CatalogOperationResult.Read(await ops.CreateService(Build<CreateServiceDto>(fields, created, index)), what));
                case ("service", "copy"):
                    return Created(entity, verb, CatalogOperationResult.Read(await ops.CopyService(new CopyServiceDto
                    {
                        SourceServiceId = RequireId(),
                        TargetServiceTypeId = Ref(op, "target_service_type_id", created, index)
                            ?? throw new ChangeSetException($"{what}: target_service_type_id is required.")
                    }), what));
                case ("service", "update"):
                {
                    var id = RequireId();
                    var current = await db.Services.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw NotFound(what, id);
                    CatalogOperationResult.Read(await ops.UpdateService(id, Merge<UpdateServiceDto>(current, fields, created, index)), what);
                    return new(entity, verb, id, null);
                }
                case ("service", "activate"):
                    return await DoneAsync(entity, verb, RequireId(), id => ops.ActivateService(id), what);
                case ("service", "deactivate"):
                    return await DoneAsync(entity, verb, RequireId(), id => ops.DeactivateService(id), what);
                case ("service", "delete"):
                    return await DoneAsync(entity, verb, RequireId(), id => ops.DeleteService(id), what);

                // ── Included amounts (ServiceThreshold) ──────────────────────────────────────
                case ("included_amount", "create"):
                {
                    var serviceId = Ref(op, "service_id", created, index) ?? throw new ChangeSetException($"{what}: service_id is required.");
                    return Created(entity, verb, CatalogOperationResult.Read(
                        await ops.CreateServiceThreshold(serviceId, Build<SaveServiceThresholdDto>(fields, created, index)), what));
                }
                case ("included_amount", "update"):
                {
                    var id = RequireId();
                    var current = await db.ServiceThresholds.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw NotFound(what, id);
                    CatalogOperationResult.Read(await ops.UpdateServiceThreshold(current.ServiceId, id,
                        Merge<SaveServiceThresholdDto>(current, fields, created, index)), what);
                    return new(entity, verb, id, null);
                }
                case ("included_amount", "delete"):
                {
                    var id = RequireId();
                    var current = await db.ServiceThresholds.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw NotFound(what, id);
                    CatalogOperationResult.Read(await ops.DeleteServiceThreshold(current.ServiceId, id), what);
                    return new(entity, verb, id, null);
                }

                // ── Rate tiers ───────────────────────────────────────────────────────────────
                case ("rate_tier", "create"):
                {
                    var serviceId = Ref(op, "service_id", created, index) ?? throw new ChangeSetException($"{what}: service_id is required.");
                    return Created(entity, verb, CatalogOperationResult.Read(
                        await ops.CreateServiceRateTier(serviceId, Build<SaveServiceRateTierDto>(fields, created, index)), what));
                }
                case ("rate_tier", "update"):
                {
                    var id = RequireId();
                    var current = await db.ServiceRateTiers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw NotFound(what, id);
                    CatalogOperationResult.Read(await ops.UpdateServiceRateTier(current.ServiceId, id,
                        Merge<SaveServiceRateTierDto>(current, fields, created, index)), what);
                    return new(entity, verb, id, null);
                }
                case ("rate_tier", "delete"):
                {
                    var id = RequireId();
                    var current = await db.ServiceRateTiers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw NotFound(what, id);
                    CatalogOperationResult.Read(await ops.DeleteServiceRateTier(current.ServiceId, id), what);
                    return new(entity, verb, id, null);
                }

                // ── Extra services (supplies, deep cleaning, same day, windows…) ─────────────
                case ("extra_service", "create"):
                    return Created(entity, verb, CatalogOperationResult.Read(await ops.CreateExtraService(Build<CreateExtraServiceDto>(fields, created, index)), what));
                case ("extra_service", "copy"):
                    return Created(entity, verb, CatalogOperationResult.Read(await ops.CopyExtraService(new CopyExtraServiceDto
                    {
                        SourceExtraServiceId = RequireId(),
                        TargetServiceTypeId = Ref(op, "target_service_type_id", created, index)
                            ?? throw new ChangeSetException($"{what}: target_service_type_id is required.")
                    }), what));
                case ("extra_service", "update"):
                {
                    var id = RequireId();
                    var current = await db.ExtraServices.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw NotFound(what, id);
                    CatalogOperationResult.Read(await ops.UpdateExtraService(id, Merge<UpdateExtraServiceDto>(current, fields, created, index)), what);
                    return new(entity, verb, id, null);
                }
                case ("extra_service", "activate"):
                    return await DoneAsync(entity, verb, RequireId(), id => ops.ActivateExtraService(id), what);
                case ("extra_service", "deactivate"):
                    return await DoneAsync(entity, verb, RequireId(), id => ops.DeactivateExtraService(id), what);
                case ("extra_service", "delete"):
                    return await DoneAsync(entity, verb, RequireId(), id => ops.DeleteExtraService(id), what);

                // ── Subscription plans (frequencies and their discounts) ─────────────────────
                case ("subscription", "create"):
                    return Created(entity, verb, CatalogOperationResult.Read(await ops.CreateSubscription(Build<CreateSubscriptionDto>(fields, created, index)), what));
                case ("subscription", "update"):
                {
                    var id = RequireId();
                    var current = await db.Subscriptions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw NotFound(what, id);
                    CatalogOperationResult.Read(await ops.UpdateSubscription(id, Merge<UpdateSubscriptionDto>(current, fields, created, index)), what);
                    return new(entity, verb, id, null);
                }
                case ("subscription", "activate"):
                    return await DoneAsync(entity, verb, RequireId(), id => ops.ActivateSubscription(id), what);
                case ("subscription", "deactivate"):
                    return await DoneAsync(entity, verb, RequireId(), id => ops.DeactivateSubscription(id), what);
                case ("subscription", "delete"):
                    return await DoneAsync(entity, verb, RequireId(), id => ops.DeleteSubscription(id), what);

                // ── Poll questions ───────────────────────────────────────────────────────────
                case ("poll_question", "create"):
                    return Created(entity, verb, CatalogOperationResult.Read(await ops.CreatePollQuestion(Build<CreatePollQuestionDto>(fields, created, index)), what));
                case ("poll_question", "update"):
                {
                    var id = RequireId();
                    var current = await db.PollQuestions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw NotFound(what, id);
                    CatalogOperationResult.Read(await ops.UpdatePollQuestion(id, Merge<PollQuestionDto>(current, fields, created, index)), what);
                    return new(entity, verb, id, null);
                }
                case ("poll_question", "delete"):
                    return await DoneAsync(entity, verb, RequireId(), id => ops.DeletePollQuestion(id), what);

                // ── Pricing configuration import (the Services tab's Import button) ──────────
                case ("pricing_configuration", "import"):
                {
                    if (!op.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
                        throw new ChangeSetException($"{what}: payload is required (the exported configuration JSON).");
                    var dto = payload.Deserialize<PricingConfigurationDto>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                        ?? throw new ChangeSetException($"{what}: payload could not be read.");
                    var pricing = context.Services.GetRequiredService<IPricingConfigurationService>();
                    var result = await pricing.ApplyAsync(dto, context.Caller.UserId);
                    if (!result.Success) throw new ChangeSetException($"{what}: {result.Message}");
                    return new(entity, verb, null, null);
                }
            }

            throw new ChangeSetException($"{what}: not supported.");
        }

        // ── Helpers ─────────────────────────────────────────────────────────────────────────

        private static async Task<OneOutcome> DoneAsync(string entity, string verb, int id, Func<int, Task<ActionResult>> call, string what)
        {
            CatalogOperationResult.Read(await call(id), what);
            return new(entity, verb, id, null);
        }

        private static OneOutcome Created(string entity, string verb, object? value)
        {
            var id = value?.GetType().GetProperty("Id")?.GetValue(value) as int?;
            return new(entity, verb, null, id);
        }

        private static ChangeSetException NotFound(string what, int id) => new($"{what}: there is no record with id {id}.");

        /// <summary>A number, or "@n" for the record operation n created.</summary>
        private static int? Ref(JsonElement op, string name, List<int?> created, int index)
        {
            if (!op.TryGetProperty(name, out var v)) return null;
            return ResolveRef(v, name, created, index);
        }

        private static int? ResolveRef(JsonElement v, string name, List<int?> created, int index)
        {
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
            if (v.ValueKind == JsonValueKind.String && v.GetString() is { } s && s.StartsWith('@')
                && int.TryParse(s[1..], out var at))
            {
                if (at < 0 || at >= index) throw new ChangeSetException($"Operation {index}: {name} \"{s}\" must point at an EARLIER operation.");
                return created[at] ?? throw new ChangeSetException($"Operation {index}: operation {at} does not create a record, so \"{s}\" means nothing.");
            }
            if (v.ValueKind == JsonValueKind.Null) return null;
            throw new ChangeSetException($"Operation {index}: {name} must be a number or \"@<operation index>\".");
        }

        /// <summary>A create DTO from the given fields alone (the DTO's own defaults for the rest).</summary>
        private static T Build<T>(JsonElement fields, List<int?> created, int index) where T : new()
        {
            var dto = new T();
            Overlay(dto, fields, created, index);
            Validate(dto, index);
            return dto;
        }

        /// <summary>An update DTO: the record's current values, then the given fields on top.</summary>
        private static T Merge<T>(object current, JsonElement fields, List<int?> created, int index) where T : new()
        {
            var dto = new T();
            var source = current.GetType();
            foreach (var p in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite && p.GetSetMethod() != null))
            {
                var from = source.GetProperty(p.Name);
                if (from == null || !from.CanRead) continue;
                var value = from.GetValue(current);
                var target = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
                var have = value == null ? null : Nullable.GetUnderlyingType(value.GetType()) ?? value.GetType();
                if (value == null || target == have) p.SetValue(dto, value);
            }
            Overlay(dto, fields, created, index);
            Validate(dto, index);
            return dto;
        }

        private static void Overlay(object dto, JsonElement fields, List<int?> created, int index)
        {
            if (fields.ValueKind != JsonValueKind.Object) return;
            var props = dto.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanWrite && p.GetSetMethod() != null && p.GetCustomAttribute<System.Text.Json.Serialization.JsonIgnoreAttribute>() == null)
                .ToDictionary(p => Key(p.Name));
            foreach (var field in fields.EnumerateObject())
            {
                if (!props.TryGetValue(Key(field.Name), out var prop))
                    throw new ChangeSetException($"Operation {index}: unknown field '{field.Name}'. Fields: {string.Join(", ", props.Values.Select(p => Snake(p.Name)))}.");
                object? value;
                try
                {
                    var target = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
                    value = target == typeof(int) && prop.Name.EndsWith("Id", StringComparison.Ordinal)
                        ? ResolveRef(field.Value, field.Name, created, index)
                        : field.Value.Deserialize(prop.PropertyType);
                }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
                {
                    throw new ChangeSetException($"Operation {index}: '{field.Name}' has the wrong type ({Describe(prop.PropertyType)} expected).");
                }
                prop.SetValue(dto, value);
            }
        }

        /// <summary>The data annotations MVC enforces on the panel's request bodies.</summary>
        private static void Validate(object dto, int index)
        {
            var problems = new List<ValidationResult>();
            if (!Validator.TryValidateObject(dto, new ValidationContext(dto), problems, validateAllProperties: true))
                throw new ChangeSetException($"Operation {index}: {string.Join(" ", problems.Select(p => p.ErrorMessage))}");
        }

        private static string Key(string name) => name.Replace("_", string.Empty).ToLowerInvariant();

        public static string Snake(string pascal) =>
            string.Concat(pascal.Select((c, i) => i > 0 && char.IsUpper(c) ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));

        private static string Describe(Type t)
        {
            var u = Nullable.GetUnderlyingType(t);
            var name = (u ?? t) switch
            {
                var x when x == typeof(string) => "text",
                var x when x == typeof(bool) => "true/false",
                var x when x == typeof(int) => "whole number",
                var x when x == typeof(decimal) => "number",
                _ => (u ?? t).Name
            };
            return u != null ? name + " or null" : name;
        }

        private static string? Str(JsonElement a, string n) =>
            a.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }
}
