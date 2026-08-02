using DreamCleaningBackend.DTOs;

namespace DreamCleaningBackend.Services
{
    /// <summary>
    /// Export / diff / import of pricing configuration between environments, resolving purely by
    /// (ServiceType.Name, Service.ServiceKey) — never by Id.
    /// </summary>
    public interface IPricingConfigurationService
    {
        /// <summary>Snapshot of current configuration. Omit the id to export every service type.</summary>
        Task<PricingConfigurationDto> ExportAsync(int? serviceTypeId = null);

        /// <summary>
        /// What an import would change, plus any blocking errors. Writes nothing.
        /// Also invoked inside <see cref="ApplyAsync"/> so validation cannot be skipped.
        /// </summary>
        Task<PricingConfigurationDiffDto> BuildDiffAsync(PricingConfigurationDto payload);

        /// <summary>Applies the payload in one transaction. Refuses if the diff has errors.</summary>
        Task<ApplyPricingConfigurationResultDto> ApplyAsync(PricingConfigurationDto payload, int actingUserId);
    }
}
