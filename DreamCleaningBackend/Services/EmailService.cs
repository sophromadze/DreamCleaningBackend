using DreamCleaningBackend.Data;
using DreamCleaningBackend.Services.Interfaces;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using MimeKit;
using MimeKit.Text;


namespace DreamCleaningBackend.Services
{
    public class EmailService : IEmailService
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<EmailService> _logger;
        private readonly ApplicationDbContext _context;

        public EmailService(IConfiguration configuration, ILogger<EmailService> logger, ApplicationDbContext context)
        {
            _configuration = configuration;
            _logger = logger;
            _context = context;
        }

        public async Task SendEmailVerificationAsync(string email, string firstName, string verificationLink)
        {
            var subject = "Verify Your Email - Dream Cleaning";
            var body = $@"
                <h2>Hi {firstName},</h2>
                <p>Thank you for registering with Dream Cleaning!</p>
                <p>Please click the button below to verify your email address:</p>
                <p style='margin: 30px 0;'>
                    <a href='{verificationLink}' 
                       style='background-color: #4CAF50; color: white; padding: 14px 20px; 
                              text-decoration: none; border-radius: 4px; display: inline-block;'>
                        Verify Email
                    </a>
                </p>
                <p>Or copy and paste this link into your browser:</p>
                <p>{verificationLink}</p>
                <p>This link will expire in 24 hours.</p>
                <p>If you didn't create an account, please ignore this email.</p>
                <br/>
                <p>Best regards,<br/>Dream Cleaning Team</p>
            ";

            await SendEmailAsync(email, subject, body);
        }

        public async Task SendPasswordResetAsync(string email, string firstName, string resetLink)
        {
            var subject = "Password Reset Request - Dream Cleaning";
            var body = $@"
                <h2>Hi {firstName},</h2>
                <p>We received a request to reset your password.</p>
                <p>Click the button below to reset your password:</p>
                <p style='margin: 30px 0;'>
                    <a href='{resetLink}' 
                       style='background-color: #2196F3; color: white; padding: 14px 20px; 
                              text-decoration: none; border-radius: 4px; display: inline-block;'>
                        Reset Password
                    </a>
                </p>
                <p>Or copy and paste this link into your browser:</p>
                <p>{resetLink}</p>
                <p>This link will expire in 1 hour.</p>
                <p>If you didn't request a password reset, please ignore this email.</p>
                <br/>
                <p>Best regards,<br/>Dream Cleaning Team</p>
            ";

            await SendEmailAsync(email, subject, body);
        }

        public async Task SendWelcomeEmailAsync(string email, string firstName)
        {
            var subject = "Welcome to Dream Cleaning!";
            var body = $@"
                <h2>Welcome {firstName}!</h2>
                <p>Your email has been verified successfully.</p>
                <p>You can now enjoy all the features of Dream Cleaning:</p>
                <ul>
                    <li>Book cleaning services</li>
                    <li>Manage your apartments</li>
                    <li>Track your orders</li>
                    <li>Subscribe for discounts</li>
                </ul>
                <p>If you have any questions, feel free to contact our support team.</p>
                <br/>
                <p>Best regards,<br/>Dream Cleaning Team</p>
            ";

            await SendEmailAsync(email, subject, body);
        }

        private async Task SendEmailAsync(string to, string subject, string html)
        {
            try
            {
                var email = new MimeMessage();
                email.From.Add(new MailboxAddress(
                    _configuration["Email:FromName"],
                    _configuration["Email:FromAddress"]
                ));
                email.To.Add(MailboxAddress.Parse(to));
                email.Subject = subject;
                email.Body = new TextPart(TextFormat.Html) { Text = html };

                using var smtp = new SmtpClient();
                await smtp.ConnectAsync(
                    _configuration["Email:SmtpHost"],
                    int.Parse(_configuration["Email:SmtpPort"]),
                    SecureSocketOptions.StartTls
                );
                await smtp.AuthenticateAsync(
                    _configuration["Email:SmtpUser"],
                    _configuration["Email:SmtpPassword"]
                );
                await smtp.SendAsync(email);
                await smtp.DisconnectAsync(true);

                _logger.LogInformation($"Email sent successfully to {to}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to send email to {to}");
                throw;
            }
        }

        // Update this method in your EmailService.cs

        public async Task SendGiftCardNotificationAsync(string recipientEmail, string recipientName,
    string senderName, string giftCardCode, decimal amount, string message, string senderEmail)
        {
            // Keep the subject line - don't remove it!
            var subject = $"You've received a Dream Cleaning gift card from {senderName}!";

            // Get gift card configuration
            var giftCardConfig = await _context.GiftCardConfigs.FirstOrDefaultAsync();
            var backgroundPath = giftCardConfig?.BackgroundImagePath;

            // Use the configured background or fall back to default
            var backgroundImageUrl = !string.IsNullOrEmpty(backgroundPath)
                ? $"{_configuration["Frontend:Url"]}{backgroundPath}"
                : $"{_configuration["Frontend:Url"]}/images/mainImage.png";

            var body = $@"
    <!DOCTYPE html>
    <html>
    <head>
        <style>
            body {{
                font-family: Arial, sans-serif;
                background-color: #f4f4f4;
                margin: 0;
                padding: 0;
            }}
            .container {{
                max-width: 600px;
                margin: 0 auto;
                background-color: #ffffff;
                padding: 20px;
            }}
            .header {{
                background-color: #007bff;
                color: white;
                padding: 20px;
                text-align: center;
                border-radius: 8px 8px 0 0;
            }}
            .header h1 {{
                margin: 0;
                font-size: 28px;
            }}
            .header p {{
                margin: 5px 0 0 0;
                font-size: 16px;
            }}
            .content {{
                padding: 30px 20px;
                text-align: center;
            }}
            .gift-card-container {{
                display: inline-block;
                margin: 0 auto;
            }}
            .gift-card {{
                background: url('{backgroundImageUrl}') center/cover no-repeat;
                border-radius: 12px;
                height: 280px;
                width: 400px;
                position: relative;
                box-shadow: 0 4px 6px rgba(0, 0, 0, 0.1);
                border: 1px solid #e0e0e0;
                overflow: hidden;
                text-align: left;
            }}
            .instructions {{
                margin-top: 30px;
                padding: 20px;
                background-color: #e8f4fd;
                border-radius: 8px;
                border-left: 4px solid #007bff;
            }}
            .instructions h3 {{
                margin-top: 0;
                color: #007bff;
            }}
            .instructions ol {{
                text-align: left;
                color: #666;
            }}
            .footer {{
                text-align: center;
                padding: 20px;
                color: #666;
                font-size: 14px;
                border-top: 1px solid #e0e0e0;
            }}
            .button {{
                background-color: #007bff;
                color: white;
                padding: 14px 30px;
                text-decoration: none;
                border-radius: 4px;
                display: inline-block;
                margin: 20px 0;
                font-size: 16px;
            }}            
            /* Mobile responsive */
            @media only screen and (max-width: 600px) {{
                .gift-card {{
                    width: 100%;
                    max-width: 350px;
                    height: 240px;
                }}
            }}
        </style>
    </head>
    <body>
        <div class='container'>
            <div class='header'>
                <h1>Dream Cleaning</h1>
                <p>Professional Cleaning Services</p>
            </div>
            
            <div class='content'>
                <h2>Congratulations, {recipientName}!</h2>
                <p style='font-size: 18px; color: #666;'>You've received a special gift from {senderName}</p>
                <p style='font-size: 14px; color: #999; margin-top: 5px;'><a href='mailto:{senderEmail}' style='color: #007bff; text-decoration: none;'><b style='color:#000;'>Send a thank-you message to</b>: {senderEmail}</a></p>
                
                <div class='gift-card-container'>
                    <div class='gift-card'>
                        <div style='display: flex; justify-content: space-between; align-items: center; padding: 16px; background: linear-gradient(45deg, rgba(0, 123, 255, 0.2), rgba(0, 86, 179, 0.25)), rgba(255, 255, 255, 0.5); border-bottom: 1px solid rgba(0, 123, 255, 0.2);'>
                            <h3 style='font-size: 20px; color: #333; margin: 0; text-shadow: 1px 1px 2px rgba(255, 255, 255, 0.8);'>Dream Cleaning</h3>
                            <div style='font-size: 24px; font-weight: bold; color: #007bff; text-shadow: 1px 1px 2px rgba(255, 255, 255, 0.8); margin-left:auto;'>${amount:F2}</div>
                        </div>
                        <div style='display: flex; height: calc(100% - 60px);'>
                            <div style='width: 30%; padding: 16px;'></div>
                            <div style='width: 70%; background-color: rgba(255, 255, 255, 0.74); padding: 16px; display: grid;'>
                                <div>
                                    <p style='font-size: 14px; color: #333; margin: 0;'>Dear {recipientName},</p>
                                    <div style='font-size: 13px; color: #666; line-height: 1.4; font-style: italic; word-wrap: break-word; margin: 0;'>
                                        <p>{message}</p>
                                    </div>                                    
                                </div>
                                <div style='text-align: center; font-family: monospace; font-size: 16px; color: #666; letter-spacing: 1px; margin: 0; margin-top: auto; padding: 10px; border-radius: 4px;'>
                                    {giftCardCode}
                                </div>
                            </div>
                        </div>
                    </div>
                </div>
                
                <div class='instructions'>
                    <h3>How to Use Your Gift Card</h3>
                    <ol>
                        <li>Visit <a href='{_configuration["Frontend:Url"]}'>{_configuration["Frontend:Url"]}</a></li>
                        <li>Fill up preferred cleaning services in booking form</li>
                        <li>Enter your gift card code <strong>{giftCardCode}</strong> at 'Promo Code/Gift Card' field</li>
                        <li>The gift card amount will be automatically applied</li>
                    </ol>
                </div>
                
                <a href='{_configuration["Frontend:Url"]}/booking' class='button' style='color:#fff !important;'>
                    Book Your Cleaning Service
                </a>
            </div>
            
            <div class='footer'>
                <p><strong>Gift Card Terms:</strong></p>
                <p>• Gift card never expires</p>
                <p>• Can be used for any Dream Cleaning services</p>
                <p>• Remaining balance can be used for future bookings</p>
                <p>• Non-refundable and non-exchangeable for cash</p>
                <br/>
                <p>Need help? Contact us at {_configuration["Email:FromAddress"]} or call (929) 930-1525</p>
                <p>&copy; {DateTime.Now.Year} Dream Cleaning. All rights reserved.</p>
            </div>
        </div>
    </body>
    </html>";

            await SendEmailAsync(recipientEmail, subject, body);
        }

        public async Task SendContactFormEmailAsync(string to, string subject, string html)
        {
            await SendEmailAsync(to, subject, html);
        }
    }
}