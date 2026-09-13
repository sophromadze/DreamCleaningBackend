using System.Security.Claims;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DreamCleaningBackend.Controllers
{
    /// <summary>
    /// The customer's own recurring cleanings: what is coming up, and paying for it.
    ///
    /// EVERY ENDPOINT IS SCOPED TO THE SIGNED-IN USER, from the token, and the user id is never
    /// a parameter. That is the whole authorization model here, and it is why the combined-payment
    /// request carries neither an amount nor a list of order ids — there is no request shape in
    /// which somebody can reach another customer's cleanings or name their own price.
    ///
    /// Deliberately a SEPARATE controller from OrderController rather than more endpoints on it:
    /// this is one feature with one owner, and the existing order endpoints (with their guest
    /// tokens, payment links and admin paths) are already carrying enough.
    /// </summary>
    [Route("api/my-recurring-orders")]
    [ApiController]
    [Authorize]
    public class MyRecurringOrdersController : ControllerBase
    {
        private readonly IRecurringCustomerPaymentService _payments;
        private readonly ILogger<MyRecurringOrdersController> _logger;

        public MyRecurringOrdersController(
            IRecurringCustomerPaymentService payments,
            ILogger<MyRecurringOrdersController> logger)
        {
            _payments = payments;
            _logger = logger;
        }

        private int UserId =>
            int.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : 0;

        /// <summary>
        /// Every generated recurring cleaning still to come, NEAREST FIRST, with what may be paid
        /// now and what "Pay all upcoming" would cost.
        ///
        /// The amounts here are the stored order totals, so the figure the customer is shown is
        /// the figure the endpoint will charge — it is not re-derived at payment time from
        /// anything the browser sends.
        /// </summary>
        [HttpGet]
        public async Task<ActionResult<UpcomingRecurringOrdersDto>> Upcoming()
        {
            if (UserId == 0) return Unauthorized();
            return Ok(await _payments.GetUpcomingAsync(UserId));
        }

        /// <summary>
        /// Starts one Stripe payment covering every unpaid upcoming recurring cleaning.
        ///
        /// Returns a client secret the page confirms in the ordinary way. Nothing is marked paid
        /// here or by the browser coming back — settlement happens on
        /// <c>payment_intent.succeeded</c>, in a transaction that re-reads every order.
        /// </summary>
        [HttpPost("pay-all")]
        public async Task<ActionResult<CombinedPaymentDto>> PayAll([FromBody] StartCombinedPaymentDto? dto)
        {
            if (UserId == 0) return Unauthorized();

            try
            {
                return Ok(await _payments.StartCombinedPaymentAsync(
                    UserId, dto ?? new StartCombinedPaymentDto()));
            }
            catch (CombinedPaymentException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Combined payment failed for user {UserId}.", UserId);
                return BadRequest(new { message = "The payment could not be started. Please try again." });
            }
        }
    }
}
