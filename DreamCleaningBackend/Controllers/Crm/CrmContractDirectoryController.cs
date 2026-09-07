using DreamCleaningBackend.Attributes;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Models.Contracts;
using DreamCleaningBackend.Services.Contracts;
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
    public class CrmContractDirectoryController : ControllerBase
    {
        private readonly ApplicationDbContext _context;

        public CrmContractDirectoryController(ApplicationDbContext context)
        {
            _context = context;
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

            return Ok(clients.Select(c => MapClient(c,
                locations.Where(l => l.ContractClientId == c.Id),
                contacts.Where(x => x.ContractClientId == c.Id))).ToList());
        }

        [HttpPost("clients")]
        [RequirePermission(Permission.Create)]
        public async Task<ActionResult<ContractClientDto>> CreateClient([FromBody] SaveContractClientDto dto)
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
                Phone = dto.Phone
            };
            _context.ContractClients.Add(client);
            await _context.SaveChangesAsync();
            return Ok(MapClient(client, Array.Empty<ContractServiceLocation>(), Array.Empty<ContractContact>()));
        }

        [HttpPut("clients/{id}")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult<ContractClientDto>> UpdateClient(int id, [FromBody] SaveContractClientDto dto)
        {
            var client = await _context.ContractClients.FirstOrDefaultAsync(c => c.Id == id);
            if (client == null) return NotFound(new { message = "Client not found." });

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
            await _context.SaveChangesAsync();

            // NOTE: this does NOT rewrite any generated contract. Every version renders from its
            // own frozen snapshot, so renaming a client here affects future contracts only.
            return Ok(MapClient(client, Array.Empty<ContractServiceLocation>(), Array.Empty<ContractContact>()));
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

        // ── scope templates ────────────────────────────────────────────────────

        [HttpGet("scope-templates")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<List<ScopeTemplateDto>>> GetScopeTemplates()
        {
            var rows = await _context.ScopeTemplates
                .Where(t => t.IsActive)
                .OrderBy(t => t.SortOrder)
                .ToListAsync();

            return Ok(rows.Select(t => new ScopeTemplateDto
            {
                Id = t.Id,
                Name = t.Name,
                PremisesType = t.PremisesType,
                AllowsCustomRows = t.AllowsCustomRows,
                Structure = ScopeStructure.Parse(t.StructureJson)
            }).ToList());
        }

        // ── contract templates ─────────────────────────────────────────────────

        [HttpGet("contract-templates")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<List<ContractTemplateDto>>> GetContractTemplates()
        {
            var rows = await _context.ContractTemplates
                .Where(t => t.IsActive)
                .OrderBy(t => t.Name)
                .Select(t => new ContractTemplateDto
                {
                    Id = t.Id,
                    Name = t.Name,
                    Version = t.Version,
                    Description = t.Description,
                    IsActive = t.IsActive
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
