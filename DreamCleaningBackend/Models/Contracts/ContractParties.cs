using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using DreamCleaningBackend.Helpers;

namespace DreamCleaningBackend.Models.Contracts
{
    /// <summary>
    /// Dream Cleaning's OWN legal identity as Contractor. Seeded with one row and editable by
    /// Admin/SuperAdmin — nothing about the company is hardcoded in the template body, which
    /// carries {{CONTRACTOR_*}} tokens only.
    /// </summary>
    public class ContractorProfile
    {
        public int Id { get; set; }

        [Required, StringLength(200)]
        public string LegalEntityName { get; set; } = string.Empty;

        [StringLength(200)]
        public string? Dba { get; set; }

        /// <summary>Free text, e.g. "a New York corporation". Rendered into the preamble.</summary>
        [Required, StringLength(120)]
        public string EntityType { get; set; } = string.Empty;

        [Required, StringLength(300)]
        public string Address { get; set; } = string.Empty;

        [Required, StringLength(100)]
        public string City { get; set; } = string.Empty;

        [Required, StringLength(50)]
        public string State { get; set; } = string.Empty;

        [Required, StringLength(20)]
        public string Zip { get; set; } = string.Empty;

        [Required, StringLength(255)]
        public string NoticeEmail { get; set; } = string.Empty;

        private string? _phone;
        [StringLength(50)]
        public string? Phone
        {
            get => _phone;
            // Stored digits-only like every other phone column; formatted for display on render.
            set => _phone = PhoneHelper.NormalizeToDigits(value);
        }

        /// <summary>Exactly one profile is the default offered on the Create Contract form.</summary>
        public bool IsDefault { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// A PERSON who may sign — never a company. Reused across contracts so the same client
    /// signer does not have to be retyped. Which side they sign for is <see cref="Role"/>.
    /// </summary>
    public class ContractContact
    {
        public int Id { get; set; }

        [Required, StringLength(100)]
        public string FirstName { get; set; } = string.Empty;

        [Required, StringLength(100)]
        public string LastName { get; set; } = string.Empty;

        [StringLength(120)]
        public string? Title { get; set; }

        [StringLength(255)]
        public string? Email { get; set; }

        private string? _phone;
        [StringLength(50)]
        public string? Phone
        {
            get => _phone;
            set => _phone = PhoneHelper.NormalizeToDigits(value);
        }

        public ContractContactRole Role { get; set; } = ContractContactRole.ClientSigner;

        /// <summary>
        /// The platform account this person signs in as, when they have one. Null is the normal
        /// case — most client signers never log in and use their emailed token link.
        ///
        /// Populated it enables the two AUTHENTICATED signing paths: a CEO/CTO contractor signer
        /// signing from the admin panel, and a business customer signing from My Contracts. It is
        /// never a substitute for the token link, which keeps working either way.
        /// </summary>
        public int? UserId { get; set; }

        [ForeignKey("UserId")]
        public virtual User? User { get; set; }

        /// <summary>Optional owning company. Null for contractor-side and unattached contacts.</summary>
        public int? ContractClientId { get; set; }
        [ForeignKey("ContractClientId")]
        public virtual ContractClient? ContractClient { get; set; }

        public bool IsActive { get; set; } = true;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        [NotMapped]
        public string FullName =>
            string.Join(" ", new[] { FirstName, LastName }.Where(s => !string.IsNullOrWhiteSpace(s))).Trim();
    }

    /// <summary>
    /// The commercial counterparty on a contract. Deliberately a separate entity from
    /// <see cref="User"/>: a commercial client is a legal entity with a principal business
    /// address, and it is never assumed to be a booking account.
    /// </summary>
    public class ContractClient
    {
        public int Id { get; set; }

        [Required, StringLength(200)]
        public string LegalEntityName { get; set; } = string.Empty;

        /// <summary>Free text, e.g. "a limited liability company".</summary>
        [Required, StringLength(120)]
        public string EntityType { get; set; } = string.Empty;

        [StringLength(50)]
        public string? FormationState { get; set; }

        [Required, StringLength(300)]
        public string PrincipalAddress { get; set; } = string.Empty;

        [Required, StringLength(100)]
        public string City { get; set; } = string.Empty;

        [Required, StringLength(50)]
        public string State { get; set; } = string.Empty;

        [Required, StringLength(20)]
        public string Zip { get; set; } = string.Empty;

        [StringLength(255)]
        public string? NoticeEmail { get; set; }

        private string? _phone;
        [StringLength(50)]
        public string? Phone
        {
            get => _phone;
            set => _phone = PhoneHelper.NormalizeToDigits(value);
        }

        /// <summary>
        /// The business-flagged customer account this commercial client belongs to, when one
        /// exists. THIS IS THE OWNERSHIP KEY for the self-service My Contracts area: a customer
        /// sees exactly the contracts whose ContractClient carries their id, and nothing else.
        ///
        /// Null is fully supported and common — a contract typed up for a client who has no
        /// account. Those stay reachable only through the emailed token link, because there is no
        /// account to authenticate against. Never infer this link from a matching email address;
        /// it is set deliberately by staff.
        /// </summary>
        public int? SourceUserId { get; set; }

        [ForeignKey("SourceUserId")]
        public virtual User? SourceUser { get; set; }

        public bool IsActive { get; set; } = true;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        public virtual ICollection<ContractServiceLocation> ServiceLocations { get; set; }
            = new List<ContractServiceLocation>();
    }

    /// <summary>
    /// Where the cleaning actually happens. Separate from the client's principal address on
    /// purpose — the reference contract's client is registered at one address and served at
    /// another, and conflating the two is the mistake this entity exists to prevent.
    /// </summary>
    public class ContractServiceLocation
    {
        public int Id { get; set; }

        public int ContractClientId { get; set; }
        [ForeignKey("ContractClientId")]
        public virtual ContractClient? ContractClient { get; set; }

        /// <summary>Consumer-facing brand operated at the premises, e.g. "Chick-fil-A".</summary>
        [StringLength(200)]
        public string? BusinessBrand { get; set; }

        [StringLength(200)]
        public string? LocationName { get; set; }

        [Required, StringLength(300)]
        public string Address { get; set; } = string.Empty;

        [Required, StringLength(100)]
        public string City { get; set; } = string.Empty;

        [Required, StringLength(50)]
        public string State { get; set; } = string.Empty;

        [Required, StringLength(20)]
        public string Zip { get; set; } = string.Empty;

        public bool IsActive { get; set; } = true;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}
