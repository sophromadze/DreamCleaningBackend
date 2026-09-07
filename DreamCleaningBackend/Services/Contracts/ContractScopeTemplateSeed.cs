namespace DreamCleaningBackend.Services.Contracts
{
    /// <summary>
    /// The seeded scope-of-work checklists. Group KEYS are the contract between a scope template
    /// and the template body: Exhibit A inlines {{SCOPE:included-areas}}, {{SCOPE:excluded-areas}},
    /// {{SCOPE:kitchen-included}}, {{SCOPE:kitchen-excluded}}, {{SCOPE:floor-included}},
    /// {{SCOPE:floor-excluded}} and {{SCOPE:restroom}}. A group with any other key is appended
    /// under {{SCOPE_ADDITIONAL}} instead of being dropped, which is how the non-restaurant
    /// templates carry premises-specific rows without needing their own agreement body.
    ///
    /// Only Restaurant is populated to match the reference Exhibit A; the others are the empty
    /// skeletons the spec calls for, ready for the admin to fill in per contract.
    /// </summary>
    public static class ContractScopeTemplateSeed
    {
        public record SeedTemplate(
            string Name,
            string PremisesType,
            bool AllowsCustomRows,
            int SortOrder,
            ScopeStructure Structure);

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

        /// <summary>Mirrors Exhibit A of the reference agreement item for item.</summary>
        private static ScopeStructure Restaurant() => new()
        {
            Groups = new List<ScopeGroup>
            {
                Group("included-areas", "Included Areas", "included", true,
                    "Dining area", "Lobby and entrance", "Customer restrooms", "Employee restroom",
                    "Kitchen, only within the limited kitchen scope described in Section A3",
                    "Hallways", "Office", "Interior windows and interior glass", "Doors", "Floors"),

                Group("excluded-areas", "Excluded Areas", "excluded", true,
                    "Back-of-house areas that are not Included Areas", "The break room", "The storage area"),

                Group("kitchen-included", "Kitchen - included", "included", true,
                    "Cleaning and wiping of exterior surfaces only, including stainless-steel exterior surfaces of kitchen equipment and fixtures"),

                Group("kitchen-excluded", "Kitchen - not included", "excluded", true,
                    "Dismantling of equipment", "Internal cleaning of any equipment, including internal fryer, oven and grill cleaning",
                    "Hood or duct cleaning", "Grease trap or grease interceptor service"),

                Group("floor-included", "Floor cleaning - included", "included", true,
                    "Sweeping", "Vacuuming where applicable", "Mopping of floors"),

                Group("floor-excluded", "Floor cleaning - not included", "excluded", true,
                    "Stripping", "Waxing", "Refinishing", "Polishing", "Restoration",
                    "Intensive grout restoration", "Floor-machine restoration", "Deep grease remediation"),

                Group("restroom", "Restroom cleaning", "included", true,
                    "Cleaning of toilets, sinks, mirrors and fixtures",
                    "Cleaning of restroom floors", "Removal of restroom trash")
            }
        };

        /// <summary>
        /// The empty skeleton shared by every non-restaurant premises type. Same group keys as
        /// Restaurant so the one seeded agreement body renders them without a second template -
        /// an unfilled group simply renders as "none" and the admin fills it in per contract.
        /// </summary>
        private static ScopeStructure GenericPremises(string premises) => new()
        {
            Groups = new List<ScopeGroup>
            {
                Group("included-areas", "Included Areas", "included", true,
                    "Reception and entrance", "Common areas", "Restrooms", "Hallways", "Offices",
                    "Interior windows and interior glass", "Doors", "Floors"),

                Group("excluded-areas", "Excluded Areas", "excluded", true,
                    "Areas not expressly identified as Included Areas", "Storage areas"),

                Group("kitchen-included", "Kitchen / pantry - included", "included", true,
                    "Cleaning and wiping of exterior surfaces only"),

                Group("kitchen-excluded", "Kitchen / pantry - not included", "excluded", true,
                    "Dismantling of equipment", "Internal cleaning of any equipment",
                    "Hood or duct cleaning", "Grease trap or grease interceptor service"),

                Group("floor-included", "Floor cleaning - included", "included", true,
                    "Sweeping", "Vacuuming where applicable", "Mopping of floors"),

                Group("floor-excluded", "Floor cleaning - not included", "excluded", true,
                    "Stripping", "Waxing", "Refinishing", "Polishing", "Restoration",
                    "Intensive grout restoration", "Floor-machine restoration", "Deep grease remediation"),

                Group("restroom", "Restroom cleaning", "included", true,
                    "Cleaning of toilets, sinks, mirrors and fixtures",
                    "Cleaning of restroom floors", "Removal of restroom trash")
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
    }
}
