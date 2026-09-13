using DreamCleaningBackend.Attributes;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DreamCleaningBackend.Controllers.Admin
{
    /// <summary>
    /// Recurring order series — "repeat this cleaning every N days/weeks/months".
    ///
    /// ADMIN AND SUPERADMIN, with the ordinary order-management permissions on top
    /// (<c>Permission.Create</c> to start a series, <c>Permission.Update</c> to change or generate
    /// one). Setting up a recurrence is exactly the work the people who take the bookings do, and
    /// it creates ordinary orders they could have created by hand one at a time.
    ///
    /// <b>Moderators are excluded</b> by the controller-level role attribute rather than by
    /// permission alone: they hold View elsewhere, and a series quietly writes real bookings for
    /// weeks ahead, which is not a read.
    ///
    /// Route lives under <c>api/admin</c> like every other admin topic controller, so nothing
    /// about the existing URL surface changes shape.
    /// </summary>
    [Route("api/admin/recurring-series")]
    [ApiController]
    [Authorize(Roles = "Admin,SuperAdmin")]
    public class AdminRecurringOrdersController : ControllerBase
    {
        private readonly IRecurringOrderSeriesService _series;
        private readonly RecurringOrderGenerationService _generator;

        public AdminRecurringOrdersController(
            IRecurringOrderSeriesService series,
            RecurringOrderGenerationService generator)
        {
            _series = series;
            _generator = generator;
        }

        private int CurrentUserId =>
            int.TryParse(User.FindFirst("UserId")?.Value, out var id) ? id : 0;

        [HttpGet]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<List<RecurringSeriesDto>>> List(
            [FromQuery] bool includeInactive = false)
            => Ok(await _series.ListAsync(includeInactive));

        [HttpGet("{id:int}")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<RecurringSeriesDto>> Get(int id)
        {
            var dto = await _series.GetAsync(id);
            return dto == null ? NotFound(new { message = "Recurring series not found." }) : Ok(dto);
        }

        /// <summary>The series an order belongs to, if any. Drives the order panel's badge.</summary>
        [HttpGet("for-order/{orderId:int}")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<RecurringSeriesDto?>> ForOrder(int orderId)
            => Ok(await _series.GetForOrderAsync(orderId));

        /// <summary>
        /// Makes an existing order recurring, and immediately fills the 30-day horizon.
        ///
        /// A 400 here is a rule the admin can act on — most often the daily interval, which is out
        /// of scope until the staffing and billing behaviour for it exists.
        /// </summary>
        [HttpPost("from-order/{orderId:int}")]
        [RequirePermission(Permission.Create)]
        public async Task<ActionResult<RecurringSeriesDto>> CreateFromOrder(
            int orderId, [FromBody] SaveRecurringSeriesDto dto)
        {
            try
            {
                return Ok(await _series.CreateAsync(orderId, dto ?? new SaveRecurringSeriesDto(), CurrentUserId));
            }
            catch (RecurringSeriesException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPut("{id:int}")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult<RecurringSeriesDto>> Update(
            int id, [FromBody] SaveRecurringSeriesDto dto)
        {
            try
            {
                return Ok(await _series.UpdateAsync(id, dto ?? new SaveRecurringSeriesDto(), CurrentUserId));
            }
            catch (RecurringSeriesException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPost("{id:int}/state/{state}")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult<RecurringSeriesDto>> SetState(int id, string state)
        {
            try { return Ok(await _series.SetStateAsync(id, state, CurrentUserId)); }
            catch (RecurringSeriesException ex) { return BadRequest(new { message = ex.Message }); }
        }

        [HttpPost("{id:int}/occurrences/{orderId:int}/skip")]
        [RequirePermission(Permission.Update)]
        public async Task<IActionResult> Skip(int id, int orderId)
        {
            try { await _series.SkipAsync(id, orderId, CurrentUserId); return Ok(); }
            catch (RecurringSeriesException ex) { return BadRequest(new { message = ex.Message }); }
        }

        [HttpPost("from-order/{orderId:int}/preview")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<RecurringPricePreviewDto>> Preview(int orderId, [FromBody] SaveRecurringSeriesDto dto)
        {
            try { return Ok(await _series.PreviewAsync(orderId, dto)); }
            catch (RecurringSeriesException ex) { return BadRequest(new { message = ex.Message }); }
        }

        /// <summary>
        /// Fills this series' horizon now instead of waiting for the daily pass.
        ///
        /// Safe to press twice: generation is idempotent, and the response says which dates were
        /// skipped because they already existed — which is how an admin can SEE that rather than
        /// having to trust it.
        /// </summary>
        [HttpPost("{id:int}/generate")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult<RecurringGenerationResultDto>> Generate(int id)
        {
            try
            {
                return Ok(await _series.GenerateAsync(id, CurrentUserId));
            }
            catch (RecurringSeriesException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        /// <summary>
        /// Runs the whole daily pass on demand — every active series, plus any automatic payment
        /// requests that have become due. SuperAdmin-only: it is the scheduled job, and running
        /// the scheduled job by hand is an operations action rather than order management.
        /// </summary>
        [HttpPost("run-sweep")]
        [Authorize(Roles = "SuperAdmin")]
        public async Task<IActionResult> RunSweep()
        {
            var created = await _generator.SweepAsync();
            return Ok(new { created, message = $"{created} order(s) generated." });
        }
    }
}
