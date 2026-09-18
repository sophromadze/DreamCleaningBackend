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
        /// Makes sure the CURRENT master agreement version exists, is the default a new contract
        /// starts from, and is the only agreement version on offer.
        ///
        /// INSERTS, NEVER REWRITES. An existing template row is admin-editable and every version
        /// generated from it froze that exact body, so an "upgrade" in place would discard a
        /// SuperAdmin's wording with no trace and leave the picker claiming a version whose text
        /// had changed underneath it. A new agreement version is therefore a NEW ROW.
        ///
        /// SUPERSEDED VERSIONS ARE DEACTIVATED, NOT DELETED. v1.0 and v1.1 were the pre-review
        /// wording; leaving superseded legal text selectable is an invitation to issue it by
        /// accident. Deactivating takes them out of the picker while keeping the row a stored
        /// snapshot or an audit entry might still point at - a deleted row would turn a historical
        /// reference into a dangling id for no gain.
        /// </summary>
        private async Task SeedContractTemplateAsync()
        {
            var templates = await _context.ContractTemplates.ToListAsync();

            var current = templates.FirstOrDefault(t =>
                t.Name == ContractTemplateSeed.TemplateName
                && t.Version == ContractTemplateSeed.TemplateVersion);

            if (current == null)
            {
                current = new ContractTemplate
                {
                    Name = ContractTemplateSeed.TemplateName,
                    Version = ContractTemplateSeed.TemplateVersion,
                    Description = ContractTemplateSeed.TemplateDescription,
                    BodyText = ContractTemplateSeed.BodyText,
                    IsActive = true
                };
                _context.ContractTemplates.Add(current);
                templates.Add(current);

                _logger.LogInformation(
                    "Seeded master service agreement template v{Version}.",
                    ContractTemplateSeed.TemplateVersion);
            }
            else if (!string.Equals(current.BodyText, ContractTemplateSeed.BodyText, StringComparison.Ordinal))
            {
                // THE SEED FILE AND THE DATABASE DISAGREE AT THE SAME VERSION, and the database
                // wins - this method inserts and never rewrites, so an admin's edit to the master
                // body survives every restart. That is the intended behaviour and it is also the
                // trap: editing BodyText without moving TemplateVersion changes NOTHING anywhere,
                // silently, and the seed file then describes a document the business does not
                // issue. That is exactly how a corrected body sat in source control while every
                // contract still promised hand soap and printed its signature block above the
                // exhibits. The fix is always to raise ContractTemplateSeed.TemplateVersion.
                _logger.LogWarning(
                    "Master service agreement template v{Version} in the database does not match "
                    + "ContractTemplateSeed.BodyText. The STORED body is what contracts render, so "
                    + "the seed edit has not been applied. If the seed is the correction, raise "
                    + "ContractTemplateSeed.TemplateVersion so it is inserted as a new version.",
                    ContractTemplateSeed.TemplateVersion);
            }

            // Retire the pre-review bodies. Matched on VERSION rather than on "anything that is
            // not current", so a template an admin authored themselves is left alone.
            var retired = templates
                .Where(t => t.Id != current.Id
                            && t.IsActive
                            && t.Name == ContractTemplateSeed.TemplateName
                            && ContractTemplateSeed.SupersededVersions.Contains(t.Version))
                .ToList();

            foreach (var template in retired)
            {
                template.IsActive = false;
                template.UpdatedAt = DateTime.UtcNow;
            }

            if (retired.Count > 0)
            {
                _logger.LogInformation(
                    "Retired {Count} superseded agreement template version(s): {Versions}.",
                    retired.Count, string.Join(", ", retired.Select(t => t.Version)));
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

        /// <summary>
        /// Inserts any missing business type, and tops up an existing one that predates the
        /// Exhibit A area/task table.
        ///
        /// THE TOP-UP IS THE ONE THING THIS SEEDER CHANGES ON AN EXISTING ROW, and it is strictly
        /// additive: it appends the <c>area-tasks</c> group when the template has none, and never
        /// touches a group that is already there. The reason it has to exist at all is that
        /// <c>{{SCOPE_TABLE:area-tasks}}</c> is referenced by the agreement body, so a template
        /// seeded before the table existed would render Exhibit A's grid empty on every contract
        /// - with nothing on screen to explain why, because the admin never removed anything.
        ///
        /// An admin who deliberately deletes the group gets it back on the next restart. That is
        /// the accepted cost of the alternative being a silently empty exhibit, and deleting the
        /// group is not how a type opts out of the table - archiving its items is.
        /// </summary>
        private async Task SeedScopeTemplatesAsync()
        {
            var existing = await _context.ScopeTemplates.ToListAsync();
            var seeds = ContractScopeTemplateSeed.All();

            var added = 0;
            var toppedUp = 0;
            var desoaped = 0;
            var relabelled = 0;

            foreach (var seed in seeds)
            {
                var row = existing.FirstOrDefault(t =>
                    string.Equals(t.Name, seed.Name, StringComparison.OrdinalIgnoreCase));

                if (row == null)
                {
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
                    continue;
                }

                var structure = ScopeStructure.Parse(row.StructureJson);
                var changed = false;

                var hasTable = structure.Groups.Any(g => string.Equals(
                    g.Key, ContractScopeTemplateSeed.AreaTasksKey, StringComparison.OrdinalIgnoreCase));

                if (!hasTable)
                {
                    var seededTable = seed.Structure.Groups.FirstOrDefault(g => string.Equals(
                        g.Key, ContractScopeTemplateSeed.AreaTasksKey, StringComparison.OrdinalIgnoreCase));

                    if (seededTable != null)
                    {
                        // First, so Exhibit A's grid opens the scope list the way the agreement reads it.
                        structure.Groups.Insert(0, seededTable);
                        toppedUp++;
                        changed = true;
                    }
                }

                if (RepairSoapWording(row.Name, structure, seed.Structure))
                {
                    desoaped++;
                    changed = true;
                }

                if (RepairRoomSpecificAreaLabel(row.Name, structure))
                {
                    relabelled++;
                    changed = true;
                }

                if (!changed) continue;

                row.StructureJson = structure.ToJson();
                row.UpdatedAt = DateTime.UtcNow;
            }

            // A business type an admin created themselves has no seeded counterpart, so it never
            // reaches the loop above. It can still be carrying soap wording or a copied A1 task
            // label naming a room, and both rules are about the DOCUMENT rather than about who
            // typed the row - so every stored template is checked, not only the seeded ones.
            foreach (var row in existing.Where(r =>
                         !seeds.Any(s => string.Equals(s.Name, r.Name, StringComparison.OrdinalIgnoreCase))))
            {
                var structure = ScopeStructure.Parse(row.StructureJson);

                var soapRepaired = RepairSoapWording(row.Name, structure, null);
                var labelRepaired = RepairRoomSpecificAreaLabel(row.Name, structure);
                if (!soapRepaired && !labelRepaired) continue;

                row.StructureJson = structure.ToJson();
                row.UpdatedAt = DateTime.UtcNow;
                if (soapRepaired) desoaped++;
                if (labelRepaired) relabelled++;
            }

            if (added > 0 || toppedUp > 0 || desoaped > 0 || relabelled > 0)
            {
                await _context.SaveChangesAsync();
                _logger.LogInformation(
                    "Contract scope templates: {Added} seeded, {ToppedUp} given the Exhibit A "
                    + "area/task table, {Desoaped} repaired to drop hand-soap wording, "
                    + "{Relabelled} repaired to drop a room name from an A1 task label.",
                    added, toppedUp, desoaped, relabelled);
            }
        }

        /// <summary>
        /// Takes hand soap out of a STORED scope checklist, in place.
        ///
        /// HAND SOAP IS OUT OF THE AGREEMENT ENTIRELY (owner's rule) - Contractor does not supply,
        /// replenish, repair or replace it, now or in the future. Removing it from the agreement
        /// body was only half the job: Exhibit A's area/task grid and the restroom checklist are
        /// SCOPE DATA, seeded into a row that this service inserts once and then never rewrites.
        /// A database seeded before the rule therefore kept printing "refill identified soap
        /// dispensers" inside the exhibit, on a document whose Section A8 says the opposite - and
        /// a contract that contradicts itself about who buys the soap is worse than one that never
        /// mentioned it.
        ///
        /// It is a REPAIR, not a rewrite: only an item that actually mentions soap is touched, and
        /// it is replaced with the seeded item of the same group and label - never with prose this
        /// method composed. An item with no seeded counterpart (an admin-authored row, or one whose
        /// label has been edited) is LOGGED rather than reworded, because guessing which half of
        /// somebody's sentence to delete is how agreed scope goes missing without anyone noticing.
        /// </summary>
        private bool RepairSoapWording(string templateName, ScopeStructure stored, ScopeStructure? seeded)
        {
            var repaired = false;

            foreach (var group in stored.Groups)
            {
                var seededGroup = seeded?.Groups.FirstOrDefault(g =>
                    string.Equals(g.Key, group.Key, StringComparison.OrdinalIgnoreCase));

                foreach (var item in group.Items)
                {
                    if (!MentionsSoap(item.Label) && !MentionsSoap(item.Detail)) continue;

                    var seededItem = seededGroup?.Items.FirstOrDefault(i =>
                        string.Equals(i.Label?.Trim(), item.Label?.Trim(), StringComparison.OrdinalIgnoreCase));

                    if (seededItem == null || MentionsSoap(seededItem.Label) || MentionsSoap(seededItem.Detail))
                    {
                        _logger.LogWarning(
                            "Scope template \"{Template}\", group \"{Group}\": the item \"{Item}\" mentions "
                            + "hand soap, which the agreement excludes in A8. There is no seeded wording to "
                            + "restore it from, so it was left alone - edit it in Commercial > Business Types.",
                            templateName, group.Key, item.Label);
                        continue;
                    }

                    item.Label = seededItem.Label;
                    item.Detail = seededItem.Detail;
                    repaired = true;
                }
            }

            return repaired;
        }


        /// <summary>
        /// Takes the room-specific wording out of a STORED A1 task label, in place.
        ///
        /// THE DEFECT: the fixed label read "Hallways, office and doors" (or "...offices...") while
        /// Included Areas immediately above it is a checklist the admin ticks per contract. A
        /// client with no office, or one who ticked "Meeting room" instead, still got a contract
        /// whose task table named the office as an area being cleaned. The A1 list is meant to be
        /// the single authority on what is in scope, and a fixed label naming a room competes with
        /// it.
        ///
        /// WHY IT NEEDS A REPAIR AT ALL: scope checklists are seeded ONCE and never rewritten -
        /// the same insert-never-rewrite rule that made the hand-soap wording outlive its own
        /// correction. Editing <c>ContractScopeTemplateSeed</c> alone would leave every existing
        /// database printing the old label with the seed file in front of you saying otherwise.
        ///
        /// It is a REPAIR, not a rewrite, and deliberately narrower than
        /// <see cref="RepairSoapWording"/> in two ways:
        ///
        ///  * Only the LABEL is replaced. The task description never named a room, so an admin who
        ///    has reworded it keeps their wording - there is nothing wrong with it to fix.
        ///  * Only a label matching <c>RetiredAreaLabels</c> is touched, and it is replaced with
        ///    the seeded constant rather than prose composed here. A row an admin has deliberately
        ///    renamed matches nothing and is left exactly as they wrote it.
        ///
        /// Scoped to the area/task group: "office" is a perfectly legitimate ITEM in the Included
        /// Areas checklist, and stripping it from there would delete an area somebody agreed to.
        /// </summary>
        private bool RepairRoomSpecificAreaLabel(string templateName, ScopeStructure stored)
        {
            var repaired = false;

            foreach (var group in stored.Groups.Where(g => string.Equals(
                         g.Key, ContractScopeTemplateSeed.AreaTasksKey, StringComparison.OrdinalIgnoreCase)))
            {
                foreach (var item in group.Items)
                {
                    var label = item.Label?.Trim();
                    if (string.IsNullOrEmpty(label)) continue;

                    if (!ContractScopeTemplateSeed.RetiredAreaLabels.Any(retired =>
                            string.Equals(retired, label, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    item.Label = ContractScopeTemplateSeed.IncludedRoomsAreaLabel;
                    repaired = true;

                    _logger.LogInformation(
                        "Scope template \"{Template}\": A1 task row \"{Old}\" renamed to \"{New}\" so the "
                        + "fixed label no longer names a room that may not be an Included Area.",
                        templateName, label, ContractScopeTemplateSeed.IncludedRoomsAreaLabel);
                }
            }

            return repaired;
        }

        private static bool MentionsSoap(string? text) =>
            !string.IsNullOrEmpty(text) && text.Contains("soap", StringComparison.OrdinalIgnoreCase);
    }
}
