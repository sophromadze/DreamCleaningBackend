using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Models;
using Microsoft.EntityFrameworkCore;

namespace DreamCleaningBackend.Services
{
    /// <summary>
    /// Export / diff / import of pricing configuration, so a setup validated locally can be
    /// applied to production without retyping it and without any Id coupling.
    ///
    /// Everything resolves by (ServiceType.Name, Service.ServiceKey). Ids are never read from the
    /// payload — production and local have already diverged on them.
    ///
    /// Import is a THREE-step flow and the middle step is not optional: build a diff, show it to
    /// the admin, apply only what the diff described. BuildDiffAsync is also called again inside
    /// ApplyAsync so a payload that fails validation can never be written even if the preview
    /// endpoint is bypassed.
    /// </summary>
    public class PricingConfigurationService : IPricingConfigurationService
    {
        private const string SupportedFormatVersion = "1.0";

        private readonly ApplicationDbContext _context;
        private readonly ILogger<PricingConfigurationService> _logger;

        public PricingConfigurationService(
            ApplicationDbContext context, ILogger<PricingConfigurationService> logger)
        {
            _context = context;
            _logger = logger;
        }

        // ===== Export =====

        public async Task<PricingConfigurationDto> ExportAsync(int? serviceTypeId = null)
        {
            var query = _context.ServiceTypes
                .Include(st => st.Services).ThenInclude(s => s.Thresholds).ThenInclude(t => t.SourceService)
                .Include(st => st.Services).ThenInclude(s => s.RateTiers)
                .AsSplitQuery()
                .AsNoTracking()
                .AsQueryable();

            if (serviceTypeId.HasValue)
                query = query.Where(st => st.Id == serviceTypeId.Value);

            var serviceTypes = await query.OrderBy(st => st.DisplayOrder).ToListAsync();

            return new PricingConfigurationDto
            {
                FormatVersion = SupportedFormatVersion,
                ExportedAt = DateTime.UtcNow,
                ServiceTypes = serviceTypes.Select(st => new PricingConfigurationServiceTypeDto
                {
                    ServiceTypeName = st.Name,
                    BasePrice = st.BasePrice,
                    TimeDuration = st.TimeDuration,
                    MinimumPrice = st.MinimumPrice,
                    Services = st.Services
                        .OrderBy(s => s.DisplayOrder)
                        .Select(s => new PricingConfigurationServiceDto
                        {
                            ServiceKey = s.ServiceKey,
                            Name = s.Name,
                            Cost = s.Cost,
                            TimeDuration = s.TimeDuration,
                            ChargeAboveThreshold = s.ChargeAboveThreshold,
                            ZeroQuantityCost = s.ZeroQuantityCost,
                            ZeroQuantityDuration = s.ZeroQuantityDuration,
                            Thresholds = s.Thresholds
                                .OrderBy(t => t.SourceQuantity)
                                .Select(t => new PricingConfigurationThresholdDto
                                {
                                    // Exported as the source's KEY, never its Id.
                                    SourceServiceKey = t.SourceService?.ServiceKey ?? string.Empty,
                                    SourceQuantity = t.SourceQuantity,
                                    IncludedQuantity = t.IncludedQuantity
                                }).ToList(),
                            RateTiers = s.RateTiers
                                .OrderBy(rt => rt.FromQuantity)
                                .Select(rt => new PricingConfigurationRateTierDto
                                {
                                    FromQuantity = rt.FromQuantity,
                                    Cost = rt.Cost,
                                    TimeDuration = rt.TimeDuration,
                                    DisplayOrder = rt.DisplayOrder
                                }).ToList()
                        }).ToList()
                }).ToList()
            };
        }

        // ===== Diff =====

        public async Task<PricingConfigurationDiffDto> BuildDiffAsync(PricingConfigurationDto payload)
        {
            var diff = new PricingConfigurationDiffDto();

            if (payload == null)
            {
                diff.Errors.Add("No configuration supplied.");
                return diff;
            }

            if (!string.Equals(payload.FormatVersion, SupportedFormatVersion, StringComparison.Ordinal))
            {
                diff.Errors.Add(
                    $"Unsupported format version '{payload.FormatVersion}'. This server understands '{SupportedFormatVersion}'.");
                return diff;
            }

            if (payload.ServiceTypes.Count == 0)
            {
                diff.Errors.Add("The configuration contains no service types.");
                return diff;
            }

            var targets = await _context.ServiceTypes
                .Include(st => st.Services).ThenInclude(s => s.Thresholds).ThenInclude(t => t.SourceService)
                .Include(st => st.Services).ThenInclude(s => s.RateTiers)
                .AsSplitQuery()
                .AsNoTracking()
                .ToListAsync();

            var anyChange = false;

            foreach (var incomingType in payload.ServiceTypes)
            {
                var typeDiff = new PricingConfigurationServiceTypeDiffDto
                {
                    ServiceTypeName = incomingType.ServiceTypeName
                };
                diff.ServiceTypes.Add(typeDiff);

                // --- Resolve the service type by name ---
                var matches = targets
                    .Where(t => string.Equals(t.Name, incomingType.ServiceTypeName, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (matches.Count == 0)
                {
                    diff.Errors.Add($"No service type named '{incomingType.ServiceTypeName}' exists here.");
                    continue;
                }

                if (matches.Count > 1)
                {
                    diff.Errors.Add(
                        $"'{incomingType.ServiceTypeName}' matches {matches.Count} service types here. " +
                        "Rename them so the target is unambiguous, then re-import.");
                    continue;
                }

                var target = matches[0];
                typeDiff.ResolvedServiceTypeId = target.Id;

                AddChange(typeDiff.Changes, "Base Price", target.BasePrice, incomingType.BasePrice, "C2");
                AddChange(typeDiff.Changes, "Duration (min)", target.TimeDuration, incomingType.TimeDuration);
                AddChange(typeDiff.Changes, "Minimum Price", target.MinimumPrice, incomingType.MinimumPrice, "C2");

                foreach (var incomingService in incomingType.Services)
                {
                    var serviceDiff = new PricingConfigurationServiceDiffDto
                    {
                        ServiceKey = incomingService.ServiceKey,
                        Name = incomingService.Name
                    };
                    typeDiff.Services.Add(serviceDiff);

                    // --- Resolve the service by key, scoped to this service type ---
                    var serviceMatches = target.Services
                        .Where(s => string.Equals(s.ServiceKey, incomingService.ServiceKey, StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    if (serviceMatches.Count == 0)
                    {
                        diff.Errors.Add(
                            $"'{incomingType.ServiceTypeName}' has no service with key '{incomingService.ServiceKey}' here.");
                        continue;
                    }

                    if (serviceMatches.Count > 1)
                    {
                        diff.Errors.Add(
                            $"Service key '{incomingService.ServiceKey}' matches {serviceMatches.Count} services " +
                            $"under '{incomingType.ServiceTypeName}' here. Keys must be unique within a service type.");
                        continue;
                    }

                    var targetService = serviceMatches[0];
                    serviceDiff.ResolvedServiceId = targetService.Id;

                    AddChange(serviceDiff.Changes, "Cost", targetService.Cost, incomingService.Cost, "C4");
                    AddChange(serviceDiff.Changes, "Duration (min)", targetService.TimeDuration, incomingService.TimeDuration);
                    AddChange(serviceDiff.Changes, "Charge above included",
                        targetService.ChargeAboveThreshold, incomingService.ChargeAboveThreshold);
                    AddChange(serviceDiff.Changes, "Cost when quantity is 0",
                        targetService.ZeroQuantityCost, incomingService.ZeroQuantityCost, "C2");
                    AddChange(serviceDiff.Changes, "Minutes when quantity is 0",
                        targetService.ZeroQuantityDuration, incomingService.ZeroQuantityDuration);

                    ValidateAndDiffThresholds(diff, serviceDiff, incomingType, incomingService, targetService, target);
                    ValidateAndDiffRateTiers(diff, serviceDiff, incomingService, targetService);
                }

                if (HasAnyChange(typeDiff)) anyChange = true;
            }

            diff.CanApply = diff.Errors.Count == 0;
            diff.IsNoOp = diff.CanApply && !anyChange;
            return diff;
        }

        private static bool HasAnyChange(PricingConfigurationServiceTypeDiffDto typeDiff)
            => typeDiff.Changes.Any(c => c.IsChanged)
               || typeDiff.Services.Any(s => s.Changes.Any(c => c.IsChanged)
                                             || s.ThresholdChanges.Count > 0
                                             || s.RateTierChanges.Count > 0);

        private void ValidateAndDiffThresholds(
            PricingConfigurationDiffDto diff,
            PricingConfigurationServiceDiffDto serviceDiff,
            PricingConfigurationServiceTypeDto incomingType,
            PricingConfigurationServiceDto incomingService,
            Service targetService,
            ServiceType targetType)
        {
            var label = $"{incomingType.ServiceTypeName} / {incomingService.ServiceKey}";

            // STEP 1 — RESOLVE KEYS TO IDS FIRST.
            // The JSON payload is keyed by name for portability, but the database enforces
            // UNIQUE (ServiceId, SourceServiceId, SourceQuantity). Validating the key strings
            // would check something the index does not: two rows whose keys differ but resolve
            // to the same source service would pass here and then die on a 1062 duplicate key
            // during apply — an opaque 500 instead of a clear message. So resolve, then validate
            // exactly the triple the index enforces.
            var resolved = new List<(int SourceServiceId, string SourceKey, int SourceQuantity, decimal Included)>();

            foreach (var t in incomingService.Thresholds)
            {
                if (t.SourceQuantity < 0)
                    diff.Errors.Add($"{label}: negative source quantity {t.SourceQuantity}.");
                if (t.IncludedQuantity < 0)
                    diff.Errors.Add($"{label}: negative included amount {t.IncludedQuantity}.");

                // The source must resolve within the SAME service type — Move in/out's sqft must
                // never point at Residential's bedrooms.
                var sourceMatches = targetType.Services
                    .Where(s => string.Equals(s.ServiceKey, t.SourceServiceKey, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (sourceMatches.Count == 0)
                {
                    diff.Errors.Add(
                        $"{label}: included amounts reference source key '{t.SourceServiceKey}', " +
                        $"which does not exist under '{incomingType.ServiceTypeName}' here.");
                    continue;
                }

                if (sourceMatches.Count > 1)
                {
                    diff.Errors.Add(
                        $"{label}: source key '{t.SourceServiceKey}' is ambiguous under '{incomingType.ServiceTypeName}'.");
                    continue;
                }

                resolved.Add((sourceMatches[0].Id, t.SourceServiceKey, t.SourceQuantity, t.IncludedQuantity));
            }

            // STEP 2 — VALIDATE THE RESOLVED TRIPLE, i.e. exactly what the unique index enforces.
            foreach (var dup in resolved.GroupBy(r => (r.SourceServiceId, r.SourceQuantity)).Where(g => g.Count() > 1))
            {
                var spellings = string.Join(", ", dup.Select(d => $"'{d.SourceKey}'").Distinct());
                diff.Errors.Add(
                    $"{label}: {dup.Count()} included amounts resolve to the same source service at quantity " +
                    $"{dup.Key.SourceQuantity} (keys {spellings}). Only one is allowed.");
            }

            if (incomingService.ChargeAboveThreshold && incomingService.Thresholds.Count == 0)
                diff.Warnings.Add(
                    $"{label}: charges only above the included amount, but has no included amounts configured. " +
                    "It will bill from zero.");

            // Row-level diff, also keyed by resolved Id. ServiceKey is NOT unique within a service
            // type at the schema level, so keying this by key string could collapse two genuinely
            // distinct sources — ToDictionary would then throw and take out the preview endpoint.
            var existing = targetService.Thresholds
                .GroupBy(t => (t.SourceServiceId, t.SourceQuantity))
                .ToDictionary(g => g.Key, g => (Included: g.First().IncludedQuantity,
                                                Key: g.First().SourceService?.ServiceKey ?? "?"));

            foreach (var r in resolved.OrderBy(r => r.SourceKey).ThenBy(r => r.SourceQuantity))
            {
                var key = (r.SourceServiceId, r.SourceQuantity);
                if (!existing.TryGetValue(key, out var old))
                    serviceDiff.ThresholdChanges.Add(
                        $"{r.SourceKey} = {r.SourceQuantity}: (none) -> {r.Included:0.##} [new]");
                else if (old.Included != r.Included)
                    serviceDiff.ThresholdChanges.Add(
                        $"{r.SourceKey} = {r.SourceQuantity}: {old.Included:0.##} -> {r.Included:0.##}");
            }

            var incomingResolvedKeys = resolved.Select(r => (r.SourceServiceId, r.SourceQuantity)).ToHashSet();

            foreach (var removed in existing.Where(e => !incomingResolvedKeys.Contains(e.Key)))
                serviceDiff.ThresholdChanges.Add(
                    $"{removed.Value.Key} = {removed.Key.SourceQuantity}: {removed.Value.Included:0.##} -> (removed)");
        }

        private void ValidateAndDiffRateTiers(
            PricingConfigurationDiffDto diff,
            PricingConfigurationServiceDiffDto serviceDiff,
            PricingConfigurationServiceDto incomingService,
            Service targetService)
        {
            var label = serviceDiff.ServiceKey;
            var tiers = incomingService.RateTiers;

            if (tiers.Count > 0)
            {
                if (!tiers.Any(t => t.FromQuantity == 0m))
                    diff.Errors.Add($"{label}: rate tiers must include a band starting at 0.");

                var dupes = tiers.GroupBy(t => t.FromQuantity).Where(g => g.Count() > 1).ToList();
                foreach (var d in dupes)
                    diff.Errors.Add($"{label}: duplicate rate tier starting at {d.Key:0.##}.");

                foreach (var t in tiers)
                {
                    if (t.FromQuantity < 0m) diff.Errors.Add($"{label}: negative tier start {t.FromQuantity:0.##}.");
                    if (t.Cost < 0m) diff.Errors.Add($"{label}: negative tier cost {t.Cost:0.####}.");
                    if (t.TimeDuration < 0m) diff.Errors.Add($"{label}: negative tier minutes {t.TimeDuration:0.####}.");
                }
            }

            // Grouped rather than ToDictionary: UNIQUE (ServiceId, FromQuantity) should make
            // duplicates impossible, but the preview endpoint must not be the thing that
            // discovers otherwise.
            var existing = targetService.RateTiers
                .GroupBy(t => t.FromQuantity)
                .ToDictionary(g => g.Key, g => g.First());

            foreach (var t in tiers.OrderBy(t => t.FromQuantity))
            {
                if (!existing.TryGetValue(t.FromQuantity, out var old))
                    serviceDiff.RateTierChanges.Add(
                        $"from {t.FromQuantity:0.##}: (none) -> {t.Cost:0.####}/unit, {t.TimeDuration:0.####} min/unit [new]");
                else if (old.Cost != t.Cost || old.TimeDuration != t.TimeDuration)
                    serviceDiff.RateTierChanges.Add(
                        $"from {t.FromQuantity:0.##}: {old.Cost:0.####}/{old.TimeDuration:0.####} -> {t.Cost:0.####}/{t.TimeDuration:0.####}");
            }

            var incomingFrom = tiers.Select(t => t.FromQuantity).ToHashSet();
            foreach (var removed in existing.Keys.Where(k => !incomingFrom.Contains(k)).OrderBy(k => k))
                serviceDiff.RateTierChanges.Add($"from {removed:0.##}: removed");
        }

        private static void AddChange<T>(
            List<PricingFieldChangeDto> changes, string field, T oldValue, T newValue, string? format = null)
        {
            string Render(T v) => v switch
            {
                null => "(not set)",
                decimal d => format != null ? d.ToString(format) : d.ToString("0.##"),
                bool b => b ? "Yes" : "No",
                _ => v.ToString() ?? string.Empty
            };

            changes.Add(new PricingFieldChangeDto
            {
                Field = field,
                OldValue = Render(oldValue),
                NewValue = Render(newValue),
                IsChanged = !EqualityComparer<T>.Default.Equals(oldValue, newValue)
            });
        }

        // ===== Apply =====

        public async Task<ApplyPricingConfigurationResultDto> ApplyAsync(
            PricingConfigurationDto payload, int actingUserId)
        {
            // Re-validate rather than trusting that a preview happened. The preview endpoint is a
            // UX affordance; this is the gate.
            var diff = await BuildDiffAsync(payload);
            if (!diff.CanApply)
            {
                return new ApplyPricingConfigurationResultDto
                {
                    Success = false,
                    Message = "Configuration rejected: " + string.Join(" ", diff.Errors)
                };
            }

            var result = new ApplyPricingConfigurationResultDto { Success = true };

            await using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                var targets = await _context.ServiceTypes
                    .Include(st => st.Services).ThenInclude(s => s.Thresholds)
                    .Include(st => st.Services).ThenInclude(s => s.RateTiers)
                    .ToListAsync();

                var now = DateTime.UtcNow;

                foreach (var incomingType in payload.ServiceTypes)
                {
                    var target = targets.Single(t =>
                        string.Equals(t.Name, incomingType.ServiceTypeName, StringComparison.OrdinalIgnoreCase));

                    target.BasePrice = incomingType.BasePrice;
                    target.TimeDuration = incomingType.TimeDuration;
                    target.MinimumPrice = incomingType.MinimumPrice;
                    target.UpdatedAt = now;
                    result.ServiceTypesUpdated++;

                    foreach (var incomingService in incomingType.Services)
                    {
                        var targetService = target.Services.Single(s =>
                            string.Equals(s.ServiceKey, incomingService.ServiceKey, StringComparison.OrdinalIgnoreCase));

                        targetService.Cost = incomingService.Cost;
                        targetService.TimeDuration = incomingService.TimeDuration;
                        targetService.ChargeAboveThreshold = incomingService.ChargeAboveThreshold;
                        targetService.ZeroQuantityCost = incomingService.ZeroQuantityCost;
                        targetService.ZeroQuantityDuration = incomingService.ZeroQuantityDuration;
                        targetService.UpdatedAt = now;
                        result.ServicesUpdated++;

                        // Replace wholesale: the payload is the complete intended state for this
                        // service, so a row absent from it must disappear rather than linger.
                        _context.ServiceThresholds.RemoveRange(targetService.Thresholds);
                        _context.ServiceRateTiers.RemoveRange(targetService.RateTiers);

                        foreach (var t in incomingService.Thresholds)
                        {
                            var sourceService = target.Services.Single(s =>
                                string.Equals(s.ServiceKey, t.SourceServiceKey, StringComparison.OrdinalIgnoreCase));

                            _context.ServiceThresholds.Add(new ServiceThreshold
                            {
                                ServiceId = targetService.Id,
                                SourceServiceId = sourceService.Id,
                                SourceQuantity = t.SourceQuantity,
                                IncludedQuantity = t.IncludedQuantity,
                                CreatedAt = now
                            });
                            result.ThresholdsWritten++;
                        }

                        var order = 1;
                        foreach (var rt in incomingService.RateTiers.OrderBy(x => x.FromQuantity))
                        {
                            _context.ServiceRateTiers.Add(new ServiceRateTier
                            {
                                ServiceId = targetService.Id,
                                FromQuantity = rt.FromQuantity,
                                Cost = rt.Cost,
                                TimeDuration = rt.TimeDuration,
                                DisplayOrder = rt.DisplayOrder > 0 ? rt.DisplayOrder : order,
                                CreatedAt = now
                            });
                            order++;
                            result.RateTiersWritten++;
                        }
                    }
                }

                await _context.SaveChangesAsync();
                await transaction.CommitAsync();

                _logger.LogWarning(
                    "Pricing configuration imported by user {UserId}: {ServiceTypes} service types, " +
                    "{Services} services, {Thresholds} included amounts, {Tiers} rate tiers.",
                    actingUserId, result.ServiceTypesUpdated, result.ServicesUpdated,
                    result.ThresholdsWritten, result.RateTiersWritten);

                result.Message =
                    $"Applied to {result.ServiceTypesUpdated} service type(s) and {result.ServicesUpdated} service(s).";
                return result;
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                _logger.LogError(ex, "Pricing configuration import failed for user {UserId}", actingUserId);
                return new ApplyPricingConfigurationResultDto
                {
                    Success = false,
                    Message = "Import failed and nothing was changed: " + ex.Message
                };
            }
        }
    }
}
