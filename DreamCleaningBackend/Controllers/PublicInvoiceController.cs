using DreamCleaningBackend.DTOs.Commercial;
using DreamCleaningBackend.Services.Commercial;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DreamCleaningBackend.Controllers
{
    /// <summary>
    /// The client-facing invoice page and its PDF.
    ///
    /// DELIBERATELY ANONYMOUS, and it must stay that way. A commercial counterparty has no account
    /// here, so the opaque 48-hex token in the URL is the whole authorization - exactly the shape
    /// the contract review pages and the tokenized customer payment links already use. Adding an
    /// auth guard would lock out the only people these endpoints exist for.
    ///
    /// What the token buys is deliberately narrow:
    ///
    ///  - READ ONE INVOICE, and only through <see cref="PublicInvoiceDto"/>, which is a separate
    ///    type from the admin DTO rather than a filtered copy of it. The internal note, the
    ///    activity log, the row id, the view counts and the token itself are not fields on it, so
    ///    a future admin-side addition cannot leak here by default.
    ///  - DOWNLOAD THAT INVOICE'S PDF.
    ///
    /// It buys NOTHING WRITEABLE. There is no endpoint here that changes an amount, a status or a
    /// payment - the only state the public side moves is the view counter, as a side effect of
    /// reading, and that can never take an invoice out of Paid or Void.
    ///
    /// The row id is never accepted as a route parameter: ids are sequential and enumerable, so
    /// /invoice/41 would let anyone walk the whole table. Only the token resolves an invoice.
    /// </summary>
    [Route("api/public/invoices")]
    [ApiController]
    [AllowAnonymous]
    public class PublicInvoiceController : ControllerBase
    {
        private readonly InvoiceService _invoices;
        private readonly InvoicePdfService _pdf;
        private readonly InvoiceCheckoutService _checkout;

        public PublicInvoiceController(
            InvoiceService invoices, InvoicePdfService pdf, InvoiceCheckoutService checkout)
        {
            _invoices = invoices;
            _pdf = pdf;
            _checkout = checkout;
        }

        /// <summary>Behind Cloudflare and Apache the socket address is the proxy's.</summary>
        private string? ClientIp =>
            Request.Headers["CF-Connecting-IP"].FirstOrDefault()
            ?? Request.Headers["X-Forwarded-For"].FirstOrDefault()?.Split(',')[0].Trim()
            ?? HttpContext.Connection.RemoteIpAddress?.ToString();

        /// <summary>
        /// The invoice behind a token.
        ///
        /// Opening it records the view. A Draft is refused: it has never been sent, so a token for
        /// one can only have come from somewhere it should not have.
        /// </summary>
        [HttpGet("{token}")]
        public async Task<ActionResult<PublicInvoiceDto>> Get(string token)
        {
            var invoice = await _invoices.FindByTokenAsync(token);

            // Same answer for a bad token and a draft, so the response cannot be used to work out
            // which invoices exist.
            if (invoice == null || invoice.Status == Models.Commercial.InvoiceStatus.Draft)
                return NotFound(new { message = "This invoice link is not valid." });

            // Frees the pay button when a Checkout session was abandoned and Stripe's expiry
            // webhook never arrived (the customer simply closed the tab). Done on read because
            // the page the customer is looking at is the thing that should unstick them.
            await _checkout.ExpireStaleAttemptsAsync(invoice.Id);

            await _invoices.RegisterViewAsync(invoice, ClientIp);

            return Ok(await _invoices.ToPublicDtoAsync(invoice));
        }

        /// <summary>
        /// Starts a Stripe-hosted payment and returns the URL to send the customer to.
        ///
        /// THE REQUEST CARRIES NO AMOUNT. The server reads the invoice's own balance, so a
        /// tampered request cannot express a different figure — the DTO has no field for one.
        /// Every other precondition (not draft, not void, balance outstanding, USD, method
        /// enabled, nothing already in flight) is re-checked server-side, because hiding a button
        /// is presentation and this endpoint is the control.
        ///
        /// Returning a URL rather than redirecting keeps the browser in charge of navigation and
        /// lets the page show its own error state when payment cannot be offered.
        /// </summary>
        [HttpPost("{token}/checkout")]
        public async Task<ActionResult<StartInvoiceCheckoutResponseDto>> StartCheckout(
            string token, [FromBody] StartInvoiceCheckoutDto? dto)
        {
            var invoice = await _invoices.FindByTokenAsync(token);

            if (invoice == null || invoice.Status == Models.Commercial.InvoiceStatus.Draft)
                return NotFound(new { message = "This invoice link is not valid." });

            var method = dto?.Method ?? Models.Commercial.InvoicePaymentRecordMethod.AchBankTransfer;

            // Only the two online methods can be started here. Anything else — cash, cheque — is
            // something an admin records after the fact, not something a customer initiates.
            if (method is not (Models.Commercial.InvoicePaymentRecordMethod.AchBankTransfer
                            or Models.Commercial.InvoicePaymentRecordMethod.Card))
            {
                return BadRequest(new { message = "That payment method is not available online." });
            }

            await _checkout.ExpireStaleAttemptsAsync(invoice.Id);

            try
            {
                var (attempt, checkoutUrl) = await _checkout.CreateCheckoutAsync(invoice, method, ClientIp);

                return Ok(new StartInvoiceCheckoutResponseDto
                {
                    CheckoutUrl = checkoutUrl,
                    AttemptId = attempt.Id,
                    Amount = attempt.Amount
                });
            }
            catch (InvoiceCheckoutUnavailableException ex)
            {
                // 503, not 400: nothing is wrong with the request — the payment route is simply
                // not available, and the page falls back to manual ACH on this status.
                return StatusCode(503, new { message = ex.Message });
            }
        }

        /// <summary>
        /// The same document the admin downloads, rendered from the same DTO the page above shows,
        /// so the client's copy and the office copy cannot disagree.
        ///
        /// Downloading does NOT count as a view - the view tracker answers "has the client opened
        /// this?", and counting the PDF fetch a browser may issue alongside the page would answer
        /// a different question badly.
        /// </summary>
        [HttpGet("{token}/pdf")]
        public async Task<IActionResult> DownloadPdf(string token)
        {
            var invoice = await _invoices.FindByTokenAsync(token);

            if (invoice == null || invoice.Status == Models.Commercial.InvoiceStatus.Draft)
                return NotFound(new { message = "This invoice link is not valid." });

            var bytes = _pdf.Render(
                await _invoices.ToPublicDtoAsync(invoice),
                _invoices.BuildPublicUrl(invoice.PublicToken));

            return File(bytes, "application/pdf",
                InvoicePdfService.BuildFileName(invoice.InvoiceNumber));
        }
    }
}
