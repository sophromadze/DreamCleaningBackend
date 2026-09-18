using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs.Commercial;
using DreamCleaningBackend.Helpers.Commercial;
using DreamCleaningBackend.Models.Commercial;
using DreamCleaningBackend.Services.Commercial;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DreamCleaningBackend.Controllers
{
    /// <summary>
    /// The self-service "My Invoices" area for business customers, alongside My Contracts.
    ///
    /// Access is by AUTHENTICATED OWNERSHIP, never by token: every read resolves through
    /// <c>ContractClient.SourceUserId == the signed-in user</c>, exactly as
    /// <see cref="MyContractsController"/> does, so one business can never see another's billing.
    /// The check is repeated on every endpoint rather than assumed from the list query - it is the
    /// only thing standing between two companies' accounts payable.
    ///
    /// ══ WHAT A CUSTOMER SEES, AND WHAT THEY DO NOT ══
    ///
    /// EVERY issued invoice, not only the paid ones: open, processing, paid, overdue and void.
    /// Being able to see what is outstanding is the point of the page, and hiding an unpaid
    /// invoice from the person who owes it would be perverse.
    ///
    /// A DRAFT IS NEVER VISIBLE. It has not been issued, its figures may still be wrong, and the
    /// admin has not decided to bill it - which is also why the public token page 404s a draft.
    /// The filter is <c>Status != Draft</c> AND <c>FirstSentAt != null</c>: both, because a status
    /// alone could in principle be moved by something other than an actual send, and "has this
    /// been sent to the client" is the question being asked.
    ///
    /// ══ WHY THERE IS NO DETAIL ENDPOINT HERE ══
    ///
    /// Opening an invoice and paying it already exist, complete, on the token-addressed public
    /// page: the totals, the tax breakdown, the ACH fee quote, Stripe checkout, the manual bank
    /// block and the PDF. This controller hands the customer their own invoices' tokens and lets
    /// them use that page. A second detail-and-payment surface would be a second place for the
    /// payment rules to drift, which is the one thing worth avoiding here more than a tidy URL.
    /// </summary>
    [Route("api/my-invoices")]
    [ApiController]
    [Authorize]
    public class MyInvoicesController : ControllerBase
    {
        private readonly ApplicationDbContext _context;

        public MyInvoicesController(ApplicationDbContext context)
        {
            _context = context;
        }

        private int CurrentUserId =>
            int.TryParse(User.FindFirst("UserId")?.Value, out var id) ? id : 0;

        /// <summary>
        /// Every ISSUED invoice belonging to the signed-in account's commercial client.
        ///
        /// Deliberately does not test <c>ContractClient.IsActive</c>: a client can be retired while
        /// invoices are still outstanding, and hiding a bill somebody owes because the account was
        /// tidied up would be the wrong kind of tidy.
        ///
        /// It does not test <c>IsArchived</c> either, for the same reason. Archiving is OUR filing
        /// decision about OUR list; a customer's copy of a bill they were sent is not ours to
        /// retract, and hiding a receipt they may need for their own accounts is the same mistake
        /// one step further on. The Draft / FirstSentAt gate below is what decides whether they
        /// ever saw it at all.
        /// </summary>
        private IQueryable<CommercialInvoice> OwnedInvoices() =>
            _context.CommercialInvoices
                .Where(i => i.Client != null
                            && i.Client.SourceUserId == CurrentUserId
                            && i.Status != InvoiceStatus.Draft
                            && i.FirstSentAt != null);

        /// <summary>
        /// The cheap check behind the header menu item, mirroring <c>has-contracts</c>.
        ///
        /// Returns a bare boolean rather than the list: it runs for every logged-in user on every
        /// page load, and it only needs to decide whether to draw one link. Both halves are
        /// required - the business flag alone shows nothing, and an invoice on an unflagged
        /// account shows nothing either.
        /// </summary>
        [HttpGet("has-invoices")]
        public async Task<ActionResult<object>> HasInvoices()
        {
            var userId = CurrentUserId;
            if (userId == 0) return Ok(new { hasInvoices = false });

            var isBusiness = await _context.Users
                .Where(u => u.Id == userId)
                .Select(u => u.IsBusiness)
                .FirstOrDefaultAsync();

            if (!isBusiness) return Ok(new { hasInvoices = false });

            return Ok(new { hasInvoices = await OwnedInvoices().AnyAsync() });
        }

        /// <summary>
        /// The customer's invoice list, newest first.
        ///
        /// Projected into <see cref="MyInvoiceListItemDto"/> - a customer-facing type whose default
        /// is to carry nothing, so a field added to the admin view later cannot leak here by
        /// accident. No internal note, no activity trail, no view counts, no row id.
        /// </summary>
        [HttpGet]
        public async Task<ActionResult<List<MyInvoiceListItemDto>>> GetMyInvoices()
        {
            if (CurrentUserId == 0) return Ok(new List<MyInvoiceListItemDto>());

            var invoices = await OwnedInvoices()
                .Include(i => i.Contract)
                .OrderByDescending(i => i.InvoiceDate).ThenByDescending(i => i.Id)
                .Take(200)
                .ToListAsync();

            if (invoices.Count == 0) return Ok(new List<MyInvoiceListItemDto>());

            // Which of these have money in flight. ACH takes days to settle, and during that
            // window the invoice legitimately still reads unpaid - telling a customer who has
            // already authorized a debit that they owe money is the most annoying thing this page
            // could do, so it says "Processing" instead.
            var ids = invoices.Select(i => i.Id).ToList();
            var cutoff = DateTime.UtcNow.AddHours(-24);

            var inFlight = await _context.CommercialInvoicePaymentAttempts
                .Where(a => ids.Contains(a.CommercialInvoiceId)
                            && (a.Status == InvoicePaymentAttemptStatus.Processing
                                || (a.Status == InvoicePaymentAttemptStatus.CheckoutOpen
                                    && a.CreatedAt >= cutoff)))
                .Select(a => a.CommercialInvoiceId)
                .Distinct()
                .ToListAsync();

            var processing = inFlight.ToHashSet();

            return Ok(invoices.Select(i =>
            {
                var dates = ServiceDateFormatter.Describe(
                    InvoiceService.ParseServiceDates(i.ServiceDatesJson),
                    i.ServiceStartDate,
                    i.ServiceEndDate);

                return new MyInvoiceListItemDto
                {
                    InvoiceNumber = i.InvoiceNumber,
                    PublicToken = i.PublicToken,
                    ContractNumber = i.Contract?.ContractNumber,
                    ServiceAddress = i.ServiceAddress,
                    InvoiceDate = i.InvoiceDate,
                    DueDate = i.DueDate,
                    ServiceStartDate = i.ServiceStartDate,
                    ServiceEndDate = i.ServiceEndDate,
                    ServiceDateLabel = dates.HasValue ? dates.Label : null,
                    ServiceDateText = dates.HasValue ? dates.Text : null,
                    Total = i.Total,
                    AmountPaid = i.AmountPaid,
                    BalanceDue = i.BalanceDue,
                    Currency = i.Currency,
                    Status = i.Status,
                    StatusLabel = InvoiceStatusPolicy.Label(i.Status),
                    PaymentInProgress = processing.Contains(i.Id),
                    PaidAt = i.PaidAt
                };
            }).ToList());
        }
    }
}
