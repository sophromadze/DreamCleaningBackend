namespace DreamCleaningBackend.Helpers
{
    public static class FloorTypeHelper
    {
        private static readonly Dictionary<string, string> DisplayNames = new(StringComparer.OrdinalIgnoreCase)
        {
            { "hardwood", "Hardwood" },
            { "engineered-wood", "Engineered Wood" },
            { "laminate", "Laminate" },
            { "vinyl", "Vinyl (LVP/LVT)" },
            { "tile", "Tile (Ceramic/Porcelain)" },
            { "natural-stone", "Natural Stone (Marble/Granite)" },
            { "carpet", "Carpet" },
            { "concrete", "Concrete" },
            { "other", "Other" }
        };

        public static string FormatFloorTypes(string? floorTypes, string? floorTypeOther)
        {
            if (string.IsNullOrWhiteSpace(floorTypes))
                return "Not specified";

            var types = floorTypes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var displayParts = new List<string>();

            foreach (var type in types)
            {
                if (type.StartsWith("other:", StringComparison.OrdinalIgnoreCase))
                {
                    var customText = type.Substring(6).Trim();
                    displayParts.Add(string.IsNullOrEmpty(customText) ? "Other" : $"Other ({customText})");
                }
                else if (type.Equals("other", StringComparison.OrdinalIgnoreCase))
                {
                    displayParts.Add(!string.IsNullOrWhiteSpace(floorTypeOther) ? $"Other ({floorTypeOther})" : "Other");
                }
                else if (DisplayNames.TryGetValue(type, out var displayName))
                {
                    displayParts.Add(displayName);
                }
                else
                {
                    displayParts.Add(type);
                }
            }

            return displayParts.Count > 0 ? string.Join(", ", displayParts) : "Not specified";
        }
    }
}
