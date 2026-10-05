using DreamCleaningBackend.Attributes;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DreamCleaningBackend.Controllers
{
    /// <summary>
    /// Admin → Invoices: REGULAR invoices for ordinary customers (2026-09). Commercial invoices
    /// stay in Commercial → Invoices; this is the simpler bill-for-one-order flow. Every rule lives
    /// in <see cref="CustomerInvoiceService"/>.
    ///
    /// Recording a bank transfer is deliberately NOT here: it goes through the existing
    /// <c>orders/{orderId}/partial-payments/{requestId}/record-manual-payment</c> endpoint with the
    /// invoice's own request id, which is the one place that completes an order when its last
    /// slice is recorded. A second copy of that completion would be the one that drifts.
    /// </summary>
    [Route("api/admin/customer-invoices")]
    [ApiController]
    [Authorize(Roles = "Admin,SuperAdmin")]
    public class AdminCustomerInvoicesController : AdminControllerBase
    {
        private readonly ICustomerInvoiceService _invoices;

        public AdminCustomerInvoicesController(ICustomerInvoiceService invoices)
        {
            _invoices = invoices;
        }

        [HttpGet]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<List<CustomerInvoiceDto>>> List(
            [FromQuery] string? search = null, [FromQuery] string? status = null,
            [FromQuery] int? userId = null, [FromQuery] int? orderId = null)
            => Ok(await _invoices.ListAsync(search, status, userId, orderId));

        [HttpGet("{id:int}")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<CustomerInvoiceDto>> Get(int id)
        {
            var dto = await _invoices.GetAsync(id);
            return dto == null ? NotFound(new { message = "Invoice not found." }) : Ok(dto);
        }

        /// <summary>The customer's unpaid orders, each saying whether it can be invoiced and why not.</summary>
        [HttpGet("orders-for-user/{userId:int}")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<List<CustomerInvoiceOrderOptionDto>>> OrdersForUser(int userId)
            => Ok(await _invoices.GetInvoiceableOrdersAsync(userId));

        /// <summary>
        /// Issues one FULL invoice, or one SPLIT invoice per amount, and sends them straight away
        /// on the channels asked for. A failed send never undoes the invoice — it is reported, and
        /// the admin can resend or copy the link.
        /// </summary>
        [HttpPost]
        [RequirePermission(Permission.Create)]
        public async Task<ActionResult> Create([FromBody] CreateCustomerInvoiceDto dto)
        {
            if (dto == null || dto.OrderId <= 0)
                return BadRequest(new { message = "Choose the order to invoice." });

            List<CustomerInvoice> created;
            try
            {
                created = await _invoices.CreateAsync(dto, GetCurrentUserId());
            }
            catch (CustomerInvoiceException ex)
            {
                return BadRequest(new { message = ex.Message });
            }

            var messages = new List<string>();
            if (dto.SendEmail || dto.SendSms)
            {
                foreach (var invoice in created)
                {
                    try
                    {
                        var send = await _invoices.SendAsync(invoice.Id, dto.SendEmail, dto.SendSms, GetCurrentUserId());
                        messages.Add(send.Message);
                    }
                    catch (CustomerInvoiceException ex)
                    {
                        messages.Add(ex.Message);
                    }
                }
            }
            else
            {
                messages.Add(created.Count == 1
                    ? $"Invoice {created[0].InvoiceNumber} created. Nothing was sent — copy the link to the customer yourself."
                    : $"{created.Count} invoices created. Nothing was sent — copy the links to the customer yourself.");
            }

            var dtos = new List<CustomerInvoiceDto>();
            foreach (var invoice in created)
            {
                var d = await _invoices.GetAsync(invoice.Id);
                if (d != null) dtos.Add(d);
            }

            return Ok(new { message = string.Join(" ", messages), invoices = dtos });
        }

        [HttpPost("{id:int}/send")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult> Send(int id, [FromBody] SendCustomerInvoiceDto dto)
        {
            try
            {
                var result = await _invoices.SendAsync(id, dto?.SendEmail ?? true, dto?.SendSms ?? true, GetCurrentUserId());
                if (!result.EmailSent && !result.SmsSent)
                    return BadRequest(new { message = result.Message });
                return Ok(new { message = result.Message, invoice = await _invoices.GetAsync(id) });
            }
            catch (CustomerInvoiceException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPost("{id:int}/void")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult> Void(int id, [FromBody] VoidCustomerInvoiceDto? dto)
        {
            try
            {
                await _invoices.VoidAsync(id, dto?.Reason, GetCurrentUserId());
                return Ok(new { message = "Invoice voided.", invoice = await _invoices.GetAsync(id) });
            }
            catch (CustomerInvoiceException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }
    }
}
