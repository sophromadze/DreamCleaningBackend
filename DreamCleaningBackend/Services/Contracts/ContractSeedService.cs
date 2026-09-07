using DreamCleaningBackend.Data;
using DreamCleaningBackend.Models.Contracts;
using Microsoft.EntityFrameworkCore;

namespace DreamCleaningBackend.Services.Contracts
{
    /// <summary>
    /// Bootstraps the contract reference data at startup: the contractor profile, its signer
    /// contact, the master agreement template and the scope checklists.
    ///
    /// This is deliberately NOT <c>HasData</c> seeding. Every row here is admin-editable, and a
    /// HasData entry emits an <c>UpdateData</c> on the next migration whose value differs - which
    /// would silently revert an admin edit to the master template or a scope list. Inserting only
    /// what is missing means an existing row is never touched again.
    /// </summary>
    public class ContractSeedService
    {
        private readonly ApplicationDbContext _context;
        private readonly ILogger<ContractSeedService> _logger;

        public ContractSeedService(ApplicationDbContext context, ILogger<ContractSeedService> logger)
        {
            _context = context;
            _logger = logger;
        }

        public async Task SeedAsync()
        {
            await SeedContractorProfileAsync();
            await SeedContractTemplateAsync();
            await SeedScopeTemplatesAsync();
        }

        private async Task SeedContractorProfileAsync()
        {
            if (await _context.ContractorProfiles.AnyAsync()) return;

            var profile = new ContractorProfile
            {
                LegalEntityName = "Nodar Alania Inc.",
                Dba = "Dream Cleaning NYC",
                EntityType = "a New York corporation",
                Address = "8800 20th Ave, Apt 2B",
                City = "Brooklyn",
                State = "NY",
                Zip = "11214",
                NoticeEmail = "hello@dreamcleaningnyc.com",
                Phone = "(929) 930-1525",
                IsDefault = true
            };
            _context.ContractorProfiles.Add(profile);
            await _context.SaveChangesAsync();

            // The contractor signer is a contact like any other, so the signature block, the
            // signing invitation and the certificate all read from one place.
            if (!await _context.ContractContacts.AnyAsync(c => c.Role == ContractContactRole.ContractorSigner))
            {
                _context.ContractContacts.Add(new ContractContact
                {
                    FirstName = "Nodar",
                    LastName = "Alania",
                    Title = "CEO",
                    Email = profile.NoticeEmail,
                    Phone = profile.Phone,
                    Role = ContractContactRole.ContractorSigner
                });
                await _context.SaveChangesAsync();
            }

            _logger.LogInformation("Seeded the default contractor profile for commercial contracts.");
        }

        private async Task SeedContractTemplateAsync()
        {
            if (await _context.ContractTemplates.AnyAsync()) return;

            _context.ContractTemplates.Add(new ContractTemplate
            {
                Name = ContractTemplateSeed.TemplateName,
                Version = ContractTemplateSeed.TemplateVersion,
                Description = ContractTemplateSeed.TemplateDescription,
                BodyText = ContractTemplateSeed.BodyText,
                IsActive = true
            });
            await _context.SaveChangesAsync();
            _logger.LogInformation("Seeded the master service agreement template v{Version}.",
                ContractTemplateSeed.TemplateVersion);
        }

        private async Task SeedScopeTemplatesAsync()
        {
            var existing = await _context.ScopeTemplates
                .Select(t => t.Name)
                .ToListAsync();

            var added = 0;
            foreach (var seed in ContractScopeTemplateSeed.All())
            {
                if (existing.Contains(seed.Name, StringComparer.OrdinalIgnoreCase)) continue;

                _context.ScopeTemplates.Add(new ScopeTemplate
                {
                    Name = seed.Name,
                    PremisesType = seed.PremisesType,
                    AllowsCustomRows = seed.AllowsCustomRows,
                    SortOrder = seed.SortOrder,
                    StructureJson = seed.Structure.ToJson(),
                    IsActive = true
                });
                added++;
            }

            if (added > 0)
            {
                await _context.SaveChangesAsync();
                _logger.LogInformation("Seeded {Count} contract scope templates.", added);
            }
        }
    }
}
