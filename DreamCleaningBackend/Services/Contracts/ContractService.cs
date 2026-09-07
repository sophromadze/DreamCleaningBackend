using System.Security.Cryptography;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Helpers.Contracts;
using DreamCleaningBackend.Models.Contracts;
using Microsoft.EntityFrameworkCore;

namespace DreamCleaningBackend.Services.Contracts
{
    /// <summary>Thrown for a rule violation an admin or client can act on; surfaced as a 400.</summary>
    public class ContractWorkflowException : Exception
    {
        public ContractWorkflowException(string message) : base(message) { }
    }

    /// <summary>
    /// The single owner of the contract lifecycle. Controllers translate HTTP; every rule about
    /// what may happen to a contract - version creation, snapshot freezing, locking, signing,
    /// execution - lives here so there is one place the workflow can be reasoned about.
    /// </summary>
    public class ContractService
    {
        private readonly ApplicationDbContext _context;
        private readonly ContractPdfService _pdf;
        private readonly ContractStorage _storage;
        private readonly ContractNotificationService _notifications;
        private readonly ILogger<ContractService> _logger;

        /// <summary>Default life of a signing link. Long enough for a real countersigning cycle.</summary>
        private const int DefaultSigningExpiryDays = 30;

        /// <summary>Life of a client review link. Longer, because review precedes negotiation.</summary>
        private const int ReviewExpiryDays = 90;

        /// <summary>
        /// What a signer attests to. Names the three things that make an electronic signature
        /// binding: that they read the agreement, accept its terms, and intend the mark they made
        /// to BE their signature.
        ///
        /// Must stay identical to the default in SignatureCaptureComponent — the token page shows
        /// this string, the two authenticated pages show the component's, and a signer's consent
        /// must not depend on which door they came through.
        /// </summary>
        public const string ConsentText =
            "I have reviewed this Agreement, agree to its terms, and adopt the signature above " +
            "as my electronic signature with the intent to be legally bound.";

        public ContractService(
            ApplicationDbContext context,
            ContractPdfService pdf,
            ContractStorage storage,
            ContractNotificationService notifications,
            ILogger<ContractService> logger)
        {
            _context = context;
            _pdf = pdf;
            _storage = storage;
            _notifications = notifications;
            _logger = logger;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Draft create / edit
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Creates a contract, or replaces the working draft of an existing one. Resolving the
        /// client / location / signer rows happens here (creating them when the form carried new
        /// ones), and the resulting snapshot is stored as the DRAFT - no version is generated and
        /// nobody is notified.
        /// </summary>
        public async Task<Contract> SaveDraftAsync(int? contractId, SaveContractDto dto, int adminId)
        {
            var template = await _context.ContractTemplates.FirstOrDefaultAsync(t => t.Id == dto.ContractTemplateId)
                ?? throw new ContractWorkflowException("Select a contract template.");

            var contractorProfile = await _context.ContractorProfiles
                .FirstOrDefaultAsync(p => p.Id == dto.ContractorProfileId)
                ?? throw new ContractWorkflowException("Select a contractor profile.");

            var client = await ResolveClientAsync(dto);
            var location = await ResolveServiceLocationAsync(dto, client.Id);
            var contractorSigner = await ResolveContractorSignerAsync(dto, contractorProfile);
            var clientSigner = await ResolveClientSignerAsync(dto, client.Id);

            ScopeTemplate? scopeTemplate = null;
            if (dto.ScopeTemplateId.HasValue)
                scopeTemplate = await _context.ScopeTemplates.FirstOrDefaultAsync(t => t.Id == dto.ScopeTemplateId);

            Contract contract;
            var isNew = !contractId.HasValue;
            if (isNew)
            {
                contract = new Contract
                {
                    ContractNumber = await GenerateContractNumberAsync(),
                    CreatedByAdminId = adminId,
                    Status = ContractStatus.Draft
                };
                _context.Contracts.Add(contract);
            }
            else
            {
                contract = await _context.Contracts.FirstOrDefaultAsync(c => c.Id == contractId!.Value)
                    ?? throw new ContractWorkflowException("Contract not found.");

                if (!CanEdit(contract.Status))
                    throw new ContractWorkflowException(
                        "This contract is locked. Create a revision before editing it.");
            }

            contract.ContractTemplateId = template.Id;
            contract.ScopeTemplateId = scopeTemplate?.Id;
            contract.ContractorProfileId = contractorProfile.Id;
            contract.ContractClientId = client.Id;
            contract.ContractServiceLocationId = location.Id;
            contract.UpdatedAt = DateTime.UtcNow;

            var snapshot = BuildSnapshot(contract, dto, template, contractorProfile, client, location,
                contractorSigner, clientSigner, scopeTemplate);
            contract.DraftSnapshotJson = snapshot.ToJson();

            await _context.SaveChangesAsync();

            await AuditAsync(contract.Id,
                isNew ? "ContractCreated" : "DraftUpdated",
                isNew
                    ? $"Contract created by {await AdminNameAsync(adminId)}"
                    : $"Draft edited by {await AdminNameAsync(adminId)}",
                ContractActorType.Admin, await AdminNameAsync(adminId));

            return contract;
        }

        /// <summary>
        /// Renders the current draft as a NEW version. Version 1 on the first call; every later
        /// call increments and supersedes. Never notifies anyone - a preview is internal.
        /// </summary>
        public async Task<ContractVersion> GeneratePreviewAsync(int contractId, int adminId)
        {
            var contract = await LoadContractAsync(contractId);

            if (!CanEdit(contract.Status))
                throw new ContractWorkflowException(
                    "This contract is locked. Create a revision before generating a new preview.");

            var snapshot = ContractSnapshot.Parse(contract.DraftSnapshotJson);
            if (snapshot.Pricing == null) snapshot.Pricing = new PricingSnapshot();

            // Recompute rather than trust: the draft may have been written by an older client
            // build, and the derived figures are quoted verbatim in Sections 14 and 15.
            ContractPricingCalculator.Recalculate(snapshot.Pricing);

            var previousVersion = await _context.ContractVersions
                .Where(v => v.ContractId == contract.Id)
                .OrderByDescending(v => v.VersionNumber)
                .FirstOrDefaultAsync();

            var versionNumber = (previousVersion?.VersionNumber ?? 0) + 1;
            snapshot.VersionNumber = versionNumber;
            snapshot.ContractNumber = contract.ContractNumber;

            var rendered = ContractRenderer.Render(snapshot);

            if (previousVersion != null)
            {
                previousVersion.IsSuperseded = true;
                // A signature belongs to the exact text it was applied to, so superseding the
                // version it lives on voids it. This is why signer rows hang off the version.
                await VoidSignersAsync(previousVersion.Id);
            }

            var version = new ContractVersion
            {
                ContractId = contract.Id,
                VersionNumber = versionNumber,
                FullSnapshotJson = snapshot.ToJson(),
                RenderedDocumentHtml = rendered.Html,
                DocumentHashSha256 = rendered.Sha256,
                GeneratedAt = DateTime.UtcNow,
                GeneratedByAdminId = adminId
            };
            _context.ContractVersions.Add(version);
            await _context.SaveChangesAsync();

            contract.CurrentVersionId = version.Id;
            contract.DraftSnapshotJson = snapshot.ToJson();
            contract.Status = ContractStatus.PreviewGenerated;
            contract.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            await StorePreviewPdfAsync(contract, version, snapshot, rendered);

            await AuditAsync(contract.Id, "VersionGenerated",
                $"Version {versionNumber} generated",
                ContractActorType.Admin, await AdminNameAsync(adminId), version.Id);

            return version;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Sending
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>Approves the current version and emails the client their review link.</summary>
        public async Task SendForClientReviewAsync(int contractId, int adminId)
        {
            var contract = await LoadContractAsync(contractId);
            var version = await RequireCurrentVersionAsync(contract);
            var snapshot = ContractSnapshot.Parse(version.FullSnapshotJson);

            if (contract.Status is ContractStatus.Voided or ContractStatus.Completed
                or ContractStatus.FullySigned or ContractStatus.PartiallySigned)
                throw new ContractWorkflowException("This contract can no longer be sent for review.");

            var email = snapshot.ClientSigner.Email ?? snapshot.Client.NoticeEmail;
            if (string.IsNullOrWhiteSpace(email))
                throw new ContractWorkflowException(
                    "The client has no email address on file, so the review link cannot be sent.");

            if (string.IsNullOrEmpty(contract.ClientReviewToken))
                contract.ClientReviewToken = GenerateToken();
            contract.ClientReviewTokenExpiresAt = DateTime.UtcNow.AddDays(ReviewExpiryDays);
            contract.Status = ContractStatus.AwaitingClientReview;
            contract.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            await AuditAsync(contract.Id, "ApprovedForReview",
                $"Approved and sent for client review by {await AdminNameAsync(adminId)}",
                ContractActorType.Admin, await AdminNameAsync(adminId), version.Id);

            await _notifications.SendClientReviewInvitationAsync(
                email,
                string.IsNullOrWhiteSpace(snapshot.ClientSigner.FullName)
                    ? snapshot.Client.LegalEntityName
                    : snapshot.ClientSigner.FullName,
                contract.ContractNumber,
                ContractorDisplayName(snapshot),
                // A client with a portal account is sent to their own authenticated page; everyone
                // else gets the token link. The token is minted either way above, so the emailed
                // fallback keeps working for a colleague they forward this to.
                await BuildClientContractUrlAsync(contract));
        }

        /// <summary>
        /// Locks the contract, creates one signer row + unguessable token per party on the CURRENT
        /// version, and emails each their own link. Either party may then sign first.
        /// </summary>
        public async Task SendForSignatureAsync(int contractId, int adminId, int? expiryDays)
        {
            var contract = await LoadContractAsync(contractId);
            var version = await RequireCurrentVersionAsync(contract);
            var snapshot = ContractSnapshot.Parse(version.FullSnapshotJson);

            if (contract.Status is ContractStatus.Voided or ContractStatus.Completed
                or ContractStatus.FullySigned)
                throw new ContractWorkflowException("This contract is already closed.");

            if (contract.Status is ContractStatus.AwaitingSignatures or ContractStatus.PartiallySigned)
                throw new ContractWorkflowException("Signing is already in progress for this version.");

            if (string.IsNullOrWhiteSpace(snapshot.ContractorSigner.Email))
                throw new ContractWorkflowException("The contractor signer has no email address on file.");
            if (string.IsNullOrWhiteSpace(snapshot.ClientSigner.Email))
                throw new ContractWorkflowException("The client signer has no email address on file.");

            var existing = await _context.ContractSigners
                .Where(s => s.ContractVersionId == version.Id)
                .ToListAsync();
            if (existing.Any(s => s.Status != ContractSignerStatus.Voided))
                throw new ContractWorkflowException("Signing links already exist for this version.");

            var expires = DateTime.UtcNow.AddDays(expiryDays is > 0 ? expiryDays!.Value : DefaultSigningExpiryDays);

            var contractorRow = NewSigner(version.Id, ContractSignerRole.ContractorSigner,
                snapshot.ContractorSigner, expires);
            var clientRow = NewSigner(version.Id, ContractSignerRole.ClientSigner,
                snapshot.ClientSigner, expires);
            _context.ContractSigners.AddRange(contractorRow, clientRow);

            contract.Status = ContractStatus.ReadyForSignature;
            contract.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            var adminName = await AdminNameAsync(adminId);
            await AuditAsync(contract.Id, "ApprovedForSigning",
                $"Approved for signing by {adminName}", ContractActorType.Admin, adminName, version.Id);

            await _notifications.SendSignatureInvitationAsync(
                contractorRow.InvitedEmail!, contractorRow.InvitedName, contract.ContractNumber,
                $"{ContractorDisplayName(snapshot)} and {snapshot.Client.LegalEntityName}",
                _notifications.BuildSigningUrl(contractorRow.SigningToken), expires);

            await _notifications.SendSignatureInvitationAsync(
                clientRow.InvitedEmail!, clientRow.InvitedName, contract.ContractNumber,
                $"{ContractorDisplayName(snapshot)} and {snapshot.Client.LegalEntityName}",
                _notifications.BuildSigningUrl(clientRow.SigningToken), expires);

            var sentAt = DateTime.UtcNow;
            contractorRow.InviteSentAt = sentAt;
            clientRow.InviteSentAt = sentAt;
            contract.Status = ContractStatus.AwaitingSignatures;
            await _context.SaveChangesAsync();

            await AuditAsync(contract.Id, "SigningLinksSent",
                $"Signing links sent to {contractorRow.InvitedName} and {clientRow.InvitedName}",
                ContractActorType.System, "System", version.Id);
        }

        /// <summary>
        /// Unlocks a contract that had already been sent for signature. Every outstanding signing
        /// link on the current version is voided immediately - a link that survived the decision
        /// to revise would let somebody sign a document nobody intends to execute any more.
        /// </summary>
        public async Task CreateRevisionAsync(int contractId, int adminId)
        {
            var contract = await LoadContractAsync(contractId);

            if (contract.Status is ContractStatus.Completed or ContractStatus.FullySigned)
                throw new ContractWorkflowException(
                    "A fully executed contract can never be revised. Create an amendment or duplicate it instead.");
            if (contract.Status == ContractStatus.Voided)
                throw new ContractWorkflowException("This contract is void.");

            if (contract.CurrentVersionId.HasValue)
                await VoidSignersAsync(contract.CurrentVersionId.Value);

            contract.Status = ContractStatus.NeedsRevision;
            contract.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            var adminName = await AdminNameAsync(adminId);
            await AuditAsync(contract.Id, "RevisionStarted",
                $"Reopened for revision by {adminName}; outstanding signing links voided",
                ContractActorType.Admin, adminName, contract.CurrentVersionId);
        }

        /// <summary>
        /// Soft-deletes a contract: it drops out of the default list and can be restored by the
        /// CTO, and the retention job hard-deletes it after six months.
        ///
        /// This REPLACED the old Void action, but it deliberately kept Void's two protective side
        /// effects. Deleting revokes the outstanding signing links and clears the client review
        /// token, because a contract somebody has decided to delete must not stay signable from an
        /// email already sitting in a counterparty's inbox. Hiding alone would have left those
        /// links live — the one thing that would have made this a downgrade rather than a rename.
        ///
        /// Executed contracts are deletable, unlike Void: this is housekeeping on a finished
        /// record, the document and its files survive the six months, and the deletion is logged.
        /// </summary>
        public async Task DeleteAsync(int contractId, int adminId)
        {
            var contract = await LoadContractAsync(contractId);
            if (contract.IsHidden) return;

            if (contract.CurrentVersionId.HasValue)
                await VoidSignersAsync(contract.CurrentVersionId.Value);

            contract.IsHidden = true;
            contract.HiddenAt = DateTime.UtcNow;
            contract.HiddenByUserId = adminId;
            contract.ClientReviewToken = null;
            contract.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            var adminName = await AdminNameAsync(adminId);
            await AuditAsync(contract.Id, "Deleted",
                $"Contract deleted by {adminName}", ContractActorType.Admin, adminName);
        }

        /// <summary>
        /// Puts a hidden contract back in the list and restarts nothing else: the signing links it
        /// lost when it was deleted stay revoked, because reviving a link somebody was told was
        /// dead is worse than asking an admin to send it again.
        /// </summary>
        public async Task RestoreAsync(int contractId, int adminId)
        {
            var contract = await LoadContractAsync(contractId);
            if (!contract.IsHidden) return;

            contract.IsHidden = false;
            contract.HiddenAt = null;
            contract.HiddenByUserId = null;
            contract.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            var adminName = await AdminNameAsync(adminId);
            await AuditAsync(contract.Id, "Restored",
                $"Contract restored by {adminName}", ContractActorType.Admin, adminName);
        }

        /// <summary>
        /// Copies a contract into a fresh Draft with a new number. The source contract and its
        /// files are never touched - which is the whole point after execution.
        /// </summary>
        public async Task<Contract> DuplicateAsync(int contractId, int adminId, bool asAmendment)
        {
            var source = await LoadContractAsync(contractId);
            var snapshot = ContractSnapshot.Parse(
                source.CurrentVersionId.HasValue
                    ? (await _context.ContractVersions.FirstAsync(v => v.Id == source.CurrentVersionId)).FullSnapshotJson
                    : source.DraftSnapshotJson);

            var copy = new Contract
            {
                ContractNumber = await GenerateContractNumberAsync(),
                ContractClientId = source.ContractClientId,
                ContractServiceLocationId = source.ContractServiceLocationId,
                ContractorProfileId = source.ContractorProfileId,
                ContractTemplateId = source.ContractTemplateId,
                ScopeTemplateId = source.ScopeTemplateId,
                Status = ContractStatus.Draft,
                CreatedByAdminId = adminId,
                DuplicatedFromContractId = source.Id
            };

            snapshot.ContractNumber = copy.ContractNumber;
            snapshot.VersionNumber = 1;
            copy.DraftSnapshotJson = snapshot.ToJson();

            _context.Contracts.Add(copy);
            await _context.SaveChangesAsync();

            var adminName = await AdminNameAsync(adminId);
            var label = asAmendment ? "Amendment" : "Duplicate";
            await AuditAsync(copy.Id, asAmendment ? "AmendmentCreated" : "DuplicateCreated",
                $"{label} of {source.ContractNumber} created by {adminName}",
                ContractActorType.Admin, adminName);
            await AuditAsync(source.Id, asAmendment ? "AmendmentStarted" : "DuplicatedFrom",
                $"{label} {copy.ContractNumber} created from this contract by {adminName}",
                ContractActorType.Admin, adminName);

            return copy;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Client-facing (token addressed)
        // ══════════════════════════════════════════════════════════════════════

        public async Task<(Contract contract, ContractVersion version, ContractSnapshot snapshot)>
            ResolveReviewTokenAsync(string token)
        {
            var contract = await _context.Contracts
                .FirstOrDefaultAsync(c => c.ClientReviewToken == token)
                ?? throw new ContractWorkflowException("This review link is not valid.");

            if (contract.ClientReviewTokenExpiresAt.HasValue &&
                contract.ClientReviewTokenExpiresAt.Value < DateTime.UtcNow)
                throw new ContractWorkflowException("This review link has expired. Please ask us for a new one.");

            if (contract.Status == ContractStatus.Voided || contract.IsHidden)
                throw new ContractWorkflowException("This agreement is no longer active.");

            var version = await RequireCurrentVersionAsync(contract);
            return (contract, version, ContractSnapshot.Parse(version.FullSnapshotJson));
        }

        /// <summary>
        /// Applies a client's edit of their own information block.
        ///
        /// Correcting a name, title, email or phone is a PERSONAL correction: it is patched
        /// straight into the current version's snapshot and no new version is created. Changing
        /// the legal entity name or the company address is a CONTRACT MODIFICATION: it produces a
        /// new version, drops the contract back to NeedsRevision and requires admin re-approval
        /// before it can be signed again. A scope, price or term change never reaches this method
        /// at all - the client review page does not expose those fields.
        /// </summary>
        public async Task<ClientEditResultDto> ApplyClientEditAsync(string token, ClientReviewInfoDto dto, string? ip)
        {
            var (contract, version, snapshot) = await ResolveReviewTokenAsync(token);
            return await ApplyClientEditCoreAsync(contract, version, snapshot, dto, ip);
        }

        /// <summary>
        /// The same edit, from an AUTHENTICATED customer session. Ownership is proved by the
        /// caller before this is reached.
        ///
        /// Deliberately not routed through the review token: that token has an expiry, which is a
        /// property of the emailed LINK and has nothing to do with a logged-in customer. Sending
        /// them through it would refuse the edit with "this review link has expired" for someone
        /// who never used a link.
        /// </summary>
        public async Task<ClientEditResultDto> ApplyClientEditFromPortalAsync(
            int contractId, ClientReviewInfoDto dto, string? ip)
        {
            var contract = await LoadContractAsync(contractId);
            if (contract.Status == ContractStatus.Voided || contract.IsHidden)
                throw new ContractWorkflowException("This agreement is no longer active.");

            var version = await RequireCurrentVersionAsync(contract);
            var snapshot = ContractSnapshot.Parse(version.FullSnapshotJson);
            return await ApplyClientEditCoreAsync(contract, version, snapshot, dto, ip);
        }

        private async Task<ClientEditResultDto> ApplyClientEditCoreAsync(
            Contract contract, ContractVersion version, ContractSnapshot snapshot,
            ClientReviewInfoDto dto, string? ip)
        {
            if (contract.Status is ContractStatus.FullySigned or ContractStatus.Completed)
                throw new ContractWorkflowException("This agreement has been executed and can no longer be edited.");
            if (contract.Status is ContractStatus.PartiallySigned)
                throw new ContractWorkflowException(
                    "This agreement has already been signed by one party. Please contact us to make a change.");

            // The personal-vs-modification rule lives in ContractClientEditPolicy, pure and
            // asserted directly — it is the expensive one to get wrong.
            var modifications = ContractClientEditPolicy.DetectModifications(snapshot, dto);
            var actor = string.IsNullOrWhiteSpace(dto.Email) ? dto.FirstName : dto.Email!;

            ContractClientEditPolicy.ApplyPersonalFields(snapshot, dto);

            if (modifications.Count == 0)
            {
                // Personal-only: patch the CURRENT version in place. The document text changes
                // (the signature block names the signer), so the stored hash is refreshed with it.
                var rerendered = ContractRenderer.Render(snapshot);
                version.FullSnapshotJson = snapshot.ToJson();
                version.RenderedDocumentHtml = rerendered.Html;
                version.DocumentHashSha256 = rerendered.Sha256;
                contract.DraftSnapshotJson = snapshot.ToJson();
                contract.UpdatedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync();

                await AuditAsync(contract.Id, "ClientDetailsEdited",
                    $"Client details edited by {dto.FirstName} {dto.LastName}".Trim(),
                    ContractActorType.Client, actor, version.Id, ip);

                return new ClientEditResultDto
                {
                    CreatedRevision = false,
                    VersionNumber = version.VersionNumber,
                    Status = contract.Status,
                    Message = "Your details have been updated."
                };
            }

            // Contract modification. Apply the company changes to the draft and generate a fresh
            // version, so the approved version on file is never silently rewritten.
            ContractClientEditPolicy.ApplyCompanyFields(snapshot, dto);
            contract.DraftSnapshotJson = snapshot.ToJson();
            await _context.SaveChangesAsync();

            var newVersion = await GenerateRevisionFromClientAsync(contract, snapshot, actor, ip);

            contract.Status = ContractStatus.NeedsRevision;
            contract.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            await AuditAsync(contract.Id, "RevisionRequested",
                $"Contract modification requested by {actor}: {string.Join(", ", modifications)}",
                ContractActorType.Client, actor, newVersion.Id, ip);

            var contractorProfile = await _context.ContractorProfiles
                .FirstOrDefaultAsync(p => p.Id == contract.ContractorProfileId);
            await _notifications.SendRevisionRequestedAsync(
                contractorProfile?.NoticeEmail ?? string.Empty,
                contract.ContractNumber,
                snapshot.Client.LegalEntityName,
                modifications,
                $"{_notifications.FrontendUrl}/admin/contracts/{contract.Id}");

            return new ClientEditResultDto
            {
                CreatedRevision = true,
                VersionNumber = newVersion.VersionNumber,
                Status = ContractStatus.NeedsRevision,
                ChangedFields = modifications,
                Message = "Thank you. Because this changes the agreement itself, we have prepared a revised " +
                          "version and our team will confirm it with you before signing."
            };
        }

        public async Task<ContractSigner> ResolveSigningTokenAsync(string token)
        {
            return await _context.ContractSigners
                .Include(s => s.Signature)
                .Include(s => s.ContractVersion)!
                    .ThenInclude(v => v!.Contract)
                .FirstOrDefaultAsync(s => s.SigningToken == token)
                ?? throw new ContractWorkflowException("This signing link is not valid.");
        }

        /// <summary>
        /// Records one signature against one version. The document hash stored here is the hash of
        /// the version being signed, so a later revision can be proved not to be what was signed.
        /// </summary>
        public async Task<SignContractResultDto> SignAsync(
            string token, SignContractDto dto, string? ip, string? userAgent)
        {
            var signer = await ResolveSigningTokenAsync(token);

            // Token expiry is a property of the LINK, so it is checked only on this path. An
            // authenticated signer has a live session instead of a mailed credential, and an
            // expired link must not lock them out of a contract they can already see.
            if (signer.Status != ContractSignerStatus.Signed && signer.TokenExpiresAt < DateTime.UtcNow)
                throw new ContractWorkflowException("This signing link has expired. Please ask us for a new one.");

            return await RecordSignatureAsync(
                signer, dto, ip, userAgent, ContractSignatureChannel.EmailLink, signedByUserId: null);
        }

        /// <summary>
        /// Records one signature, whatever route it arrived by. The three channels differ ONLY in
        /// how the signer was identified before reaching here - a mailed token, an admin session,
        /// or a customer session. What lands in the evidence row is identical, which is the point:
        /// the audit trail must not get weaker because somebody signed from inside the app.
        /// </summary>
        private async Task<SignContractResultDto> RecordSignatureAsync(
            ContractSigner signer, SignContractDto dto, string? ip, string? userAgent,
            ContractSignatureChannel channel, int? signedByUserId)
        {
            var version = signer.ContractVersion
                ?? throw new ContractWorkflowException("This signing link is not valid.");
            var contract = version.Contract
                ?? throw new ContractWorkflowException("This signing link is not valid.");

            if (signer.Status == ContractSignerStatus.Voided || version.IsSuperseded)
                throw new ContractWorkflowException(
                    "This version has been replaced. Please use the most recent link we sent you.");
            if (signer.Status == ContractSignerStatus.Signed)
                throw new ContractWorkflowException("You have already signed this agreement.");
            if (contract.Status == ContractStatus.Voided || contract.IsHidden)
                throw new ContractWorkflowException("This agreement is no longer active.");
            if (!dto.ConsentAccepted)
                throw new ContractWorkflowException("Please confirm the electronic signature consent before signing.");
            if (string.IsNullOrWhiteSpace(dto.SignatureData))
                throw new ContractWorkflowException("Please draw or type your signature.");

            var snapshot = ContractSnapshot.Parse(version.FullSnapshotJson);

            // The contractor's name and title come from the contractor profile and are not the
            // signer's to change; the client's title genuinely may still need filling in.
            var name = signer.Role == ContractSignerRole.ContractorSigner
                ? signer.InvitedName
                : Trim(dto.SignerName, 200, signer.InvitedName);
            var title = signer.Role == ContractSignerRole.ContractorSigner
                ? signer.InvitedTitle
                : Trim(dto.SignerTitle, 120, signer.InvitedTitle);

            var signature = new ContractSignature
            {
                ContractSignerId = signer.Id,
                SignerNameAtSigning = name,
                SignerTitleAtSigning = title,
                SignerEmailAtSigning = Trim(dto.SignerEmail, 255, signer.InvitedEmail),
                SignatureImageOrTypedText = dto.SignatureData,
                SignatureMethod = dto.SignatureMethod,
                SignedAt = DateTime.UtcNow,
                IpAddress = Truncate(ip, 45),
                UserAgent = Truncate(userAgent, 500),
                DocumentHashAtSigning = version.DocumentHashSha256,
                ConsentAccepted = true,
                SigningChannel = channel,
                SignedByUserId = signedByUserId
            };
            _context.ContractSignatures.Add(signature);
            signer.Status = ContractSignerStatus.Signed;

            // Update the snapshot's signer record so the executed document prints what was typed.
            if (signer.Role == ContractSignerRole.ClientSigner)
            {
                snapshot.ClientSigner.Title = title;
                version.FullSnapshotJson = snapshot.ToJson();
            }

            var siblings = await _context.ContractSigners
                .Where(s => s.ContractVersionId == version.Id && s.Status != ContractSignerStatus.Voided)
                .ToListAsync();
            var signedCount = siblings.Count(s => s.Status == ContractSignerStatus.Signed);
            var allSigned = signedCount == siblings.Count && siblings.Count > 0;

            contract.Status = allSigned ? ContractStatus.FullySigned : ContractStatus.PartiallySigned;
            contract.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            // The timeline names the route, because "signed from the admin panel" and "signed from
            // the emailed link" are materially different claims about how they were identified.
            var via = channel switch
            {
                ContractSignatureChannel.AdminPanel => " from the admin panel",
                ContractSignatureChannel.CustomerPortal => " from their account",
                _ => ""
            };
            await AuditAsync(contract.Id, "Signed",
                $"{FirstNameOf(name)} signed{via}",
                channel == ContractSignatureChannel.AdminPanel ? ContractActorType.Admin : ContractActorType.Client,
                signature.SignerEmailAtSigning ?? name, version.Id, ip);

            var message = "Thank you. Your signature has been recorded.";
            if (allSigned)
            {
                await CompleteAsync(contract, version, snapshot);
                message = "Thank you. Both parties have now signed and the executed agreement is on its way to you.";
            }

            return new SignContractResultDto
            {
                Status = contract.Status,
                StatusLabel = StatusLabel(contract.Status),
                FullyExecuted = allSigned,
                SignedAt = signature.SignedAt,
                Message = message
            };
        }

        /// <summary>
        /// Finds the signer row an AUTHENTICATED account is entitled to sign on the current
        /// version, or null. Deliberately matches on the frozen <see cref="ContractSigner.UserId"/>
        /// and the role, never on a matching email address - an address is not an identity claim.
        /// </summary>
        public async Task<ContractSigner?> FindSignerForAccountAsync(
            int contractId, int userId, ContractSignerRole role)
        {
            var contract = await _context.Contracts.FirstOrDefaultAsync(c => c.Id == contractId);
            if (contract?.CurrentVersionId == null) return null;

            return await _context.ContractSigners
                .Include(s => s.Signature)
                .Include(s => s.ContractVersion)!
                    .ThenInclude(v => v!.Contract)
                .FirstOrDefaultAsync(s =>
                    s.ContractVersionId == contract.CurrentVersionId.Value &&
                    s.Role == role &&
                    s.UserId == userId &&
                    s.Status != ContractSignerStatus.Voided);
        }

        /// <summary>
        /// A CEO/CTO signing as the contractor from inside the admin panel. A deliberate exception
        /// to the token-link-only design, and narrow: it signs the CONTRACTOR row only, only when
        /// that row names this exact account, and only while it is still pending. The mailed link
        /// keeps working in parallel and is not disturbed.
        /// </summary>
        public async Task<SignContractResultDto> SignAsContractorInAppAsync(
            int contractId, int userId, SignContractDto dto, string? ip, string? userAgent)
        {
            var signer = await FindSignerForAccountAsync(contractId, userId, ContractSignerRole.ContractorSigner)
                ?? throw new ContractWorkflowException(
                    "This contract is not awaiting your signature as the contractor.");

            return await RecordSignatureAsync(
                signer, dto, ip, userAgent, ContractSignatureChannel.AdminPanel, userId);
        }

        /// <summary>
        /// A business customer signing their own contract from My Contracts. Ownership is proved
        /// by the caller before we get here (ContractClient.SourceUserId), and this only ever
        /// resolves the CLIENT signer row - a customer can never reach the contractor side.
        /// </summary>
        public async Task<SignContractResultDto> SignAsCustomerAsync(
            int contractId, int userId, SignContractDto dto, string? ip, string? userAgent)
        {
            // The client signer row may not carry a UserId (a contract typed up before the account
            // was linked), so fall back to the role row on the current version. Ownership has
            // already been established, which is what makes that safe.
            var signer = await FindSignerForAccountAsync(contractId, userId, ContractSignerRole.ClientSigner)
                ?? await FindClientSignerOnCurrentVersionAsync(contractId)
                ?? throw new ContractWorkflowException("This contract is not awaiting your signature.");

            return await RecordSignatureAsync(
                signer, dto, ip, userAgent, ContractSignatureChannel.CustomerPortal, userId);
        }

        private async Task<ContractSigner?> FindClientSignerOnCurrentVersionAsync(int contractId)
        {
            var contract = await _context.Contracts.FirstOrDefaultAsync(c => c.Id == contractId);
            if (contract?.CurrentVersionId == null) return null;

            return await _context.ContractSigners
                .Include(s => s.Signature)
                .Include(s => s.ContractVersion)!
                    .ThenInclude(v => v!.Contract)
                .FirstOrDefaultAsync(s =>
                    s.ContractVersionId == contract.CurrentVersionId.Value &&
                    s.Role == ContractSignerRole.ClientSigner &&
                    s.Status != ContractSignerStatus.Voided);
        }

        /// <summary>
        /// Called once, when the second signature lands: renders and files the executed document,
        /// marks the contract Completed, and mails both parties. Failures here never roll back the
        /// signature — a stored signature is the legally meaningful act, and the paperwork can
        /// always be produced again afterwards.
        /// </summary>
        private async Task CompleteAsync(Contract contract, ContractVersion version, ContractSnapshot snapshot)
        {
            try
            {
                var executed = await RenderAndFileExecutedAsync(contract, version, snapshot);

                contract.Status = ContractStatus.Completed;
                contract.CompletedAt = DateTime.UtcNow;
                contract.UpdatedAt = DateTime.UtcNow;
                // The review token is deliberately KEPT. The client has no login, so it is their
                // only way back to the executed document; the review page is read-only from here
                // on because CanEdit is false for a Completed contract.
                await _context.SaveChangesAsync();

                await AuditAsync(contract.Id, "FullyExecuted",
                    "Fully executed PDF generated", ContractActorType.System, "System", version.Id);

                await SendExecutedCopyAsync(contract, version, snapshot, executed,
                    ContractActorType.System, "System");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to finalize contract {Number} after full signature.", contract.ContractNumber);
                await AuditAsync(contract.Id, "ExecutionFailed",
                    "Both parties signed, but the executed PDF could not be generated. Regenerate it from the contract page.",
                    ContractActorType.System, "System", version.Id);
            }
        }

        /// <summary>
        /// Renders the executed document + certificate and files both, returning the PDF bytes.
        /// Sends nothing and changes no status — that separation is what lets Regenerate be a
        /// genuinely non-destructive action.
        /// </summary>
        private async Task<byte[]> RenderAndFileExecutedAsync(
            Contract contract, ContractVersion version, ContractSnapshot snapshot)
        {
            var rendered = ContractRenderer.Render(snapshot);
            var block = await BuildSignatureBlockAsync(version.Id, snapshot);
            var certificate = await BuildCertificateAsync(contract, version, snapshot);

            var executed = _pdf.GenerateDocument(rendered, snapshot, block, certificate, draftWatermark: false);
            var fileName = ContractNotificationService.ExecutedFileName(
                contract.ContractNumber, snapshot.Client.LegalEntityName);
            var path = await _storage.SaveAsync(contract.ContractNumber, fileName, executed);
            await ReplaceFileAsync(version.Id, ContractFileType.FinalExecuted, path, fileName, executed.LongLength);

            var certBytes = _pdf.GenerateCertificate(snapshot, certificate);
            var certName = $"{contract.ContractNumber}-Signature-Certificate.pdf";
            var certPath = await _storage.SaveAsync(contract.ContractNumber, certName, certBytes);
            await ReplaceFileAsync(version.Id, ContractFileType.AuditCertificate, certPath, certName, certBytes.LongLength);

            return executed;
        }

        /// <summary>
        /// Where a CLIENT's "view contract" link should point, in priority order:
        ///
        ///   1. their own portal page, when the contract's client is linked to a customer account —
        ///      an authenticated page beats a bearer token whenever one is available;
        ///   2. the tokenised review page otherwise, MINTING the token if the contract never had
        ///      one (a contract sent straight to signature skips the review step and so never got
        ///      issued one).
        ///
        /// It must never fall back to the bare site URL. It used to, which is why the executed
        /// email's "View contract" button opened the marketing homepage: the SPA's `**` route
        /// redirects anything unmatched to `/`, so a bad link fails silently rather than visibly.
        /// </summary>
        private async Task<string> BuildClientContractUrlAsync(Contract contract)
        {
            var linkedUserId = await _context.ContractClients
                .Where(c => c.Id == contract.ContractClientId)
                .Select(c => c.SourceUserId)
                .FirstOrDefaultAsync();

            if (linkedUserId.HasValue)
                return $"{_notifications.FrontendUrl}/profile/contracts/{contract.Id}";

            if (string.IsNullOrEmpty(contract.ClientReviewToken))
            {
                contract.ClientReviewToken = GenerateToken();
                contract.ClientReviewTokenExpiresAt = DateTime.UtcNow.AddDays(ReviewExpiryDays);
                await _context.SaveChangesAsync();
            }

            return _notifications.BuildReviewUrl(contract.ClientReviewToken!);
        }

        /// <summary>Mails the executed copy to both parties and records that it went out.</summary>
        private async Task SendExecutedCopyAsync(
            Contract contract, ContractVersion version, ContractSnapshot snapshot,
            byte[] executed, ContractActorType actorType, string actor)
        {
            var fileName = ContractNotificationService.ExecutedFileName(
                contract.ContractNumber, snapshot.Client.LegalEntityName);

            var adminUrl = $"{_notifications.FrontendUrl}/admin/contracts/{contract.Id}";
            var clientUrl = await BuildClientContractUrlAsync(contract);

            await _notifications.SendFullyExecutedAsync(
                snapshot.ContractorSigner.Email ?? snapshot.Contractor.NoticeEmail,
                snapshot.ContractorSigner.FullName, contract.ContractNumber, adminUrl, executed, fileName);
            await _notifications.SendFullyExecutedAsync(
                snapshot.ClientSigner.Email ?? snapshot.Client.NoticeEmail ?? string.Empty,
                snapshot.ClientSigner.FullName, contract.ContractNumber,
                clientUrl, executed, fileName);

            await AuditAsync(contract.Id, "ExecutedCopySent",
                "Executed copy emailed to both parties", actorType, actor, version.Id);
        }

        /// <summary>
        /// Re-renders and re-files the executed PDF from the frozen snapshot. Deliberately SILENT:
        /// it emails nobody and moves no status, so it is safe to hand to anyone who can see the
        /// contract. Re-sending the copy is <see cref="ResendExecutedCopyAsync"/>, a separate act.
        /// </summary>
        public async Task RegenerateExecutedAsync(int contractId, int adminId)
        {
            var contract = await LoadContractAsync(contractId);
            if (contract.Status is not (ContractStatus.FullySigned or ContractStatus.Completed))
                throw new ContractWorkflowException("Only a fully signed contract has an executed copy to generate.");

            var version = await RequireCurrentVersionAsync(contract);
            var snapshot = ContractSnapshot.Parse(version.FullSnapshotJson);

            await RenderAndFileExecutedAsync(contract, version, snapshot);

            // A contract left FullySigned by an earlier failed execution becomes Completed now
            // that the document exists. Nothing is mailed; the send is its own action.
            if (contract.Status == ContractStatus.FullySigned)
            {
                contract.Status = ContractStatus.Completed;
                contract.CompletedAt ??= DateTime.UtcNow;
                contract.UpdatedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync();
            }

            var adminName = await AdminNameAsync(adminId);
            await AuditAsync(contract.Id, "ExecutedRegenerated",
                $"Executed PDF regenerated by {adminName} (not sent)",
                ContractActorType.Admin, adminName, version.Id);
        }

        /// <summary>
        /// Emails the already-filed executed copy to both parties again. Renders nothing new — if
        /// the file is missing, that is a Regenerate problem and is reported as one rather than
        /// silently producing a different document than the one on file.
        /// </summary>
        public async Task ResendExecutedCopyAsync(int contractId, int adminId)
        {
            var contract = await LoadContractAsync(contractId);
            if (contract.Status is not (ContractStatus.FullySigned or ContractStatus.Completed))
                throw new ContractWorkflowException("Only a fully executed contract has a copy to send.");

            var version = await RequireCurrentVersionAsync(contract);
            var snapshot = ContractSnapshot.Parse(version.FullSnapshotJson);

            var file = await _context.ContractFiles
                .Where(f => f.ContractVersionId == version.Id && f.FileType == ContractFileType.FinalExecuted)
                .OrderByDescending(f => f.CreatedAt)
                .FirstOrDefaultAsync();

            if (file == null || !_storage.Exists(file.FilePath))
                throw new ContractWorkflowException(
                    "The executed copy is not on file. Regenerate the executed PDF first, then send it.");

            var executed = await _storage.ReadAsync(file.FilePath);
            var adminName = await AdminNameAsync(adminId);
            await SendExecutedCopyAsync(contract, version, snapshot, executed,
                ContractActorType.Admin, adminName);
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Shared building blocks
        // ══════════════════════════════════════════════════════════════════════

        public async Task<ContractSignatureBlockDto> BuildSignatureBlockAsync(int versionId, ContractSnapshot snapshot)
        {
            var signers = await _context.ContractSigners
                .Include(s => s.Signature)
                .Where(s => s.ContractVersionId == versionId && s.Status != ContractSignerStatus.Voided)
                .ToListAsync();

            ContractSignaturePartyDto Party(ContractSignerRole role, string label, string entity,
                string fallbackName, string? fallbackTitle)
            {
                var row = signers.FirstOrDefault(s => s.Role == role);
                var sig = row?.Signature;
                return new ContractSignaturePartyDto
                {
                    PartyLabel = label,
                    EntityName = entity,
                    SignerName = sig?.SignerNameAtSigning ?? row?.InvitedName ?? fallbackName,
                    SignerTitle = sig?.SignerTitleAtSigning ?? row?.InvitedTitle ?? fallbackTitle,
                    HasSigned = sig != null,
                    SignedAt = sig?.SignedAt,
                    Method = sig?.SignatureMethod,
                    SignatureMark = sig?.SignatureImageOrTypedText ?? string.Empty
                };
            }

            return new ContractSignatureBlockDto
            {
                Contractor = Party(ContractSignerRole.ContractorSigner, "CONTRACTOR",
                    ContractorDisplayName(snapshot),
                    snapshot.ContractorSigner.FullName, snapshot.ContractorSigner.Title),
                Client = Party(ContractSignerRole.ClientSigner, "CLIENT",
                    snapshot.Client.LegalEntityName,
                    snapshot.ClientSigner.FullName, snapshot.ClientSigner.Title)
            };
        }

        private async Task<ContractCertificateData> BuildCertificateAsync(
            Contract contract, ContractVersion version, ContractSnapshot snapshot)
        {
            var signers = await _context.ContractSigners
                .Include(s => s.Signature)
                .Where(s => s.ContractVersionId == version.Id && s.Status != ContractSignerStatus.Voided)
                .OrderBy(s => s.Role)
                .ToListAsync();

            return new ContractCertificateData
            {
                ContractNumber = contract.ContractNumber,
                VersionNumber = version.VersionNumber,
                DocumentHash = version.DocumentHashSha256,
                EffectiveDate = snapshot.EffectiveDate,
                GeneratedAt = DateTime.UtcNow,
                Signers = signers.Select(s => new ContractCertificateSigner
                {
                    PartyLabel = s.Role == ContractSignerRole.ContractorSigner ? "CONTRACTOR" : "CLIENT",
                    EntityName = s.Role == ContractSignerRole.ContractorSigner
                        ? ContractorDisplayName(snapshot)
                        : snapshot.Client.LegalEntityName,
                    Name = s.Signature?.SignerNameAtSigning ?? s.InvitedName,
                    Title = s.Signature?.SignerTitleAtSigning ?? s.InvitedTitle,
                    Email = s.Signature?.SignerEmailAtSigning ?? s.InvitedEmail,
                    SignedAt = s.Signature?.SignedAt,
                    IpAddress = s.Signature?.IpAddress,
                    UserAgent = s.Signature?.UserAgent,
                    Method = s.Signature?.SignatureMethod,
                    DocumentHashAtSigning = s.Signature?.DocumentHashAtSigning,
                    ConsentAccepted = s.Signature?.ConsentAccepted ?? false
                }).ToList()
            };
        }

        private async Task StorePreviewPdfAsync(
            Contract contract, ContractVersion version, ContractSnapshot snapshot, RenderedContract rendered)
        {
            try
            {
                var block = await BuildSignatureBlockAsync(version.Id, snapshot);
                var bytes = _pdf.GenerateDocument(rendered, snapshot, block, null, draftWatermark: true);
                var fileName = $"{contract.ContractNumber}-v{version.VersionNumber}-Preview.pdf";
                var path = await _storage.SaveAsync(contract.ContractNumber, fileName, bytes);

                version.RenderedDocumentPath = path;
                await ReplaceFileAsync(version.Id, ContractFileType.Preview, path, fileName, bytes.LongLength);
                await _context.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                // A missing preview PDF must not block the workflow - the HTML preview is the
                // authoritative on-screen view and the PDF can be regenerated.
                _logger.LogError(ex, "Preview PDF generation failed for contract {Number}.", contract.ContractNumber);
            }
        }

        private async Task ReplaceFileAsync(
            int versionId, ContractFileType type, string path, string fileName, long size)
        {
            var existing = await _context.ContractFiles
                .Where(f => f.ContractVersionId == versionId && f.FileType == type)
                .ToListAsync();
            if (existing.Count > 0) _context.ContractFiles.RemoveRange(existing);

            _context.ContractFiles.Add(new ContractFile
            {
                ContractVersionId = versionId,
                FileType = type,
                FilePath = path,
                FileName = fileName,
                FileSizeBytes = size
            });
            await _context.SaveChangesAsync();
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Internals
        // ══════════════════════════════════════════════════════════════════════

        private async Task<ContractVersion> GenerateRevisionFromClientAsync(
            Contract contract, ContractSnapshot snapshot, string actor, string? ip)
        {
            var previous = await _context.ContractVersions
                .Where(v => v.ContractId == contract.Id)
                .OrderByDescending(v => v.VersionNumber)
                .FirstAsync();

            previous.IsSuperseded = true;
            await VoidSignersAsync(previous.Id);

            snapshot.VersionNumber = previous.VersionNumber + 1;
            ContractPricingCalculator.Recalculate(snapshot.Pricing);
            var rendered = ContractRenderer.Render(snapshot);

            var version = new ContractVersion
            {
                ContractId = contract.Id,
                VersionNumber = snapshot.VersionNumber,
                FullSnapshotJson = snapshot.ToJson(),
                RenderedDocumentHtml = rendered.Html,
                DocumentHashSha256 = rendered.Sha256,
                GeneratedAt = DateTime.UtcNow,
                // A client-driven revision is still attributed to the admin who owns the contract;
                // the audit row records that the CLIENT caused it.
                GeneratedByAdminId = contract.CreatedByAdminId
            };
            _context.ContractVersions.Add(version);
            await _context.SaveChangesAsync();

            contract.CurrentVersionId = version.Id;
            contract.DraftSnapshotJson = snapshot.ToJson();
            await _context.SaveChangesAsync();

            await StorePreviewPdfAsync(contract, version, snapshot, rendered);
            await AuditAsync(contract.Id, "VersionGenerated",
                $"Version {version.VersionNumber} generated", ContractActorType.Client, actor, version.Id, ip);

            return version;
        }

        private async Task VoidSignersAsync(int versionId)
        {
            var signers = await _context.ContractSigners
                .Where(s => s.ContractVersionId == versionId && s.Status != ContractSignerStatus.Voided)
                .ToListAsync();
            foreach (var s in signers) s.Status = ContractSignerStatus.Voided;
            if (signers.Count > 0) await _context.SaveChangesAsync();
        }

        private static ContractSigner NewSigner(
            int versionId, ContractSignerRole role, SignerSnapshot signer, DateTime expires) => new()
            {
                ContractVersionId = versionId,
                ContractContactId = signer.ContactId,
                UserId = signer.UserId,
                Role = role,
                SigningToken = GenerateToken(),
                TokenExpiresAt = expires,
                Status = ContractSignerStatus.Pending,
                InvitedName = signer.FullName,
                InvitedTitle = signer.Title,
                InvitedEmail = signer.Email
            };

        /// <summary>24 random bytes -> 48 hex characters. Same shape as the payment-link token.</summary>
        public static string GenerateToken() =>
            Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();

        private async Task<Contract> LoadContractAsync(int contractId) =>
            await _context.Contracts.FirstOrDefaultAsync(c => c.Id == contractId)
            ?? throw new ContractWorkflowException("Contract not found.");

        private async Task<ContractVersion> RequireCurrentVersionAsync(Contract contract)
        {
            if (!contract.CurrentVersionId.HasValue)
                throw new ContractWorkflowException("Generate a contract preview first.");
            return await _context.ContractVersions.FirstOrDefaultAsync(v => v.Id == contract.CurrentVersionId)
                ?? throw new ContractWorkflowException("Generate a contract preview first.");
        }

        /// <summary>
        /// DC-YYYY-NNNN, sequential within the calendar year. The unique index is the real guard;
        /// this just picks the next free number and retries if two admins create at the same
        /// instant.
        /// </summary>
        private async Task<string> GenerateContractNumberAsync()
        {
            var year = DateTime.UtcNow.Year;
            var prefix = $"DC-{year}-";

            for (var attempt = 0; attempt < 5; attempt++)
            {
                var numbers = await _context.Contracts
                    .Where(c => c.ContractNumber.StartsWith(prefix))
                    .Select(c => c.ContractNumber)
                    .ToListAsync();

                var next = numbers
                    .Select(n => int.TryParse(n.Substring(prefix.Length), out var v) ? v : 0)
                    .DefaultIfEmpty(0)
                    .Max() + 1 + attempt;

                var candidate = $"{prefix}{next:D4}";
                if (!await _context.Contracts.AnyAsync(c => c.ContractNumber == candidate))
                    return candidate;
            }
            // Falls back to a wider number rather than failing the create outright.
            return $"{prefix}{DateTime.UtcNow.DayOfYear:D3}{DateTime.UtcNow:HHmmss}";
        }

        private async Task<ContractClient> ResolveClientAsync(SaveContractDto dto)
        {
            if (dto.ContractClientId.HasValue)
            {
                var existing = await _context.ContractClients
                    .FirstOrDefaultAsync(c => c.Id == dto.ContractClientId.Value)
                    ?? throw new ContractWorkflowException("The selected client no longer exists.");

                // The form is fully editable even for an existing client, so apply what came back.
                if (dto.NewClient != null)
                {
                    existing.LegalEntityName = dto.NewClient.LegalEntityName.Trim();
                    existing.EntityType = dto.NewClient.EntityType.Trim();
                    existing.FormationState = dto.NewClient.FormationState?.Trim();
                    existing.PrincipalAddress = dto.NewClient.PrincipalAddress.Trim();
                    existing.City = dto.NewClient.City.Trim();
                    existing.State = dto.NewClient.State.Trim();
                    existing.Zip = dto.NewClient.Zip.Trim();
                    existing.NoticeEmail = dto.NewClient.NoticeEmail?.Trim();
                    existing.Phone = dto.NewClient.Phone;
                    existing.SourceUserId = await ValidateSourceUserAsync(dto.NewClient.SourceUserId);
                    existing.UpdatedAt = DateTime.UtcNow;
                    await _context.SaveChangesAsync();
                }
                return existing;
            }

            if (dto.NewClient == null)
                throw new ContractWorkflowException("Select an existing client or enter a new one.");

            var client = new ContractClient
            {
                LegalEntityName = dto.NewClient.LegalEntityName.Trim(),
                EntityType = dto.NewClient.EntityType.Trim(),
                FormationState = dto.NewClient.FormationState?.Trim(),
                PrincipalAddress = dto.NewClient.PrincipalAddress.Trim(),
                City = dto.NewClient.City.Trim(),
                State = dto.NewClient.State.Trim(),
                Zip = dto.NewClient.Zip.Trim(),
                NoticeEmail = dto.NewClient.NoticeEmail?.Trim(),
                Phone = dto.NewClient.Phone,
                SourceUserId = await ValidateSourceUserAsync(dto.NewClient.SourceUserId)
            };
            _context.ContractClients.Add(client);
            await _context.SaveChangesAsync();
            return client;
        }

        /// <summary>
        /// A commercial client may only be linked to an account that is actually flagged as a
        /// business. Enforced here rather than in the UI because the link is what grants portal
        /// access - pointing it at a residential customer would hand them a contracts area.
        /// </summary>
        private async Task<int?> ValidateSourceUserAsync(int? sourceUserId)
        {
            if (!sourceUserId.HasValue) return null;

            var user = await _context.Users
                .Where(u => u.Id == sourceUserId.Value)
                .Select(u => new { u.Id, u.IsBusiness, u.IsActive })
                .FirstOrDefaultAsync();

            if (user == null)
                throw new ContractWorkflowException("The linked customer account no longer exists.");
            if (!user.IsBusiness)
                throw new ContractWorkflowException(
                    "That customer is not flagged as a business. Turn on the business flag on their account first.");

            return user.Id;
        }

        private async Task<ContractServiceLocation> ResolveServiceLocationAsync(SaveContractDto dto, int clientId)
        {
            if (dto.ContractServiceLocationId.HasValue)
            {
                var existing = await _context.ContractServiceLocations
                    .FirstOrDefaultAsync(l => l.Id == dto.ContractServiceLocationId.Value)
                    ?? throw new ContractWorkflowException("The selected service location no longer exists.");

                if (dto.NewServiceLocation != null)
                {
                    existing.BusinessBrand = dto.NewServiceLocation.BusinessBrand?.Trim();
                    existing.LocationName = dto.NewServiceLocation.LocationName?.Trim();
                    existing.Address = dto.NewServiceLocation.Address.Trim();
                    existing.City = dto.NewServiceLocation.City.Trim();
                    existing.State = dto.NewServiceLocation.State.Trim();
                    existing.Zip = dto.NewServiceLocation.Zip.Trim();
                    existing.UpdatedAt = DateTime.UtcNow;
                    await _context.SaveChangesAsync();
                }
                return existing;
            }

            if (dto.NewServiceLocation == null)
                throw new ContractWorkflowException(
                    "Enter the service location. It is where the cleaning happens and is not assumed " +
                    "to be the client's business address.");

            var location = new ContractServiceLocation
            {
                ContractClientId = clientId,
                BusinessBrand = dto.NewServiceLocation.BusinessBrand?.Trim(),
                LocationName = dto.NewServiceLocation.LocationName?.Trim(),
                Address = dto.NewServiceLocation.Address.Trim(),
                City = dto.NewServiceLocation.City.Trim(),
                State = dto.NewServiceLocation.State.Trim(),
                Zip = dto.NewServiceLocation.Zip.Trim()
            };
            _context.ContractServiceLocations.Add(location);
            await _context.SaveChangesAsync();
            return location;
        }

        /// <summary>
        /// The contractor signer is whoever the contractor profile designates. Name and title are
        /// NOT per-contract editable, which is why only the email can be overridden here.
        /// </summary>
        private async Task<ContractContact> ResolveContractorSignerAsync(
            SaveContractDto dto, ContractorProfile profile)
        {
            var contact = dto.ContractorSignerContactId.HasValue
                ? await _context.ContractContacts.FirstOrDefaultAsync(c => c.Id == dto.ContractorSignerContactId.Value)
                : await _context.ContractContacts
                    .Where(c => c.Role == ContractContactRole.ContractorSigner && c.IsActive)
                    .OrderBy(c => c.Id)
                    .FirstOrDefaultAsync();

            if (contact == null)
            {
                contact = new ContractContact
                {
                    FirstName = profile.LegalEntityName,
                    LastName = string.Empty,
                    Title = "Authorized Representative",
                    Email = dto.ContractorSignerEmail?.Trim() ?? profile.NoticeEmail,
                    Role = ContractContactRole.ContractorSigner
                };
                _context.ContractContacts.Add(contact);
                await _context.SaveChangesAsync();
                return contact;
            }

            if (!string.IsNullOrWhiteSpace(dto.ContractorSignerEmail) &&
                !string.Equals(contact.Email, dto.ContractorSignerEmail.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                contact.Email = dto.ContractorSignerEmail.Trim();
                contact.UpdatedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync();
            }
            return contact;
        }

        private async Task<ContractContact> ResolveClientSignerAsync(SaveContractDto dto, int clientId)
        {
            if (dto.ClientSignerContactId.HasValue)
            {
                var existing = await _context.ContractContacts
                    .FirstOrDefaultAsync(c => c.Id == dto.ClientSignerContactId.Value)
                    ?? throw new ContractWorkflowException("The selected client signer no longer exists.");

                if (dto.NewClientSigner != null)
                {
                    existing.FirstName = dto.NewClientSigner.FirstName.Trim();
                    existing.LastName = dto.NewClientSigner.LastName.Trim();
                    existing.Title = dto.NewClientSigner.Title?.Trim();
                    existing.Email = dto.NewClientSigner.Email?.Trim();
                    existing.Phone = dto.NewClientSigner.Phone;
                    existing.ContractClientId ??= clientId;
                    existing.UpdatedAt = DateTime.UtcNow;
                    await _context.SaveChangesAsync();
                }
                return existing;
            }

            if (dto.NewClientSigner == null)
                throw new ContractWorkflowException("Select or enter the person who will sign for the client.");

            var contact = new ContractContact
            {
                FirstName = dto.NewClientSigner.FirstName.Trim(),
                LastName = dto.NewClientSigner.LastName.Trim(),
                Title = dto.NewClientSigner.Title?.Trim(),
                Email = dto.NewClientSigner.Email?.Trim(),
                Phone = dto.NewClientSigner.Phone,
                Role = ContractContactRole.ClientSigner,
                ContractClientId = clientId
            };
            _context.ContractContacts.Add(contact);
            await _context.SaveChangesAsync();
            return contact;
        }

        private static ContractSnapshot BuildSnapshot(
            Contract contract, SaveContractDto dto, ContractTemplate template,
            ContractorProfile profile, ContractClient client, ContractServiceLocation location,
            ContractContact contractorSigner, ContractContact clientSigner, ScopeTemplate? scopeTemplate)
        {
            var pricing = new PricingSnapshot
            {
                PriceMode = dto.Pricing.PriceMode,
                PriceInput = dto.Pricing.PriceInput,
                SalesTaxRatePercent = dto.Pricing.SalesTaxRatePercent,
                CancellationPercent = dto.Pricing.CancellationPercent,
                InvoiceTiming = dto.Pricing.InvoiceTiming,
                PaymentDeadlineHours = dto.Pricing.PaymentDeadlineHours,
                PaymentMethod = dto.Pricing.PaymentMethod,
                LateChargePercent = dto.Pricing.LateChargePercent,
                ReturnedPaymentFee = dto.Pricing.ReturnedPaymentFee
            };
            // Derived figures are computed here and nowhere else - anything the client posted for
            // them was ignored by the DTO in the first place.
            ContractPricingCalculator.Recalculate(pricing);

            var scope = dto.Scope?.Groups.Count > 0
                ? dto.Scope.Clone()
                : (scopeTemplate != null ? ScopeStructure.Parse(scopeTemplate.StructureJson) : new ScopeStructure());

            return new ContractSnapshot
            {
                ContractNumber = contract.ContractNumber,
                VersionNumber = 1,
                EffectiveDate = dto.EffectiveDate,

                ContractTemplateId = template.Id,
                ContractTemplateName = template.Name,
                ContractTemplateVersion = template.Version,
                // Copied, not referenced: a later edit of the master template must never rewrite
                // a version that has already been generated, reviewed or signed.
                TemplateBodyText = template.BodyText,

                ScopeTemplateId = scopeTemplate?.Id,
                ScopeTemplateName = scopeTemplate?.Name ?? string.Empty,
                PremisesType = string.IsNullOrWhiteSpace(dto.PremisesType)
                    ? (scopeTemplate?.PremisesType ?? "premises")
                    : dto.PremisesType.Trim(),

                Contractor = new ContractorSnapshot
                {
                    Id = profile.Id,
                    LegalEntityName = profile.LegalEntityName,
                    Dba = profile.Dba,
                    EntityType = profile.EntityType,
                    Address = profile.Address,
                    City = profile.City,
                    State = profile.State,
                    Zip = profile.Zip,
                    NoticeEmail = profile.NoticeEmail,
                    Phone = profile.Phone
                },
                Client = new ClientSnapshot
                {
                    Id = client.Id,
                    LegalEntityName = client.LegalEntityName,
                    EntityType = client.EntityType,
                    FormationState = client.FormationState,
                    PrincipalAddress = client.PrincipalAddress,
                    City = client.City,
                    State = client.State,
                    Zip = client.Zip,
                    NoticeEmail = client.NoticeEmail,
                    Phone = client.Phone,
                    SourceUserId = client.SourceUserId
                },
                ServiceLocation = new ServiceLocationSnapshot
                {
                    Id = location.Id,
                    BusinessBrand = location.BusinessBrand,
                    LocationName = location.LocationName,
                    Address = location.Address,
                    City = location.City,
                    State = location.State,
                    Zip = location.Zip
                },
                ContractorSigner = new SignerSnapshot
                {
                    ContactId = contractorSigner.Id,
                    UserId = contractorSigner.UserId,
                    FirstName = contractorSigner.FirstName,
                    LastName = contractorSigner.LastName,
                    Title = contractorSigner.Title,
                    Email = contractorSigner.Email,
                    Phone = contractorSigner.Phone
                },
                ClientSigner = new SignerSnapshot
                {
                    ContactId = clientSigner.Id,
                    UserId = clientSigner.UserId,
                    FirstName = clientSigner.FirstName,
                    LastName = clientSigner.LastName,
                    Title = clientSigner.Title,
                    Email = clientSigner.Email,
                    Phone = clientSigner.Phone
                },
                Schedule = dto.Schedule ?? new ScheduleSnapshot(),
                Term = dto.Term ?? new TermSnapshot(),
                Pricing = pricing,
                Advanced = dto.Advanced ?? new AdvancedTermsSnapshot(),
                Scope = scope
            };
        }

        public async Task AuditAsync(
            int contractId, string eventType, string description, ContractActorType actorType,
            string? actor, int? versionId = null, string? ip = null)
        {
            _context.ContractAuditLogs.Add(new ContractAuditLog
            {
                ContractId = contractId,
                EventType = eventType,
                EventDescription = Truncate(description, 1000) ?? eventType,
                ActorType = actorType,
                ActorIdentifier = Truncate(actor, 255),
                ContractVersionId = versionId,
                IpAddress = Truncate(ip, 45),
                Timestamp = DateTime.UtcNow
            });
            await _context.SaveChangesAsync();
        }

        public async Task<string> AdminNameAsync(int adminId)
        {
            var user = await _context.Users
                .Where(u => u.Id == adminId)
                .Select(u => new { u.FirstName, u.LastName })
                .FirstOrDefaultAsync();
            if (user == null) return "an administrator";
            var name = $"{user.FirstName} {user.LastName}".Trim();
            return string.IsNullOrWhiteSpace(name) ? "an administrator" : name;
        }

        public static string ContractorDisplayName(ContractSnapshot snapshot) =>
            string.IsNullOrWhiteSpace(snapshot.Contractor.Dba)
                ? snapshot.Contractor.LegalEntityName
                : $"{snapshot.Contractor.LegalEntityName} d/b/a {snapshot.Contractor.Dba}";

        /// <summary>Editable states. Everything from ReadyForSignature on requires a revision.</summary>
        public static bool CanEdit(ContractStatus status) =>
            status is ContractStatus.Draft or ContractStatus.PreviewGenerated
                or ContractStatus.AwaitingClientReview or ContractStatus.NeedsRevision;

        public static string StatusLabel(ContractStatus status) => status switch
        {
            ContractStatus.Draft => "Draft",
            ContractStatus.PreviewGenerated => "Preview generated",
            ContractStatus.AwaitingClientReview => "Awaiting client review",
            ContractStatus.NeedsRevision => "Needs revision",
            ContractStatus.ReadyForSignature => "Ready for signature",
            ContractStatus.AwaitingSignatures => "Awaiting signatures",
            ContractStatus.PartiallySigned => "Partially signed",
            ContractStatus.FullySigned => "Fully signed",
            ContractStatus.Completed => "Completed",
            ContractStatus.Voided => "Voided",
            ContractStatus.Expired => "Expired",
            _ => status.ToString()
        };

        private static string FirstNameOf(string fullName)
        {
            var trimmed = (fullName ?? string.Empty).Trim();
            var space = trimmed.IndexOf(' ');
            return space > 0 ? trimmed.Substring(0, space) : (trimmed.Length == 0 ? "A signer" : trimmed);
        }

        private static string Trim(string? value, int max, string? fallback)
        {
            var v = value?.Trim();
            if (string.IsNullOrEmpty(v)) return fallback ?? string.Empty;
            return v.Length > max ? v.Substring(0, max) : v;
        }

        private static string? Truncate(string? value, int max)
        {
            if (string.IsNullOrEmpty(value)) return value;
            return value.Length > max ? value.Substring(0, max) : value;
        }
    }
}
