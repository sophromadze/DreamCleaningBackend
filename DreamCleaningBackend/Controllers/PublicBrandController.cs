using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DreamCleaningBackend.Controllers
{
    /// <summary>
    /// Serves the Dream Cleaning logo at a STABLE, ABSOLUTE, PUBLIC HTTPS URL, for use in emails.
    ///
    /// WHY AN ENDPOINT RATHER THAN A FILE PATH. An email client renders somewhere we control
    /// nothing: it will not read a local path, it will not follow a development URL, and it will
    /// not authenticate. It needs one unchanging https:// address that any inbox in the world can
    /// fetch anonymously. The same PNG the contract and invoice PDFs embed lives in the app's
    /// Assets folder, which is NOT under the publicly-served uploads root - so this is the one
    /// deliberate, read-only door onto it.
    ///
    /// It is a brand asset and nothing else: no invoice, contract, client or customer information
    /// passes through here, which is why it is safely anonymous. Gzip-free, long-cached, and it
    /// 404s rather than throwing if the file is missing, so a deployment that forgot to copy it
    /// degrades to an email with alt text instead of a broken send.
    /// </summary>
    [Route("api/public/brand")]
    [ApiController]
    [AllowAnonymous]
    public class PublicBrandController : ControllerBase
    {
        /// <summary>
        /// The URL an email should reference. Built from the site URL rather than the request, so
        /// a mail composed by a background job still points at the public domain.
        /// </summary>
        public static string BuildLogoUrl(string frontendUrl) =>
            $"{frontendUrl.TrimEnd('/')}/api/public/brand/logo.png";

        /// <summary>Alt text, so the mail still reads correctly with images blocked.</summary>
        public const string LogoAltText = "Dream Cleaning NYC";

        [HttpGet("logo.png")]
        [ResponseCache(Duration = 60 * 60 * 24 * 30, Location = ResponseCacheLocation.Any)]
        public IActionResult Logo()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "contract-logo.png");

            // A missing file is not an error worth 500-ing over: the email falls back to its alt
            // text, and the invoice is still perfectly readable.
            if (!System.IO.File.Exists(path)) return NotFound();

            return PhysicalFile(path, "image/png");
        }
    }
}
