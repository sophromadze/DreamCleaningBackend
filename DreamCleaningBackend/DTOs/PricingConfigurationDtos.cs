namespace DreamCleaningBackend.DTOs
{
    /// <summary>
    /// Portable pricing configuration for moving a validated setup between environments.
    ///
    /// NOTHING IN THIS PAYLOAD IS KEYED BY ID. Production and local have diverged on surrogate
    /// keys — the same service type is Id 4 in production and Id 15 locally — so every reference
    /// resolves by (ServiceType.Name, Service.ServiceKey), which name the same business concepts
    /// in both environments. Adding an Id here, including ServiceTypeId, would reintroduce the
    /// exact class of silent mis-targeting this format exists to avoid.
    /// </summary>
    public class PricingConfigurationDto
    {
        /// <summary>Bumped only on a breaking shape change; import rejects versions it doesn't know.</summary>
        public string FormatVersion { get; set; } = "1.0";

        public DateTime ExportedAt { get; set; }

        /// <summary>Free text for the human: which environment this came from.</summary>
        public string? SourceNote { get; set; }

        public List<PricingConfigurationServiceTypeDto> ServiceTypes { get; set; } = new();
    }

    public class PricingConfigurationServiceTypeDto
    {
        /// <summary>Resolution key. Must match exactly one ServiceType.Name in the target.</summary>
        public string ServiceTypeName { get; set; } = string.Empty;

        public decimal BasePrice { get; set; }
        public decimal TimeDuration { get; set; }
        public decimal MinimumPrice { get; set; }

        public List<PricingConfigurationServiceDto> Services { get; set; } = new();
    }

    public class PricingConfigurationServiceDto
    {
        /// <summary>Resolution key, scoped to the parent service type.</summary>
        public string ServiceKey { get; set; } = string.Empty;

        /// <summary>Informational only — never used to resolve. Helps a human read the file.</summary>
        public string? Name { get; set; }

        public decimal Cost { get; set; }
        public decimal TimeDuration { get; set; }
        public bool ChargeAboveThreshold { get; set; }
        public decimal? ZeroQuantityCost { get; set; }
        public decimal? ZeroQuantityDuration { get; set; }

        public List<PricingConfigurationThresholdDto> Thresholds { get; set; } = new();
        public List<PricingConfigurationRateTierDto> RateTiers { get; set; } = new();
    }

    public class PricingConfigurationThresholdDto
    {
        /// <summary>ServiceKey of the source service, resolved within the same service type.</summary>
        public string SourceServiceKey { get; set; } = string.Empty;
        public int SourceQuantity { get; set; }
        public decimal IncludedQuantity { get; set; }
    }

    public class PricingConfigurationRateTierDto
    {
        /// <summary>Measured ABOVE the included allowance, not in absolute units.</summary>
        public decimal FromQuantity { get; set; }
        public decimal Cost { get; set; }
        public decimal TimeDuration { get; set; }
        public int DisplayOrder { get; set; }
    }

    // ===== Diff preview =====

    /// <summary>
    /// What an import WOULD do. Always produced before anything is written — the admin confirms
    /// against this, so it must show every field that will change, not only the new tables.
    /// </summary>
    public class PricingConfigurationDiffDto
    {
        /// <summary>False when any blocking error exists; the apply endpoint refuses too.</summary>
        public bool CanApply { get; set; }

        /// <summary>Blocking problems: unresolved/ambiguous names, invalid tiers or thresholds.</summary>
        public List<string> Errors { get; set; } = new();

        /// <summary>Non-blocking notes, e.g. "charges above threshold but has no included amounts".</summary>
        public List<string> Warnings { get; set; } = new();

        /// <summary>True when nothing in the payload differs from the target.</summary>
        public bool IsNoOp { get; set; }

        public List<PricingConfigurationServiceTypeDiffDto> ServiceTypes { get; set; } = new();
    }

    public class PricingConfigurationServiceTypeDiffDto
    {
        public string ServiceTypeName { get; set; } = string.Empty;

        /// <summary>Null when the name did not resolve. Shown so the admin can confirm the target.</summary>
        public int? ResolvedServiceTypeId { get; set; }

        public List<PricingFieldChangeDto> Changes { get; set; } = new();
        public List<PricingConfigurationServiceDiffDto> Services { get; set; } = new();
    }

    public class PricingConfigurationServiceDiffDto
    {
        public string ServiceKey { get; set; } = string.Empty;
        public string? Name { get; set; }
        public int? ResolvedServiceId { get; set; }

        public List<PricingFieldChangeDto> Changes { get; set; } = new();

        /// <summary>Human-readable row-level changes, e.g. "bedrooms = 2: 850 -> 900 (changed)".</summary>
        public List<string> ThresholdChanges { get; set; } = new();
        public List<string> RateTierChanges { get; set; } = new();
    }

    public class PricingFieldChangeDto
    {
        public string Field { get; set; } = string.Empty;
        public string OldValue { get; set; } = string.Empty;
        public string NewValue { get; set; } = string.Empty;
        public bool IsChanged { get; set; }
    }

    public class ApplyPricingConfigurationResultDto
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public int ServiceTypesUpdated { get; set; }
        public int ServicesUpdated { get; set; }
        public int ThresholdsWritten { get; set; }
        public int RateTiersWritten { get; set; }
    }
}
