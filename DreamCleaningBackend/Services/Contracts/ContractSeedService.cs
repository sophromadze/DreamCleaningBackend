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

        /// <summary>
        /// Makes sure the CURRENT master agreement version exists and is the default a new
        /// contract starts from.
        ///
        /// ADDITIVE ONLY. An existing template row is never rewritten, because it is admin-editable
        /// and because every version generated from it froze that exact body - a "helpful" upgrade
        /// in place would discard a SuperAdmin's wording with no trace and leave the picker
        /// claiming a version whose text had changed underneath it. So a new agreement version is
        /// a NEW ROW, marked default, with the previous one left active and still selectable.
        ///
        /// The default flag moves; nothing else does.
        /// </summary>
        private async Task SeedContractTemplateAsync()
        {
            var templates = await _context.ContractTemplates.ToListAsync();

            var current = templates.FirstOrDefault(t =>
                t.Name == ContractTemplateSeed.TemplateName
                && t.Version == ContractTemplateSeed.CurrentTemplateVersion);

            if (current == null)
            {
                current = new ContractTemplate
                {
                    Name = ContractTemplateSeed.TemplateName,
                    Version = ContractTemplateSeed.CurrentTemplateVersion,
                    Description = ContractTemplateSeed.CurrentTemplateDescription,
                    BodyText = ContractTemplateSeed.CurrentBodyText,
                    IsActive = true
                };
                _context.ContractTemplates.Add(current);
                templates.Add(current);

                _logger.LogInformation(
                    "Seeded master service agreement template v{Version}. Existing versions were left untouched.",
                    ContractTemplateSeed.CurrentTemplateVersion);
            }

            // Exactly one default. Assigned every run so a database seeded before the flag existed
            // ends up pointing at the current version rather than at nothing.
            if (!current.IsDefault || templates.Any(t => t.IsDefault && t.Id != current.Id))
            {
                foreach (var template in templates) template.IsDefault = false;
                current.IsDefault = true;
            }

            await _context.SaveChangesAsync();
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
