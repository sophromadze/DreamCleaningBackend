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

        [HttpPost("quote-request")]
        public async Task<IActionResult> SendQuoteRequest(QuoteRequestDto quoteRequest)
        {
            try
            {
                if (!ModelState.IsValid)
                {
                    return BadRequest(new { message = "Please fill in all required fields correctly." });
                }

                // Get the company email from configuration
                var companyEmail = _configuration["Email:CompanyEmail"] ?? _configuration["Email:FromAddress"];
                
                // Validate company email before attempting to send
                if (string.IsNullOrWhiteSpace(companyEmail))
                {
                    _logger.LogError("Company email is not configured. Cannot send quote request notification email.");
                    return StatusCode(500, new { message = "Email service is not configured. Please contact support." });
                }

                var subject = $"New Free Quote Request from {quoteRequest.Name}";
                var messageContent = !string.IsNullOrWhiteSpace(quoteRequest.Message) 
                    ? quoteRequest.Message 
                    : "No additional information provided.";

                var body = $@"
                    <h2>New Free Quote Request</h2>
                    <p>A customer has requested a free quote and needs your help:</p>
                    
                    <table style='width: 100%; border-collapse: collapse; margin: 20px 0;'>
                        <tr>
                            <td style='padding: 10px; border: 1px solid #ddd; background-color: #f8f9fa; font-weight: bold; width: 30%;'>Name:</td>
                            <td style='padding: 10px; border: 1px solid #ddd;'>{quoteRequest.Name}</td>
                        </tr>
                        <tr>
                            <td style='padding: 10px; border: 1px solid #ddd; background-color: #f8f9fa; font-weight: bold;'>Phone:</td>
                            <td style='padding: 10px; border: 1px solid #ddd;'><a href='tel:+1{quoteRequest.Phone}'>{FormatPhoneNumber(quoteRequest.Phone)}</a></td>
                        </tr>
                        {(string.IsNullOrWhiteSpace(quoteRequest.Message) ? "" : $@"
                        <tr>
                            <td style='padding: 10px; border: 1px solid #ddd; background-color: #f8f9fa; font-weight: bold; vertical-align: top;'>Additional Information:</td>
                            <td style='padding: 10px; border: 1px solid #ddd; white-space: pre-wrap;'>{quoteRequest.Message}</td>
                        </tr>")}
                    </table>
                    
                    <div style='background: #e8f5e9; padding: 15px; border-radius: 8px; margin: 20px 0;'>
                        <p><strong>📞 Call Back Required</strong></p>
                        <p>This customer is too busy to fill out the full booking form or needs assistance choosing the right service. Please call them back to help with their quote request.</p>
                    </div>
                    
                    <p style='margin-top: 30px; color: #666; font-size: 14px;'>
                        <strong>Submitted on:</strong> {DateTime.UtcNow:MMMM dd, yyyy at h:mm tt}
                    </p>
                ";

                // Send email to company
                await _emailService.SendContactFormEmailAsync(companyEmail, subject, body);

                _logger.LogInformation($"Quote request submitted by {quoteRequest.Name} (Phone: {FormatPhoneNumber(quoteRequest.Phone)})");

                return Ok(new { message = "Your quote request has been received! We'll call you back soon." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending quote request");
                return StatusCode(500, new { message = "An error occurred while sending your quote request. Please try again later." });
            }
        }

        private string FormatPhoneNumber(string phone)
        {
            if (string.IsNullOrEmpty(phone) || phone.Length != 10)
                return phone;

            return $"({phone.Substring(0, 3)}) {phone.Substring(3, 3)}-{phone.Substring(6)}";
        }
    }
}