using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Models.Contracts;
using DreamCleaningBackend.Services.Contracts;

namespace DreamCleaningBackend.Helpers.Contracts
{
    /// <summary>
    /// THE RULE THAT DECIDES WHETHER A CLIENT EDIT COSTS A VERSION.
    ///
    /// On the review page a client can change ten fields. Five of them describe the PERSON who
    /// signs - name, title, email, phone - and correcting one is a typo fix: it is applied to the
    /// current version in place and the client can carry straight on to signature. The other five
    /// describe the COMPANY - its legal entity name and its principal address - and changing one
    /// changes who the agreement is with and where notice is served. That is a contract
    /// modification: it produces a new version, drops the contract back to NeedsRevision, and an
    /// admin has to approve it before anybody can sign.
    ///
    /// Kept pure (no context, no service) for the same reason the other policy helpers in this
    /// folder are: the rule is the expensive part to get wrong, and it can then be asserted
    /// directly rather than through a database and a mail server.
    ///
    /// Scope, price and term never reach this policy - the review page does not expose them, and
    /// only an admin can move them.
    /// </summary>
    public static class ContractClientEditPolicy
    {
        /// <summary>
        /// The company-level fields the client changed, named the way the audit row and the
        /// revision-request email report them. Empty means the edit was personal-only.
        ///
        /// Comparison is trimmed and case-insensitive: retyping "chick tastic llc" over
        /// "Chick Tastic LLC" is not a renegotiation, and treating it as one would put the
        /// contract through a pointless approval cycle.
        /// </summary>
        public static List<string> DetectModifications(ContractSnapshot snapshot, ClientReviewInfoDto dto)
        {
            var changes = new List<string>();
            if (snapshot == null || dto == null) return changes;

            if (Differs(snapshot.Client.LegalEntityName, dto.CompanyLegalName)) changes.Add("Legal entity name");
            if (Differs(snapshot.Client.PrincipalAddress, dto.CompanyAddress)) changes.Add("Company address");
            if (Differs(snapshot.Client.City, dto.City)) changes.Add("Company city");
            if (Differs(snapshot.Client.State, dto.State)) changes.Add("Company state");
            if (Differs(snapshot.Client.Zip, dto.Zip)) changes.Add("Company ZIP");
            return changes;
        }

        /// <summary>True when the edit is a contract modification rather than a personal correction.</summary>
        public static bool IsContractModification(ContractSnapshot snapshot, ClientReviewInfoDto dto) =>
            DetectModifications(snapshot, dto).Count > 0;

        /// <summary>
        /// Applies the person-level corrections. The client's own notice email and phone follow
        /// the signer, because Section 32 serves notice on the address the client just corrected.
        /// </summary>
        public static void ApplyPersonalFields(ContractSnapshot snapshot, ClientReviewInfoDto dto)
        {
            snapshot.ClientSigner.FirstName = Trimmed(dto.FirstName) ?? snapshot.ClientSigner.FirstName;
            snapshot.ClientSigner.LastName = Trimmed(dto.LastName) ?? snapshot.ClientSigner.LastName;
            snapshot.ClientSigner.Title = Trimmed(dto.Title);
            snapshot.ClientSigner.Email = Trimmed(dto.Email);
            snapshot.ClientSigner.Phone = Trimmed(dto.Phone);

            if (!string.IsNullOrWhiteSpace(dto.Phone)) snapshot.Client.Phone = dto.Phone.Trim();
            if (!string.IsNullOrWhiteSpace(dto.Email)) snapshot.Client.NoticeEmail = dto.Email.Trim();
        }

        /// <summary>
        /// Applies the company-level changes. Only ever called on a snapshot that is about to
        /// become a NEW version - writing these into an already-approved or signed version is the
        /// exact thing the versioning exists to prevent.
        /// </summary>
        public static void ApplyCompanyFields(ContractSnapshot snapshot, ClientReviewInfoDto dto)
        {
            snapshot.Client.LegalEntityName = Trimmed(dto.CompanyLegalName) ?? snapshot.Client.LegalEntityName;
            snapshot.Client.PrincipalAddress = Trimmed(dto.CompanyAddress) ?? snapshot.Client.PrincipalAddress;
            if (!string.IsNullOrWhiteSpace(dto.City)) snapshot.Client.City = dto.City.Trim();
            if (!string.IsNullOrWhiteSpace(dto.State)) snapshot.Client.State = dto.State.Trim();
            if (!string.IsNullOrWhiteSpace(dto.Zip)) snapshot.Client.Zip = dto.Zip.Trim();
        }

        /// <summary>
        /// Whether the client may still edit anything at all. Once either party has signed, the
        /// document is the thing that was signed and stops being editable from the review page.
        /// </summary>
        public static bool ClientMayEdit(ContractStatus status) => status is
            ContractStatus.PreviewGenerated or ContractStatus.AwaitingClientReview or
            ContractStatus.NeedsRevision or ContractStatus.ReadyForSignature or
            ContractStatus.AwaitingSignatures;

        private static bool Differs(string? a, string? b) =>
            !string.Equals((a ?? string.Empty).Trim(), (b ?? string.Empty).Trim(),
                StringComparison.OrdinalIgnoreCase);

        private static string? Trimmed(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
