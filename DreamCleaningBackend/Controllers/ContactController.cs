using Microsoft.AspNetCore.Mvc;
using DreamCleaningBackend.Services.Interfaces;
using System.ComponentModel.DataAnnotations;
using DreamCleaningBackend.DTOs;

namespace DreamCleaningBackend.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class ContactController : ControllerBase
    {
        private readonly IEmailService _emailService;
        private readonly IConfiguration _configuration;
        private readonly ILogger<ContactController> _logger;

        public ContactController(
            IEmailService emailService,
            IConfiguration configuration,
            ILogger<ContactController> logger)
        {
            _emailService = emailService;
            _configuration = configuration;
            _logger = logger;
        }

        [HttpPost]
        public async Task<IActionResult> SendContactMessage(ContactFormDto contactForm)
        {
            try
            {
                if (!ModelState.IsValid)
                {
                    return BadRequest(new { message = "Please fill in all required fields correctly." });
                }

                // Get the company email from configuration
                var companyEmail = _configuration["Email:CompanyEmail"] ?? _configuration["Email:FromAddress"];

                var subject = $"New Contact Form Message from {contactForm.FullName}";
                var body = $@"
                    <h2>New Contact Form Submission</h2>
                    <p>You have received a new message from the contact form:</p>
                    
                    <table style='width: 100%; border-collapse: collapse; margin: 20px 0;'>
                        <tr>
                            <td style='padding: 10px; border: 1px solid #ddd; background-color: #f8f9fa; font-weight: bold; width: 30%;'>Full Name:</td>
                            <td style='padding: 10px; border: 1px solid #ddd;'>{contactForm.FullName}</td>
                        </tr>
                        <tr>
                            <td style='padding: 10px; border: 1px solid #ddd; background-color: #f8f9fa; font-weight: bold;'>Email:</td>
                            <td style='padding: 10px; border: 1px solid #ddd;'><a href='mailto:{contactForm.Email}'>{contactForm.Email}</a></td>
                        </tr>
                        <tr>
                            <td style='padding: 10px; border: 1px solid #ddd; background-color: #f8f9fa; font-weight: bold;'>Phone:</td>
                            <td style='padding: 10px; border: 1px solid #ddd;'>{FormatPhoneNumber(contactForm.Phone)}</td>
                        </tr>
                        <tr>
                            <td style='padding: 10px; border: 1px solid #ddd; background-color: #f8f9fa; font-weight: bold; vertical-align: top;'>Message:</td>
                            <td style='padding: 10px; border: 1px solid #ddd; white-space: pre-wrap;'>{contactForm.Message}</td>
                        </tr>
                    </table>
                    
                    <p style='margin-top: 30px; color: #666; font-size: 14px;'>
                        <strong>Submitted on:</strong> {DateTime.UtcNow:MMMM dd, yyyy at h:mm tt}
                    </p>
                ";

                // Send email to company
                await SendEmailAsync(companyEmail, subject, body);

                // Optionally, send confirmation email to the user
                if (_configuration.GetValue<bool>("Email:SendContactConfirmation", true))
                {
                    await SendConfirmationEmail(contactForm.Email, contactForm.FullName);
                }

                _logger.LogInformation($"Contact form submitted by {contactForm.FullName} ({contactForm.Email})");

                return Ok(new { message = "Your message has been sent successfully!" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending contact form message");
                return StatusCode(500, new { message = "An error occurred while sending your message. Please try again later." });
            }
        }

        private async Task SendEmailAsync(string to, string subject, string html)
        {
            try
            {
                // Use reflection to call the private SendEmailAsync method in EmailService
                // Or better, make it public or create a new public method
                var method = _emailService.GetType().GetMethod("SendEmailAsync",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

                if (method != null)
                {
                    await (Task)method.Invoke(_emailService, new object[] { to, subject, html });
                }
                else
                {
                    // Fallback: Create a simple email sending method
                    await _emailService.SendContactFormEmailAsync(to, subject, html);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send email");
                throw;
            }
        }

        private async Task SendConfirmationEmail(string email, string name)
        {
            var subject = "Thank you for contacting Dream Cleaning";
            var body = $@"
                <h2>Hi {name},</h2>
                <p>Thank you for reaching out to Dream Cleaning!</p>
                <p>We have received your message and will get back to you as soon as possible.</p>
                <p>Our team typically responds within 24 hours during business days.</p>
                <br/>
                <p>If you need immediate assistance, please feel free to call us at <strong>(929) 930-1525</strong>.</p>
                <br/>
                <p>Best regards,<br/>Dream Cleaning Team</p>
            ";

            await SendEmailAsync(email, subject, body);
        }

        private string FormatPhoneNumber(string phone)
        {
            if (string.IsNullOrEmpty(phone) || phone.Length != 10)
                return phone;

            return $"({phone.Substring(0, 3)}) {phone.Substring(3, 3)}-{phone.Substring(6)}";
        }
    }
}