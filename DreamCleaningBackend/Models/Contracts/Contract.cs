using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DreamCleaningBackend.Models.Contracts
{
    /// <summary>
    /// One commercial agreement with one client at one service location. The contract row is the
    /// stable identity (number, parties, status); every rendered document lives on a
    /// <see cref="ContractVersion"/>, which carries a FROZEN snapshot of everything it rendered.
    /// </summary>
    public class Contract
    {
        public int Id { get; set; }

        /// <summary>
        /// DCC-YYYY-XXXXXXXX with a cryptographically random 8-digit tail, e.g.
        /// DCC-2026-48392175. Unique, and never reused - not even after a contract is deleted.
        ///
        /// LEGACY ROWS KEEP THE OLD DC-YYYY-NNNN FORMAT. Contracts created before 2026-09 are
        /// deliberately not migrated: the number is printed on an executed legal document and
        /// quoted in email threads, so rewriting it would orphan every reference that already
        /// exists outside this database. Nothing parses this column - it is only displayed and
        /// matched exactly - so the two formats coexist safely. See
        /// ContractService.GenerateContractNumberAsync.
        ///
        /// 20 chars leaves headroom: the new format is 17.
        /// </summary>
        [Required, StringLength(20)]
        public string ContractNumber { get; set; } = string.Empty;

        public int ContractClientId { get; set; }
        [ForeignKey("ContractClientId")]
        public virtual ContractClient? ContractClient { get; set; }

        public int ContractServiceLocationId { get; set; }
        [ForeignKey("ContractServiceLocationId")]
        public virtual ContractServiceLocation? ServiceLocation { get; set; }

        public int ContractorProfileId { get; set; }
        [ForeignKey("ContractorProfileId")]
        public virtual ContractorProfile? ContractorProfile { get; set; }

        public int ContractTemplateId { get; set; }
        [ForeignKey("ContractTemplateId")]
        public virtual ContractTemplate? ContractTemplate { get; set; }

        public int? ScopeTemplateId { get; set; }
        [ForeignKey("ScopeTemplateId")]
        public virtual ScopeTemplate? ScopeTemplate { get; set; }

        public ContractStatus Status { get; set; } = ContractStatus.Draft;

        /// <summary>
        /// The version currently on the table - the one the review link renders and the one
        /// signers are created against. Null only between row insert and the first Generate.
        /// </summary>
        public int? CurrentVersionId { get; set; }

        /// <summary>
        /// Contract-level token behind /contract/review/{token}. Deliberately NOT per-version:
        /// a revision must not strand the link already in the inbox of the client, and the review
        /// page always resolves the CURRENT version.
        /// </summary>
        [StringLength(64)]
        public string? ClientReviewToken { get; set; }

        public DateTime? ClientReviewTokenExpiresAt { get; set; }

        /// <summary>
        /// The working draft the Create/Edit form round-trips (serialized ContractSnapshot).
        /// A generated version copies this; editing after a Generate mutates the draft only,
        /// never a version that has already been rendered.
        /// </summary>
        [Column(TypeName = "LONGTEXT")]
        public string DraftSnapshotJson { get; set; } = "{}";

        public int CreatedByAdminId { get; set; }
        [ForeignKey("CreatedByAdminId")]
        public virtual User? CreatedByAdmin { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>Set when both parties have signed and the executed PDF exists.</summary>
        public DateTime? CompletedAt { get; set; }

        /// <summary>
        /// Reason captured when a contract was voided. LEGACY: nothing sets this any more — the
        /// Void action was replaced by the soft delete below (2026-09). Kept so contracts voided
        /// before that change still render their reason.
        /// </summary>
        [StringLength(500)]
        public string? VoidReason { get; set; }

        /// <summary>
        /// Soft delete. Follows the same <c>IsHidden</c> pattern as <see cref="Order"/>: the row
        /// stays exactly as it was and simply drops out of the default list, recoverable by the
        /// CTO through "Show hidden contracts".
        ///
        /// Hiding is NOT merely cosmetic — it also revokes the outstanding signing links and the
        /// client review token, and every signing/review path treats a hidden contract as closed.
        /// A contract somebody has decided to delete must not remain signable from an email
        /// sitting in a counterparty's inbox.
        ///
        /// After <c>ContractRetention:HiddenMonths</c> (default 6) a background job hard-deletes
        /// the row and everything under it, leaving a ContractDeletionLog entry behind.
        /// </summary>
        public bool IsHidden { get; set; } = false;

        /// <summary>When it was hidden. Starts the retention clock; null while visible.</summary>
        public DateTime? HiddenAt { get; set; }

        public int? HiddenByUserId { get; set; }

        [ForeignKey("HiddenByUserId")]
        public virtual User? HiddenByUser { get; set; }

        /// <summary>Set when this contract was produced by "Duplicate as New Contract".</summary>
        public int? DuplicatedFromContractId { get; set; }

        public virtual ICollection<ContractVersion> Versions { get; set; } = new List<ContractVersion>();
    }

    /// <summary>
    /// A point-in-time rendering of a contract. <see cref="FullSnapshotJson"/> is a COMPLETE
    /// frozen copy of every field the document was built from - contractor, client, signers,
    /// location, schedule, term, pricing, scope, advanced terms. Rendering a stored version must
    /// never re-resolve any of it from the live parent tables: a client renaming their company
    /// two years later must not silently rewrite what somebody signed.
    /// </summary>
    public class ContractVersion
    {
        public int Id { get; set; }

        public int ContractId { get; set; }
        [ForeignKey("ContractId")]
        public virtual Contract? Contract { get; set; }

        public int VersionNumber { get; set; }

        [Column(TypeName = "LONGTEXT")]
        public string FullSnapshotJson { get; set; } = "{}";

        /// <summary>Rendered document HTML, stored so a later read never re-renders.</summary>
        [Column(TypeName = "LONGTEXT")]
        public string RenderedDocumentHtml { get; set; } = string.Empty;

        /// <summary>Relative path of the rendered preview PDF, if one has been produced.</summary>
        [StringLength(500)]
        public string? RenderedDocumentPath { get; set; }

        /// <summary>SHA-256 over the rendered document text. What a signer attests to.</summary>
        [Required, StringLength(64)]
        public string DocumentHashSha256 { get; set; } = string.Empty;

        public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;

        public int GeneratedByAdminId { get; set; }
        [ForeignKey("GeneratedByAdminId")]
        public virtual User? GeneratedByAdmin { get; set; }

        /// <summary>
        /// True once a newer version exists. A superseded version is read-only forever and its
        /// signer rows are voided - a signature never carries forward across versions.
        /// </summary>
        public bool IsSuperseded { get; set; }

        public virtual ICollection<ContractSigner> Signers { get; set; } = new List<ContractSigner>();
        public virtual ICollection<ContractFile> Files { get; set; } = new List<ContractFile>();
    }

    /// <summary>
    /// The invitation of one party to sign ONE version. Belongs to the version, not the contract,
    /// so a new version necessarily means fresh signing - see <see cref="ContractVersion"/>.
    /// </summary>
    public class ContractSigner
    {
        public int Id { get; set; }

        public int ContractVersionId { get; set; }
        [ForeignKey("ContractVersionId")]
        public virtual ContractVersion? ContractVersion { get; set; }

        public int? ContractContactId { get; set; }
        [ForeignKey("ContractContactId")]
        public virtual ContractContact? Contact { get; set; }

        /// <summary>
        /// The account permitted to sign this row from an AUTHENTICATED session, frozen at the
        /// moment signing was opened (copied from the version's snapshot, not followed live).
        ///
        /// Frozen on purpose: the in-app signing check compares the logged-in account against this
        /// value, so reading it live through the mutable contact would mean re-pointing a contact
        /// at a different account silently transferred the right to sign a document already out
        /// for signature. Null = token link only, which is the normal case.
        /// </summary>
        public int? UserId { get; set; }

        [ForeignKey("UserId")]
        public virtual User? User { get; set; }

        public ContractSignerRole Role { get; set; }

        /// <summary>Opaque 48-hex token. The link grants signing as THIS signer only.</summary>
        [Required, StringLength(64)]
        public string SigningToken { get; set; } = string.Empty;

        public DateTime TokenExpiresAt { get; set; }

        public ContractSignerStatus Status { get; set; } = ContractSignerStatus.Pending;

        /// <summary>Snapshot of who was invited, so a later contact edit cannot rewrite history.</summary>
        [Required, StringLength(200)]
        public string InvitedName { get; set; } = string.Empty;

        [StringLength(120)]
        public string? InvitedTitle { get; set; }

        [StringLength(255)]
        public string? InvitedEmail { get; set; }

        public DateTime? InviteSentAt { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public virtual ContractSignature? Signature { get; set; }
    }

    /// <summary>
    /// The evidence record. Everything here is captured AT SIGNING TIME and is never updated:
    /// the name/title/email as typed, the mark, the method, when, from where, and the hash of the
    /// exact document rendered on the screen of the signer.
    /// </summary>
    public class ContractSignature
    {
        public int Id { get; set; }

        public int ContractSignerId { get; set; }
        [ForeignKey("ContractSignerId")]
        public virtual ContractSigner? ContractSigner { get; set; }

        [Required, StringLength(200)]
        public string SignerNameAtSigning { get; set; } = string.Empty;

        [StringLength(120)]
        public string? SignerTitleAtSigning { get; set; }

        [StringLength(255)]
        public string? SignerEmailAtSigning { get; set; }

        /// <summary>
        /// Draw = a PNG data URI of the drawn mark. Type = the typed text, rendered in a script
        /// face on the document. LONGTEXT because a drawn mark is a base64 image.
        /// </summary>
        [Column(TypeName = "LONGTEXT")]
        public string SignatureImageOrTypedText { get; set; } = string.Empty;

        public ContractSignatureMethod SignatureMethod { get; set; }

        public DateTime SignedAt { get; set; } = DateTime.UtcNow;

        [StringLength(45)]
        public string? IpAddress { get; set; }

        [StringLength(500)]
        public string? UserAgent { get; set; }

        [Required, StringLength(64)]
        public string DocumentHashAtSigning { get; set; } = string.Empty;

        public bool ConsentAccepted { get; set; }

        /// <summary>
        /// Which route captured this signature. Part of the evidence, not a convenience field:
        /// EmailLink means the only credential was the opaque token, while AdminPanel and
        /// CustomerPortal mean an authenticated session, and knowing which applied is the
        /// difference between two very different identity claims.
        ///
        /// Defaults to EmailLink so any row written before the authenticated paths existed reads
        /// correctly — that is genuinely how every one of them was signed.
        /// </summary>
        public ContractSignatureChannel SigningChannel { get; set; } = ContractSignatureChannel.EmailLink;

        /// <summary>
        /// The logged-in account that signed, for the authenticated channels. Null for EmailLink,
        /// where there is no account behind the signature. Recorded ALONGSIDE the frozen
        /// name/title above rather than instead of it: the document keeps saying what it said when
        /// it was signed, and this answers "which account was that" separately.
        /// </summary>
        public int? SignedByUserId { get; set; }

        [ForeignKey("SignedByUserId")]
        public virtual User? SignedByUser { get; set; }
    }

    /// <summary>
    /// Plain-language contract timeline. Separate from the app-wide <see cref="AuditLog"/> on
    /// purpose: this one is READ BY PEOPLE - it renders as the timeline on the contract detail
    /// page and as the audit certificate in the executed PDF - so it stores a sentence, not a
    /// field-level JSON diff.
    /// </summary>
    public class ContractAuditLog
    {
        public long Id { get; set; }

        public int ContractId { get; set; }
        [ForeignKey("ContractId")]
        public virtual Contract? Contract { get; set; }

        [Required, StringLength(60)]
        public string EventType { get; set; } = string.Empty;

        [Required, StringLength(1000)]
        public string EventDescription { get; set; } = string.Empty;

        public ContractActorType ActorType { get; set; }

        /// <summary>Admin name, client email, or "System" - whoever the description names.</summary>
        [StringLength(255)]
        public string? ActorIdentifier { get; set; }

        public int? ContractVersionId { get; set; }

        [StringLength(45)]
        public string? IpAddress { get; set; }

        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// A generated artefact on disk. Files live OUTSIDE the publicly served uploads root and are
    /// only reachable through an authorized or token-scoped download endpoint.
    /// </summary>
    public class ContractFile
    {
        public int Id { get; set; }

        public int ContractVersionId { get; set; }
        [ForeignKey("ContractVersionId")]
        public virtual ContractVersion? ContractVersion { get; set; }

        public ContractFileType FileType { get; set; }

        /// <summary>Path relative to the configured contract storage root.</summary>
        [Required, StringLength(500)]
        public string FilePath { get; set; } = string.Empty;

        [Required, StringLength(255)]
        public string FileName { get; set; } = string.Empty;

        public long FileSizeBytes { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
