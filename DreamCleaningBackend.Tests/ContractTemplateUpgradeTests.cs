using System;
using System.Linq;
using System.Threading.Tasks;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.Models.Contracts;
using DreamCleaningBackend.Services.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// GETTING A CORRECTED AGREEMENT BODY INTO A DATABASE THAT ALREADY HAS ONE.
    ///
    /// <c>ContractSeedService</c> inserts and never rewrites, matched on Name AND Version. That
    /// rule protects a SuperAdmin's edit to the master template from being reverted on every
    /// restart, and it is correct — but it also means an edit to <c>ContractTemplateSeed.BodyText</c>
    /// reaches nothing once a row at that version exists.
    ///
    /// That is not hypothetical. A "2.0" row seeded from the pre-correction body kept promising
    /// hand soap in Section 6(c), A5, A8(a), Exhibit A's site details and Exhibit B, and kept
    /// printing the signature block between Section 35 and Exhibit A — while the seed file in
    /// source control said the opposite, and every rendering test passed against the seed. The
    /// document the business actually issued came from the database.
    ///
    /// Three things close it, and this file pins all three:
    ///
    ///  1. <b>A corrected body is a NEW VERSION.</b> 2.1 is inserted, 2.0 is retired, the default
    ///     moves. Retiring is what takes superseded legal text out of the picker.
    ///  2. <b>A retired template cannot be re-issued from a draft.</b> The picker lists active
    ///     templates only, so a stale id is invisible on screen while still being sent on save.
    ///  3. <b>Scope data is repaired, not just the body.</b> Exhibit A's area/task grid and the
    ///     restroom checklist live in <c>ScopeTemplate.StructureJson</c>, seeded once and never
    ///     rewritten — so they carried soap wording into a document whose A8 excludes it.
    /// </summary>
    public class ContractTemplateUpgradeTests
    {
        // ── Fixtures ──────────────────────────────────────────────────────────────────────────

        private static ApplicationDbContext NewContext() =>
            new(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase($"contract-template-upgrade-{Guid.NewGuid()}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options);

        private static ContractSeedService Seeder(ApplicationDbContext context) =>
            new(context, NullLogger<ContractSeedService>.Instance);

        /// <summary>The shape of the row a pre-correction startup left behind.</summary>
        private static ContractTemplate SoapDraftRow() => new()
        {
            Name = ContractTemplateSeed.TemplateName,
            Version = "2.0",
            Description = "The attorney-drafted agreement, before hand soap was struck out.",
            BodyText =
                "## 6. FEES, ALL-INCLUSIVE PRICING AND SUPPLIES\n"
                + "(c) Contractor supplies hand soap where replenishment is included in Exhibit A.\n"
                + "@SIGNATURE_BLOCK\n"
                + "## EXHIBIT A\n",
            IsActive = true,
            IsDefault = true
        };

        // ══════════════════════════════════════════════════════════════════════════════════════
        //  1. The corrected body reaches the database as a new version
        // ══════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// The seeded version must not be one the seeder is also told to retire, or startup would
        /// insert the current body and immediately deactivate it — leaving no active template at
        /// all and every new contract unable to pick one.
        /// </summary>
        [Fact]
        public void TheCurrentVersionIsNotAlsoListedAsSuperseded()
        {
            Assert.DoesNotContain(
                ContractTemplateSeed.TemplateVersion, ContractTemplateSeed.SupersededVersions);
        }

        [Fact]
        public async Task AStaleSoapVersionIsRetiredAndTheCorrectedBodyBecomesTheDefault()
        {
            using var context = NewContext();
            context.ContractTemplates.Add(SoapDraftRow());
            await context.SaveChangesAsync();

            await Seeder(context).SeedAsync();

            var stale = await context.ContractTemplates.SingleAsync(t => t.Version == "2.0");
            var current = await context.ContractTemplates.SingleAsync(
                t => t.Version == ContractTemplateSeed.TemplateVersion);

            // The stale row is kept — an audit entry can still point at it — but it leaves the
            // picker and stops being what a new contract starts from.
            Assert.False(stale.IsActive);
            Assert.False(stale.IsDefault);
            Assert.True(current.IsActive);
            Assert.True(current.IsDefault);

            // Exactly one default, always. Two would make "the template a new contract starts
            // from" a matter of row order.
            Assert.Single(await context.ContractTemplates.Where(t => t.IsDefault).ToListAsync());

            // And the body a new contract now renders is the corrected one.
            Assert.Equal(ContractTemplateSeed.BodyText, current.BodyText);
            Assert.DoesNotContain("Contractor supplies hand soap", current.BodyText);
        }

        /// <summary>
        /// The stale row's WORDING is never corrected in place, only retired. Rewriting it would
        /// be the same operation as reverting a SuperAdmin's edit, which is the whole reason this
        /// seeder inserts rather than updates.
        /// </summary>
        [Fact]
        public async Task ARetiredRowKeepsItsOwnBody()
        {
            using var context = NewContext();
            var row = SoapDraftRow();
            var original = row.BodyText;
            context.ContractTemplates.Add(row);
            await context.SaveChangesAsync();

            await Seeder(context).SeedAsync();

            var stale = await context.ContractTemplates.SingleAsync(t => t.Version == "2.0");
            Assert.Equal(original, stale.BodyText);
        }

        /// <summary>
        /// A template an admin authored themselves is not a superseded seed version, so it stays
        /// active and selectable. Only versions this file names are retired.
        /// </summary>
        [Fact]
        public async Task AnAdminAuthoredTemplateIsLeftAlone()
        {
            using var context = NewContext();
            context.ContractTemplates.Add(new ContractTemplate
            {
                Name = "Warehouse agreement",
                Version = "1.0",
                BodyText = "# WAREHOUSE\n",
                IsActive = true
            });
            await context.SaveChangesAsync();

            await Seeder(context).SeedAsync();

            var mine = await context.ContractTemplates.SingleAsync(t => t.Name == "Warehouse agreement");
            Assert.True(mine.IsActive);
        }

        /// <summary>Running twice changes nothing the first run did not already do.</summary>
        [Fact]
        public async Task SeedingIsIdempotent()
        {
            using var context = NewContext();
            context.ContractTemplates.Add(SoapDraftRow());
            await context.SaveChangesAsync();

            await Seeder(context).SeedAsync();
            await Seeder(context).SeedAsync();

            Assert.Equal(1, await context.ContractTemplates
                .CountAsync(t => t.Version == ContractTemplateSeed.TemplateVersion));
            Assert.Single(await context.ContractTemplates.Where(t => t.IsDefault).ToListAsync());
        }

        // ══════════════════════════════════════════════════════════════════════════════════════
        //  2. Scope data is repaired too
        // ══════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// The seeded checklists themselves must be clean — this is the source the repair below
        /// restores stale rows from, so soap here would reinstate it rather than remove it.
        /// </summary>
        [Fact]
        public void NoSeededScopeChecklistMentionsSoap()
        {
            foreach (var seed in ContractScopeTemplateSeed.All())
            {
                foreach (var group in seed.Structure.Groups)
                {
                    foreach (var item in group.Items)
                    {
                        Assert.DoesNotContain("soap", item.Label ?? "", StringComparison.OrdinalIgnoreCase);
                        Assert.DoesNotContain("soap", item.Detail ?? "", StringComparison.OrdinalIgnoreCase);
                    }
                }
            }
        }

        /// <summary>
        /// EXHIBIT A'S GRID IS SCOPE DATA, NOT TEMPLATE TEXT.
        ///
        /// Taking soap out of the agreement body left the area/task row still saying "refill
        /// identified soap dispensers under A5 and A8" — inside an exhibit of a document whose
        /// A8 says Contractor never supplies it. A contract that contradicts itself about who
        /// buys the soap is worse than one that never raised the subject.
        /// </summary>
        [Fact]
        public async Task AStoredAreaTaskRowIsRepairedBackToTheSeededWording()
        {
            using var context = NewContext();

            var stale = new ScopeStructure
            {
                Groups =
                {
                    new ScopeGroup
                    {
                        Key = ContractScopeTemplateSeed.AreaTasksKey,
                        Title = "Areas and tasks at each visit",
                        Inline = false,
                        Items =
                        {
                            new ScopeItem
                            {
                                Label = "Customer and employee restrooms",
                                Detail = "Clean toilets, sinks, fixtures, mirrors and floors; remove "
                                    + "ordinary trash and refill identified soap dispensers under A5 "
                                    + "and A8. Specialized biohazard remediation is excluded.",
                                Selected = true
                            }
                        }
                    }
                }
            };

            context.ScopeTemplates.Add(new ScopeTemplate
            {
                Name = "Restaurant",
                PremisesType = "restaurant",
                SortOrder = 1,
                StructureJson = stale.ToJson(),
                IsActive = true
            });
            await context.SaveChangesAsync();

            await Seeder(context).SeedAsync();

            var row = await context.ScopeTemplates.SingleAsync(t => t.Name == "Restaurant");
            var repaired = ScopeStructure.Parse(row.StructureJson);
            var restrooms = repaired.Groups
                .Single(g => g.Key == ContractScopeTemplateSeed.AreaTasksKey)
                .Items.Single(i => i.Label == "Customer and employee restrooms");

            Assert.DoesNotContain("soap", restrooms.Detail ?? "", StringComparison.OrdinalIgnoreCase);

            // Restored from the SEEDED wording, not from prose the repair composed — the tasks
            // that were actually agreed still have to be described.
            Assert.Contains("Clean toilets, sinks, fixtures, mirrors and floors", restrooms.Detail);
            Assert.Contains("biohazard", restrooms.Detail);
        }

        /// <summary>
        /// The repair touches ONLY what mentions soap. An admin who reworded a different row keeps
        /// their wording — a seeder that "restored" every item to the seed would be silently
        /// deleting scope somebody negotiated.
        /// </summary>
        [Fact]
        public async Task AnEditedRowThatSaysNothingAboutSoapIsNotOverwritten()
        {
            using var context = NewContext();

            var stored = new ScopeStructure
            {
                Groups =
                {
                    new ScopeGroup
                    {
                        Key = ContractScopeTemplateSeed.AreaTasksKey,
                        Title = "Areas and tasks at each visit",
                        Inline = false,
                        Items =
                        {
                            new ScopeItem
                            {
                                Label = "Kitchen",
                                Detail = "Our own agreed wording for this client's kitchen.",
                                Selected = true
                            },
                            new ScopeItem
                            {
                                Label = "Customer and employee restrooms",
                                Detail = "Clean fixtures and refill the soap dispensers.",
                                Selected = true
                            }
                        }
                    }
                }
            };

            context.ScopeTemplates.Add(new ScopeTemplate
            {
                Name = "Restaurant",
                PremisesType = "restaurant",
                SortOrder = 1,
                StructureJson = stored.ToJson(),
                IsActive = true
            });
            await context.SaveChangesAsync();

            await Seeder(context).SeedAsync();

            var row = await context.ScopeTemplates.SingleAsync(t => t.Name == "Restaurant");
            var after = ScopeStructure.Parse(row.StructureJson)
                .Groups.Single(g => g.Key == ContractScopeTemplateSeed.AreaTasksKey);

            Assert.Equal(
                "Our own agreed wording for this client's kitchen.",
                after.Items.Single(i => i.Label == "Kitchen").Detail);
            Assert.DoesNotContain(
                "soap",
                after.Items.Single(i => i.Label == "Customer and employee restrooms").Detail ?? "",
                StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// A row with no seeded counterpart is LOGGED, never reworded. Deciding which half of
        /// somebody's sentence to delete is how agreed scope goes missing with nobody noticing —
        /// so the admin is told where it is and edits it in Commercial → Business Types.
        /// </summary>
        [Fact]
        public async Task AnAdminAuthoredSoapRowIsLeftForAHumanRatherThanRewritten()
        {
            using var context = NewContext();

            var stored = new ScopeStructure
            {
                Groups =
                {
                    new ScopeGroup
                    {
                        Key = "washrooms",
                        Title = "Washrooms",
                        Items =
                        {
                            new ScopeItem { Label = "topping up the soap in the staff washroom", Selected = true }
                        }
                    }
                }
            };

            context.ScopeTemplates.Add(new ScopeTemplate
            {
                Name = "Client's own type",
                PremisesType = "premises",
                SortOrder = 9,
                StructureJson = stored.ToJson(),
                IsActive = true
            });
            await context.SaveChangesAsync();

            await Seeder(context).SeedAsync();

            var row = await context.ScopeTemplates.SingleAsync(t => t.Name == "Client's own type");
            var item = ScopeStructure.Parse(row.StructureJson).Groups.Single().Items.Single();

            Assert.Equal("topping up the soap in the staff washroom", item.Label);
        }

        // ══════════════════════════════════════════════════════════════════════════════════════
        //  3. A retired template cannot be re-issued
        // ══════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// The picker is ACTIVE-ONLY, which is what makes a stale id invisible: the select renders
        /// blank, the model keeps the retired version, and saving sends it straight back. Pinning
        /// the filter here explains why <c>SaveDraftAsync</c> has to substitute server-side rather
        /// than trusting the form to send something current.
        /// </summary>
        [Fact]
        public async Task OnlyActiveTemplatesAreOfferedOnceTheStaleOneIsRetired()
        {
            using var context = NewContext();
            context.ContractTemplates.Add(SoapDraftRow());
            await context.SaveChangesAsync();

            await Seeder(context).SeedAsync();

            var offered = await context.ContractTemplates
                .Where(t => t.IsActive)
                .Select(t => t.Version)
                .ToListAsync();

            Assert.Equal(new[] { ContractTemplateSeed.TemplateVersion }, offered);
        }

        // ── the A1 task label that named a room (2026-09-16) ───────────────────

        /// <summary>
        /// THE STORED LABEL IS REPAIRED, because editing the seed alone reaches nothing.
        ///
        /// Scope checklists are seeded ONCE and never rewritten — the same insert-never-rewrite
        /// rule that let the hand-soap wording outlive its own correction. So a database seeded
        /// before this change keeps printing "Hallways, office and doors" in Exhibit A, on a
        /// contract whose Included Areas list may well have no office ticked, with the corrected
        /// seed sitting in source control saying otherwise.
        /// </summary>
        [Fact]
        public async Task AStoredA1TaskLabelNamingARoomIsRepaired()
        {
            using var context = NewContext();

            var stale = new ScopeStructure
            {
                Groups =
                {
                    new ScopeGroup
                    {
                        Key = ContractScopeTemplateSeed.AreaTasksKey,
                        Title = "Areas and tasks at each visit",
                        Kind = "included",
                        Inline = false,
                        Items =
                        {
                            new ScopeItem
                            {
                                Label = "Hallways, office and doors",
                                Detail = "Clean exposed floors, identified touchpoints and accessible "
                                    + "cleared surfaces. Do not handle files, electronics, cash or "
                                    + "private materials.",
                                Selected = true
                            }
                        }
                    },
                    // The office as a SELECTABLE AREA is legitimate and must survive untouched —
                    // this is the half an over-broad repair would delete.
                    new ScopeGroup
                    {
                        Key = "included-areas",
                        Title = "Included Areas",
                        Kind = "included",
                        Inline = true,
                        Items = { new ScopeItem { Label = "the office", Selected = true } }
                    }
                }
            };

            context.ScopeTemplates.Add(new ScopeTemplate
            {
                Name = "Restaurant",
                PremisesType = "restaurant",
                SortOrder = 1,
                StructureJson = stale.ToJson(),
                IsActive = true
            });
            await context.SaveChangesAsync();

            await Seeder(context).SeedAsync();

            var repaired = ScopeStructure.Parse(
                (await context.ScopeTemplates.SingleAsync(t => t.Name == "Restaurant")).StructureJson);

            var table = repaired.Groups.Single(g => g.Key == ContractScopeTemplateSeed.AreaTasksKey);
            var row = table.Items.Single();

            Assert.Equal(ContractScopeTemplateSeed.IncludedRoomsAreaLabel, row.Label);
            Assert.DoesNotContain("office", row.Label, StringComparison.OrdinalIgnoreCase);

            // ONLY THE LABEL. The task description never named a room, so there was nothing wrong
            // with it to fix and an admin who reworded it keeps their wording.
            Assert.Contains("Do not handle files, electronics, cash or private materials", row.Detail);

            // And the checklist item is untouched: "the office" is an area somebody can tick.
            var areas = repaired.Groups.Single(g => g.Key == "included-areas");
            Assert.Equal("the office", areas.Items.Single().Label);
        }

        /// <summary>The plural spelling the non-restaurant skeleton used is repaired too.</summary>
        [Fact]
        public async Task ThePluralSpellingOfTheStaleLabelIsAlsoRepaired()
        {
            using var context = NewContext();

            var stale = new ScopeStructure
            {
                Groups =
                {
                    new ScopeGroup
                    {
                        Key = ContractScopeTemplateSeed.AreaTasksKey,
                        Title = "Areas and tasks at each visit",
                        Kind = "included",
                        Inline = false,
                        Items =
                        {
                            new ScopeItem { Label = "Hallways, offices and doors", Selected = true }
                        }
                    }
                }
            };

            context.ScopeTemplates.Add(new ScopeTemplate
            {
                Name = "Office",
                PremisesType = "office",
                SortOrder = 2,
                StructureJson = stale.ToJson(),
                IsActive = true
            });
            await context.SaveChangesAsync();

            await Seeder(context).SeedAsync();

            var repaired = ScopeStructure.Parse(
                (await context.ScopeTemplates.SingleAsync(t => t.Name == "Office")).StructureJson);

            Assert.Equal(
                ContractScopeTemplateSeed.IncludedRoomsAreaLabel,
                repaired.Groups.Single(g => g.Key == ContractScopeTemplateSeed.AreaTasksKey)
                    .Items.Single().Label);
        }

        /// <summary>
        /// A BUSINESS TYPE AN ADMIN CREATED THEMSELVES IS REPAIRED TOO — it has no seeded
        /// counterpart, so it never reaches the seeded loop, and the rule is about the document
        /// rather than about who typed the row.
        /// </summary>
        [Fact]
        public async Task AnAdminAuthoredTypeCarryingTheStaleLabelIsRepaired()
        {
            using var context = NewContext();

            var stale = new ScopeStructure
            {
                Groups =
                {
                    new ScopeGroup
                    {
                        Key = ContractScopeTemplateSeed.AreaTasksKey,
                        Title = "Areas and tasks at each visit",
                        Kind = "included",
                        Inline = false,
                        Items =
                        {
                            new ScopeItem { Label = "Hallways, office and doors", Selected = true }
                        }
                    }
                }
            };

            context.ScopeTemplates.Add(new ScopeTemplate
            {
                Name = "Client's own type",
                PremisesType = "premises",
                SortOrder = 9,
                StructureJson = stale.ToJson(),
                IsActive = true
            });
            await context.SaveChangesAsync();

            await Seeder(context).SeedAsync();

            var repaired = ScopeStructure.Parse(
                (await context.ScopeTemplates.SingleAsync(t => t.Name == "Client's own type"))
                    .StructureJson);

            Assert.Equal(
                ContractScopeTemplateSeed.IncludedRoomsAreaLabel,
                repaired.Groups.Single(g => g.Key == ContractScopeTemplateSeed.AreaTasksKey)
                    .Items.Single().Label);
        }

        /// <summary>
        /// A LABEL AN ADMIN DELIBERATELY REWORDED IS LEFT ALONE.
        ///
        /// The repair matches the retired literals and nothing else. Anything a person typed
        /// themselves is their wording, even when it happens to name a room — guessing which half
        /// of somebody's sentence to rewrite is how agreed scope goes missing unnoticed.
        /// </summary>
        [Fact]
        public async Task AnAdminRewordedTaskLabelIsNotTouched()
        {
            using var context = NewContext();

            var custom = new ScopeStructure
            {
                Groups =
                {
                    new ScopeGroup
                    {
                        Key = ContractScopeTemplateSeed.AreaTasksKey,
                        Title = "Areas and tasks at each visit",
                        Kind = "included",
                        Inline = false,
                        Items =
                        {
                            new ScopeItem { Label = "Corridors, the back office and doors", Selected = true },
                            new ScopeItem { Label = "Monitoring room", Selected = true }
                        }
                    }
                }
            };

            context.ScopeTemplates.Add(new ScopeTemplate
            {
                Name = "Client's own type",
                PremisesType = "premises",
                SortOrder = 9,
                StructureJson = custom.ToJson(),
                IsActive = true
            });
            await context.SaveChangesAsync();

            await Seeder(context).SeedAsync();

            var after = ScopeStructure.Parse(
                (await context.ScopeTemplates.SingleAsync(t => t.Name == "Client's own type"))
                    .StructureJson);
            var items = after.Groups.Single(g => g.Key == ContractScopeTemplateSeed.AreaTasksKey).Items;

            Assert.Equal("Corridors, the back office and doors", items[0].Label);
            Assert.Equal("Monitoring room", items[1].Label);
        }

        /// <summary>Seeding twice changes nothing the second time — the repair is idempotent.</summary>
        [Fact]
        public async Task RepairingTheLabelTwiceIsANoOp()
        {
            using var context = NewContext();

            var stale = new ScopeStructure
            {
                Groups =
                {
                    new ScopeGroup
                    {
                        Key = ContractScopeTemplateSeed.AreaTasksKey,
                        Title = "Areas and tasks at each visit",
                        Kind = "included",
                        Inline = false,
                        Items =
                        {
                            new ScopeItem { Label = "Hallways, office and doors", Selected = true }
                        }
                    }
                }
            };

            context.ScopeTemplates.Add(new ScopeTemplate
            {
                Name = "Restaurant",
                PremisesType = "restaurant",
                SortOrder = 1,
                StructureJson = stale.ToJson(),
                IsActive = true
            });
            await context.SaveChangesAsync();

            await Seeder(context).SeedAsync();
            var afterFirst = (await context.ScopeTemplates.SingleAsync(t => t.Name == "Restaurant"))
                .StructureJson;

            await Seeder(context).SeedAsync();
            var afterSecond = (await context.ScopeTemplates.SingleAsync(t => t.Name == "Restaurant"))
                .StructureJson;

            Assert.Equal(afterFirst, afterSecond);
        }

        /// <summary>
        /// THE SAME MECHANISM, EVERY ROUND — every stale seeded version leaves the picker, not
        /// just the most recent one.
        ///
        /// Each correction adds one: v2.2 dropped the Client mailing address, v2.3 made a backup
        /// on-call contact optional, v2.4 took the example rooms out of A2, v2.5 put the service
        /// address in the preamble, and v2.6 added the consolidated Section 36. A database
        /// carrying every earlier row has to end with ALL of them retired and only the current one default;
        /// retiring just the newest would leave the soap draft selectable again, which is the
        /// failure the SupersededVersions list exists to prevent.
        /// </summary>
        [Fact]
        public async Task EveryStaleSeededVersionIsRetiredTogether()
        {
            using var context = NewContext();
            context.ContractTemplates.Add(SoapDraftRow());
            context.ContractTemplates.Add(new ContractTemplate
            {
                Name = ContractTemplateSeed.TemplateName,
                Version = "2.1",
                Description = "The drafted agreement, before the mailing address came out of it.",
                BodyText =
                    "This Agreement is between the Parties, with its business mailing address at "
                    + "{{CLIENT_NOTICE_MAILING_ADDRESS}}.\n"
                    + "@SIGNATURE_BLOCK\n",
                IsActive = true,
                IsDefault = false
            });
            context.ContractTemplates.Add(new ContractTemplate
            {
                Name = ContractTemplateSeed.TemplateName,
                Version = "2.2",
                Description = "The drafted agreement, while a backup on-call contact was obliged.",
                BodyText =
                    "(c) Client shall designate primary and backup on-call contacts in Exhibit B.\n"
                    + "@SIGNATURE_BLOCK\n",
                IsActive = true,
                IsDefault = false
            });
            context.ContractTemplates.Add(new ContractTemplate
            {
                Name = ContractTemplateSeed.TemplateName,
                Version = "2.3",
                Description = "The drafted agreement, while A2 still named example rooms.",
                BodyText =
                    "An area expressly identified as an Included Area in A1, such as the office, "
                    + "the employee restroom or hallways, remains included.\n"
                    + "@SIGNATURE_BLOCK\n",
                IsActive = true,
                IsDefault = false
            });
            context.ContractTemplates.Add(new ContractTemplate
            {
                Name = ContractTemplateSeed.TemplateName,
                Version = "2.4",
                Description = "The drafted agreement, while the preamble named no premises.",
                BodyText =
                    "...and {{CLIENT_LEGAL_NAME}}, {{CLIENT_ENTITY_DESCRIPTION}} (\"Client\").\n"
                    + "@SIGNATURE_BLOCK\n",
                IsActive = true,
                IsDefault = false
            });
            context.ContractTemplates.Add(new ContractTemplate
            {
                Name = ContractTemplateSeed.TemplateName,
                Version = "2.5",
                Description = "The drafted agreement, before Section 36 consolidated cancellation.",
                BodyText =
                    "By signing below, each Party agrees to this Master Service Agreement, "
                    + "including Sections 1 through 35, Exhibit A and Exhibit B.\n"
                    + "@SIGNATURE_BLOCK\n",
                IsActive = true,
                IsDefault = false
            });
            await context.SaveChangesAsync();

            await Seeder(context).SeedAsync();

            var rows = await context.ContractTemplates
                .Where(t => t.Name == ContractTemplateSeed.TemplateName)
                .ToListAsync();

            Assert.False(rows.Single(t => t.Version == "2.0").IsActive);
            Assert.False(rows.Single(t => t.Version == "2.1").IsActive);
            Assert.False(rows.Single(t => t.Version == "2.2").IsActive);
            Assert.False(rows.Single(t => t.Version == "2.3").IsActive);
            Assert.False(rows.Single(t => t.Version == "2.4").IsActive);
            Assert.False(rows.Single(t => t.Version == "2.5").IsActive);

            var current = rows.Single(t => t.Version == ContractTemplateSeed.TemplateVersion);
            Assert.True(current.IsActive);
            Assert.True(current.IsDefault);
            Assert.Single(rows.Where(t => t.IsDefault));

            // Each retired row keeps its OWN wording, so a version generated from it still renders
            // the text it was signed with — the seeder stops offering them, it never rewrites them.
            Assert.Contains("business mailing address",
                rows.Single(t => t.Version == "2.1").BodyText);
            Assert.DoesNotContain("business mailing address", current.BodyText);

            Assert.Contains("designate primary and backup on-call contacts",
                rows.Single(t => t.Version == "2.2").BodyText);
            Assert.DoesNotContain("designate primary and backup on-call contacts", current.BodyText);
            Assert.Contains("may designate a backup on-call contact if available", current.BodyText);

            Assert.Contains("such as the office",
                rows.Single(t => t.Version == "2.3").BodyText);
            Assert.DoesNotContain("such as the office", current.BodyText);
            Assert.Contains(
                "An area expressly identified as an Included Area in A1 remains included",
                current.BodyText);

            // 2.4's preamble named no premises at all, and keeps not naming one - an executed
            // agreement re-renders the sentence its counterparty signed, however the current
            // template reads.
            Assert.DoesNotContain("with Services to be performed at",
                rows.Single(t => t.Version == "2.4").BodyText);
            Assert.Contains(
                "{{CLIENT_ENTITY_DESCRIPTION}}, with Services to be performed at "
                + "{{SERVICE_FULL_ADDRESS}}",
                current.BodyText);

            // 2.5's signature block named 35 sections and had no Section 36; an agreement executed
            // against it keeps saying so, while the current template executes 36 sections.
            Assert.DoesNotContain("## 36.", rows.Single(t => t.Version == "2.5").BodyText);
            Assert.Contains("Sections 1 through 35", rows.Single(t => t.Version == "2.5").BodyText);
            Assert.Contains("## 36. CANCELLATION, RESCHEDULING AND CONTRACT TERMINATION",
                current.BodyText);
            Assert.Contains("Sections 1 through 36", current.BodyText);
        }
    }
}
