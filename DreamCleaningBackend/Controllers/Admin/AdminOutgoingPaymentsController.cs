using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DreamCleaningBackend.Controllers
{
    /// <summary>
    /// Outgoing Payments — what the company owes its cleaners for finished jobs, and the record of
    /// paying it. Same <c>api/admin</c> prefix and topic-controller pattern as the rest of
    /// <c>Controllers/Admin</c>.
    ///
    /// <b>SuperAdmin only, at the attribute level.</b> Unlike the Company tabs, this is NOT behind
    /// a grantable page-view key: every write here changes what the company reports as its labour
    /// cost (the per-cleaner figures re-sum onto <c>Order.CleanerTotalSalary</c>, which Statistics
    /// and Finances read), and it records money leaving the business. There is deliberately no way
    /// to grant a regular Admin read-only access — adding one means deciding what a reader may see
    /// of other people's wages, which is a separate decision from the ones the page-view grants
    /// were built for.
    /// </summary>
    [Route("api/admin/outgoing-payments")]
    [ApiController]
    [Authorize(Roles = "SuperAdmin")]
    public class AdminOutgoingPaymentsController : AdminControllerBase
    {
        private readonly IOutgoingPaymentService _service;
        private readonly ILogger<AdminOutgoingPaymentsController> _logger;

        public AdminOutgoingPaymentsController(
            IOutgoingPaymentService service,
            ILogger<AdminOutgoingPaymentsController> logger)
        {
            _service = service;
            _logger = logger;
        }

        /// <summary>
        /// Finished orders with a payout line per assigned cleaner. Dates are NY service dates
        /// (the same basis Statistics and Finances bucket by), not UTC timestamps.
        /// </summary>
        [HttpGet]
        public async Task<ActionResult<OutgoingPaymentListDto>> GetOutgoingPayments(
            [FromQuery] DateTime? from = null,
            [FromQuery] DateTime? to = null,
            [FromQuery] string paidStatus = "all",
            [FromQuery] bool warningsOnly = false,
            [FromQuery] string? search = null,
            [FromQuery] int? cleanerId = null,
            [FromQuery] int page = 1,
            // 20 a page, matching the admin Orders tab.
            [FromQuery] int pageSize = 20)
        {
            // An unrecognised value is treated as "all" rather than rejected: this is a read-only
            // filter, and showing everything is the safe failure.
            var status = paidStatus?.ToLowerInvariant() switch
            {
                "unpaid" => OutgoingPaymentPaidStatus.Unpaid,
                "paid" => OutgoingPaymentPaidStatus.Paid,
                _ => OutgoingPaymentPaidStatus.All
            };

            var result = await _service.GetAsync(new OutgoingPaymentQuery
            {
                From = from,
                To = to,
                PaidStatus = status,
                WarningsOnly = warningsOnly,
                Search = search,
                CleanerId = cleanerId,
                Page = page,
                PageSize = pageSize
            });

            return Ok(result);
        }

        /// <summary>One order's payout sheet — used to refresh a single card after a change.</summary>
        [HttpGet("order/{orderId}")]
        public async Task<ActionResult<OutgoingPaymentOrderDto>> GetOrder(int orderId)
        {
            var order = await _service.GetOrderAsync(orderId);
            return order == null
                ? NotFound(new { message = "That order was not found, or it is not finished yet." })
                : Ok(order);
        }

        /// <summary>
        /// Changes one cleaner's hourly rate and/or paid hours on one order. The order's stored
        /// total salary is re-summed from every line and saved, so reporting follows immediately.
        /// </summary>
        [HttpPut("order/{orderId}/cleaner/{orderCleanerId}")]
        public async Task<ActionResult<OutgoingPaymentOrderDto>> UpdateCleanerPayroll(
            int orderId, int orderCleanerId, [FromBody] UpdateCleanerPayrollDto dto)
        {
            if (dto == null)
                return BadRequest(new { message = "No changes were supplied." });

            if (!dto.UpdateHourlyRate && !dto.UpdateBillableMinutes)
                return BadRequest(new { message = "No changes were supplied." });

            if (dto.UpdateHourlyRate && dto.HourlyRate is < 0)
                return BadRequest(new { message = "An hourly rate cannot be negative." });

            if (dto.UpdateBillableMinutes && dto.BillableMinutes is < 0)
                return BadRequest(new { message = "Hours cannot be negative." });

            try
            {
                var order = await _service.UpdateCleanerPayrollAsync(orderId, orderCleanerId, dto);
                return order == null
                    ? NotFound(new { message = "That cleaner is not assigned to that finished order." })
                    : Ok(order);
            }
            catch (InvalidOperationException ex)
            {
                // Already-paid line: a real, actionable rule, so it is reported as one.
                return BadRequest(new { message = ex.Message });
            }
        }

        /// <summary>
        /// Changes the ORDER's hourly rate — the default every assigned cleaner without a
        /// per-cleaner override is paid at. It is written through to Order.CleanerHourlyRate, so
        /// the order itself carries the new rate and Statistics/Finances follow immediately.
        /// </summary>
        [HttpPut("order/{orderId}/hourly-rate")]
        public async Task<ActionResult<OutgoingPaymentOrderDto>> UpdateOrderHourlyRate(
            int orderId, [FromBody] UpdateOrderHourlyRateDto dto)
        {
            if (dto == null || dto.HourlyRate < 0)
                return BadRequest(new { message = "An hourly rate cannot be negative." });

            var order = await _service.UpdateOrderHourlyRateAsync(orderId, dto.HourlyRate);

            if (order == null)
                return NotFound(new { message = "That order was not found, or it is not finished yet." });

            _logger.LogInformation(
                "Order {OrderId} cleaner hourly rate set to {Rate} by user {UserId}",
                orderId, dto.HourlyRate, GetCurrentUserId());

            return Ok(order);
        }

        /// <summary>Marks ONE cleaner paid for one order, freezing what they were handed.</summary>
        [HttpPost("order/{orderId}/cleaner/{orderCleanerId}/pay")]
        public async Task<ActionResult<OutgoingPaymentOrderDto>> MarkCleanerPaid(
            int orderId, int orderCleanerId, [FromBody] MarkCleanerPaidDto? dto)
        {
            var order = await _service.MarkCleanerPaidAsync(
                orderId, orderCleanerId, dto ?? new MarkCleanerPaidDto(), GetCurrentUserId());

            if (order == null)
                return NotFound(new { message = "That cleaner is not assigned to that finished order." });

            _logger.LogInformation(
                "Cleaner payout recorded: order {OrderId}, assignment {AssignmentId}, by user {UserId}",
                orderId, orderCleanerId, GetCurrentUserId());

            return Ok(order);
        }

        /// <summary>Marks every not-yet-paid cleaner on one order paid, each via their own saved method.</summary>
        [HttpPost("order/{orderId}/pay")]
        public async Task<ActionResult<OutgoingPaymentOrderDto>> MarkOrderPaid(
            int orderId, [FromBody] MarkOrderPaidDto? dto)
        {
            var order = await _service.MarkOrderPaidAsync(orderId, dto ?? new MarkOrderPaidDto(), GetCurrentUserId());

            if (order == null)
                return NotFound(new { message = "That order was not found, or it is not finished yet." });

            _logger.LogInformation(
                "All cleaner payouts recorded for order {OrderId} by user {UserId}", orderId, GetCurrentUserId());

            return Ok(order);
        }

        /// <summary>Undoes a payout marking — for the case where it was ticked by mistake.</summary>
        [HttpPost("order/{orderId}/cleaner/{orderCleanerId}/unpay")]
        public async Task<ActionResult<OutgoingPaymentOrderDto>> UndoCleanerPayment(int orderId, int orderCleanerId)
        {
            var order = await _service.UndoCleanerPaymentAsync(orderId, orderCleanerId);

            if (order == null)
                return NotFound(new { message = "That cleaner is not assigned to that finished order." });

            _logger.LogInformation(
                "Cleaner payout REVERSED: order {OrderId}, assignment {AssignmentId}, by user {UserId}",
                orderId, orderCleanerId, GetCurrentUserId());

            return Ok(order);
        }
    }
}
