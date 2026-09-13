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

        /// <summary>
        /// The structure as a NEW contract should see it: archived groups and items removed.
        ///
        /// Applied when a template is copied onto a draft, never when a stored snapshot is read -
        /// a signed contract keeps every row it was signed with, whatever the master template has
        /// done since.
        /// </summary>
        public ScopeStructure WithoutArchived()
        {
            var copy = Clone();
            copy.Groups = copy.Groups.Where(g => !g.Archived).ToList();
            foreach (var group in copy.Groups)
                group.Items = group.Items.Where(i => !i.Archived).ToList();
            return copy;
        }
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

        /// <summary>
        /// Retired on the MASTER template. Archived rather than deleted because a category that
        /// has been used is quoted in signed agreements, and the template editor must not offer a
        /// destructive action whose consequences are invisible from the screen it lives on.
        ///
        /// It only ever hides the group from NEW contracts: a contract snapshot is a deep copy,
        /// so archiving here cannot reach a document that already exists.
        /// </summary>
        public bool Archived { get; set; }

        public List<ScopeItem> Items { get; set; } = new();
    }

    public class ScopeItem
    {
        public string Label { get; set; } = string.Empty;

        /// <summary>
        /// Whether this row is ticked. On a MASTER template it is the default the admin sees
        /// pre-selected when they pick the business type; on a CONTRACT it is the admin's own
        /// choice, and an unselected item is not written into the document at all.
        /// </summary>
        public bool Selected { get; set; } = true;

        /// <summary>True for rows the admin typed on this contract (Custom templates).</summary>
        public bool IsCustom { get; set; }

        /// <summary>Retired on the master template. Same reasoning as <see cref="ScopeGroup.Archived"/>.</summary>
        public bool Archived { get; set; }
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
