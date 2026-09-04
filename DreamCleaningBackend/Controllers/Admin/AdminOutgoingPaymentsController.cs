using Microsoft.EntityFrameworkCore;
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
        private readonly IAdminSalaryPayoutService _salaries;
        private readonly ILogger<AdminOutgoingPaymentsController> _logger;

        public AdminOutgoingPaymentsController(
            IOutgoingPaymentService service,
            IAdminSalaryPayoutService salaries,
            ILogger<AdminOutgoingPaymentsController> logger)
        {
            _service = service;
            _salaries = salaries;
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
                // A rule the service refused on — reported verbatim, because these are written to
                // be actionable by the admin who typed the value. (An already-PAID line is no
                // longer one of them: since 2026-09 raising its hours is allowed and shows the
                // difference as still to pay instead.)
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

        /// <summary>
        /// Marks ONE unassigned staffing slot paid — the person who worked the job but is not in
        /// the system. Addressed by slot index, since there is no assignment id to use.
        /// </summary>
        [HttpPost("order/{orderId}/unassigned/{slotIndex}/pay")]
        public async Task<ActionResult<OutgoingPaymentOrderDto>> MarkUnassignedSlotPaid(
            int orderId, int slotIndex, [FromBody] MarkCleanerPaidDto? dto)
        {
            var order = await _service.MarkUnassignedSlotPaidAsync(
                orderId, slotIndex, dto ?? new MarkCleanerPaidDto(), GetCurrentUserId());

            if (order == null)
                return NotFound(new { message = "That order has no unassigned payout in that position." });

            _logger.LogInformation(
                "Unassigned payout recorded: order {OrderId}, slot {SlotIndex}, by user {UserId}",
                orderId, slotIndex, GetCurrentUserId());

            return Ok(order);
        }

        /// <summary>Undoes a payout recorded against an unassigned staffing slot.</summary>
        [HttpPost("order/{orderId}/unassigned/{slotIndex}/unpay")]
        public async Task<ActionResult<OutgoingPaymentOrderDto>> UndoUnassignedSlotPayment(int orderId, int slotIndex)
        {
            var order = await _service.UndoUnassignedSlotPaymentAsync(orderId, slotIndex);

            if (order == null)
                return NotFound(new { message = "There is no recorded payout in that position." });

            _logger.LogInformation(
                "Unassigned payout REVERSED: order {OrderId}, slot {SlotIndex}, by user {UserId}",
                orderId, slotIndex, GetCurrentUserId());

            return Ok(order);
        }

        // ── Staff salaries ─────────────────────────────────────────────────────────
        //
        // A salary is per PERSON per MONTH and paid in two instalments, so it takes its own
        // endpoints rather than being squeezed into the per-order shape above. What is OWED is
        // derived from the Salaries expenses (never stored); only the payments are recorded.

        /// <summary>Every staff salary for one month, split into its two instalments.</summary>
        [HttpGet("salaries")]
        public async Task<ActionResult<AdminSalaryPayoutListDto>> GetSalaries(
            [FromQuery] int? year = null, [FromQuery] int? month = null)
        {
            var now = DateTime.UtcNow;
            try
            {
                return Ok(await _salaries.GetAsync(year ?? now.Year, month ?? now.Month));
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        /// <summary>
        /// Records one instalment as paid, freezing what was handed over. The payee is addressed by
        /// key rather than by user id because a salary can be owed to somebody with no account.
        /// </summary>
        [HttpPost("salaries/{year:int}/{month:int}/{half:int}/pay")]
        public async Task<ActionResult<AdminSalaryPayoutListDto>> MarkSalaryPaid(
            int year, int month, int half, [FromQuery] string payeeKey, [FromBody] MarkSalaryPaidDto? dto)
        {
            if (string.IsNullOrWhiteSpace(payeeKey))
                return BadRequest(new { message = "Which salary is being paid was not supplied." });

            try
            {
                var result = await _salaries.MarkPaidAsync(
                    year, month, payeeKey, half, dto ?? new MarkSalaryPaidDto(), GetCurrentUserId());

                _logger.LogInformation(
                    "Salary payment recorded: {PayeeKey} {Year}-{Month} instalment {Half}, by user {UserId}",
                    payeeKey, year, month, half, GetCurrentUserId());

                return Ok(result);
            }
            catch (DbUpdateException)
            {
                // The UNIQUE index caught a double-submit. Reported as the rule it is, not a 500.
                return BadRequest(new { message = "That payment has already been recorded." });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        /// <summary>
        /// Sets where an employee's salary is sent — an IBAN, a card or an ID number. Free text:
        /// the format differs by country and by person, and validating one we cannot know would
        /// block a real payment. Blank clears it.
        /// </summary>
        [HttpPut("salaries/{year:int}/{month:int}/payee-details")]
        public async Task<ActionResult<AdminSalaryPayoutListDto>> UpdateSalaryPayeeDetails(
            int year, int month, [FromQuery] string payeeKey, [FromBody] UpdateSalaryPayeeDetailsDto dto)
        {
            if (string.IsNullOrWhiteSpace(payeeKey))
                return BadRequest(new { message = "Whose payment details these are was not supplied." });

            try
            {
                var result = await _salaries.UpdatePayeeDetailsAsync(
                    year, month, payeeKey, dto ?? new UpdateSalaryPayeeDetailsDto(), GetCurrentUserId());

                // The value itself is a bank destination and is deliberately NOT logged here — the
                // audit row records the change, and application logs are a wider audience.
                _logger.LogInformation(
                    "Salary payment details updated for {PayeeKey} by user {UserId}",
                    payeeKey, GetCurrentUserId());

                return Ok(result);
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        /// <summary>Undoes a recorded salary payment — for the case where it was ticked by mistake.</summary>
        [HttpPost("salaries/{year:int}/{month:int}/{half:int}/unpay")]
        public async Task<ActionResult<AdminSalaryPayoutListDto>> UndoSalaryPayment(
            int year, int month, int half, [FromQuery] string payeeKey)
        {
            if (string.IsNullOrWhiteSpace(payeeKey))
                return BadRequest(new { message = "Which salary is being undone was not supplied." });

            try
            {
                var result = await _salaries.UndoPaymentAsync(year, month, payeeKey, half);

                _logger.LogInformation(
                    "Salary payment REVERSED: {PayeeKey} {Year}-{Month} instalment {Half}, by user {UserId}",
                    payeeKey, year, month, half, GetCurrentUserId());

                return Ok(result);
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
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
