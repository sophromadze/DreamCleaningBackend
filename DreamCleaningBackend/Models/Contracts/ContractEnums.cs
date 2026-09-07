namespace DreamCleaningBackend.Models.Contracts
{
    /// <summary>
    /// Lifecycle of a commercial contract. Every transition writes a <see cref="ContractAuditLog"/>
    /// row with a human-readable description — see ContractService.
    /// </summary>
    public enum ContractStatus
    {
        Draft = 0,
        PreviewGenerated = 1,
        AwaitingClientReview = 2,
        NeedsRevision = 3,
        ReadyForSignature = 4,
        AwaitingSignatures = 5,
        PartiallySigned = 6,
        FullySigned = 7,
        Completed = 8,
        Voided = 9,
        Expired = 10
    }

    /// <summary>
    /// What a <see cref="ContractContact"/> is for. A contact is always a PERSON — the company
    /// they sign for lives on <see cref="ContractClient"/> / <see cref="ContractorProfile"/>.
    /// </summary>
    public enum ContractContactRole
    {
        ContractorSigner = 0,
        ClientSigner = 1,
        Other = 2
    }

    /// <summary>Which side of the signature block a signer row represents.</summary>
    public enum ContractSignerRole
    {
        ContractorSigner = 0,
        ClientSigner = 1
    }

    /// <summary>
    /// Voided means the version this signer belonged to was superseded. A superseded signature
    /// never carries forward — the signer gets a fresh row (and a fresh token) on the new version.
    /// </summary>
    public enum ContractSignerStatus
    {
        Pending = 0,
        Signed = 1,
        Voided = 2
    }

    /// <summary>How the signature mark was produced. Upload is deliberately not offered.</summary>
    public enum ContractSignatureMethod
    {
        Draw = 0,
        Type = 1
    }

    /// <summary>
    /// WHERE a signature was captured. Recorded on every signature so the audit trail can always
    /// distinguish the three routes afterwards, which matters because they authenticate the signer
    /// in completely different ways.
    ///
    /// <see cref="EmailLink"/> is the original and remains the default path for every signer type:
    /// the opaque per-signer token is the only credential. <see cref="AdminPanel"/> and
    /// <see cref="CustomerPortal"/> are authenticated sessions, where identity comes from the
    /// logged-in account instead of a token.
    /// </summary>
    public enum ContractSignatureChannel
    {
        /// <summary>Signed from the emailed token link. Works for every signer, always.</summary>
        EmailLink = 0,

        /// <summary>A CEO/CTO contractor signer signing from inside the admin panel.</summary>
        AdminPanel = 1,

        /// <summary>A business customer signing from their own My Contracts area.</summary>
        CustomerPortal = 2
    }

    /// <summary>Who performed an audited action.</summary>
    public enum ContractActorType
    {
        Admin = 0,
        Client = 1,
        System = 2
    }

    /// <summary>Kind of generated artefact stored under <see cref="ContractFile"/>.</summary>
    public enum ContractFileType
    {
        Preview = 0,
        FinalExecuted = 1,
        AuditCertificate = 2
    }

    /// <summary>
    /// How the admin entered the price on the Pricing &amp; Payment panel. The server derives
    /// pre-tax / tax / total / cancellation / remaining / lockout from this plus the rate —
    /// a client-submitted computed figure is never trusted.
    /// </summary>
    public enum ContractPriceMode
    {
        /// <summary>The amount typed is the tax-INCLUSIVE total the client pays per visit.</summary>
        TaxInclusive = 0,
        /// <summary>The amount typed is the pre-tax service fee; tax is added on top.</summary>
        PreTax = 1
    }
}
