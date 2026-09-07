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
    /// The self-service "My Contracts" area for business customers.
    ///
    /// Access here is by AUTHENTICATED OWNERSHIP, never by token: every endpoint resolves the
    /// contract through <c>ContractClient.SourceUserId == the signed-in user</c>, so iterating
    /// contract ids returns 404 rather than somebody else's agreement. That check is repeated on
    /// every single endpoint on purpose — it is the only thing standing between two businesses.
    ///
    /// This portal only ever exposes the CLIENT side. A customer can never see or sign the
    /// contractor signature, and a contract whose client has no linked account (SourceUserId null)
    /// is invisible here entirely — those remain reachable by emailed link alone, which is also
    /// still the fallback for everyone else.
    /// </summary>
    [Route("api/my-contracts")]
    [ApiController]
    [Authorize]
    public class MyContractsController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly ContractService _contracts;

        public MyContractsController(ApplicationDbContext context, ContractService contracts)
        {
            _context = context;
            _contracts = contracts;
        }

        private int CurrentUserId =>
            int.TryParse(User.FindFirst("UserId")?.Value, out var id) ? id : 0;

        private string? ClientIp =>
            Request.Headers["CF-Connecting-IP"].FirstOrDefault()
            ?? Request.Headers["X-Forwarded-For"].FirstOrDefault()?.Split(',')[0].Trim()
            ?? HttpContext.Connection.RemoteIpAddress?.ToString();

        /// <summary>
        /// Every contract owned by the signed-in account, as a query the other endpoints reuse.
        /// A contract with no generated version is excluded — there is nothing for a customer to
        /// look at until one exists.
        /// </summary>
        private IQueryable<Contract> OwnedContracts() =>
            _context.Contracts
                .Where(c => c.ContractClient != null
                            && c.ContractClient.SourceUserId == CurrentUserId
                            && c.CurrentVersionId != null);

        /// <summary>
        /// The cheap check behind the header menu item. Deliberately returns a bare boolean rather
        /// than the contract list: the dropdown renders on every page load for every logged-in
        /// user, and it only needs to know whether to draw one link.
        /// </summary>
        [HttpGet("has-contracts")]
        public async Task<ActionResult<object>> HasContracts()
        {
            var userId = CurrentUserId;
            if (userId == 0) return Ok(new { hasContracts = false });

            // Both halves are required: the business flag alone shows nothing, and a contract
            // alone (on an unflagged account) shows nothing either.
            var isBusiness = await _context.Users
                .Where(u => u.Id == userId)
                .Select(u => u.IsBusiness)
                .FirstOrDefaultAsync();

            if (!isBusiness) return Ok(new { hasContracts = false });

            var hasAny = await OwnedContracts().AnyAsync();
            return Ok(new { hasContracts = hasAny });
        }

        [HttpGet]
        public async Task<ActionResult<List<MyContractListItemDto>>> GetMyContracts()
        {
            var contracts = await OwnedContracts()
                .Include(c => c.ServiceLocation)
                .OrderByDescending(c => c.UpdatedAt)
                .ToListAsync();

            var versionIds = contracts.Select(c => c.CurrentVersionId!.Value).ToList();

            var versions = await _context.ContractVersions
                .Where(v => versionIds.Contains(v.Id))
                .Select(v => new { v.Id, v.VersionNumber, v.GeneratedAt })
                .ToListAsync();

            // Only the CLIENT signer matters to a customer; the contractor's state is not theirs
            // to see, so it is never loaded here.
            var pendingClientSigners = await _context.ContractSigners
                .Where(s => versionIds.Contains(s.ContractVersionId)
                            && s.Role == ContractSignerRole.ClientSigner
                            && s.Status == ContractSignerStatus.Pending)
                .Select(s => s.ContractVersionId)
                .ToListAsync();

            return Ok(contracts.Select(c =>
            {
                var version = versions.FirstOrDefault(v => v.Id == c.CurrentVersionId);
                return new MyContractListItemDto
                {
                    Id = c.Id,
                    ContractNumber = c.ContractNumber,
                    Status = c.Status,
                    StatusLabel = ContractService.StatusLabel(c.Status),
                    VersionNumber = version?.VersionNumber ?? 0,
                    VersionDate = version?.GeneratedAt ?? c.UpdatedAt,
                    ServiceLocationLabel = ContractReadService.LocationLabel(c.ServiceLocation),
                    AwaitingYourSignature =
                        c.Status is ContractStatus.ReadyForSignature or ContractStatus.AwaitingSignatures
                            or ContractStatus.PartiallySigned
                        && pendingClientSigners.Contains(c.CurrentVersionId!.Value),
                    IsExecuted = c.Status is ContractStatus.Completed or ContractStatus.FullySigned
                };
            }).ToList());
        }

        /// <summary>
        /// One contract, in the same shape the public review page consumes so the Angular side can
        /// reuse those components wholesale rather than growing a second renderer.
        /// </summary>
        [HttpGet("{id}")]
        public async Task<ActionResult<ContractReviewPageDto>> GetMyContract(int id)
        {
            var contract = await OwnedContracts().FirstOrDefaultAsync(c => c.Id == id);
            // 404, not 403: a customer has no business learning that a contract id exists at all.
            if (contract == null) return NotFound(new { message = "Contract not found." });

            var version = await _context.ContractVersions
                .FirstAsync(v => v.Id == contract.CurrentVersionId!.Value);
            var snapshot = ContractSnapshot.Parse(version.FullSnapshotJson);
            var block = await _contracts.BuildSignatureBlockAsync(version.Id, snapshot);

            var clientSigner = await _context.ContractSigners
                .Where(s => s.ContractVersionId == version.Id
                            && s.Role == ContractSignerRole.ClientSigner
                            && s.Status == ContractSignerStatus.Pending)
                .FirstOrDefaultAsync();

            string? message = contract.Status switch
            {
                ContractStatus.NeedsRevision =>
                    "We are preparing a revised version based on your changes. You will be able to sign once it is confirmed.",
                ContractStatus.PartiallySigned when clientSigner == null =>
                    "You have signed. We are waiting on the countersignature.",
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
                CanEdit = ContractClientEditPolicy.ClientMayEdit(contract.Status),
                CanContinueToSignature = clientSigner != null,
                // No token is handed back: this session IS the credential here, and echoing the
                // signing token would put a bearer credential into a page that does not need one.
                SigningToken = null,
                Message = message
            });
        }

        /// <summary>
        /// The same personal-info-vs-contract-modification rule as the token flow, unchanged. A
        /// name or email correction lands on the current version; a legal-name or address change
        /// produces a revision that staff must re-approve.
        /// </summary>
        [HttpPut("{id}/information")]
        public async Task<ActionResult<ClientEditResultDto>> UpdateInformation(
            int id, [FromBody] ClientReviewInfoDto dto)
        {
            var contract = await OwnedContracts().FirstOrDefaultAsync(c => c.Id == id);
            if (contract == null) return NotFound(new { message = "Contract not found." });

            // Shares the token page's core, so the versioning rule has exactly one implementation
            // regardless of how the customer arrived — but bypasses the token itself, whose expiry
            // describes an emailed link and is meaningless for a logged-in session.
            return Ok(await _contracts.ApplyClientEditFromPortalAsync(id, dto, ClientIp));
        }

        /// <summary>
        /// Signs as the CLIENT, from an authenticated session. Recorded with
        /// SigningChannel = CustomerPortal and the same IP/user-agent/document-hash evidence the
        /// other two channels capture.
        /// </summary>
        [HttpPost("{id}/sign")]
        public async Task<ActionResult<SignContractResultDto>> Sign(
            int id, [FromBody] SignContractDto dto)
        {
            var contract = await OwnedContracts().FirstOrDefaultAsync(c => c.Id == id);
            if (contract == null) return NotFound(new { message = "Contract not found." });

            var result = await _contracts.SignAsCustomerAsync(
                id, CurrentUserId, dto, ClientIp, Request.Headers.UserAgent.FirstOrDefault());
            return Ok(result);
        }

        /// <summary>The executed copy, for a contract this account owns.</summary>
        [HttpGet("{id}/executed-document")]
        public async Task<ActionResult> DownloadExecuted(int id, [FromServices] ContractStorage storage)
        {
            var contract = await OwnedContracts().FirstOrDefaultAsync(c => c.Id == id);
            if (contract == null) return NotFound(new { message = "Contract not found." });

            if (contract.Status is not (ContractStatus.Completed or ContractStatus.FullySigned))
                return BadRequest(new { message = "This agreement has not been fully executed yet." });

            var file = await _context.ContractFiles
                .Where(f => f.ContractVersionId == contract.CurrentVersionId!.Value
                            && f.FileType == ContractFileType.FinalExecuted)
                .OrderByDescending(f => f.CreatedAt)
                .FirstOrDefaultAsync();

            if (file == null || !storage.Exists(file.FilePath))
                return NotFound(new { message = "The executed copy is not available yet. Please contact us." });

            var bytes = await storage.ReadAsync(file.FilePath);
            return File(bytes, "application/pdf", file.FileName);
        }
    }
}
