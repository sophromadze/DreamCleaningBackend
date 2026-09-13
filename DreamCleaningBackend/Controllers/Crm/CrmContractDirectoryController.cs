using DreamCleaningBackend.Attributes;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Helpers.Contracts;
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
    /// Reference data behind the Create Contract form: the contractor profile, commercial clients,
    /// their service locations, signer contacts, and the two kinds of template.
    ///
    /// One access rule differs from the rest of the file, and it is deliberate: master CONTRACT
    /// TEMPLATE writes are SuperAdmin-only. Editing the template body re-words every agreement the
    /// business signs from then on, which is a different order of decision from filling in one
    /// contract. Everything else here - clients, contacts, locations, the contractor profile,
    /// scope checklists - is ordinary Admin work.
    /// </summary>
    [Route("api/crm/contract-directory")]
    [ApiController]
    [Authorize(Roles = "Admin,SuperAdmin")]
    public class CrmContractDirectoryController : AdminControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly BusinessClientService _businessClients;
        private readonly IAuditService _auditService;

        public CrmContractDirectoryController(
            ApplicationDbContext context,
            BusinessClientService businessClients,
            IAuditService auditService)
        {
            _context = context;
            _businessClients = businessClients;
            _auditService = auditService;
        }

        // ── contractor profiles ────────────────────────────────────────────────

        [HttpGet("contractor-profiles")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<List<ContractorProfileDto>>> GetContractorProfiles()
        {
            var rows = await _context.ContractorProfiles
                .OrderByDescending(p => p.IsDefault).ThenBy(p => p.Id)
                .ToListAsync();
            return Ok(rows.Select(MapContractor).ToList());
        }

        [HttpPut("contractor-profiles/{id}")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult<ContractorProfileDto>> UpdateContractorProfile(
            int id, [FromBody] SaveContractorProfileDto dto)
        {
            var profile = await _context.ContractorProfiles.FirstOrDefaultAsync(p => p.Id == id);
            if (profile == null) return NotFound(new { message = "Contractor profile not found." });

            profile.LegalEntityName = dto.LegalEntityName.Trim();
            profile.Dba = dto.Dba?.Trim();
            profile.EntityType = dto.EntityType.Trim();
            profile.Address = dto.Address.Trim();
            profile.City = dto.City.Trim();
            profile.State = dto.State.Trim();
            profile.Zip = dto.Zip.Trim();
            profile.NoticeEmail = dto.NoticeEmail.Trim();
            profile.Phone = dto.Phone;
            profile.UpdatedAt = DateTime.UtcNow;

            if (dto.IsDefault && !profile.IsDefault)
            {
                // Exactly one default, so the create form never has to guess which to preselect.
                var others = await _context.ContractorProfiles.Where(p => p.Id != id && p.IsDefault).ToListAsync();
                foreach (var other in others) other.IsDefault = false;
                profile.IsDefault = true;
            }

            await _context.SaveChangesAsync();
            return Ok(MapContractor(profile));
        }

        [HttpPost("contractor-profiles")]
        [RequirePermission(Permission.Create)]
        public async Task<ActionResult<ContractorProfileDto>> CreateContractorProfile(
            [FromBody] SaveContractorProfileDto dto)
        {
            var profile = new ContractorProfile
            {
                LegalEntityName = dto.LegalEntityName.Trim(),
                Dba = dto.Dba?.Trim(),
                EntityType = dto.EntityType.Trim(),
                Address = dto.Address.Trim(),
                City = dto.City.Trim(),
                State = dto.State.Trim(),
                Zip = dto.Zip.Trim(),
                NoticeEmail = dto.NoticeEmail.Trim(),
                Phone = dto.Phone,
                IsDefault = dto.IsDefault
            };

            if (profile.IsDefault)
            {
                var others = await _context.ContractorProfiles.Where(p => p.IsDefault).ToListAsync();
                foreach (var other in others) other.IsDefault = false;
            }

            _context.ContractorProfiles.Add(profile);
            await _context.SaveChangesAsync();
            return Ok(MapContractor(profile));
        }

        // ── clients ────────────────────────────────────────────────────────────

        /// <summary>
        /// The client roster behind the Create Contract form.
        ///
        /// ACTIVE ONLY, and that is what makes a deactivated client disappear from contract
        /// selection without any further work: it is one filter, in one place, shared by everyone
        /// who picks a client to work with.
        /// </summary>
        [HttpGet("clients")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<List<ContractClientDto>>> GetClients([FromQuery] string? search)
        {
            var query = _context.ContractClients.Where(c => c.IsActive);
            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.Trim();
                query = query.Where(c => c.LegalEntityName.Contains(term));
            }

            var clients = await query.OrderBy(c => c.LegalEntityName).Take(200).ToListAsync();
            var ids = clients.Select(c => c.Id).ToList();

            var locations = await _context.ContractServiceLocations
                .Where(l => ids.Contains(l.ContractClientId) && l.IsActive)
                .ToListAsync();
            var contacts = await _context.ContractContacts
                .Where(c => c.ContractClientId != null && ids.Contains(c.ContractClientId!.Value) && c.IsActive)
                .ToListAsync();

            // The linked account's NAME, resolved once for the page rather than per row.
            //
            // The contract form shows it as a read-only "Linked account" line, so selecting a
            // client hydrates its whole relationship in one gesture. Without it the admin had to
            // choose the linked customer SEPARATELY to see who the client belonged to - the
            // duplicated-selector confusion this endpoint's caller was rebuilt to end.
            var linkedIds = clients.Where(c => c.SourceUserId != null)
                .Select(c => c.SourceUserId!.Value).Distinct().ToList();

            var accounts = linkedIds.Count == 0
                ? new Dictionary<int, (string Name, string? Email)>()
                : (await _context.Users
                        .Where(u => linkedIds.Contains(u.Id))
                        .Select(u => new { u.Id, u.FirstName, u.LastName, u.Email })
                        .ToListAsync())
                    .ToDictionary(
                        u => u.Id,
                        u => (Name: $"{u.FirstName} {u.LastName}".Trim(),
                              Email: NoEmailHelper.IsPlaceholder(u.Email) ? null : u.Email));

            return Ok(clients.Select(c =>
            {
                var dto = MapClient(c,
                    locations.Where(l => l.ContractClientId == c.Id),
                    contacts.Where(x => x.ContractClientId == c.Id));

                if (c.SourceUserId != null && accounts.TryGetValue(c.SourceUserId.Value, out var account))
                {
                    dto.SourceUserName = account.Name;
                    dto.SourceUserEmail = account.Email;
                }

                return dto;
            }).ToList());
        }

        /// <summary>
        /// Creates a commercial client ON ITS OWN — no contract, and therefore no DCC number
        /// consumed and no document generated.
        ///
        /// WHY THIS IS REACHABLE FROM THE UI (2026-09). A commercial client and a contract are
        /// separate facts: <c>CommercialInvoice.ContractId</c> is nullable precisely because plenty
        /// of commercial work is billed before an agreement is signed, or without one. Until now
        /// the only way to get a row into <c>ContractClients</c> was to save a contract draft, so
        /// billing a trial clean meant fabricating an agreement nobody intended to sign just to
        /// populate the invoice form's client dropdown. This endpoint existed already and had no
        /// caller; Commercial → Clients and the invoice form now use it.
        ///
        /// CREATION ONLY. Editing an existing client stays where it was, on the contract, because
        /// a rename there is governed by <see cref="Helpers.Contracts.ContractClientEditPolicy"/>
        /// — a change to the counterparty's legal name is a contract modification, not a typo fix.
        /// Nothing here touches an existing row, so none of that is weakened.
        /// </summary>
        [HttpPost("clients")]
        [RequirePermission(Permission.Create)]
        public async Task<ActionResult<ContractClientDto>> CreateClient(
            [FromBody] CreateCommercialClientDto dto)
        {
            var client = new ContractClient
            {
                LegalEntityName = dto.LegalEntityName.Trim(),
                EntityType = dto.EntityType.Trim(),
                FormationState = dto.FormationState?.Trim(),
                PrincipalAddress = dto.PrincipalAddress.Trim(),
                City = dto.City.Trim(),
                State = dto.State.Trim(),
                Zip = dto.Zip.Trim(),
                NoticeEmail = dto.NoticeEmail?.Trim(),
                Phone = dto.Phone,

                // The same rule the contract form applies, from the same place. A link is an
                // access grant to My Contracts, so it is only ever what staff explicitly chose -
                // never inferred from NoticeEmail matching some account's address.
                SourceUserId = await BusinessAccountLinkPolicy.ResolveAsync(_context, dto.SourceUserId)
            };
            _context.ContractClients.Add(client);
            await _context.SaveChangesAsync();

            // Saved after the client so both carry its id. Optional, and a client with neither is
            // perfectly valid - it is simply one an admin has to complete before an invoice to it
            // can be emailed.
            var locations = new List<ContractServiceLocation>();
            var contacts = new List<ContractContact>();

            if (dto.ServiceLocation != null)
            {
                var location = new ContractServiceLocation
                {
                    ContractClientId = client.Id,
                    BusinessBrand = dto.ServiceLocation.BusinessBrand?.Trim(),
                    LocationName = dto.ServiceLocation.LocationName?.Trim(),
                    Address = dto.ServiceLocation.Address.Trim(),
                    City = dto.ServiceLocation.City.Trim(),
                    State = dto.ServiceLocation.State.Trim(),
                    Zip = dto.ServiceLocation.Zip.Trim()
                };
                _context.ContractServiceLocations.Add(location);
                locations.Add(location);
            }

            if (dto.BillingContact != null)
            {
                var contact = new ContractContact
                {
                    ContractClientId = client.Id,
                    FirstName = dto.BillingContact.FirstName.Trim(),
                    LastName = dto.BillingContact.LastName.Trim(),
                    Title = dto.BillingContact.Title?.Trim(),
                    Email = dto.BillingContact.Email?.Trim(),
                    Phone = dto.BillingContact.Phone,
                    Role = dto.BillingContact.Role
                };
                _context.ContractContacts.Add(contact);
                contacts.Add(contact);
            }

            if (locations.Count > 0 || contacts.Count > 0)
                await _context.SaveChangesAsync();

            await _auditService.LogActionAsync(
                AuditEntityTypes.CommercialClient, client.Id, "Create",
                null,
                new
                {
                    ClientId = client.Id,
                    Company = client.LegalEntityName,
                    LinkedAccountId = client.SourceUserId,
                    Origin = client.SourceUserId == null ? "Standalone" : "Linked to a customer account"
                },
                new[] { "Company" },
                GetCurrentUserId());

            return Ok(MapClient(client, locations, contacts));
        }

        /// <summary>
        /// Edits a commercial client, from Commercial → Clients.
        ///
        /// ══ WHAT THIS CANNOT DO TO A CONTRACT ══
        ///
        /// Nothing, and that is structural rather than a rule enforced here. Every contract
        /// VERSION renders from its own frozen <c>ContractSnapshot</c>, copied at generation time —
        /// so an executed agreement keeps the counterparty name, address and signatures it was
        /// signed with no matter what this row later says, and renaming a client affects FUTURE
        /// contracts only. There is deliberately no second edit policy competing with that: the
        /// snapshot freeze is the protection, and <c>ContractClientEditPolicy</c> continues to
        /// govern the other direction — a CLIENT editing their own details on the review page,
        /// where a legal-name change is a contract modification that needs re-approval.
        ///
        /// The UI warns when the client has contracts, so the admin knows the existing documents
        /// are not being rewritten. Guarded by <c>ContractSnapshotIsFrozen…</c> in the specs.
        ///
        /// ══ THE ONE FIELD THAT SYNCS BACK ══
        ///
        /// For a client linked to a business account, the PHONE is written through to that
        /// account in this same save, and nothing else is. See <see cref="BusinessClientMapper"/>
        /// for why the other "overlapping" fields are not overlapping at all — a legal entity name
        /// is not a person's name, and <c>User.Email</c> is a login identity with a verification
        /// flow behind it that a billing screen must not reassign.
        /// </summary>
        [HttpPut("clients/{id}")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult<ContractClientDto>> UpdateClient(
            int id, [FromBody] UpdateCommercialClientDto dto)
        {
            var client = await _context.ContractClients.FirstOrDefaultAsync(c => c.Id == id);
            if (client == null) return NotFound(new { message = "Client not found." });

            var before = new
            {
                client.LegalEntityName,
                client.EntityType,
                client.PrincipalAddress,
                client.City,
                client.State,
                client.Zip,
                client.NoticeEmail,
                client.Phone
            };
            var phoneBefore = client.Phone;

            client.LegalEntityName = dto.LegalEntityName.Trim();
            client.EntityType = dto.EntityType.Trim();
            client.FormationState = dto.FormationState?.Trim();
            client.PrincipalAddress = dto.PrincipalAddress.Trim();
            client.City = dto.City.Trim();
            client.State = dto.State.Trim();
            client.Zip = dto.Zip.Trim();
            client.NoticeEmail = dto.NoticeEmail?.Trim();
            client.Phone = dto.Phone;
            client.UpdatedAt = DateTime.UtcNow;

            // SourceUserId is NOT read off the DTO. The link is created and removed by the
            // business flag on the account (and by Delete here); letting an edit re-point it would
            // be a second, quieter way to grant somebody access to another company's contracts.

            ApplyBillingContact(client, dto.BillingContact);
            ApplyServiceLocation(client, dto.ServiceLocation);

            // The one write-through, in the same SaveChanges as everything above.
            if (client.SourceUserId.HasValue)
            {
                var account = await _context.Users
                    .FirstOrDefaultAsync(u => u.Id == client.SourceUserId.Value);

                if (account != null
                    && BusinessClientMapper.ShouldWritePhoneThrough(account.Phone, phoneBefore))
                {
                    account.Phone = client.Phone;
                    account.UpdatedAt = DateTime.UtcNow;
                }
            }

            await _context.SaveChangesAsync();

            await _auditService.LogActionAsync(
                AuditEntityTypes.CommercialClient, client.Id, "Update",
                before,
                new
                {
                    client.LegalEntityName,
                    client.EntityType,
                    client.PrincipalAddress,
                    client.City,
                    client.State,
                    client.Zip,
                    client.NoticeEmail,
                    client.Phone
                },
                actingUserId: GetCurrentUserId());

            return Ok(await LoadClientDtoAsync(client));
        }

        /// <summary>
        /// SOFT DELETE — the client is deactivated, never removed.
        ///
        /// Everything carrying its name stays exactly where it is: contracts, contract versions and
        /// signatures, invoices, payments, payment history, activity logs and every DCC/DCI
        /// reference number. They resolve the client by id and do not test IsActive, so a
        /// historical invoice opened tomorrow still shows the company it was addressed to. What
        /// changes is only that the client stops being offered — the invoice dropdown, the contract
        /// client picker and the default Clients list all filter on IsActive.
        ///
        /// FOR A LINKED CLIENT this also clears the account's business designation, in the same
        /// transaction, and the UI says so before it happens. Without that the deletion would not
        /// stick: the account would still be flagged as a business, and the next flag save would
        /// bring the client straight back. The USER IS NOT DELETED — they keep their account,
        /// their bookings and their history, and re-ticking the business flag on the Users tab
        /// brings this same client row back with its contracts and invoices intact.
        ///
        /// Which is why the admin panel offers this as <b>"Move to Customers"</b> on a linked
        /// client rather than as a deletion — same call, and the account simply goes back to being
        /// an ordinary customer. A standalone client has no account to move back to and keeps the
        /// Delete wording. The response message follows whichever act it performed.
        ///
        /// Permission.Deactivate, not Permission.Delete: this IS a deactivation, it is fully
        /// reversible, and Delete is SuperAdmin-only — the people who run the commercial tab
        /// (Admins) are the ones who retire a client.
        /// </summary>
        [HttpDelete("clients/{id}")]
        [RequirePermission(Permission.Deactivate)]
        public async Task<ActionResult> DeactivateClient(int id)
        {
            var client = await _context.ContractClients.FirstOrDefaultAsync(c => c.Id == id);
            if (client == null) return NotFound(new { message = "Client not found." });

            if (!client.IsActive)
                return Ok(new { message = "That client is already inactive.", isActive = false });

            if (client.SourceUserId.HasValue)
            {
                await _businessClients.RemoveBusinessClientAsync(client, GetCurrentUserId());

                return Ok(new
                {
                    // Worded as the move it is: the admin pressed "Move to Customers", and what
                    // they care about is that the account is an ordinary customer again.
                    message = $"{client.LegalEntityName} was moved back to Customers — the business " +
                              "designation has been taken off the linked customer account, and they " +
                              "are no longer listed under Business Clients. Their contracts and " +
                              "invoices are unchanged.",
                    isActive = false,
                    businessFlagRemoved = true
                });
            }

            client.IsActive = false;
            client.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            await _auditService.LogActionAsync(
                AuditEntityTypes.CommercialClient, client.Id,
                BusinessClientService.ActionDeactivated,
                null,
                new { ClientId = client.Id, Company = client.LegalEntityName, Status = "Inactive" },
                new[] { "Status" },
                GetCurrentUserId());

            return Ok(new
            {
                message = $"{client.LegalEntityName} was removed from Commercial → Clients. " +
                          "Their contracts and invoices are unchanged.",
                isActive = false,
                businessFlagRemoved = false
            });
        }

        /// <summary>
        /// Brings a STANDALONE client back. Deliberately refuses a linked one: that client went
        /// away because the account stopped being a business, so it comes back the same way — by
        /// re-ticking the business flag on the Users tab, which reactivates this very row. Two
        /// routes to the same state is how the two end up disagreeing.
        /// </summary>
        [HttpPost("clients/{id}/restore")]
        [RequirePermission(Permission.Activate)]
        public async Task<ActionResult> RestoreClient(int id)
        {
            var client = await _context.ContractClients.FirstOrDefaultAsync(c => c.Id == id);
            if (client == null) return NotFound(new { message = "Client not found." });

            if (client.SourceUserId.HasValue)
                return BadRequest(new
                {
                    message = "This client belongs to a customer account. Turn the business flag " +
                              "back on for that customer and it will be listed again."
                });

            if (client.IsActive)
                return Ok(new { message = "That client is already active.", isActive = true });

            client.IsActive = true;
            client.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            await _auditService.LogActionAsync(
                AuditEntityTypes.CommercialClient, client.Id,
                BusinessClientService.ActionReactivated,
                null,
                new { ClientId = client.Id, Company = client.LegalEntityName, Status = "Active" },
                new[] { "Status" },
                GetCurrentUserId());

            return Ok(new { message = $"{client.LegalEntityName} is listed again.", isActive = true });
        }

        // ── shared write helpers ───────────────────────────────────────────────

        /// <summary>
        /// Creates, updates or clears the client's primary billing contact from one optional
        /// block. "Cleared" means deactivated, not deleted — a contact may be a contract's signer.
        /// </summary>
        private void ApplyBillingContact(ContractClient client, SaveContractContactDto? dto)
        {
            var existing = _context.ContractContacts
                .Where(c => c.ContractClientId == client.Id && c.IsActive)
                .OrderBy(c => c.Role == ContractContactRole.ClientSigner ? 0 : 1).ThenBy(c => c.Id)
                .FirstOrDefault();

            if (dto == null)
            {
                if (existing != null)
                {
                    existing.IsActive = false;
                    existing.UpdatedAt = DateTime.UtcNow;
                }
                return;
            }

            if (existing == null)
            {
                _context.ContractContacts.Add(new ContractContact
                {
                    ContractClientId = client.Id,
                    FirstName = dto.FirstName.Trim(),
                    LastName = dto.LastName.Trim(),
                    Title = dto.Title?.Trim(),
                    Email = dto.Email?.Trim(),
                    Phone = dto.Phone,
                    Role = dto.Role
                });
                return;
            }

            existing.FirstName = dto.FirstName.Trim();
            existing.LastName = dto.LastName.Trim();
            existing.Title = dto.Title?.Trim();
            existing.Email = dto.Email?.Trim();
            existing.Phone = dto.Phone;
            existing.UpdatedAt = DateTime.UtcNow;
        }

        /// <summary>
        /// Same shape for the client's first service location. A client with several keeps the
        /// rest; this edits the one the Clients screen shows and adds one when there is none.
        /// </summary>
        private void ApplyServiceLocation(ContractClient client, SaveContractServiceLocationDto? dto)
        {
            if (dto == null) return;

            var existing = _context.ContractServiceLocations
                .Where(l => l.ContractClientId == client.Id && l.IsActive)
                .OrderBy(l => l.Id)
                .FirstOrDefault();

            if (existing == null)
            {
                _context.ContractServiceLocations.Add(new ContractServiceLocation
                {
                    ContractClientId = client.Id,
                    BusinessBrand = dto.BusinessBrand?.Trim(),
                    LocationName = dto.LocationName?.Trim(),
                    Address = dto.Address.Trim(),
                    City = dto.City.Trim(),
                    State = dto.State.Trim(),
                    Zip = dto.Zip.Trim()
                });
                return;
            }

            existing.BusinessBrand = dto.BusinessBrand?.Trim();
            existing.LocationName = dto.LocationName?.Trim();
            existing.Address = dto.Address.Trim();
            existing.City = dto.City.Trim();
            existing.State = dto.State.Trim();
            existing.Zip = dto.Zip.Trim();
            existing.UpdatedAt = DateTime.UtcNow;
        }

        /// <summary>Re-reads the client's contacts and locations so the caller gets the saved truth.</summary>
        private async Task<ContractClientDto> LoadClientDtoAsync(ContractClient client)
        {
            var locations = await _context.ContractServiceLocations
                .Where(l => l.ContractClientId == client.Id && l.IsActive)
                .OrderBy(l => l.Id)
                .ToListAsync();

            var contacts = await _context.ContractContacts
                .Where(c => c.ContractClientId == client.Id && c.IsActive)
                .OrderBy(c => c.Role == ContractContactRole.ClientSigner ? 0 : 1).ThenBy(c => c.Id)
                .ToListAsync();

            return MapClient(client, locations, contacts);
        }

        // ── service locations ──────────────────────────────────────────────────

        [HttpGet("clients/{clientId}/locations")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<List<ContractServiceLocationDto>>> GetLocations(int clientId)
        {
            var rows = await _context.ContractServiceLocations
                .Where(l => l.ContractClientId == clientId && l.IsActive)
                .OrderBy(l => l.Id)
                .ToListAsync();
            return Ok(rows.Select(MapLocation).ToList());
        }

        [HttpPost("locations")]
        [RequirePermission(Permission.Create)]
        public async Task<ActionResult<ContractServiceLocationDto>> CreateLocation(
            [FromBody] SaveContractServiceLocationDto dto)
        {
            if (!await _context.ContractClients.AnyAsync(c => c.Id == dto.ContractClientId))
                return BadRequest(new { message = "Select the client this location belongs to first." });

            var location = new ContractServiceLocation
            {
                ContractClientId = dto.ContractClientId,
                BusinessBrand = dto.BusinessBrand?.Trim(),
                LocationName = dto.LocationName?.Trim(),
                Address = dto.Address.Trim(),
                City = dto.City.Trim(),
                State = dto.State.Trim(),
                Zip = dto.Zip.Trim()
            };
            _context.ContractServiceLocations.Add(location);
            await _context.SaveChangesAsync();
            return Ok(MapLocation(location));
        }

        [HttpPut("locations/{id}")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult<ContractServiceLocationDto>> UpdateLocation(
            int id, [FromBody] SaveContractServiceLocationDto dto)
        {
            var location = await _context.ContractServiceLocations.FirstOrDefaultAsync(l => l.Id == id);
            if (location == null) return NotFound(new { message = "Service location not found." });

            location.BusinessBrand = dto.BusinessBrand?.Trim();
            location.LocationName = dto.LocationName?.Trim();
            location.Address = dto.Address.Trim();
            location.City = dto.City.Trim();
            location.State = dto.State.Trim();
            location.Zip = dto.Zip.Trim();
            location.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
            return Ok(MapLocation(location));
        }

        // ── contacts ───────────────────────────────────────────────────────────

        [HttpGet("contacts")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<List<ContractContactDto>>> GetContacts(
            [FromQuery] ContractContactRole? role, [FromQuery] int? clientId, [FromQuery] string? search)
        {
            var query = _context.ContractContacts.Where(c => c.IsActive);
            if (role.HasValue) query = query.Where(c => c.Role == role.Value);
            if (clientId.HasValue) query = query.Where(c => c.ContractClientId == clientId.Value);
            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.Trim();
                query = query.Where(c =>
                    c.FirstName.Contains(term) || c.LastName.Contains(term) ||
                    (c.Email != null && c.Email.Contains(term)));
            }

            var rows = await query.OrderBy(c => c.LastName).ThenBy(c => c.FirstName).Take(200).ToListAsync();
            return Ok(rows.Select(MapContact).ToList());
        }

        [HttpPost("contacts")]
        [RequirePermission(Permission.Create)]
        public async Task<ActionResult<ContractContactDto>> CreateContact([FromBody] SaveContractContactDto dto)
        {
            var contact = new ContractContact
            {
                FirstName = dto.FirstName.Trim(),
                LastName = dto.LastName.Trim(),
                Title = dto.Title?.Trim(),
                Email = dto.Email?.Trim(),
                Phone = dto.Phone,
                Role = dto.Role,
                ContractClientId = dto.ContractClientId
            };
            _context.ContractContacts.Add(contact);
            await _context.SaveChangesAsync();
            return Ok(MapContact(contact));
        }

        [HttpPut("contacts/{id}")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult<ContractContactDto>> UpdateContact(
            int id, [FromBody] SaveContractContactDto dto)
        {
            var contact = await _context.ContractContacts.FirstOrDefaultAsync(c => c.Id == id);
            if (contact == null) return NotFound(new { message = "Contact not found." });

            contact.FirstName = dto.FirstName.Trim();
            contact.LastName = dto.LastName.Trim();
            contact.Title = dto.Title?.Trim();
            contact.Email = dto.Email?.Trim();
            contact.Phone = dto.Phone;
            contact.Role = dto.Role;
            contact.ContractClientId = dto.ContractClientId;
            contact.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
            return Ok(MapContact(contact));
        }

        // ── Business types and their scope-of-work templates ───────────────────
        //
        // A ScopeTemplate IS the business type: Restaurant, Gym/Studio, Office, and whatever an
        // admin adds next. Its StructureJson holds the ordered CATEGORIES, each holding ITEMS with
        // a default-selected flag - so "Business type -> scope template -> categories -> items" is
        // the shape already in the database, and nothing about the premises types is hardcoded.
        //
        // EVERY EDIT HERE AFFECTS FUTURE CONTRACTS ONLY. Picking a template on a contract DEEP
        // COPIES it into that contract's snapshot, and a generated version freezes the copy, so
        // renaming a category or retiring an item cannot reach a document that already exists -
        // structurally, not by a rule someone has to remember.

        /// <summary>
        /// The business types offered when creating a contract, with their default checklists.
        ///
        /// Archived categories and items are stripped: they exist so a retired row keeps rendering
        /// on the signed contracts that used it, not so it keeps being offered on new ones. The
        /// template EDITOR reads the unfiltered structure through the endpoint below.
        /// </summary>
        [HttpGet("scope-templates")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<List<ScopeTemplateDto>>> GetScopeTemplates(
            [FromQuery] bool includeArchived = false)
        {
            var rows = await _context.ScopeTemplates
                .Where(t => includeArchived || t.IsActive)
                .OrderBy(t => t.SortOrder).ThenBy(t => t.Id)
                .ToListAsync();

            return Ok(rows.Select(t => ToScopeTemplateDto(t, includeArchived)).ToList());
        }

        /// <summary>One business type in full, INCLUDING archived rows, for the template editor.</summary>
        [HttpGet("scope-templates/{id}")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<ScopeTemplateDto>> GetScopeTemplate(int id)
        {
            var row = await _context.ScopeTemplates.FirstOrDefaultAsync(t => t.Id == id);
            if (row == null) return NotFound(new { message = "Business type not found." });

            return Ok(ToScopeTemplateDto(row, includeArchived: true));
        }

        [HttpPost("scope-templates")]
        [RequirePermission(Permission.Create)]
        public async Task<ActionResult<ScopeTemplateDto>> CreateScopeTemplate(
            [FromBody] SaveScopeTemplateDto dto)
        {
            var name = (dto.Name ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(name))
                return BadRequest(new { message = "Enter a name for the business type." });

            if (await _context.ScopeTemplates.AnyAsync(t => t.Name == name))
                return BadRequest(new { message = $"A business type called \"{name}\" already exists." });

            var row = new ScopeTemplate
            {
                Name = name,
                PremisesType = Fallback(dto.PremisesType, "premises"),
                AllowsCustomRows = dto.AllowsCustomRows,
                SortOrder = dto.SortOrder,
                StructureJson = (dto.Structure ?? new ScopeStructure()).ToJson(),
                IsActive = true
            };

            _context.ScopeTemplates.Add(row);
            await _context.SaveChangesAsync();

            await _auditService.LogActionAsync(AuditEntityTypes.CommercialClient, row.Id,
                "ScopeTemplateCreated", null,
                new { row.Id, row.Name, row.PremisesType }, new[] { "Name" }, GetCurrentUserId());

            return Ok(ToScopeTemplateDto(row, includeArchived: true));
        }

        /// <summary>
        /// Renames a business type, or replaces its checklist wholesale.
        ///
        /// The whole structure is sent and stored as one document - categories reordered, items
        /// added, renamed, re-flagged or archived - because the editor manipulates a tree and
        /// per-row endpoints would turn one screenful of edits into a dozen requests that can
        /// half-fail.
        /// </summary>
        [HttpPut("scope-templates/{id}")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult<ScopeTemplateDto>> UpdateScopeTemplate(
            int id, [FromBody] SaveScopeTemplateDto dto)
        {
            var row = await _context.ScopeTemplates.FirstOrDefaultAsync(t => t.Id == id);
            if (row == null) return NotFound(new { message = "Business type not found." });

            var name = (dto.Name ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(name))
                return BadRequest(new { message = "Enter a name for the business type." });

            if (await _context.ScopeTemplates.AnyAsync(t => t.Name == name && t.Id != id))
                return BadRequest(new { message = $"A business type called \"{name}\" already exists." });

            row.Name = name;
            row.PremisesType = Fallback(dto.PremisesType, row.PremisesType);
            row.AllowsCustomRows = dto.AllowsCustomRows;
            row.SortOrder = dto.SortOrder;
            if (dto.Structure != null) row.StructureJson = dto.Structure.ToJson();
            row.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            await _auditService.LogActionAsync(AuditEntityTypes.CommercialClient, row.Id,
                "ScopeTemplateUpdated", null,
                new { row.Id, row.Name, row.PremisesType, Note = "Affects future contracts only." },
                new[] { "Name" }, GetCurrentUserId());

            return Ok(ToScopeTemplateDto(row, includeArchived: true));
        }

        /// <summary>
        /// Archives a business type. Never a hard delete.
        ///
        /// Deactivate rather than Delete for the same reason a commercial client is: contracts
        /// join it by id and do not test IsActive, so every existing agreement keeps resolving the
        /// template it was built from. What changes is only that it stops being OFFERED.
        /// </summary>
        [HttpDelete("scope-templates/{id}")]
        [RequirePermission(Permission.Deactivate)]
        public async Task<ActionResult> ArchiveScopeTemplate(int id)
        {
            var row = await _context.ScopeTemplates.FirstOrDefaultAsync(t => t.Id == id);
            if (row == null) return NotFound(new { message = "Business type not found." });

            row.IsActive = false;
            row.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            await _auditService.LogActionAsync(AuditEntityTypes.CommercialClient, row.Id,
                "ScopeTemplateArchived", null, new { row.Id, row.Name }, new[] { "Name" }, GetCurrentUserId());

            return Ok(new { message = $"\"{row.Name}\" is no longer offered on new contracts." });
        }

        [HttpPost("scope-templates/{id}/restore")]
        [RequirePermission(Permission.Activate)]
        public async Task<ActionResult> RestoreScopeTemplate(int id)
        {
            var row = await _context.ScopeTemplates.FirstOrDefaultAsync(t => t.Id == id);
            if (row == null) return NotFound(new { message = "Business type not found." });

            row.IsActive = true;
            row.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            return Ok(new { message = $"\"{row.Name}\" is available again." });
        }

        private static ScopeTemplateDto ToScopeTemplateDto(ScopeTemplate t, bool includeArchived)
        {
            var structure = ScopeStructure.Parse(t.StructureJson);

            return new ScopeTemplateDto
            {
                Id = t.Id,
                Name = t.Name,
                PremisesType = t.PremisesType,
                AllowsCustomRows = t.AllowsCustomRows,
                SortOrder = t.SortOrder,
                IsActive = t.IsActive,
                Structure = includeArchived ? structure : structure.WithoutArchived()
            };
        }

        private static string Fallback(string? value, string fallback) =>
            string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

        // ── contract templates ─────────────────────────────────────────────────

        [HttpGet("contract-templates")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<List<ContractTemplateDto>>> GetContractTemplates()
        {
            // DEFAULT FIRST, then newest. The master agreement is versioned by ADDING a row, so
            // ordering by name alone put v1.0 at the top of a list the form preselects from - which
            // is how a new contract silently kept rendering superseded language. The flag is also
            // returned so the form can preselect deliberately rather than trusting this order.
            var rows = await _context.ContractTemplates
                .Where(t => t.IsActive)
                .OrderByDescending(t => t.IsDefault)
                .ThenByDescending(t => t.Id)
                .Select(t => new ContractTemplateDto
                {
                    Id = t.Id,
                    Name = t.Name,
                    Version = t.Version,
                    Description = t.Description,
                    IsActive = t.IsActive,
                    IsDefault = t.IsDefault
                })
                .ToListAsync();
            return Ok(rows);
        }

        /// <summary>
        /// The body itself is SuperAdmin-only to READ as well as to write - it is the master legal
        /// text, and the only screen that needs it is the SuperAdmin template editor. Filling in a
        /// contract never requires it: the body is copied into the snapshot server-side.
        /// </summary>
        [HttpGet("contract-templates/{id}")]
        [Authorize(Roles = "SuperAdmin")]
        public async Task<ActionResult<ContractTemplateDto>> GetContractTemplate(int id)
        {
            var t = await _context.ContractTemplates.FirstOrDefaultAsync(x => x.Id == id);
            if (t == null) return NotFound(new { message = "Template not found." });

            return Ok(new ContractTemplateDto
            {
                Id = t.Id,
                Name = t.Name,
                Version = t.Version,
                Description = t.Description,
                IsActive = t.IsActive,
                BodyText = t.BodyText
            });
        }

        [HttpPut("contract-templates/{id}")]
        [Authorize(Roles = "SuperAdmin")]
        public async Task<ActionResult<ContractTemplateDto>> UpdateContractTemplate(
            int id, [FromBody] SaveContractTemplateDto dto)
        {
            var t = await _context.ContractTemplates.FirstOrDefaultAsync(x => x.Id == id);
            if (t == null) return NotFound(new { message = "Template not found." });

            t.Name = dto.Name.Trim();
            t.Version = dto.Version.Trim();
            t.Description = dto.Description?.Trim();
            t.BodyText = dto.BodyText;
            t.IsActive = dto.IsActive;
            t.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            // Existing contracts are untouched by design: each version froze a copy of the body it
            // rendered, so this edit reaches only versions generated from now on.
            return Ok(new ContractTemplateDto
            {
                Id = t.Id, Name = t.Name, Version = t.Version,
                Description = t.Description, IsActive = t.IsActive, BodyText = t.BodyText
            });
        }

        [HttpPost("contract-templates")]
        [Authorize(Roles = "SuperAdmin")]
        public async Task<ActionResult<ContractTemplateDto>> CreateContractTemplate(
            [FromBody] SaveContractTemplateDto dto)
        {
            var t = new ContractTemplate
            {
                Name = dto.Name.Trim(),
                Version = dto.Version.Trim(),
                Description = dto.Description?.Trim(),
                BodyText = dto.BodyText,
                IsActive = dto.IsActive
            };
            _context.ContractTemplates.Add(t);
            await _context.SaveChangesAsync();

            return Ok(new ContractTemplateDto
            {
                Id = t.Id, Name = t.Name, Version = t.Version,
                Description = t.Description, IsActive = t.IsActive, BodyText = t.BodyText
            });
        }

        // ── mapping ────────────────────────────────────────────────────────────

        private static ContractorProfileDto MapContractor(ContractorProfile p) => new()
        {
            Id = p.Id,
            LegalEntityName = p.LegalEntityName,
            Dba = p.Dba,
            EntityType = p.EntityType,
            Address = p.Address,
            City = p.City,
            State = p.State,
            Zip = p.Zip,
            NoticeEmail = p.NoticeEmail,
            Phone = p.Phone,
            IsDefault = p.IsDefault
        };

        private static ContractClientDto MapClient(
            ContractClient c,
            IEnumerable<ContractServiceLocation> locations,
            IEnumerable<ContractContact> contacts) => new()
        {
            Id = c.Id,
            LegalEntityName = c.LegalEntityName,
            EntityType = c.EntityType,
            FormationState = c.FormationState,
            PrincipalAddress = c.PrincipalAddress,
            City = c.City,
            State = c.State,
            Zip = c.Zip,
            NoticeEmail = c.NoticeEmail,
            Phone = c.Phone,
            IsActive = c.IsActive,

            // Returned so the caller can see WHICH account was linked, rather than having to trust
            // that the one it asked for was accepted. The name and email stay unset here: nothing
            // on this surface displays them, and filling them would cost a join on every row.
            SourceUserId = c.SourceUserId,

            ServiceLocations = locations.Select(MapLocation).ToList(),
            Contacts = contacts.Select(MapContact).ToList()
        };

        private static ContractServiceLocationDto MapLocation(ContractServiceLocation l) => new()
        {
            Id = l.Id,
            ContractClientId = l.ContractClientId,
            BusinessBrand = l.BusinessBrand,
            LocationName = l.LocationName,
            Address = l.Address,
            City = l.City,
            State = l.State,
            Zip = l.Zip,
            DisplayLabel = ContractReadService.LocationLabel(l)
        };

        private static ContractContactDto MapContact(ContractContact c) => new()
        {
            Id = c.Id,
            FirstName = c.FirstName,
            LastName = c.LastName,
            FullName = c.FullName,
            Title = c.Title,
            Email = c.Email,
            Phone = c.Phone,
            Role = c.Role,
            ContractClientId = c.ContractClientId
        };
    }
}
