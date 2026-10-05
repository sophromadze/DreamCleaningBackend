using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models;
using Microsoft.EntityFrameworkCore;

namespace DreamCleaningBackend.Services
{
    /// <summary>
    /// Export / diff / import of pricing configuration, so a setup validated locally can be
    /// applied to production without retyping it and without any Id coupling.
    ///
    /// Service types resolve by ServiceType.ServiceKey when both the file and the database have one,
    /// and by name otherwise (ResolveServiceType); services resolve by Service.ServiceKey. Ids are
    /// never read from the payload — production and local have already diverged on them.
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
                    ServiceKey = st.ServiceKey,
                    DisplayPrice = new PricingConfigurationDisplayPriceDto
                    {
                        Amount = st.DisplayPrice,
                        Unit = st.DisplayPriceUnit
                    },
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

            // Two types in one file claiming the same key could never both be saved (unique index).
            var duplicateKeys = payload.ServiceTypes
                .Select(st => ServiceTypeKeyPolicy.Normalize(st.ServiceKey))
                .Where(k => k != null)
                .GroupBy(k => k)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key);
            foreach (var duplicateKey in duplicateKeys)
                diff.Errors.Add($"Service key \"{duplicateKey}\" appears on more than one service type in this file.");

            var anyChange = false;
            var resolvedTargetIds = new HashSet<int>();

            foreach (var incomingType in payload.ServiceTypes)
            {
                var typeDiff = new PricingConfigurationServiceTypeDiffDto
                {
                    ServiceTypeName = incomingType.ServiceTypeName
                };
                diff.ServiceTypes.Add(typeDiff);

                // --- Resolve the service type: by key when both sides have one, else by name ---
                var target = ResolveServiceType(incomingType, targets, out var resolveError);
                if (target == null)
                {
                    diff.Errors.Add(resolveError!);
                    continue;
                }

                // Two entries landing on one type would apply twice, the second silently winning.
                if (!resolvedTargetIds.Add(target.Id))
                {
                    diff.Errors.Add(
                        $"'{incomingType.ServiceTypeName}' resolves to \"{target.Name}\", which another entry in this " +
                        "file already resolves to. Each service type may appear once.");
                    continue;
                }

                typeDiff.ResolvedServiceTypeId = target.Id;

                AddChange(typeDiff.Changes, "Base Price", target.BasePrice, incomingType.BasePrice, "C2");
                AddChange(typeDiff.Changes, "Duration (min)", target.TimeDuration, incomingType.TimeDuration);
                AddChange(typeDiff.Changes, "Minimum Price", target.MinimumPrice, incomingType.MinimumPrice, "C2");

                // Service key: only a file that carries one can change it (null = leave as is).
                var incomingKey = ServiceTypeKeyPolicy.Normalize(incomingType.ServiceKey);
                if (incomingKey != null)
                {
                    var keyProblem = ServiceTypeKeyPolicy.DescribeProblem(incomingKey);
                    if (keyProblem != null)
                    {
                        diff.Errors.Add($"'{incomingType.ServiceTypeName}': {keyProblem}");
                    }
                    else
                    {
                        var keyOwner = targets.FirstOrDefault(t =>
                            t.Id != target.Id && string.Equals(t.ServiceKey, incomingKey, StringComparison.OrdinalIgnoreCase));
                        if (keyOwner != null)
                            diff.Errors.Add(
                                $"'{incomingType.ServiceTypeName}': {ServiceTypeKeyPolicy.DescribeDuplicate(incomingKey, keyOwner.Name)} " +
                                "Clear it there first, then re-import.");

                        AddChange(typeDiff.Changes, "Service Key", target.ServiceKey, incomingKey);
                    }
                }

                // Display price: only a file that carries the object can change it (absent = leave as is).
                if (incomingType.DisplayPrice != null)
                {
                    var (amount, unit, displayPriceError) = ServiceTypeDisplayPricePolicy.Resolve(
                        incomingType.DisplayPrice.Amount, incomingType.DisplayPrice.Unit);
                    if (displayPriceError != null)
                    {
                        diff.Errors.Add($"'{incomingType.ServiceTypeName}': {displayPriceError}");
                    }
                    else
                    {
                        AddChange(typeDiff.Changes, "Display Price", target.DisplayPrice, amount, "C2");
                        AddChange(typeDiff.Changes, "Display Price Unit", target.DisplayPriceUnit, unit);
                    }
                }

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

        /// <summary>
        /// Finds the existing service type a file entry describes, or returns null with a message.
        ///
        /// By ServiceKey when BOTH the entry and an existing type have one; by name only when either
        /// side has no key (older exports, or types nobody has keyed yet). Id diverges between
        /// databases and names are editable, so a key present on both sides is the stronger identity
        /// - but when the key and the name point at DIFFERENT types, or both sides are keyed and the
        /// keys differ, that is reported for a person to resolve, never guessed between.
        /// </summary>
        private static ServiceType? ResolveServiceType(
            PricingConfigurationServiceTypeDto incoming, IReadOnlyList<ServiceType> targets, out string? error)
        {
            error = null;
            var label = incoming.ServiceTypeName;
            var incomingKey = ServiceTypeKeyPolicy.Normalize(incoming.ServiceKey);

            var nameMatches = targets
                .Where(t => string.Equals(t.Name, incoming.ServiceTypeName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (incomingKey != null)
            {
                var keyMatches = targets
                    .Where(t => string.Equals(t.ServiceKey, incomingKey, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                // The unique index makes this impossible; if it ever happens, refuse rather than pick.
                if (keyMatches.Count > 1)
                {
                    error = $"'{label}': service key \"{incomingKey}\" is held by {keyMatches.Count} service types here.";
                    return null;
                }

                if (keyMatches.Count == 1)
                {
                    var byKey = keyMatches[0];

                    // A name that matches only OTHER types means the two identities disagree. A name
                    // that matches nothing is just a rename, and the key decides.
                    if (nameMatches.Count > 0 && !nameMatches.Any(t => t.Id == byKey.Id))
                    {
                        var others = string.Join(", ", nameMatches.Select(t => $"\"{t.Name}\""));
                        error = $"'{label}': service key \"{incomingKey}\" points to \"{byKey.Name}\" here, " +
                                $"but the name points to {others}. Fix the key or the name so they agree, then re-import.";
                        return null;
                    }

                    return byKey;
                }

                // No existing type holds this key: fall back to the name below - but only onto a type
                // without a key of its own, since a different key there is a conflict, not a match.
            }

            if (nameMatches.Count == 0)
            {
                error = $"No service type named '{label}' exists here." +
                        (incomingKey != null ? $" No service type has the key \"{incomingKey}\" either." : string.Empty);
                return null;
            }

            if (nameMatches.Count > 1)
            {
                error = $"'{label}' matches {nameMatches.Count} service types here. " +
                        "Rename them, or give each a service key, so the target is unambiguous, then re-import.";
                return null;
            }

            var byName = nameMatches[0];
            var existingKey = ServiceTypeKeyPolicy.Normalize(byName.ServiceKey);
            if (incomingKey != null && existingKey != null)
            {
                error = $"'{label}': the name matches \"{byName.Name}\" here, but its service key is \"{existingKey}\" " +
                        $"while the file says \"{incomingKey}\". Fix the key or the name so they agree, then re-import.";
                return null;
            }

            return byName;
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
                    // Same resolver as the diff, which has already accepted every entry.
                    var target = ResolveServiceType(incomingType, targets, out var resolveError)
                        ?? throw new InvalidOperationException(resolveError);

                    target.BasePrice = incomingType.BasePrice;
                    target.TimeDuration = incomingType.TimeDuration;
                    target.MinimumPrice = incomingType.MinimumPrice;

                    // Already validated by BuildDiffAsync. Null leaves the key alone - see the DTO.
                    var incomingKey = ServiceTypeKeyPolicy.Normalize(incomingType.ServiceKey);
                    if (incomingKey != null)
                        target.ServiceKey = incomingKey;

                    // Already validated by BuildDiffAsync. An absent object leaves it alone - see the DTO.
                    if (incomingType.DisplayPrice != null)
                    {
                        var (amount, unit, _) = ServiceTypeDisplayPricePolicy.Resolve(
                            incomingType.DisplayPrice.Amount, incomingType.DisplayPrice.Unit);
                        target.DisplayPrice = amount;
                        target.DisplayPriceUnit = unit;
                    }
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
