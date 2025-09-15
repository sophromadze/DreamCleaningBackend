using Microsoft.AspNetCore.Mvc;
using DreamCleaningBackend.Services.Interfaces;

namespace DreamCleaningBackend.Controllers
{
    [Route("api/test")]
    [ApiController]
    public class TestController : ControllerBase
    {
        private readonly IEmailService _emailService;
        private readonly ILogger<TestController> _logger;

        public TestController(IEmailService emailService, ILogger<TestController> logger)
        {
            _emailService = emailService;
            _logger = logger;
        }

        [HttpPost("email")]
        public async Task<ActionResult> TestEmail([FromBody] TestEmailDto dto)
        {
            try
            {
                _logger.LogInformation($"Testing email sending to: {dto.Email}");
                
                await _emailService.SendEmailAsync(
                    dto.Email,
                    "Test Email from Dream Cleaning",
                    "<h2>Test Email</h2><p>This is a test email to verify email functionality.</p>"
                );
                
                return Ok(new { message = "Test email sent successfully" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending test email");
                return StatusCode(500, new { message = ex.Message });
            }
        }

        public class TestEmailDto
        {
            public string Email { get; set; } = string.Empty;
        }
    }
}
