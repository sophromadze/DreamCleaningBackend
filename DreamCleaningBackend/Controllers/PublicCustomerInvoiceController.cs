using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DreamCleaningBackend.Controllers
{
    /// <summary>
    /// The public page behind a regular customer invoice link (/pay-invoice/{token}). Anonymous on
    /// purpose: the customer may not be logged in, or may be logged in as somebody else (a
    /// relative paying for them), so the opaque token is the whole authorization — the same rule
    /// as the order payment link and the commercial invoice page.
    ///
    /// Paying goes through the order payment page (card), Stripe's hosted bank (ACH) page opened
    /// by <c>ach-checkout</c>, or an admin recording a bank transfer; the GETs change nothing. An unknown token answers 404 with the same message
    /// whatever the reason, so the response can't be used to probe which invoices exist.
    /// </summary>
    [Route("api/public/customer-invoices")]
    [ApiController]
    [AllowAnonymous]
    public class PublicCustomerInvoiceController : ControllerBase
    {
        private readonly ICustomerInvoiceService _invoices;
        private readonly CustomerInvoiceAchService _ach;

        public PublicCustomerInvoiceController(ICustomerInvoiceService invoices, CustomerInvoiceAchService ach)
        {
            _invoices = invoices;
            _ach = ach;
        }

        /// <summary>
        /// Starts an online BANK payment (Stripe ACH) of the invoice's amount due plus the ACH
        /// processing fee, and returns the Stripe-hosted page to send the customer to. Takes NO
        /// amount — the server reads it from the invoice. 503 when bank payment is switched off or
        /// Stripe is unavailable, so the page can fall back to card / manual bank transfer.
        /// </summary>
        [HttpPost("{token}/ach-checkout")]
        public async Task<ActionResult<StartCustomerInvoiceAchResponseDto>> StartAchCheckout(string token)
        {
            try
            {
                var (attempt, url) = await _ach.StartCheckoutAsync(token, HttpContext.Connection.RemoteIpAddress?.ToString());
                return Ok(new StartCustomerInvoiceAchResponseDto
                {
                    CheckoutUrl = url,
                    Amount = attempt.Amount,
                    ProcessingFee = attempt.ProcessingFee,
                    TotalCharged = attempt.TotalCharged
                });
            }
            catch (CustomerInvoiceException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
            catch (Services.Commercial.InvoiceCheckoutUnavailableException ex)
            {
                return StatusCode(503, new { message = ex.Message });
            }
        }

        [HttpGet("{token}")]
        public async Task<ActionResult<PublicCustomerInvoiceDto>> Get(string token)
        {
            // Bring any open bank payment up to date with Stripe first, so a customer returning
            // from Stripe sees "processing" / "paid" even when the webhook is late or missing.
            await _ach.SyncWithStripeAsync(token);

            var dto = await _invoices.GetPublicAsync(token);
            return dto == null ? NotFound(new { message = "Invoice not found." }) : Ok(dto);
        }

        /// <summary>
        /// The invoice as a real PDF document — what the page's download button saves and what the
        /// invoice email attaches. It replaced <c>window.print()</c>, which captured the website's
        /// header, footer and floating buttons around the bill.
        /// </summary>
        [HttpGet("{token}/pdf")]
        public async Task<IActionResult> DownloadPdf(string token)
        {
            var pdf = await _invoices.RenderPdfAsync(token);
            return pdf == null
                ? NotFound(new { message = "Invoice not found." })
                : File(pdf.Value.Bytes, "application/pdf", pdf.Value.FileName);
        }
    }
}
