using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DreamCleaningBackend.Controllers
{
    /// <summary>
    /// The signed-in customer's own REGULAR invoices (DCR-…) — the profile's Invoices tab
    /// (2026-09). Scoped by the caller's user id from the token, never by a parameter, so nobody
    /// can list another customer's bills. Commercial invoices keep their own
    /// <c>api/my-invoices</c> (business accounts).
    /// </summary>
    [Route("api/customer-invoices/mine")]
    [ApiController]
    [Authorize]
    public class MyCustomerInvoicesController : ControllerBase
    {
        private readonly ICustomerInvoiceService _invoices;

        public MyCustomerInvoicesController(ICustomerInvoiceService invoices)
        {
            _invoices = invoices;
        }

        private int CurrentUserId =>
            int.TryParse(User.FindFirst("UserId")?.Value, out var id) ? id : 0;

        [HttpGet]
        public async Task<ActionResult<List<MyCustomerInvoiceDto>>> List()
            => Ok(await _invoices.ListForCustomerAsync(CurrentUserId));
    }
}
