using DreamCleaningBackend.Attributes;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.Helpers.Contracts;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Models.Contracts;
using DreamCleaningBackend.Services;
using DreamCleaningBackend.Services.Contracts;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DreamCleaningBackend.Controllers.Crm
{
    /// <summary>
    /// Commercial contracts: the CRM Contracts tab. Creating, previewing, versioning, sending for
    /// review and sending for signature all live here.
    ///
    /// Open to Admin AND SuperAdmin - day-to-day contract work is ordinary admin work, and
    /// restricting it to SuperAdmin would leave one person able to onboard a commercial client.
    /// Only master-TEMPLATE management is SuperAdmin-only, and that lives on the directory
    /// controller next door.
    ///
    /// Nothing in this controller touches the booking payment flow: a contract is paperwork, and
    /// no charge, card or checkout is involved at any point.
    /// </summary>
    [Route("api/crm/contracts")]
    [ApiController]
    [Authorize(Roles = "Admin,SuperAdmin")]
    public class CrmContractsController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly ContractService _contracts;
        private readonly ContractReadService _read;
        private readonly ContractStorage _storage;
        private readonly ContractAuthorizationService _authorization;

        public CrmContractsController(
            ApplicationDbContext context,
            ContractService contracts,
            ContractReadService read,
            ContractStorage storage,
            ContractAuthorizationService authorization)
        {
            _context = context;
            _contracts = contracts;
            _read = read;
            _storage = storage;
            _authorization = authorization;
        }

        private int CurrentUserId =>
            int.TryParse(User.FindFirst("UserId")?.Value, out var id) ? id : 0;

        /// <summary>Behind Cloudflare + Apache the socket address is the proxy, so prefer the
        /// forwarded headers - the signing record is only useful with the real client IP.</summary>
        private string? ClientIp =>
            Request.Headers["CF-Connecting-IP"].FirstOrDefault()
            ?? Request.Headers["X-Forwarded-For"].FirstOrDefault()?.Split(',')[0].Trim()
            ?? HttpContext.Connection.RemoteIpAddress?.ToString();

        // ── list / detail ──────────────────────────────────────────────────────

        [HttpGet]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<List<ContractListItemDto>>> GetAll(
            [FromQuery] string? search, [FromQuery] ContractStatus? status,
            [FromQuery] bool includeHidden = false)
        {
            await _authorization.EnsureCanAsync(ContractAction.ViewContracts);
            return Ok(await _read.GetListAsync(search, status, includeHidden));
        }

        [HttpGet("{id}")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<ContractDetailDto>> GetOne(int id)
        {
            await _authorization.EnsureCanAsync(ContractAction.ViewContracts);
            var detail = await _read.GetDetailAsync(id);
            return detail == null ? NotFound(new { message = "Contract not found." }) : Ok(detail);
        }

        /// <summary>
        /// A specific historical version's document, so an admin can read exactly what was sent or
        /// signed at the time rather than the current text.
        /// </summary>
        [HttpGet("{id}/versions/{versionId}")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult> GetVersionDocument(int id, int versionId)
        {
            await _authorization.EnsureCanAsync(ContractAction.ViewContracts);
            var version = await _context.ContractVersions
                .FirstOrDefaultAsync(v => v.Id == versionId && v.ContractId == id);
            if (version == null) return NotFound(new { message = "Version not found." });

            var snapshot = ContractSnapshot.Parse(version.FullSnapshotJson);
            var block = await _contracts.BuildSignatureBlockAsync(version.Id, snapshot);

            return Ok(new
            {
                versionNumber = version.VersionNumber,
                generatedAt = version.GeneratedAt,
                documentHash = version.DocumentHashSha256,
                isSuperseded = version.IsSuperseded,
                documentHtml = version.RenderedDocumentHtml,
                signatureBlock = block,
                snapshot
            });
        }

        // ── create / edit ──────────────────────────────────────────────────────

        [HttpPost]
        [RequirePermission(Permission.Create)]
        public async Task<ActionResult<ContractDetailDto>> Create([FromBody] SaveContractDto dto)
        {
            await _authorization.EnsureCanAsync(ContractAction.CreateContract);
            try
            {
                var contract = await _contracts.SaveDraftAsync(null, dto, CurrentUserId);
                return Ok(await _read.GetDetailAsync(contract.Id));
            }
            catch (ContractWorkflowException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPut("{id}")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult<ContractDetailDto>> Update(int id, [FromBody] SaveContractDto dto)
        {
            // The matrix splits on whether a document has been produced yet. Filling in a contract
            // that has never been generated is still "Create"; changing one after a preview exists
            // is "Back to Edit", which a Manager does not hold. This is exactly why a Manager
            // cannot fix their own typo once they have generated a preview.
            var hasVersion = await _context.ContractVersions.AnyAsync(v => v.ContractId == id);
            await _authorization.EnsureCanAsync(
                hasVersion ? ContractAction.BackToEdit : ContractAction.CreateContract);

            await _contracts.SaveDraftAsync(id, dto, CurrentUserId);
            return Ok(await _read.GetDetailAsync(id));
        }

        /// <summary>
        /// Renders the draft as a new version. Explicitly notifies nobody - the first message a
        /// counterparty gets is the review invitation, which is a separate, deliberate action.
        /// </summary>
        [HttpPost("{id}/generate-preview")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult<ContractDetailDto>> GeneratePreview(int id)
        {
            await _authorization.EnsureCanAsync(ContractAction.GeneratePreview);
            try
            {
                await _contracts.GeneratePreviewAsync(id, CurrentUserId);
                return Ok(await _read.GetDetailAsync(id));
            }
            catch (ContractWorkflowException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        /// <summary>Server-side echo of the derived pricing while the admin is still typing.</summary>
        [HttpPost("pricing-preview")]
        [RequirePermission(Permission.View)]
        public ActionResult<ContractPricingPreviewDto> PricingPreview([FromBody] ContractPricingInputDto dto)
        {
            var pricing = new PricingSnapshot
            {
                PriceMode = dto.PriceMode,
                PriceInput = dto.PriceInput,
                SalesTaxRatePercent = dto.SalesTaxRatePercent,
                CancellationPercent = dto.CancellationPercent,
                LateChargePercent = dto.LateChargePercent,
                LiabilityCapMultiple = dto.LiabilityCapMultiple
            };
            ContractPricingCalculator.Recalculate(pricing);

            return Ok(new ContractPricingPreviewDto
            {
                PreTaxPrice = pricing.PreTaxPrice,
                SalesTaxAmount = pricing.SalesTaxAmount,
                TotalPrice = pricing.TotalPrice,
                CancellationAmount = pricing.CancellationAmount,
                RemainingBalance = pricing.RemainingBalance,
                LockoutFee = pricing.LockoutFee,
                LiabilityCapAmount = pricing.LiabilityCapAmount,
                LateChargeAnnualPercent = pricing.LateChargeAnnualPercent
            });
        }

        // ── workflow ───────────────────────────────────────────────────────────

        [HttpPost("{id}/send-for-review")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult<ContractDetailDto>> SendForReview(int id)
        {
            await _authorization.EnsureCanAsync(ContractAction.SendForReview);
            await _contracts.SendForClientReviewAsync(id, CurrentUserId);
            return Ok(await _read.GetDetailAsync(id));
        }

        [HttpPost("{id}/send-for-signature")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult<ContractDetailDto>> SendForSignature(
            int id, [FromBody] SendForSignatureDto? dto)
        {
            await _authorization.EnsureCanAsync(ContractAction.SendForSignature);
            await _contracts.SendForSignatureAsync(id, CurrentUserId, dto?.ExpiryDays);
            return Ok(await _read.GetDetailAsync(id));
        }

        /// <summary>Unlocks a sent contract for editing and voids its outstanding signing links.</summary>
        [HttpPost("{id}/revise")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult<ContractDetailDto>> Revise(int id)
        {
            await _authorization.EnsureCanAsync(ContractAction.CreateRevision);
            await _contracts.CreateRevisionAsync(id, CurrentUserId);
            return Ok(await _read.GetDetailAsync(id));
        }

        /// <summary>
        /// ARCHIVES a contract - the UI's "Archive" option. CTO-only, as Void was.
        ///
        /// The route keeps its original <c>/delete</c> spelling because deployed clients call it
        /// and links to it exist; only the label changed. The row and everything under it survive
        /// and can be restored. A contract archived for longer than the retention window is then
        /// considered for permanent removal by <c>ContractRetentionService</c> - which now applies
        /// <c>ContractHardDeletePolicy</c>, so a signed agreement is kept indefinitely rather than
        /// silently destroyed on a timer.
        /// </summary>
        [HttpPost("{id}/delete")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult<ContractDetailDto>> Delete(int id)
        {
            await _authorization.EnsureCanAsync(ContractAction.DeleteContract);
            await _contracts.DeleteAsync(id, CurrentUserId);
            return Ok(await _read.GetDetailAsync(id));
        }

        [HttpPost("{id}/restore")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult<ContractDetailDto>> Restore(int id)
        {
            await _authorization.EnsureCanAsync(ContractAction.RestoreContract);
            await _contracts.RestoreAsync(id, CurrentUserId);
            return Ok(await _read.GetDetailAsync(id));
        }

        /// <summary>
        /// PERMANENT delete. Destroys the contract, every version, signature, file, signer and
        /// audit row under it, and the PDFs on disk. There is no undo.
        ///
        /// CTO-only, like archiving — the same decision, taken further. Three separate gates stand
        /// in front of it and each catches a different mistake:
        ///
        ///  * <see cref="ContractHardDeletePolicy"/>, applied server-side in
        ///    <see cref="ContractPurgeService"/>, refuses anything carrying a signature, a linked
        ///    invoice, a recurring template or an amendment built from it. The same policy gates
        ///    the retention sweep, so a timer cannot destroy what this endpoint would refuse.
        ///  * The typed confirmation below, verified HERE and not only in Angular. A request
        ///    hand-rolled against this route has to name the contract it means, which is what
        ///    stops a mistyped id destroying somebody else's agreement.
        ///  * The shared client, location, contractor profile, template and contacts are all
        ///    Restrict-mapped, so nothing shared can follow the contract out.
        /// </summary>
        [HttpDelete("{id}/permanent")]
        [RequirePermission(Permission.Delete)]
        public async Task<ActionResult> PermanentlyDelete(
            int id,
            [FromQuery] string? confirmation,
            [FromServices] ContractPurgeService purge,
            [FromServices] IAuditService audit)
        {
            await _authorization.EnsureCanAsync(ContractAction.DeleteContract);

            var contract = await _context.Contracts
                .Include(c => c.ContractClient)
                .Include(c => c.HiddenByUser)
                .FirstOrDefaultAsync(c => c.Id == id);

            if (contract == null) return NotFound(new { message = "Contract not found." });

            // "DELETE DCC-2026-48392175". Compared case-insensitively on the trimmed string: the
            // point is that the admin read the number off the contract in front of them, not that
            // they matched our capitalisation.
            var expected = $"DELETE {contract.ContractNumber}";
            if (!string.Equals(confirmation?.Trim(), expected, StringComparison.OrdinalIgnoreCase))
                return BadRequest(new { message = $"Type {expected} to confirm permanent deletion." });

            // Captured before the row goes: after the purge there is nothing left to read them off.
            var number = contract.ContractNumber;
            var clientName = contract.ContractClient?.LegalEntityName;
            var status = contract.Status.ToString();
            var versionCount = await _context.ContractVersions.CountAsync(v => v.ContractId == id);

            try
            {
                await purge.PurgeAsync(contract, await AdminNameForLogAsync());
            }
            catch (ContractPurgeRefusedException ex)
            {
                return BadRequest(new { message = ex.Message });
            }

            // The app-wide trail, which lives outside the aggregate that was just destroyed.
            // ContractDeletionLog is the module's own durable record and was written inside the
            // purge transaction; this is the copy a finance-wide audit search finds.
            await audit.LogActionAsync(
                AuditEntityTypes.ContractHardDelete,
                id,
                "ContractPermanentlyDeleted",
                null,
                new
                {
                    Contract = number,
                    Client = clientName,
                    StatusAtDeletion = status,
                    VersionCount = versionCount,
                    DeletedAt = DateTime.UtcNow
                },
                actingUserId: CurrentUserId);

            return Ok(new { message = $"Contract {number} was permanently deleted." });
        }

        /// <summary>The acting admin's name, for the surviving ContractDeletionLog row.</summary>
        private async Task<string> AdminNameForLogAsync()
        {
            var name = await _context.Users
                .Where(u => u.Id == CurrentUserId)
                .Select(u => (u.FirstName + " " + u.LastName).Trim())
                .FirstOrDefaultAsync();
            return string.IsNullOrWhiteSpace(name) ? "Admin" : name;
        }

        /// <summary>
        /// "Create Amendment" / "Duplicate as New Contract" - the only ways to change terms after
        /// execution. The original document and its files are never mutated.
        ///
        /// One endpoint, but TWO permissions: the matrix lets a Manager duplicate a contract and
        /// not amend one, and both arrive here. Gating on the flag rather than the route is what
        /// stops `?asAmendment=true` being a way around that.
        /// </summary>
        [HttpPost("{id}/duplicate")]
        [RequirePermission(Permission.Create)]
        public async Task<ActionResult<ContractDetailDto>> Duplicate(int id, [FromQuery] bool asAmendment = false)
        {
            await _authorization.EnsureCanAsync(
                asAmendment ? ContractAction.CreateAmendment : ContractAction.Duplicate);
            var copy = await _contracts.DuplicateAsync(id, CurrentUserId, asAmendment);
            return Ok(await _read.GetDetailAsync(copy.Id));
        }

        /// <summary>Re-renders and re-files the executed PDF. Sends nothing.</summary>
        [HttpPost("{id}/regenerate-executed")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult<ContractDetailDto>> RegenerateExecuted(int id)
        {
            await _authorization.EnsureCanAsync(ContractAction.RegenerateExecutedPdf);
            await _contracts.RegenerateExecutedAsync(id, CurrentUserId);
            return Ok(await _read.GetDetailAsync(id));
        }

        /// <summary>
        /// Emails the executed copy on file to both parties again. Split from Regenerate so that
        /// re-rendering a PDF can never surprise anyone by putting mail in a client's inbox.
        /// </summary>
        [HttpPost("{id}/resend-executed")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult<ContractDetailDto>> ResendExecuted(int id)
        {
            await _authorization.EnsureCanAsync(ContractAction.ResendExecutedCopy);
            await _contracts.ResendExecutedCopyAsync(id, CurrentUserId);
            return Ok(await _read.GetDetailAsync(id));
        }

        // ── in-app contractor signing ──────────────────────────────────────────

        /// <summary>
        /// A CEO/CTO signing as the contractor without leaving the admin panel - the deliberate
        /// exception to the token-link-only design. Two gates, both server-side: the matrix
        /// (CEO/CTO only) and the signer row itself, which must name THIS account. An admin who is
        /// not the designated signer gets a 400 no matter what the UI showed them.
        /// </summary>
        [HttpPost("{id}/sign-as-contractor")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult<SignContractResultDto>> SignAsContractor(
            int id, [FromBody] SignContractDto dto)
        {
            await _authorization.EnsureCanAsync(ContractAction.SignAsContractor);
            var result = await _contracts.SignAsContractorInAppAsync(
                id, CurrentUserId, dto, ClientIp, Request.Headers.UserAgent.FirstOrDefault());
            return Ok(result);
        }

        /// <summary>What the signed-in account may do, so the panel renders only real options.</summary>
        [HttpGet("my-permissions")]
        public async Task<ActionResult<Dictionary<string, object>>> GetMyPermissions()
        {
            return Ok(await _authorization.DescribeCapabilitiesAsync());
        }

        // ── files ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Contract PDFs are stored OUTSIDE the publicly served uploads root, so this authorized
        /// endpoint is the only way to fetch one.
        /// </summary>
        [HttpGet("files/{fileId}")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult> DownloadFile(int fileId)
        {
            var file = await _context.ContractFiles.FirstOrDefaultAsync(f => f.Id == fileId);
            if (file == null) return NotFound(new { message = "File not found." });
            if (!_storage.Exists(file.FilePath))
                return NotFound(new { message = "The stored file is missing. Regenerate it from the contract page." });

            var bytes = await _storage.ReadAsync(file.FilePath);
            return File(bytes, "application/pdf", file.FileName);
        }

        // ── signer maintenance ────────────────────────────────────────────────

        /// <summary>Re-sends one signer's invitation without disturbing the other party's link.</summary>
        [HttpPost("{id}/signers/{signerId}/resend")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult> ResendSignerInvite(
            int id, int signerId, [FromServices] ContractNotificationService notifications)
        {
            var signer = await _context.ContractSigners
                .Include(s => s.ContractVersion)
                .FirstOrDefaultAsync(s => s.Id == signerId && s.ContractVersion!.ContractId == id);

            if (signer == null) return NotFound(new { message = "Signer not found." });
            if (signer.Status == ContractSignerStatus.Signed)
                return BadRequest(new { message = "That party has already signed." });
            if (signer.Status == ContractSignerStatus.Voided)
                return BadRequest(new { message = "That signing link was voided by a newer version." });
            if (string.IsNullOrWhiteSpace(signer.InvitedEmail))
                return BadRequest(new { message = "That signer has no email address on file." });

            var contract = await _context.Contracts.FirstAsync(c => c.Id == id);
            var snapshot = ContractSnapshot.Parse(signer.ContractVersion!.FullSnapshotJson);

            await notifications.SendSignatureInvitationAsync(
                signer.InvitedEmail!, signer.InvitedName, contract.ContractNumber,
                $"{ContractService.ContractorDisplayName(snapshot)} and {snapshot.Client.LegalEntityName}",
                notifications.BuildSigningUrl(signer.SigningToken), signer.TokenExpiresAt);

            signer.InviteSentAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            var adminName = await _contracts.AdminNameAsync(CurrentUserId);
            await _contracts.AuditAsync(id, "SigningLinkResent",
                $"Signing link re-sent to {signer.InvitedName} by {adminName}",
                ContractActorType.Admin, adminName, signer.ContractVersionId, ClientIp);

            return Ok(new { message = $"Signing link re-sent to {signer.InvitedEmail}." });
        }
    }
}
