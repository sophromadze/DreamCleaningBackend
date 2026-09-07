using System.Text.Json;
using System.Text.Json.Serialization;

namespace DreamCleaningBackend.Services.Contracts
{
    /// <summary>
    /// The shape stored in <c>ScopeTemplate.StructureJson</c> and copied into a contract snapshot.
    /// A group is one checklist block; its <see cref="ScopeGroup.Key"/> is what the template body
    /// references as <c>{{SCOPE:key}}</c>. Groups the body never references are appended to the
    /// document under {{SCOPE_ADDITIONAL}} rather than being silently dropped.
    /// </summary>
    public class ScopeStructure
    {
        public List<ScopeGroup> Groups { get; set; } = new();

        /// <summary>Empty structure, used when a template row is unreadable rather than throwing.</summary>
        public static ScopeStructure Empty() => new();

        public static ScopeStructure Parse(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return Empty();
            try
            {
                return JsonSerializer.Deserialize<ScopeStructure>(json, ContractJson.Options) ?? Empty();
            }
            catch (JsonException)
            {
                return Empty();
            }
        }

        public string ToJson() => JsonSerializer.Serialize(this, ContractJson.Options);

        /// <summary>Deep copy, so toggling items on a contract can never write back to the template.</summary>
        public ScopeStructure Clone() => Parse(ToJson());
    }

    public class ScopeGroup
    {
        /// <summary>Stable identifier referenced by the template body. Never renamed in place.</summary>
        public string Key { get; set; } = string.Empty;

        /// <summary>Heading shown on the admin checklist and on an appended scope block.</summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>
        /// "included" or "excluded". Only affects the wording of an APPENDED block; groups the
        /// template body references supply their own surrounding sentence.
        /// </summary>
        public string Kind { get; set; } = "included";

        /// <summary>
        /// Rendered as a comma-joined sentence fragment (the reference Exhibit A style) rather
        /// than a bullet list when the template body inlines it.
        /// </summary>
        public bool Inline { get; set; } = true;

        public List<ScopeItem> Items { get; set; } = new();
    }

    public class ScopeItem
    {
        public string Label { get; set; } = string.Empty;

        /// <summary>Admin toggle. Unselected items are not written into the document at all.</summary>
        public bool Selected { get; set; } = true;

        /// <summary>True for rows the admin typed on this contract (Custom templates).</summary>
        public bool IsCustom { get; set; }
    }

    /// <summary>Shared serializer options so snapshots round-trip identically everywhere.</summary>
    public static class ContractJson
    {
        public static readonly JsonSerializerOptions Options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            WriteIndented = false
        };
    }
}
