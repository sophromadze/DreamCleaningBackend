using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs.Commercial;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services;
using DreamCleaningBackend.Services.Commercial;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DreamCleaningBackend.Controllers.Admin
{
    /// <summary>
    /// The company's billing identity and the bank account commercial invoices are paid into.
    ///
    /// THE READ AND THE WRITE ARE GATED DIFFERENTLY, on purpose.
    ///
    /// The READ is Admin-and-up because an admin sending an invoice legitimately needs to see the
    /// payment instructions their client is about to receive - being unable to check them would
    /// make it impossible to answer "which account should we pay into?" on a phone call. What that
    /// read returns is MASKED for anyone who cannot edit: last four digits only.
    ///
    /// The WRITE is SuperAdmin-only. Changing the destination account silently redirects every
    /// future payment the business receives, which is precisely how invoice fraud is committed, so
    /// it sits with the smallest possible group and every change is audit-logged.
    ///
    /// WHAT IS NOT HERE, AND MUST NOT BE ADDED: online banking credentials, a bank API key or
    /// secret, an OAuth token. These are RECEIVING coordinates only. When the reconciliation
    /// integration in the backlog is built, its secrets belong in configuration or a secret
    /// manager, never in this table or this endpoint.
    /// </summary>
    [Route("api/admin/commercial/billing-settings")]
    [ApiController]
    [Authorize(Roles = "Admin,SuperAdmin")]
    public class AdminBillingSettingsController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly BillingSettingsService _settings;
        private readonly IAuditService _audit;

        public AdminBillingSettingsController(
            ApplicationDbContext context,
            BillingSettingsService settings,
            IAuditService audit)
        {
            _context = context;
            _settings = settings;
            _audit = audit;
        }

        private int CurrentUserId =>
            int.TryParse(User.FindFirst("UserId")?.Value, out var id) ? id : 0;

        private bool IsSuperAdmin => User.IsInRole(nameof(UserRole.SuperAdmin));

        /// <summary>
        /// The settings. The account number comes back in full only for a SuperAdmin, who is the
        /// only person who can change it anyway.
        /// </summary>
        [HttpGet]
        public async Task<ActionResult<BillingSettingsDto>> Get()
        {
            var settings = await _settings.GetOrCreateAsync();
            return Ok(_settings.ToDto(settings, IsSuperAdmin));
        }

        /// <summary>
        /// The commercial billing DEFAULTS a new contract or invoice starts from: tax mode, tax
        /// rate, contract price mode, due terms and the ACH fee configuration.
        ///
        /// A separate, narrower endpoint rather than the full settings read, because the two
        /// creation forms need these five values and nothing else. Handing them a payload that
        /// also carries the company's bank account - which neither form displays - would be a leak
        /// waiting for a future copy-paste.
        ///
        /// ONE SOURCE, TWO CONSUMERS. Contract creation reads the price mode and rate here;
        /// invoice creation reads the tax type and rate here; editing either and ticking "save as
        /// default" writes back here. That is what stops a contract quoting tax-inclusive while its
        /// invoices add 8.875% on top.
        /// </summary>
        [HttpGet("defaults")]
        public async Task<ActionResult<CommercialBillingDefaultsDto>> GetDefaults()
        {
            var settings = await _settings.GetOrCreateAsync();
            return Ok(_settings.ToDefaultsDto(settings));
        }

        /// <summary>
        /// Saves the settings. SuperAdmin only.
        ///
        /// The audit row NAMES the fields that changed but never QUOTES the bank values - an audit
        /// trail that reproduces the account number in plain text simply moves the sensitive value
        /// into a second, longer-lived and more widely-read table.
        /// </summary>
        [HttpPut]
        [Authorize(Roles = "SuperAdmin")]
        public async Task<ActionResult<BillingSettingsDto>> Update([FromBody] SaveBillingSettingsDto dto)
        {
            var settings = await _settings.GetOrCreateAsync();

            var summary = BillingSettingsService.DescribeChanges(settings, dto);
            var bankFieldsMoved = summary.Contains("routing number")
                                  || summary.Contains("account number")
                                  || summary.Contains("bank name")
                                  || summary.Contains("account holder");

            _settings.Apply(settings, dto, CurrentUserId);
            await _context.SaveChangesAsync();

            await _audit.LogActionAsync(
                AuditEntityTypes.BillingSettingsChange,
                0,
                "BillingSettingsUpdated",
                null,
                new
                {
                    Summary = summary,
                    BankDetailsChanged = bankFieldsMoved,
                    UpdatedAt = settings.UpdatedAt
                },
                actingUserId: CurrentUserId);

            return Ok(_settings.ToDto(settings, true));
        }
    }
}
