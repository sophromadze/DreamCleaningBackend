using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using DreamCleaningBackend.Attributes;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Services;
using DreamCleaningBackend.Services.Interfaces;

namespace DreamCleaningBackend.Controllers
{
    // SuperAdmin has full access. Regular Admins granted the "expenses" page get read-only (GET)
    // access via [RequirePageView]; all mutations stay [Authorize(Roles = "SuperAdmin")].
    [Route("api/expenses")]
    [ApiController]
    [Authorize]
    public class ExpensesController : ControllerBase
    {
        private readonly IExpenseService _expenseService;

        public ExpensesController(IExpenseService expenseService)
        {
            _expenseService = expenseService;
        }

        private int GetUserId()
        {
            var claim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            return int.Parse(claim!);
        }

        [HttpGet]
        [RequirePageView(AdminViewablePages.Expenses)]
        public async Task<ActionResult<List<ExpenseDto>>> GetAll()
        {
            return Ok(await _expenseService.GetAllAsync());
        }

        [HttpGet("{id}")]
        [RequirePageView(AdminViewablePages.Expenses)]
        public async Task<ActionResult<ExpenseDto>> GetById(int id)
        {
            var row = await _expenseService.GetByIdAsync(id);
            if (row == null) return NotFound();
            return Ok(row);
        }

        [HttpPost]
        [Authorize(Roles = "SuperAdmin")]
        public async Task<ActionResult<ExpenseDto>> Create([FromBody] CreateExpenseDto dto)
        {
            try
            {
                var created = await _expenseService.CreateAsync(dto, GetUserId());
                return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPut("{id}")]
        [Authorize(Roles = "SuperAdmin")]
        public async Task<ActionResult<ExpenseDto>> Update(int id, [FromBody] UpdateExpenseDto dto)
        {
            try
            {
                return Ok(await _expenseService.UpdateAsync(id, dto));
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpDelete("{id}")]
        [Authorize(Roles = "SuperAdmin")]
        public async Task<ActionResult> Delete(int id)
        {
            var ok = await _expenseService.DeleteAsync(id);
            return ok ? NoContent() : NotFound();
        }

        // Breakdown for statistics — expands recurring expenses into per-occurrence rows
        // within [from, to]. Defaults to the last 12 months when omitted.
        [HttpGet("breakdown")]
        [RequirePageView(AdminViewablePages.Expenses)]
        public async Task<ActionResult<ExpenseBreakdownDto>> GetBreakdown(
            [FromQuery] DateTime? from,
            [FromQuery] DateTime? to)
        {
            var (f, t) = ResolveRange(from, to);
            return Ok(await _expenseService.GetBreakdownAsync(f, t));
        }

        // Grouped Category → Name → entries view for one calendar month (defaults to current).
        [HttpGet("grouped")]
        [RequirePageView(AdminViewablePages.Expenses)]
        public async Task<ActionResult<GroupedExpensesDto>> GetGrouped(
            [FromQuery] int? year,
            [FromQuery] int? month)
        {
            var now = DateTime.UtcNow;
            try
            {
                return Ok(await _expenseService.GetGroupedAsync(year ?? now.Year, month ?? now.Month));
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        // ── Category management ────────────────────────────────────────────────

        [HttpGet("categories")]
        [RequirePageView(AdminViewablePages.Expenses)]
        public async Task<ActionResult<List<ExpenseCategoryDto>>> GetCategories()
        {
            return Ok(await _expenseService.GetCategoriesAsync());
        }

        [HttpPost("categories")]
        [Authorize(Roles = "SuperAdmin")]
        public async Task<ActionResult<ExpenseCategoryDto>> CreateCategory([FromBody] SaveExpenseCategoryDto dto)
        {
            try
            {
                return Ok(await _expenseService.CreateCategoryAsync(dto));
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPut("categories/{id}")]
        [Authorize(Roles = "SuperAdmin")]
        public async Task<ActionResult<ExpenseCategoryDto>> UpdateCategory(int id, [FromBody] SaveExpenseCategoryDto dto)
        {
            try
            {
                return Ok(await _expenseService.UpdateCategoryAsync(id, dto));
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpDelete("categories/{id}")]
        [Authorize(Roles = "SuperAdmin")]
        public async Task<ActionResult> DeleteCategory(int id)
        {
            try
            {
                var ok = await _expenseService.DeleteCategoryAsync(id);
                return ok ? NoContent() : NotFound();
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        private static (DateTime From, DateTime To) ResolveRange(DateTime? from, DateTime? to)
        {
            // Match statistics convention: `to` is treated as inclusive (so we add one day).
            var f = from?.Date ?? DateTime.UtcNow.Date.AddYears(-1);
            var t = (to?.Date ?? DateTime.UtcNow.Date).AddDays(1);
            return (DateTime.SpecifyKind(f, DateTimeKind.Utc), DateTime.SpecifyKind(t, DateTimeKind.Utc));
        }
    }
}
