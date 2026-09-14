namespace DreamCleaningBackend.Services.Contracts
{
    /// <summary>
    /// The seeded scope-of-work checklists - one per BUSINESS TYPE, editable in
    /// Commercial -> Business Types.
    ///
    /// Group KEYS are the contract between a scope template and the agreement body. Exhibit A
    /// inlines {{SCOPE:included-areas}}, {{SCOPE:excluded-areas}}, {{SCOPE:kitchen-included}},
    /// {{SCOPE:kitchen-excluded}}, {{SCOPE:floor-included}}, {{SCOPE:floor-excluded}} and
    /// {{SCOPE:restroom}}, and expands {{SCOPE_TABLE:area-tasks}} into the "Area | Tasks and
    /// limits" grid. A group with any other key is appended under {{SCOPE_ADDITIONAL}} instead of
    /// being dropped, which is how a premises type carries its own rows without needing its own
    /// agreement body.
    ///
    /// WHY THE AREA/TASK TABLE IS DATA RATHER THAN PROSE IN THE TEMPLATE. The drafted agreement
    /// states six rows written for a restaurant - one of them is the limited kitchen scope. An
    /// office or a gym contract quoting a restaurant's kitchen limits reads as a contract nobody
    /// checked, and fixing it in the body would take a SuperAdmin template edit that would then
    /// apply to restaurants too. As a scope group it is per-business-type, admin-editable, and
    /// DEEP-COPIED onto each contract, so editing a type can never reach an agreement that
    /// already exists.
    ///
    /// Every type carries the same keys so one agreement body serves them all; an unfilled group
    /// renders as "none" and the admin fills it in per contract.
    /// </summary>
    public static class ContractScopeTemplateSeed
    {
        public record SeedTemplate(
            string Name,
            string PremisesType,
            bool AllowsCustomRows,
            int SortOrder,
            ScopeStructure Structure);

        /// <summary>
        /// The area/task table's group key. Named here because <c>ContractSeedService</c> uses it
        /// to detect a template seeded before the table existed and top it up.
        /// </summary>
        public const string AreaTasksKey = "area-tasks";

        public static IReadOnlyList<SeedTemplate> All() => new List<SeedTemplate>
        {
            new("Restaurant", "restaurant", false, 1, Restaurant()),
            new("Office", "office", false, 2, GenericPremises("office")),
            new("Retail/Showroom", "retail location", false, 3, GenericPremises("retail location")),
            new("Medical/Dental", "practice", false, 4, GenericPremises("practice")),
            new("Gym/Studio", "studio", false, 5, GenericPremises("studio")),
            new("Building Common Areas", "building", false, 6, GenericPremises("building")),
            new("Custom", "premises", true, 7, Custom())
        };

        /// <summary>Mirrors Exhibit A of the drafted agreement item for item.</summary>
        private static ScopeStructure Restaurant() => new()
        {
            Groups = new List<ScopeGroup>
            {
                Table(AreaTasksKey, "Areas and tasks at each visit",
                    ("Dining area, lobby and entrance",
                        "Clean floors under A4. Wipe accessible cleared tables, seating and identified touchpoints; do not handle food, utensils or personal property. Sanitizing is included only if expressly identified in A1."),
                    ("Customer and employee restrooms",
                        "Clean toilets, sinks, fixtures, mirrors and floors and remove ordinary trash under A5. Specialized biohazard remediation is excluded."),
                    ("Kitchen",
                        "Clean exposed floors and identified accessible exterior non-food-contact equipment and fixture surfaces, including stainless steel, subject to the limited scope and exclusions in A3."),
                    ("Hallways, office and doors",
                        "Clean exposed floors, identified touchpoints and accessible cleared surfaces. Do not handle files, electronics, cash or private materials."),
                    ("Interior glass",
                        "Clean identified interior windows and glass safely reachable from the floor with ordinary extension tools, subject to Section 25."),
                    ("Ordinary waste",
                        "Move ordinary collected waste to lawful on-site receptacles designated by Client. Preserve required separation of waste streams. No off-site hauling.")),

                Group("included-areas", "Included Areas", "included", true,
                    "the dining area", "the lobby and entrance", "the customer restrooms",
                    "the employee restroom", "the kitchen within the limited scope in A3",
                    "hallways", "the office", "interior windows and glass", "doors", "floors"),

                Group("excluded-areas", "Excluded Areas", "excluded", true,
                    "the break room", "the storage area",
                    "back-of-house areas that are not Included Areas"),

                Group("kitchen-included", "Kitchen - included", "included", true,
                    "cleaning and wiping the accessible exterior non-food-contact surfaces of the equipment and fixtures identified in A1, including their stainless-steel surfaces"),

                Group("kitchen-excluded", "Kitchen - not included", "excluded", true,
                    "dismantling", "internal equipment cleaning", "hood or duct cleaning",
                    "hood-filter cleaning", "grease-trap or interceptor service", "restoration",
                    "repair", "heavy degreasing", "deep grease remediation",
                    "connected-appliance moving", "inaccessible areas behind equipment"),

                Group("floor-included", "Floor cleaning - included", "included", true,
                    "sweeping", "vacuuming where applicable", "mopping"),

                Group("floor-excluded", "Floor cleaning - not included", "excluded", true,
                    "stripping", "waxing", "refinishing", "polishing", "restoration",
                    "intensive grout restoration", "floor-machine restoration",
                    "deep grease remediation"),

                Group("restroom", "Restroom cleaning", "included", true,
                    "cleaning toilets, sinks, mirrors, fixtures and floors",
                    "removing ordinary trash")
            }
        };

        /// <summary>
        /// The skeleton shared by every non-restaurant premises type. Same group keys as
        /// Restaurant so the one seeded agreement body renders them without a second template.
        ///
        /// The area/task table drops the restaurant-specific kitchen row and keeps a pantry row
        /// instead: a gym has no line cook, and a table that says otherwise is the kind of detail
        /// a counterparty reads as boilerplate nobody checked.
        /// </summary>
        private static ScopeStructure GenericPremises(string premises) => new()
        {
            Groups = new List<ScopeGroup>
            {
                Table(AreaTasksKey, "Areas and tasks at each visit",
                    ("Reception, entrance and common areas",
                        "Clean floors under A4. Wipe accessible cleared surfaces, seating and identified touchpoints; do not handle personal property."),
                    ("Restrooms",
                        "Clean toilets, sinks, fixtures, mirrors and floors and remove ordinary trash under A5. Specialized biohazard remediation is excluded."),
                    ("Kitchen or pantry",
                        "Clean exposed floors and identified accessible exterior non-food-contact surfaces, subject to the limited scope and exclusions in A3."),
                    ("Hallways, offices and doors",
                        "Clean exposed floors, identified touchpoints and accessible cleared surfaces. Do not handle files, electronics, cash or private materials."),
                    ("Interior glass",
                        "Clean identified interior windows and glass safely reachable from the floor with ordinary extension tools, subject to Section 25."),
                    ("Ordinary waste",
                        "Move ordinary collected waste to lawful on-site receptacles designated by Client. Preserve required separation of waste streams. No off-site hauling.")),

                Group("included-areas", "Included Areas", "included", true,
                    "the reception and entrance", "common areas", "restrooms", "hallways",
                    "offices", "interior windows and glass", "doors", "floors"),

                Group("excluded-areas", "Excluded Areas", "excluded", true,
                    "storage areas", "areas not expressly identified as Included Areas"),

                Group("kitchen-included", "Kitchen / pantry - included", "included", true,
                    "cleaning and wiping the accessible exterior non-food-contact surfaces identified in A1"),

                Group("kitchen-excluded", "Kitchen / pantry - not included", "excluded", true,
                    "dismantling", "internal equipment cleaning", "hood or duct cleaning",
                    "grease-trap or interceptor service", "restoration", "repair",
                    "heavy degreasing", "inaccessible areas behind equipment"),

                Group("floor-included", "Floor cleaning - included", "included", true,
                    "sweeping", "vacuuming where applicable", "mopping"),

                Group("floor-excluded", "Floor cleaning - not included", "excluded", true,
                    "stripping", "waxing", "refinishing", "polishing", "restoration",
                    "intensive grout restoration", "floor-machine restoration",
                    "deep grease remediation"),

                Group("restroom", "Restroom cleaning", "included", true,
                    "cleaning toilets, sinks, mirrors, fixtures and floors",
                    "removing ordinary trash")
            }
        };

        /// <summary>
        /// Open-ended: the same referenced keys so Exhibit A still renders, but every group is
        /// empty and the admin adds rows by hand.
        /// </summary>
        private static ScopeStructure Custom() => new()
        {
            Groups = new List<ScopeGroup>
            {
                Table(AreaTasksKey, "Areas and tasks at each visit"),
                Group("included-areas", "Included Areas", "included", true),
                Group("excluded-areas", "Excluded Areas", "excluded", true),
                Group("kitchen-included", "Kitchen - included", "included", true),
                Group("kitchen-excluded", "Kitchen - not included", "excluded", true),
                Group("floor-included", "Floor cleaning - included", "included", true),
                Group("floor-excluded", "Floor cleaning - not included", "excluded", true),
                Group("restroom", "Restroom cleaning", "included", true),
                Group("additional-tasks", "Additional tasks", "included", false)
            }
        };

        private static ScopeGroup Group(string key, string title, string kind, bool inline, params string[] items) =>
            new()
            {
                Key = key,
                Title = title,
                Kind = kind,
                Inline = inline,
                Items = items.Select(i => new ScopeItem { Label = i, Selected = true }).ToList()
            };

        /// <summary>
        /// A group rendered as Exhibit A table ROWS rather than as an inline sentence fragment -
        /// each item carries an area in its Label and that area's tasks in its Detail.
        ///
        /// <c>Inline</c> is false so that if this group ever loses its {{SCOPE_TABLE}} token and
        /// falls through to Additional Scope, it is not joined into one unreadable run-on
        /// sentence of six paragraphs.
        /// </summary>
        private static ScopeGroup Table(string key, string title, params (string Area, string Tasks)[] rows) =>
            new()
            {
                Key = key,
                Title = title,
                Kind = "included",
                Inline = false,
                Items = rows
                    .Select(r => new ScopeItem { Label = r.Area, Detail = r.Tasks, Selected = true })
                    .ToList()
            };
    }
}
