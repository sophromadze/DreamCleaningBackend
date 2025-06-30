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

        public async Task SendCleanerAssignmentNotificationAsync(string email, string cleanerName,
            DateTime serviceDate, string serviceTime, string serviceTypeName, string address)
        {
            // Find the order
            var order = await _context.Orders
                .Include(o => o.ServiceType)
                .Include(o => o.OrderServices)
                    .ThenInclude(os => os.Service)
                .Include(o => o.OrderExtraServices)
                    .ThenInclude(oes => oes.ExtraService)
                .Include(o => o.OrderCleaners)
                    .ThenInclude(oc => oc.Cleaner)
                .Where(o => o.ServiceDate.Date == serviceDate.Date &&
                       o.ServiceType.Name == serviceTypeName)
                .FirstOrDefaultAsync();

            // Get cleaner-specific additional instructions
            var cleanerAdditionalInstructions = order?.OrderCleaners
                .FirstOrDefault(oc => oc.Cleaner.Email == email)?.TipsForCleaner;

            // Check if there's a cleaners service to determine if we should divide duration
            bool hasCleanersService = order?.OrderServices.Any(os =>
                os.Service.ServiceKey != null && os.Service.ServiceKey.ToLower().Contains("cleaner")) ?? false;

            // Calculate duration per cleaner
            decimal durationPerCleaner = 0;
            string formattedDuration = "";

            if (order != null)
            {
                if (hasCleanersService)
                {
                    // If there's a cleaners service, show the total duration (it's already calculated correctly)
                    durationPerCleaner = order.TotalDuration;
                }
                else
                {
                    // If no cleaners service, divide by number of maids
                    durationPerCleaner = (decimal)order.TotalDuration / (order.MaidsCount > 0 ? order.MaidsCount : 1);
                }

                formattedDuration = FormatDurationRounded((int)durationPerCleaner);
            }

            var subject = "New Cleaning Job Assignment - Dream Cleaning";
            var body = $@"
        <h2>Hi {cleanerName},</h2>
        <p>You have been assigned to a new cleaning job with complete details below:</p>
        
        <div style='background: #f8f9fa; padding: 20px; border-radius: 8px; margin: 20px 0; border-left: 4px solid #28a745;'>
            <h3>📋 Job Overview</h3>
            <p><strong>Service Type:</strong> {(order?.ServiceType.Name ?? serviceTypeName)}</p>
            <p><strong>Date:</strong> {serviceDate:dddd, MMMM dd, yyyy}</p>
            <p><strong>Time:</strong> {FormatTimeForEmail(TimeSpan.Parse(serviceTime))}</p>
            <p><strong>Duration:</strong> {formattedDuration}{(hasCleanersService ? "" : " per cleaner")}</p>
            <p><strong>Team Size:</strong> {(order?.MaidsCount ?? 1)} cleaner(s)</p>
        </div>
        
        <div style='background: #e7f3ff; padding: 20px; border-radius: 8px; margin: 20px 0; border-left: 4px solid #007bff;'>
            <h3>👥 Client Information</h3>
            <p><strong>Client Name:</strong> {(order?.ContactFirstName ?? "")} {(order?.ContactLastName ?? "")}</p>
            <p><strong>Phone:</strong> {(order?.ContactPhone ?? "")}</p>
            <p><strong>Email:</strong> {(order?.ContactEmail ?? "")}</p>
            <p><strong>Entry Method:</strong> {(order?.EntryMethod ?? "To be confirmed")}</p>
        </div>
        
        <div style='background: #fff3cd; padding: 20px; border-radius: 8px; margin: 20px 0; border-left: 4px solid #ffc107;'>
            <h3>📍 Complete Service Address</h3>
            <p><strong>Address:</strong> {(order?.ServiceAddress ?? address)}</p>
            {(!string.IsNullOrEmpty(order?.AptSuite) ? $"<p><strong>Apt/Suite:</strong> {order.AptSuite}</p>" : "")}
            <p><strong>City:</strong> {(order?.City ?? "")}</p>
            <p><strong>State:</strong> {(order?.State ?? "")}</p>
            <p><strong>Postal Code:</strong> {(order?.ZipCode ?? "")}</p>
        </div>
        
        {(order?.OrderServices.Any() == true ? $@"
        <div style='background: #f8f9fa; padding: 20px; border-radius: 8px; margin: 20px 0;'>
            <h3>🧹 Services to Perform</h3>
            <ul style='margin: 10px 0; padding-left: 20px;'>
                {string.Join("", order.OrderServices.Select(os => $"<li style='margin: 5px 0;'>{FormatServiceForEmail(os, order.Id)}</li>"))}
            </ul>
        </div>" : "")}
        
        {(order?.OrderExtraServices.Any() == true ? $@"
        <div style='background: #f0f8ff; padding: 20px; border-radius: 8px; margin: 20px 0;'>
            <h3>✨ Extra Services</h3>
            <ul style='margin: 10px 0; padding-left: 20px;'>
                {string.Join("", order.OrderExtraServices.Select(oes =>
                            $"<li style='margin: 5px 0;'>{oes.ExtraService.Name} - Quantity: {oes.Quantity}" +
                            (oes.Hours > 0 ? $", Hours: {oes.Hours:0.#}" : "") + "</li>"))}
            </ul>
        </div>" : "")}

        {(order?.Tips > 0 ? $@"
        <div style='background: #d4edda; padding: 20px; border-radius: 8px; margin: 20px 0; border-left: 4px solid #28a745;'>
            <h3>💰 Tips for Cleaning Team</h3>
            <p style='margin: 0; font-size: 1.2em; font-weight: bold; color: #155724; text-align: center;'>${order.Tips:F2}</p>
        </div>" : "")}
        
        {(!string.IsNullOrEmpty(order?.SpecialInstructions) ? $@"
        <div style='background: #ffe6e6; padding: 20px; border-radius: 8px; margin: 20px 0; border-left: 4px solid #dc3545;'>
            <h3>⚠️ Special Instructions from Client</h3>
            <p style='margin: 0; font-style: italic; color: #721c24;'>{order.SpecialInstructions}</p>
        </div>" : "")}
        
        {(!string.IsNullOrEmpty(cleanerAdditionalInstructions) ? $@"
        <div style='background: #e8f5e9; padding: 20px; border-radius: 8px; margin: 20px 0; border-left: 4px solid #28a745;'>
            <h3>💡 Additional Instructions for You</h3>
            <p style='margin: 0; font-style: italic; color: #155724;'>{cleanerAdditionalInstructions}</p>
        </div>" : "")}
        
        <div style='background: #f8f9fa; padding: 20px; border-radius: 8px; margin: 20px 0; text-align: center;'>
            <p><strong>📱 Next Steps:</strong></p>
            <p>1. Log in to your cleaner dashboard for any updates</p>
            <p>2. Arrive on time and prepared</p>
            <p>3. Contact support if you have any questions</p>
        </div>
        
        <p>If you have any questions or concerns about this assignment, please contact our support team immediately.</p>
        
        <br/>
        <p>Best regards,<br/>Dream Cleaning Team</p>
    ";

            await SendEmailAsync(email, subject, body);
        }

        private string FormatTimeForEmail(TimeSpan time)
        {
            var hours = time.Hours;
            var minutes = time.Minutes;
            var ampm = hours >= 12 ? "PM" : "AM";
            var displayHour = hours % 12;
            if (displayHour == 0) displayHour = 12;

            return $"{displayHour}:{minutes:D2} {ampm}";
        }

        private string FormatDurationRounded(int minutes)
        {
            var hours = minutes / 60;
            var mins = minutes % 60;

            // Rounding logic: if mins >= 45, round up to next hour
            // if mins >= 15, round to 30; if less, round to 0
            if (mins >= 45)
            {
                hours += 1;
                mins = 0;
            }
            else if (mins >= 15)
            {
                mins = 30;
            }
            else
            {
                mins = 0;
            }

            if (hours == 0 && mins == 0)
            {
                return "0 minutes";
            }
            else if (hours == 0)
            {
                return $"{mins} minutes";
            }
            else if (mins == 0)
            {
                return $"{hours}h";
            }
            else
            {
                return $"{hours}h {mins}min";
            }
        }

        private string FormatServiceForEmail(Models.OrderService orderService, int orderId)
        {
            // Handle special cases like bedroom = 0 for studio
            if (orderService.Service.ServiceKey == "bedrooms" && orderService.Quantity == 0)
            {
                return "Studio";
            }

            // Handle cleaners service with hours
            if (orderService.Service.ServiceKey != null && orderService.Service.ServiceKey.ToLower().Contains("cleaner"))
            {
                // Check if there's an hours service to get the hours
                var hoursService = _context.OrderServices
                    .Where(os => os.OrderId == orderId &&
                                os.Service.ServiceKey != null &&
                                os.Service.ServiceKey.ToLower().Contains("hour"))
                    .FirstOrDefault();

                if (hoursService != null)
                {
                    return $"{orderService.Service.Name} - Quantity: {orderService.Quantity}, Hours: {hoursService.Quantity}";
                }
            }

            return $"{orderService.Service.Name} - Quantity: {orderService.Quantity}";
        }

        public async Task SendCleanerReminderNotificationAsync(string email, string cleanerName,
            DateTime serviceDate, string serviceTime, string serviceTypeName, string address, bool isDayBefore)
        {
            var timeFrame = isDayBefore ? "tomorrow" : "in 4 hours";
            var subject = $"Cleaning Job Reminder - Service {timeFrame}";

            var body = $@"
        <h2>Hi {cleanerName},</h2>
        <p>This is a reminder that you have a cleaning job scheduled for <strong>{timeFrame}</strong>:</p>
        
        <div style='background: #fff3cd; padding: 20px; border-radius: 8px; margin: 20px 0; border-left: 4px solid #ffc107;'>
            <h3>Job Details:</h3>
            <p><strong>Service Type:</strong> {serviceTypeName}</p>
            <p><strong>Date:</strong> {serviceDate:dddd, MMMM dd, yyyy}</p>
            <p><strong>Time:</strong> {serviceTime}</p>
            <p><strong>Address:</strong> {address}</p>
        </div>
        
        <p>Please make sure you're prepared and arrive on time. Check your cleaner dashboard for any special instructions.</p>
        
        <br/>
        <p>Best regards,<br/>Dream Cleaning Team</p>
    ";

            await SendEmailAsync(email, subject, body);
        }

        public async Task SendCleanerRemovalNotificationAsync(string email, string cleanerName,
            DateTime serviceDate, string serviceTime, string serviceTypeName)
        {
            var subject = "Cleaning Job Assignment Removed - Dream Cleaning";
            var body = $@"
        <h2>Hi {cleanerName},</h2>
        <p>You have been removed from a cleaning job assignment:</p>
        
        <div style='background: #fff3cd; padding: 20px; border-radius: 8px; margin: 20px 0; border-left: 4px solid #ffc107;'>
            <h3>Job Details:</h3>
            <p><strong>Service Type:</strong> {serviceTypeName}</p>
            <p><strong>Date:</strong> {serviceDate:dddd, MMMM dd, yyyy}</p>
            <p><strong>Time:</strong> {serviceTime}</p>
        </div>
        
        <p>If you have any questions, please contact our support team.</p>
        
        <br/>
        <p>Best regards,<br/>Dream Cleaning Team</p>
    ";

            await SendEmailAsync(email, subject, body);
        }
    }
}