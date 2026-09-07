using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Models.Contracts;
using Microsoft.EntityFrameworkCore;

namespace DreamCleaningBackend.Services.Contracts
{
    /// <summary>
    /// Read models for the admin Contracts screens. Split from <see cref="ContractService"/> so
    /// the workflow rules are not buried in projection code; this class never mutates anything.
    /// </summary>
    public class ContractReadService
    {
        private readonly ApplicationDbContext _context;
        private readonly ContractService _contracts;
        private readonly ContractNotificationService _notifications;
        private readonly ContractAuthorizationService _authorization;

        public ContractReadService(
            ApplicationDbContext context,
            ContractService contracts,
            ContractNotificationService notifications,
            ContractAuthorizationService authorization)
        {
            _context = context;
            _contracts = contracts;
            _notifications = notifications;
            _authorization = authorization;
        }

        public async Task<List<ContractListItemDto>> GetListAsync(
            string? search, ContractStatus? status, bool includeHidden = false)
        {
            var query = _context.Contracts
                .Include(c => c.ContractClient)
                .Include(c => c.ServiceLocation)
                .Include(c => c.CreatedByAdmin)
                .AsQueryable();

            // Soft-deleted contracts drop out unless "Show hidden contracts" is on — the same
            // pattern the orders table uses.
            if (!includeHidden) query = query.Where(c => !c.IsHidden);

            if (status.HasValue) query = query.Where(c => c.Status == status.Value);

            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.Trim();
                query = query.Where(c =>
                    c.ContractNumber.Contains(term) ||
                    (c.ContractClient != null && c.ContractClient.LegalEntityName.Contains(term)) ||
                    (c.ServiceLocation != null && c.ServiceLocation.Address.Contains(term)) ||
                    (c.ServiceLocation != null && c.ServiceLocation.BusinessBrand != null &&
                     c.ServiceLocation.BusinessBrand.Contains(term)));
            }

            var contracts = await query
                .OrderByDescending(c => c.UpdatedAt)
                .ToListAsync();

            var contractIds = contracts.Select(c => c.Id).ToList();

            // Signature progress is per CURRENT version - a voided signer from a superseded
            // version must not count toward "1 of 2 signed" on the version now on the table.
            var currentVersionIds = contracts
                .Where(c => c.CurrentVersionId.HasValue)
                .Select(c => c.CurrentVersionId!.Value)
                .ToList();

            var signerRows = await _context.ContractSigners
                .Where(s => currentVersionIds.Contains(s.ContractVersionId) &&
                            s.Status != ContractSignerStatus.Voided)
                .Select(s => new { s.ContractVersionId, s.Status })
                .ToListAsync();

            var versionNumbers = await _context.ContractVersions
                .Where(v => contractIds.Contains(v.ContractId))
                .Select(v => new { v.Id, v.VersionNumber })
                .ToListAsync();

            return contracts.Select(c =>
            {
                var snapshot = ContractSnapshot.Parse(c.DraftSnapshotJson);
                var signers = c.CurrentVersionId.HasValue
                    ? signerRows.Where(s => s.ContractVersionId == c.CurrentVersionId.Value).ToList()
                    : new();

                return new ContractListItemDto
                {
                    Id = c.Id,
                    ContractNumber = c.ContractNumber,
                    ClientLegalName = c.ContractClient?.LegalEntityName ?? snapshot.Client.LegalEntityName,
                    ServiceLocationLabel = LocationLabel(c.ServiceLocation),
                    Status = c.Status,
                    StatusLabel = ContractService.StatusLabel(c.Status),
                    CurrentVersionNumber = c.CurrentVersionId.HasValue
                        ? versionNumbers.FirstOrDefault(v => v.Id == c.CurrentVersionId.Value)?.VersionNumber ?? 0
                        : 0,
                    CreatedAt = c.CreatedAt,
                    UpdatedAt = c.UpdatedAt,
                    EffectiveDate = snapshot.EffectiveDate,
                    TotalPrice = snapshot.Pricing?.TotalPrice ?? 0m,
                    CreatedByAdminName = c.CreatedByAdmin == null
                        ? null
                        : $"{c.CreatedByAdmin.FirstName} {c.CreatedByAdmin.LastName}".Trim(),
                    SignerCount = signers.Count,
                    SignedCount = signers.Count(s => s.Status == ContractSignerStatus.Signed),
                    IsHidden = c.IsHidden
                };
            }).ToList();
        }

        public async Task<ContractDetailDto?> GetDetailAsync(int contractId)
        {
            var contract = await _context.Contracts
                .Include(c => c.CreatedByAdmin)
                .FirstOrDefaultAsync(c => c.Id == contractId);
            if (contract == null) return null;

            var versions = await _context.ContractVersions
                .Include(v => v.GeneratedByAdmin)
                .Include(v => v.Files)
                .Where(v => v.ContractId == contractId)
                .OrderByDescending(v => v.VersionNumber)
                .ToListAsync();

            var current = versions.FirstOrDefault(v => v.Id == contract.CurrentVersionId);
            var currentSnapshot = current == null ? null : ContractSnapshot.Parse(current.FullSnapshotJson);

            var signers = current == null
                ? new List<ContractSigner>()
                : await _context.ContractSigners
                    .Include(s => s.Signature)
                    .Where(s => s.ContractVersionId == current.Id)
                    .OrderBy(s => s.Role)
                    .ToListAsync();

            var audit = await _context.ContractAuditLogs
                .Where(a => a.ContractId == contractId)
                .OrderBy(a => a.Timestamp).ThenBy(a => a.Id)
                .ToListAsync();

            // Unresolved tokens are recomputed rather than stored: they are a preview-time warning
            // for the admin, not part of the frozen record.
            var unresolved = currentSnapshot == null
                ? new List<string>()
                : ContractRenderer.Render(currentSnapshot).UnresolvedTokens;

            // Composed here rather than in the browser so the admin preview shows the same block
            // — including the actual signature marks once signed — that the client and the
            // executed PDF show.
            var signatureBlock = current == null || currentSnapshot == null
                ? new ContractSignatureBlockDto()
                : await _contracts.BuildSignatureBlockAsync(current.Id, currentSnapshot);

            var canEdit = ContractService.CanEdit(contract.Status);

            return new ContractDetailDto
            {
                Id = contract.Id,
                ContractNumber = contract.ContractNumber,
                Status = contract.Status,
                StatusLabel = ContractService.StatusLabel(contract.Status),
                CreatedAt = contract.CreatedAt,
                UpdatedAt = contract.UpdatedAt,
                CompletedAt = contract.CompletedAt,
                CreatedByAdminName = contract.CreatedByAdmin == null
                    ? null
                    : $"{contract.CreatedByAdmin.FirstName} {contract.CreatedByAdmin.LastName}".Trim(),
                VoidReason = contract.VoidReason,
                DuplicatedFromContractId = contract.DuplicatedFromContractId,

                CurrentVersionId = current?.Id,
                CurrentVersionNumber = current?.VersionNumber ?? 0,
                DocumentHtml = current?.RenderedDocumentHtml ?? string.Empty,
                DocumentHash = current?.DocumentHashSha256 ?? string.Empty,
                UnresolvedTokens = unresolved,
                SignatureBlock = signatureBlock,

                Draft = ContractSnapshot.Parse(contract.DraftSnapshotJson),
                CurrentSnapshot = currentSnapshot,

                ClientReviewUrl = string.IsNullOrEmpty(contract.ClientReviewToken)
                    ? null
                    : _notifications.BuildReviewUrl(contract.ClientReviewToken),

                Versions = versions.Select(v => new ContractVersionDto
                {
                    Id = v.Id,
                    VersionNumber = v.VersionNumber,
                    GeneratedAt = v.GeneratedAt,
                    GeneratedByAdminName = v.GeneratedByAdmin == null
                        ? null
                        : $"{v.GeneratedByAdmin.FirstName} {v.GeneratedByAdmin.LastName}".Trim(),
                    DocumentHashSha256 = v.DocumentHashSha256,
                    IsSuperseded = v.IsSuperseded,
                    IsCurrent = v.Id == contract.CurrentVersionId,
                    Files = v.Files.Select(f => new ContractFileDto
                    {
                        Id = f.Id,
                        FileType = f.FileType,
                        FileName = f.FileName,
                        FileSizeBytes = f.FileSizeBytes,
                        CreatedAt = f.CreatedAt
                    }).ToList()
                }).ToList(),

                Signers = signers.Select(s => new ContractSignerDto
                {
                    Id = s.Id,
                    Role = s.Role,
                    InvitedName = s.InvitedName,
                    InvitedTitle = s.InvitedTitle,
                    InvitedEmail = s.InvitedEmail,
                    Status = s.Status,
                    InviteSentAt = s.InviteSentAt,
                    TokenExpiresAt = s.TokenExpiresAt,
                    SignedAt = s.Signature?.SignedAt,
                    SignatureMethod = s.Signature?.SignatureMethod,
                    // Only live links are handed back; a voided token would be a dead end an admin
                    // could copy to a client in good faith.
                    SigningUrl = s.Status == ContractSignerStatus.Voided
                        ? null
                        : _notifications.BuildSigningUrl(s.SigningToken)
                }).ToList(),

                AuditLog = audit.Select(a => new ContractAuditEntryDto
                {
                    Id = a.Id,
                    EventType = a.EventType,
                    EventDescription = a.EventDescription,
                    ActorType = a.ActorType,
                    ActorIdentifier = a.ActorIdentifier,
                    ContractVersionId = a.ContractVersionId,
                    Timestamp = a.Timestamp
                }).ToList(),

                CanEdit = canEdit,
                CanGeneratePreview = canEdit,
                CanSendForReview = canEdit && current != null,
                CanSendForSignature = current != null &&
                    contract.Status is ContractStatus.PreviewGenerated
                        or ContractStatus.AwaitingClientReview or ContractStatus.NeedsRevision,
                IsHidden = contract.IsHidden,
                HiddenAt = contract.HiddenAt,
                // Delete replaced Void: available on any visible contract, including an executed
                // one — the document and its files survive the retention window either way.
                CanDelete = !contract.IsHidden,
                CanRestore = contract.IsHidden,
                IsLocked = !canEdit,

                // Identity only — the matrix decides whether the button is usable, and the API
                // enforces both when it is clicked.
                IsPendingContractorSigner = signers.Any(s =>
                    s.Role == ContractSignerRole.ContractorSigner &&
                    s.Status == ContractSignerStatus.Pending &&
                    s.UserId == _authorization.CurrentUserId &&
                    _authorization.CurrentUserId != 0)
            };
        }

        public static string LocationLabel(ContractServiceLocation? location)
        {
            if (location == null) return string.Empty;
            var brand = string.IsNullOrWhiteSpace(location.BusinessBrand)
                ? location.LocationName
                : location.BusinessBrand;
            var address = $"{location.Address}, {location.City}, {location.State} {location.Zip}".Trim();
            return string.IsNullOrWhiteSpace(brand) ? address : $"{brand} - {address}";
        }
    }
}
