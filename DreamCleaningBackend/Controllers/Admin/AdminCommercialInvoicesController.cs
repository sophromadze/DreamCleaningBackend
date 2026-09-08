using DreamCleaningBackend.Attributes;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs.Commercial;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Helpers.Commercial;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Models.Commercial;
using DreamCleaningBackend.Models.Contracts;
using DreamCleaningBackend.Services;
using DreamCleaningBackend.Services.Commercial;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DreamCleaningBackend.Controllers.Admin
{
    /// <summary>
    /// Commercial invoicing: the Commercial → Invoices tab.
    ///
    /// ADMIN AND SUPERADMIN. Billing a commercial client is day-to-day work - the people who
    /// arrange the cleaning are the people who invoice for it - so it follows the app's ordinary
    /// role hierarchy rather than the Contracts module's OrgTitle matrix. Two actions are
    /// SuperAdmin-only and marked per action: VOIDING an invoice (a permanent financial record
    /// that a number was issued and cancelled) and editing the BANKING settings, which lives on
    /// its own controller next door.
    ///
    /// Moderators hold Permission.View elsewhere and are deliberately excluded here: the
    /// controller-level role attribute keeps them out entirely, so widening a permission later
    /// cannot hand them billing as a side effect.
    ///
    /// NOTHING HERE TOUCHES THE RESIDENTIAL CHECKOUT. No PaymentIntent is created, no card is
    /// charged, no Stripe call is made. A commercial client pays by ACH into the company account
    /// and an admin records it.
    /// </summary>
    [Route("api/admin/commercial/invoices")]
    [ApiController]
    [Authorize(Roles = "Admin,SuperAdmin")]
    public class AdminCommercialInvoicesController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly InvoiceService _invoices;
        private readonly InvoicePaymentService _payments;
        private readonly InvoiceEmailService _emails;
        private readonly InvoicePdfService _pdf;
        private readonly InvoiceOverdueService _overdue;
        private readonly IAuditService _audit;

        public AdminCommercialInvoicesController(
            ApplicationDbContext context,
            InvoiceService invoices,
            InvoicePaymentService payments,
            InvoiceEmailService emails,
            InvoicePdfService pdf,
            InvoiceOverdueService overdue,
            IAuditService audit)
        {
            _context = context;
            _invoices = invoices;
            _payments = payments;
            _emails = emails;
            _pdf = pdf;
            _overdue = overdue;
            _audit = audit;
        }

        private int CurrentUserId =>
            int.TryParse(User.FindFirst("UserId")?.Value, out var id) ? id : 0;

        // ── List ──────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The invoice table plus the four summary cards.
        ///
        /// The summary is computed over the WHOLE filtered set, not the current page - "Total
        /// outstanding" that only counted the twenty rows on screen would be actively misleading.
        /// </summary>
        [HttpGet]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<InvoiceListResponseDto>> List(
            [FromQuery] string? search = null,
            [FromQuery] InvoiceStatus? status = null,
            [FromQuery] int? clientId = null,
            [FromQuery] InvoicePaymentMethod? paymentMethod = null,
            [FromQuery] DateTime? fromDate = null,
            [FromQuery] DateTime? toDate = null,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 25)
        {
            var query = _context.CommercialInvoices
                .Include(i => i.Client)
                .Include(i => i.Contract)
                .AsQueryable();

            if (status.HasValue) query = query.Where(i => i.Status == status.Value);
            if (clientId is > 0) query = query.Where(i => i.ContractClientId == clientId.Value);
            if (paymentMethod.HasValue) query = query.Where(i => i.PaymentMethod == paymentMethod.Value);
            if (fromDate.HasValue) query = query.Where(i => i.InvoiceDate >= fromDate.Value.Date);
            if (toDate.HasValue) query = query.Where(i => i.InvoiceDate <= toDate.Value.Date);

            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.Trim();

                // Every field the spec asks to be searchable. The contact join is a subquery
                // rather than an Include so a client with many contacts does not multiply rows.
                query = query.Where(i =>
                    i.InvoiceNumber.Contains(term)
                    || (i.Client != null && i.Client.LegalEntityName.Contains(term))
                    || (i.Client != null && i.Client.NoticeEmail != null && i.Client.NoticeEmail.Contains(term))
                    || (i.ServiceAddress != null && i.ServiceAddress.Contains(term))
                    || (i.Contract != null && i.Contract.ContractNumber.Contains(term))
                    || (i.PoNumber != null && i.PoNumber.Contains(term))
                    || _context.ContractContacts.Any(c =>
                        c.ContractClientId == i.ContractClientId
                        && ((c.FirstName + " " + c.LastName).Contains(term)
                            || (c.Email != null && c.Email.Contains(term)))));
            }

            var summary = await BuildSummaryAsync(query);
            var totalCount = await query.CountAsync();

            pageSize = Math.Clamp(pageSize, 1, 200);
            page = Math.Max(1, page);

            var rows = await query
                .OrderByDescending(i => i.InvoiceDate).ThenByDescending(i => i.Id)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(i => new InvoiceListItemDto
                {
                    Id = i.Id,
                    InvoiceNumber = i.InvoiceNumber,
                    ContractClientId = i.ContractClientId,
                    ClientName = i.Client!.LegalEntityName,
                    ServiceAddress = i.ServiceAddress,
                    ContractNumber = i.Contract != null ? i.Contract.ContractNumber : null,
                    InvoiceDate = i.InvoiceDate,
                    DueDate = i.DueDate,
                    Total = i.Total,
                    AmountPaid = i.AmountPaid,
                    BalanceDue = i.BalanceDue,
                    Status = i.Status,
                    PaymentMethod = i.PaymentMethod,
                    PaidAt = i.PaidAt,
                    LastSentAt = i.LastSentAt,
                    HasBeenSent = i.FirstSentAt != null
                })
                .ToListAsync();

            foreach (var row in rows) row.StatusLabel = InvoiceStatusPolicy.Label(row.Status);

            return Ok(new InvoiceListResponseDto
            {
                Invoices = rows,
                Summary = summary,
                TotalCount = totalCount,
                Page = page,
                PageSize = pageSize
            });
        }

        /// <summary>
        /// The four cards. "Paid this month" comes from the PAYMENT rows by payment date, not from
        /// invoices whose status happens to be Paid - the question is how much money arrived this
        /// month, and an invoice raised in August and settled in September belongs to September.
        /// </summary>
        private async Task<InvoiceSummaryDto> BuildSummaryAsync(IQueryable<CommercialInvoice> filtered)
        {
            var today = DateTime.UtcNow.Date;
            var monthStart = new DateTime(today.Year, today.Month, 1);

            var outstanding = filtered.Where(i =>
                i.Status != InvoiceStatus.Void &&
                i.Status != InvoiceStatus.Draft &&
                i.BalanceDue > 0m);

            var overdue = outstanding.Where(i => i.Status == InvoiceStatus.Overdue);

            var paidThisMonth = await _context.CommercialInvoicePayments
                .Where(p => p.PaymentDate >= monthStart && p.PaymentDate < monthStart.AddMonths(1))
                .SumAsync(p => (decimal?)p.Amount) ?? 0m;

            return new InvoiceSummaryDto
            {
                TotalOutstanding = await outstanding.SumAsync(i => (decimal?)i.BalanceDue) ?? 0m,
                OutstandingCount = await outstanding.CountAsync(),
                Overdue = await overdue.SumAsync(i => (decimal?)i.BalanceDue) ?? 0m,
                OverdueCount = await overdue.CountAsync(),
                PaidThisMonth = paidThisMonth,
                DraftCount = await filtered.CountAsync(i => i.Status == InvoiceStatus.Draft)
            };
        }

        // ── Detail ────────────────────────────────────────────────────────────────────────────

        [HttpGet("{id:int}")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<InvoiceDetailDto>> Get(int id)
        {
            var dto = await _invoices.GetDetailAsync(id);
            return dto == null ? NotFound(new { message = "Invoice not found." }) : Ok(dto);
        }

        // ── Create / update ───────────────────────────────────────────────────────────────────

        [HttpPost]
        [RequirePermission(Permission.Create)]
        public async Task<ActionResult<InvoiceDetailDto>> Create([FromBody] SaveInvoiceDto dto)
        {
            var invoice = await _invoices.CreateAsync(dto, CurrentUserId);
            var detail = await _invoices.GetDetailAsync(invoice.Id);
            return CreatedAtAction(nameof(Get), new { id = invoice.Id }, detail);
        }

        [HttpPut("{id:int}")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult<InvoiceDetailDto>> Update(int id, [FromBody] SaveInvoiceDto dto)
        {
            await _invoices.UpdateAsync(id, dto, CurrentUserId);
            return Ok(await _invoices.GetDetailAsync(id));
        }

        /// <summary>Only an unsent Draft. Anything issued is voided instead - see Void.</summary>
        [HttpDelete("{id:int}")]
        [RequirePermission(Permission.Delete)]
        public async Task<IActionResult> Delete(int id)
        {
            await _invoices.DeleteDraftAsync(id, CurrentUserId);
            return Ok(new { message = "Draft invoice deleted." });
        }

        // ── Actions ───────────────────────────────────────────────────────────────────────────

        /// <summary>Emails the invoice to the billing contact and stamps it Sent.</summary>
        [HttpPost("{id:int}/send")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult<InvoiceDetailDto>> Send(int id, [FromBody] SendInvoiceDto dto)
        {
            var log = await _emails.SendInvoiceAsync(id, dto ?? new SendInvoiceDto(), CurrentUserId);

            if (log.Status == InvoiceEmailStatus.Failed)
            {
                // 502, not 500: the invoice is fine, the mail transport is not, and the failure is
                // already recorded in the invoice's email history for the admin to see.
                return StatusCode(502, new
                {
                    message = "The invoice could not be emailed. " + (log.FailureReason ?? "Please try again."),
                    invoice = await _invoices.GetDetailAsync(id)
                });
            }

            return Ok(await _invoices.GetDetailAsync(id));
        }

        [HttpPost("{id:int}/reminder")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult<InvoiceDetailDto>> SendReminder(
            int id, [FromBody] SendInvoiceReminderDto dto)
        {
            var log = await _emails.SendReminderAsync(id, dto ?? new SendInvoiceReminderDto(), CurrentUserId);

            if (log.Status == InvoiceEmailStatus.Failed)
                return StatusCode(502, new
                {
                    message = "The reminder could not be sent. " + (log.FailureReason ?? "Please try again.")
                });

            return Ok(await _invoices.GetDetailAsync(id));
        }

        [HttpPost("{id:int}/receipt")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult<InvoiceDetailDto>> SendReceipt(int id)
        {
            var log = await _emails.SendPaymentReceiptAsync(id, CurrentUserId);

            if (log.Status == InvoiceEmailStatus.Failed)
                return StatusCode(502, new
                {
                    message = "The receipt could not be sent. " + (log.FailureReason ?? "Please try again.")
                });

            return Ok(await _invoices.GetDetailAsync(id));
        }

        /// <summary>
        /// Records money received. Also serves "Mark as Paid", which is the same call with the
        /// balance prefilled - there is deliberately no endpoint that sets the status directly.
        /// </summary>
        [HttpPost("{id:int}/payments")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult<InvoiceDetailDto>> RecordPayment(
            int id, [FromBody] RecordInvoicePaymentDto dto)
        {
            var (invoice, payment, becamePaid) = await _payments.RecordAsync(id, dto, CurrentUserId);

            // Money movement goes to the app-wide audit log as well as the invoice's own timeline:
            // the timeline is what an admin reads on the page, the audit log is what a financial
            // review searches across every entity.
            await _audit.LogActionAsync(
                AuditEntityTypes.CommercialInvoicePaymentAction,
                invoice.Id,
                "PaymentRecorded",
                null,
                new
                {
                    Invoice = invoice.InvoiceNumber,
                    Client = invoice.Client?.LegalEntityName,
                    Amount = payment.Amount,
                    Method = InvoiceService.PaymentMethodLabel(payment.PaymentMethod),
                    payment.PaymentDate,
                    Reference = payment.TransactionReference,
                    invoice.BalanceDue,
                    PaidInFull = becamePaid
                },
                actingUserId: CurrentUserId);

            return Ok(await _invoices.GetDetailAsync(id));
        }

        /// <summary>
        /// Reverses a payment. There is no edit or delete for a payment row - a correction is
        /// recorded as its own auditable entry.
        /// </summary>
        [HttpPost("{id:int}/payments/{paymentId:int}/reverse")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult<InvoiceDetailDto>> ReversePayment(
            int id, int paymentId, [FromBody] VoidInvoiceDto dto)
        {
            var invoice = await _payments.ReverseAsync(id, paymentId, dto?.Reason ?? string.Empty, CurrentUserId);

            await _audit.LogActionAsync(
                AuditEntityTypes.CommercialInvoicePaymentAction,
                invoice.Id,
                "PaymentReversed",
                null,
                new
                {
                    Invoice = invoice.InvoiceNumber,
                    ReversedPaymentId = paymentId,
                    Reason = dto?.Reason,
                    invoice.AmountPaid,
                    invoice.BalanceDue
                },
                actingUserId: CurrentUserId);

            return Ok(await _invoices.GetDetailAsync(id));
        }

        /// <summary>
        /// Voids an invoice. SUPERADMIN ONLY, per action.
        ///
        /// Voiding is a permanent financial statement: the number stays reserved forever and the
        /// invoice can never be collected. That is a different kind of decision from raising or
        /// sending one, so it sits a level above the admins who do the day-to-day billing.
        /// </summary>
        [HttpPost("{id:int}/void")]
        [Authorize(Roles = "SuperAdmin")]
        public async Task<ActionResult<InvoiceDetailDto>> Void(int id, [FromBody] VoidInvoiceDto dto)
        {
            var invoice = await _invoices.VoidAsync(id, dto?.Reason ?? string.Empty, CurrentUserId);

            await _audit.LogActionAsync(
                AuditEntityTypes.CommercialInvoiceVoid,
                invoice.Id,
                "InvoiceVoided",
                null,
                new
                {
                    Invoice = invoice.InvoiceNumber,
                    Client = invoice.Client?.LegalEntityName,
                    invoice.Total,
                    Reason = invoice.VoidReason,
                    invoice.VoidedAt
                },
                actingUserId: CurrentUserId);

            return Ok(await _invoices.GetDetailAsync(id));
        }

        [HttpPost("{id:int}/duplicate")]
        [RequirePermission(Permission.Create)]
        public async Task<ActionResult<InvoiceDetailDto>> Duplicate(int id)
        {
            var copy = await _invoices.DuplicateAsync(id, CurrentUserId);
            return Ok(await _invoices.GetDetailAsync(copy.Id));
        }

        /// <summary>The admin's copy of the client-facing PDF - byte-identical to it.</summary>
        [HttpGet("{id:int}/pdf")]
        [RequirePermission(Permission.View)]
        public async Task<IActionResult> DownloadPdf(int id)
        {
            var invoice = await _context.CommercialInvoices
                .Include(i => i.Items)
                .Include(i => i.Client)
                .Include(i => i.Contract)
                .FirstOrDefaultAsync(i => i.Id == id);

            if (invoice == null) return NotFound(new { message = "Invoice not found." });

            var bytes = _pdf.Render(
                await _invoices.ToPublicDtoAsync(invoice),
                _invoices.BuildPublicUrl(invoice.PublicToken));

            await _invoices.LogActivityAsync(invoice.Id, "invoice_downloaded",
                "Invoice PDF downloaded.", CurrentUserId);

            return File(bytes, "application/pdf", InvoicePdfService.BuildFileName(invoice.InvoiceNumber));
        }

        /// <summary>Runs the overdue sweep now instead of waiting for the daily pass.</summary>
        [HttpPost("run-overdue-sweep")]
        [Authorize(Roles = "SuperAdmin")]
        public async Task<IActionResult> RunOverdueSweep()
        {
            var changed = await _overdue.SweepAsync();
            return Ok(new { message = $"{changed} invoice(s) marked overdue.", changed });
        }

        // ── Lookups for the create form ───────────────────────────────────────────────────────

        /// <summary>
        /// The commercial client roster: every client with its billing details, service locations
        /// and contracts, so selecting one fills the invoice form without further round trips.
        ///
        /// ONE LIST, TWO KINDS OF CLIENT, and the caller is not asked to care which. A client
        /// created automatically from a business-flagged customer account and one typed in by hand
        /// are the same row in the same table; the only difference reaching the UI is a badge
        /// (<c>SourceUserId</c>) and what the delete confirmation has to warn about.
        ///
        /// <paramref name="includeInactive"/> DEFAULTS TO FALSE and the invoice form never passes
        /// it — a retired client must not be offered as something new to bill. It exists solely for
        /// the Clients screen's "show inactive" toggle, and a spec pins the default.
        /// </summary>
        [HttpGet("clients")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<List<InvoiceClientOptionDto>>> Clients(
            [FromQuery] bool includeInactive = false)
        {
            var clients = await _context.ContractClients
                .Where(c => includeInactive || c.IsActive)
                .Include(c => c.ServiceLocations.Where(l => l.IsActive))
                .Include(c => c.SourceUser)
                .OrderBy(c => c.LegalEntityName)
                .ToListAsync();

            var clientIds = clients.Select(c => c.Id).ToList();

            var contacts = await _context.ContractContacts
                .Where(c => c.ContractClientId != null && clientIds.Contains(c.ContractClientId.Value) && c.IsActive)
                .OrderBy(c => c.Role == ContractContactRole.ClientSigner ? 0 : 1).ThenBy(c => c.Id)
                .ToListAsync();

            // Hidden contracts are excluded: a deleted agreement must not be offered as something
            // to bill against.
            var contracts = await _context.Contracts
                .Where(c => clientIds.Contains(c.ContractClientId) && !c.IsHidden)
                .Include(c => c.ServiceLocation)
                .OrderByDescending(c => c.CreatedAt)
                .ToListAsync();

            // Counts only - what a delete would leave behind. No amounts, no payment data.
            var invoiceCounts = await _context.CommercialInvoices
                .Where(i => clientIds.Contains(i.ContractClientId))
                .GroupBy(i => i.ContractClientId)
                .Select(g => new { ClientId = g.Key, Count = g.Count() })
                .ToListAsync();

            var result = clients.Select(client =>
            {
                var contact = contacts.FirstOrDefault(c => c.ContractClientId == client.Id);
                var location = client.ServiceLocations.OrderBy(l => l.Id).FirstOrDefault();
                var account = client.SourceUser;

                return new InvoiceClientOptionDto
                {
                    Id = client.Id,
                    LegalEntityName = client.LegalEntityName,
                    BillingContactName = contact?.FullName,
                    BillingEmail = contact?.Email ?? client.NoticeEmail,
                    BillingPhone = contact?.Phone ?? client.Phone,
                    BillingAddress = InvoiceService.BuildClientAddress(client),

                    IsActive = client.IsActive,
                    SourceUserId = client.SourceUserId,
                    LinkedAccountName = account == null
                        ? null
                        : $"{account.FirstName} {account.LastName}".Trim(),
                    // Resolved through NoEmailHelper so a no-email placeholder is never displayed
                    // as though somebody could write to it.
                    LinkedAccountEmail = account == null ? null : NoEmailHelper.ResolveRealEmail(account),
                    InvoiceCount = invoiceCounts.FirstOrDefault(x => x.ClientId == client.Id)?.Count ?? 0,

                    EntityType = client.EntityType,
                    FormationState = client.FormationState,
                    PrincipalAddress = client.PrincipalAddress,
                    City = client.City,
                    State = client.State,
                    Zip = client.Zip,
                    NoticeEmail = client.NoticeEmail,
                    PrimaryLocation = location == null ? null : new InvoiceClientLocationFieldsDto
                    {
                        Id = location.Id,
                        BusinessBrand = location.BusinessBrand,
                        LocationName = location.LocationName,
                        Address = location.Address,
                        City = location.City,
                        State = location.State,
                        Zip = location.Zip
                    },

                    BillingContactFirstName = contact?.FirstName,
                    BillingContactLastName = contact?.LastName,
                    BillingContactTitle = contact?.Title,

                    Locations = client.ServiceLocations
                        .OrderBy(l => l.Id)
                        .Select(l => new InvoiceLocationOptionDto
                        {
                            Id = l.Id,
                            Label = string.IsNullOrWhiteSpace(l.LocationName)
                                ? (l.BusinessBrand ?? l.Address)
                                : l.LocationName!,
                            Address = InvoiceService.FormatLocation(l)
                        })
                        .ToList(),
                    Contracts = contracts
                        .Where(c => c.ContractClientId == client.Id)
                        .Select(BuildContractOption)
                        .ToList()
                };
            }).ToList();

            return Ok(result);
        }

        /// <summary>
        /// A contract as the invoice form sees it, with the figures it prefills from.
        ///
        /// The pricing is read from the CURRENT VERSION's frozen snapshot rather than recomputed:
        /// what the invoice should quote is what the parties actually signed, not what the same
        /// inputs would price today.
        /// </summary>
        private InvoiceContractOptionDto BuildContractOption(Contract contract)
        {
            var option = new InvoiceContractOptionDto
            {
                Id = contract.Id,
                ContractNumber = contract.ContractNumber,
                StatusLabel = contract.Status.ToString(),
                ServiceLocationId = contract.ContractServiceLocationId,
                ServiceAddress = contract.ServiceLocation == null
                    ? null
                    : InvoiceService.FormatLocation(contract.ServiceLocation)
            };

            try
            {
                var snapshot = Services.Contracts.ContractSnapshot.Parse(contract.DraftSnapshotJson);

                option.AgreedAmount = snapshot.Pricing.TotalPrice > 0m
                    ? snapshot.Pricing.TotalPrice
                    : null;
                option.TaxRate = snapshot.Pricing.SalesTaxRatePercent;
                // A contract price is quoted to the client tax-inclusive, so an invoice raised
                // from it defaults to the same treatment - re-adding tax on top would overbill.
                option.TaxType = InvoiceTaxType.Included;
                option.PaymentTerms = snapshot.Pricing.PaymentMethod;
                option.ServiceDescription =
                    $"Commercial cleaning services - {snapshot.Schedule.VisitsPerPeriod} visit(s) per "
                    + $"{snapshot.Schedule.FrequencyUnit}";
            }
            catch
            {
                // A malformed or empty snapshot must not take the whole client list down; the
                // contract is still selectable, just without prefill.
            }

            return option;
        }
    }
}
