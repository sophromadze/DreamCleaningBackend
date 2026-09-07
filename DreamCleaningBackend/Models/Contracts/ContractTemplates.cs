using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DreamCleaningBackend.Models.Contracts
{
    /// <summary>
    /// A scope-of-work checklist skeleton (Restaurant, Office, Retail/Showroom, ...). The
    /// checklist itself is JSON — see <see cref="StructureJson"/> — so a new premises type is a
    /// data row, never a code change. Selecting one on contract creation copies its groups into
    /// the contract's own snapshot, where the admin may toggle any item off before generating.
    /// </summary>
    public class ScopeTemplate
    {
        public int Id { get; set; }

        [Required, StringLength(120)]
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// The noun the agreement uses for the premises ("restaurant", "office", "facility").
        /// Renders {{PREMISES_TYPE}} so Section 1(b) and 5(f) read naturally for non-restaurants.
        /// </summary>
        [StringLength(60)]
        public string PremisesType { get; set; } = "premises";

        /// <summary>
        /// Serialized <c>ScopeStructure</c> (Services/Contracts/ContractScopeModels.cs): an
        /// ordered list of groups, each with a stable Key the template body references as
        /// <c>{{SCOPE:key}}</c>, and a list of toggleable items.
        /// </summary>
        [Column(TypeName = "LONGTEXT")]
        public string StructureJson { get; set; } = "{}";

        /// <summary>Open-ended templates let the admin add/remove checklist rows freely.</summary>
        public bool AllowsCustomRows { get; set; }

        public int SortOrder { get; set; }
        public bool IsActive { get; set; } = true;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// The master agreement body, stored as lightly-marked text carrying {{PLACEHOLDER}} tokens.
    /// Nothing client-, price-, schedule- or term-specific is hardcoded here; see
    /// <c>ContractPlaceholders</c> for the full token list and
    /// <c>ContractTemplateSeed</c> for the seeded v1 body.
    ///
    /// Template MANAGEMENT is SuperAdmin-only — an edit here re-words every future contract.
    /// </summary>
    public class ContractTemplate
    {
        public int Id { get; set; }

        [Required, StringLength(200)]
        public string Name { get; set; } = string.Empty;

        [Required, StringLength(40)]
        public string Version { get; set; } = "1.0";

        /// <summary>
        /// Marked-up body. Line prefixes: <c>#</c> document title, <c>##</c> section heading,
        /// <c>-</c> bullet, <c>|</c> two-column exhibit row (<c>|Item|Terms</c>), blank line =
        /// paragraph break. The legal sentences themselves are unmodified from the source MSA.
        /// </summary>
        [Column(TypeName = "LONGTEXT")]
        public string BodyText { get; set; } = string.Empty;

        [StringLength(500)]
        public string? Description { get; set; }

        public bool IsActive { get; set; } = true;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}
