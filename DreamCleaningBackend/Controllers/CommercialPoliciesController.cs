using DreamCleaningBackend.Helpers.Commercial;
using DreamCleaningBackend.Services.Commercial;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DreamCleaningBackend.Controllers
{
    /// <summary>
    /// The two public downloads behind /commercial-cleaning-policies: the complete Commercial
    /// Cleaning Policies and the standalone Cancellation and Termination Policy.
    ///
    /// ANONYMOUS ON PURPOSE. These are published documents a prospective client reads before there
    /// is any relationship at all - requiring a sign-in would defeat the point of publishing them.
    /// Nothing here reads the database, and nothing here is client-specific: the content is a
    /// compile-time constant, so no client's price, address, contract or invoice can reach it.
    ///
    /// The page itself renders from the frontend mirror of the same content rather than calling
    /// this controller, so the policy text is in the prerendered HTML for a reader and for a search
    /// engine. These endpoints exist for the downloads alone.
    /// </summary>
    [ApiController]
    [AllowAnonymous]
    [Route("api/commercial-policies")]
    public class CommercialPoliciesController : ControllerBase
    {
        private const string Pdf = "application/pdf";

        private readonly CommercialPolicyPdfService _pdf;

        public CommercialPoliciesController(CommercialPolicyPdfService pdf)
        {
            _pdf = pdf;
        }

        /// <summary>
        /// The published version and effective date, so a caller can tell which revision the
        /// downloads below correspond to without parsing a PDF.
        /// </summary>
        [HttpGet("version")]
        public IActionResult GetVersion() => Ok(new
        {
            version = CommercialPolicyDocument.Version,
            effectiveDate = CommercialPolicyDocument.EffectiveDate,
            legalIdentity = CommercialPolicyDocument.LegalIdentity
        });

        [HttpGet("complete.pdf")]
        public IActionResult GetCompletePdf() =>
            File(_pdf.GenerateComplete(), Pdf, CommercialPolicyDocument.CompletePdfFileName);

        [HttpGet("cancellation-termination.pdf")]
        public IActionResult GetCancellationPdf() =>
            File(_pdf.GenerateCancellationAndTermination(), Pdf,
                CommercialPolicyDocument.CancellationPdfFileName);
    }
}
