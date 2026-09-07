using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Helpers.Contracts;
using DreamCleaningBackend.Models.Contracts;
using DreamCleaningBackend.Services.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DreamCleaningBackend.Controllers
{
    /// <summary>
    /// The client-facing half of the contract system: reviewing an agreement and signing it.
    /// Anonymous by necessity - a commercial counterparty has no account here - and therefore
    /// addressed purely by opaque token, exactly like the tokenized customer payment links.
    ///
    /// Two token families, and they are not interchangeable:
    ///   review token  - contract-level, resolves the CURRENT version, allows the client to fix
    ///                   their own details. Never signs anything.
    ///   signing token - per signer, per VERSION. Signs as that one party only; it can neither
    ///                   sign for the other side nor edit the document.
    /// </summary>
    [Route("api/contracts")]
    [ApiController]
    [AllowAnonymous]
    public class ContractSigningController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly ContractService _contracts;
        private readonly ContractStorage _storage;

        public ContractSigningController(
            ApplicationDbContext context, ContractService contracts, ContractStorage storage)
        {
            _context = context;
            _contracts = contracts;
            _storage = storage;
        }

        /// <summary>Cloudflare/Apache sit in front, so the socket address is the proxy.</summary>
        private string? ClientIp =>
            Request.Headers["CF-Connecting-IP"].FirstOrDefault()
            ?? Request.Headers["X-Forwarded-For"].FirstOrDefault()?.Split(',')[0].Trim()
            ?? HttpContext.Connection.RemoteIpAddress?.ToString();

        private string? ClientUserAgent => Request.Headers.UserAgent.FirstOrDefault();

        // ── review ─────────────────────────────────────────────────────────────

        [HttpGet("review/{token}")]
        public async Task<ActionResult<ContractReviewPageDto>> GetReview(string token)
        {
            try
            {
                var (contract, version, snapshot) = await _contracts.ResolveReviewTokenAsync(token);
                var block = await _contracts.BuildSignatureBlockAsync(version.Id, snapshot);

                // The client's own signing link on THIS version, if signing has started. Handed
                // back so "Continue to Signature" works without a second email round trip; the
                // contractor's link is never exposed here.
                var clientSigner = await _context.ContractSigners
                    .Where(s => s.ContractVersionId == version.Id &&
                                s.Role == ContractSignerRole.ClientSigner &&
                                s.Status == ContractSignerStatus.Pending)
                    .FirstOrDefaultAsync();

                var canEdit = ContractClientEditPolicy.ClientMayEdit(contract.Status);

                string? message = contract.Status switch
                {
                    ContractStatus.NeedsRevision =>
                        "We are preparing a revised version based on your changes. You will be able to sign once it is confirmed.",
                    ContractStatus.PartiallySigned =>
                        "One party has signed. This agreement can no longer be edited here.",
                    ContractStatus.FullySigned or ContractStatus.Completed =>
                        "This agreement has been fully executed.",
                    _ => null
                };

                return Ok(new ContractReviewPageDto
                {
                    ContractNumber = contract.ContractNumber,
                    ContractorDisplayName = ContractService.ContractorDisplayName(snapshot),
                    ClientLegalName = snapshot.Client.LegalEntityName,
                    VersionNumber = version.VersionNumber,
                    Status = contract.Status,
                    StatusLabel = ContractService.StatusLabel(contract.Status),
                    DocumentHtml = version.RenderedDocumentHtml,
                    SignatureBlock = block,
                    YourInformation = new ClientReviewInfoDto
                    {
                        CompanyLegalName = snapshot.Client.LegalEntityName,
                        FirstName = snapshot.ClientSigner.FirstName,
                        LastName = snapshot.ClientSigner.LastName,
                        Title = snapshot.ClientSigner.Title,
                        Email = snapshot.ClientSigner.Email,
                        Phone = snapshot.ClientSigner.Phone,
                        CompanyAddress = snapshot.Client.PrincipalAddress,
                        City = snapshot.Client.City,
                        State = snapshot.Client.State,
                        Zip = snapshot.Client.Zip
                    },
                    CanEdit = canEdit && contract.Status is not (ContractStatus.PartiallySigned
                        or ContractStatus.FullySigned or ContractStatus.Completed),
                    CanContinueToSignature = clientSigner != null &&
                                             clientSigner.TokenExpiresAt > DateTime.UtcNow,
                    SigningToken = clientSigner?.SigningToken,
                    Message = message
                });
            }
            catch (ContractWorkflowException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        /// <summary>
        /// The client's own edit of their information block. A name/title/email/phone correction
        /// is applied to the current version; a legal-name or company-address change is a contract
        /// modification and produces a new version that an admin must re-approve.
        /// </summary>
        [HttpPut("review/{token}/information")]
        public async Task<ActionResult<ClientEditResultDto>> UpdateInformation(
            string token, [FromBody] ClientReviewInfoDto dto)
        {
            try
            {
                return Ok(await _contracts.ApplyClientEditAsync(token, dto, ClientIp));
            }
            catch (ContractWorkflowException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        /// <summary>Executed copy for the client, reachable only with their review token.</summary>
        [HttpGet("review/{token}/executed-document")]
        public async Task<ActionResult> DownloadExecuted(string token)
        {
            try
            {
                var (contract, version, _) = await _contracts.ResolveReviewTokenAsync(token);
                if (contract.Status is not (ContractStatus.Completed or ContractStatus.FullySigned))
                    return BadRequest(new { message = "This agreement has not been fully executed yet." });

                var file = await _context.ContractFiles
                    .Where(f => f.ContractVersionId == version.Id && f.FileType == ContractFileType.FinalExecuted)
                    .OrderByDescending(f => f.CreatedAt)
                    .FirstOrDefaultAsync();

                if (file == null || !_storage.Exists(file.FilePath))
                    return NotFound(new { message = "The executed copy is not available yet. Please contact us." });

                var bytes = await _storage.ReadAsync(file.FilePath);
                return File(bytes, "application/pdf", file.FileName);
            }
            catch (ContractWorkflowException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        // ── signing ────────────────────────────────────────────────────────────

        [HttpGet("sign/{token}")]
        public async Task<ActionResult<ContractSigningPageDto>> GetSigningPage(string token)
        {
            try
            {
                var signer = await _contracts.ResolveSigningTokenAsync(token);
                var version = signer.ContractVersion!;
                var contract = version.Contract!;
                var snapshot = ContractSnapshot.Parse(version.FullSnapshotJson);
                var block = await _contracts.BuildSignatureBlockAsync(version.Id, snapshot);

                var superseded = version.IsSuperseded || signer.Status == ContractSignerStatus.Voided;
                var expired = signer.TokenExpiresAt < DateTime.UtcNow;

                string? message = null;
                if (superseded)
                    message = "This version has been replaced. Please use the most recent link we sent you.";
                else if (expired)
                    message = "This signing link has expired. Please ask us for a new one.";
                else if (contract.Status == ContractStatus.Voided || contract.IsHidden)
                    message = "This agreement is no longer active.";

                var isContractor = signer.Role == ContractSignerRole.ContractorSigner;

                return Ok(new ContractSigningPageDto
                {
                    ContractNumber = contract.ContractNumber,
                    VersionNumber = version.VersionNumber,
                    DocumentHtml = version.RenderedDocumentHtml,
                    SignatureBlock = block,
                    Role = signer.Role,
                    PartyEntityName = isContractor
                        ? ContractService.ContractorDisplayName(snapshot)
                        : snapshot.Client.LegalEntityName,
                    SignerName = signer.Signature?.SignerNameAtSigning ?? signer.InvitedName,
                    SignerTitle = signer.Signature?.SignerTitleAtSigning ?? signer.InvitedTitle,
                    SignerEmail = signer.InvitedEmail,
                    // The contractor signs as whoever the contractor profile designates, so both
                    // fields are fixed. A client signer may still need to supply their title.
                    NameLocked = isContractor,
                    TitleLocked = isContractor,
                    AlreadySigned = signer.Status == ContractSignerStatus.Signed,
                    SignedAt = signer.Signature?.SignedAt,
                    Expired = expired,
                    Superseded = superseded,
                    ConsentText = ContractService.ConsentText,
                    Message = message
                });
            }
            catch (ContractWorkflowException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPost("sign/{token}")]
        public async Task<ActionResult<SignContractResultDto>> Sign(string token, [FromBody] SignContractDto dto)
        {
            try
            {
                return Ok(await _contracts.SignAsync(token, dto, ClientIp, ClientUserAgent));
            }
            catch (ContractWorkflowException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }
    }
}
