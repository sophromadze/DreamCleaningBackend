using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Services.Interfaces;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using MimeKit;
using MimeKit.Text;
using System.IO;


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

        public async Task SendRealEmailVerificationCodeAsync(string email, string firstName, string code)
        {
            var subject = "Your verification code - Dream Cleaning";
            var body = $@"
                <h2>Hi {firstName},</h2>
                <p>Use this code to verify your email address for Dream Cleaning:</p>
                <p style='font-size: 24px; font-weight: bold; letter-spacing: 4px; margin: 24px 0;'>{code}</p>
                <p>This code expires in 10 minutes.</p>
                <p>If you didn't request this, you can safely ignore this email.</p>
                <br/>
                <p>Best regards,<br/>Dream Cleaning Team</p>
            ";
            await SendEmailAsync(email, subject, body);
        }

        public async Task SendAccountMergeConfirmationAsync(string email, string firstName, string code)
        {
            var subject = "Confirm Account Merge — Dream Cleaning";
            var body = $@"
                <h2>Hi {firstName},</h2>
                <p>Someone is trying to merge an Apple Sign In account with your Dream Cleaning account ({email}).</p>
                <p>If this was you, enter this code to confirm:</p>
                <p style='font-size: 24px; font-weight: bold; letter-spacing: 4px; margin: 24px 0;'>{code}</p>
                <p>This code expires in 10 minutes.</p>
                <p>If you did not request this, please ignore this email. Your account will not be changed.</p>
                <br/>
                <p>— Dream Cleaning Team</p>
            ";
            await SendEmailAsync(email, subject, body);
        }

        public async Task SendEmailAsync(string to, string subject, string html)
        {
            // Check if email sending is disabled in configuration
            var emailEnabled = _configuration.GetValue<bool>("Email:EnableEmailSending", true);

            _logger.LogInformation($"Attempting to send email to {to} with subject: {subject}");
            _logger.LogInformation($"Email sending enabled: {emailEnabled}");

            if (!emailEnabled)
            {
                _logger.LogInformation($"Email sending is disabled. Would have sent email to {to} with subject: {subject}");
                return;
            }

            const int timeoutMs = 30000; // 30 second timeout

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

                _logger.LogInformation($"Email configured - From: {_configuration["Email:FromAddress"]}, To: {to}");

                using var smtp = new SmtpClient();

                // Add timeout to prevent hanging
                smtp.Timeout = timeoutMs;

                using var cts = new CancellationTokenSource(timeoutMs);

                _logger.LogInformation($"Connecting to SMTP server: {_configuration["Email:SmtpHost"]}:{_configuration["Email:SmtpPort"]}");

                await smtp.ConnectAsync(
                    _configuration["Email:SmtpHost"],
                    int.Parse(_configuration["Email:SmtpPort"]),
                    SecureSocketOptions.StartTls,
                    cts.Token
                );

                _logger.LogInformation("SMTP connection established, authenticating...");

                await smtp.AuthenticateAsync(
                    _configuration["Email:SmtpUser"],
                    _configuration["Email:SmtpPassword"],
                    cts.Token
                );

                _logger.LogInformation("SMTP authentication successful, sending email...");

                await smtp.SendAsync(email, cts.Token);
                await smtp.DisconnectAsync(true, cts.Token);

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

            _logger.LogInformation($"[GIFT CARD EMAIL] Starting email send to {recipientEmail} for gift card {giftCardCode}");

            // Get gift card configuration
            var giftCardConfig = await _context.GiftCardConfigs.FirstOrDefaultAsync();
            var backgroundPath = giftCardConfig?.BackgroundImagePath;
            
            _logger.LogInformation($"[GIFT CARD EMAIL] Gift card config retrieved. Config exists: {giftCardConfig != null}, BackgroundPath: {backgroundPath ?? "NULL"}");

            // Get base64 encoded image for email embedding
            string backgroundImageDataUri = "";
            try
            {
                string imagePath = null;
                if (!string.IsNullOrEmpty(backgroundPath))
                {
                    // Try to load the configured background image
                    var fileUploadPath = _configuration["FileUpload:Path"];
                    if (!string.IsNullOrEmpty(fileUploadPath))
                    {
                        // Normalize the path: remove leading slash
                        var normalizedPath = backgroundPath.TrimStart('/', '\\');
                        
                        // Split the path by both forward and back slashes to handle any format
                        var pathParts = normalizedPath.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
                        
                        // Combine the file upload path with all path parts
                        var fullImagePath = Path.Combine(new[] { fileUploadPath }.Concat(pathParts).ToArray());
                        
                        _logger.LogInformation($"[GIFT CARD EMAIL] Attempting to load gift card background image. DB Path: {backgroundPath}, FileUploadPath: {fileUploadPath}, FullPath: {fullImagePath}");
                        
                        if (File.Exists(fullImagePath))
                        {
                            imagePath = fullImagePath;
                            _logger.LogInformation($"[GIFT CARD EMAIL] Successfully found gift card background image at: {fullImagePath}");
                        }
                        else
                        {
                            _logger.LogWarning($"[GIFT CARD EMAIL] Gift card background image not found at: {fullImagePath}");
                            // Try alternative path constructions
                            var altPath1 = Path.Combine(fileUploadPath, backgroundPath.TrimStart('/'));
                            var altPath2 = Path.Combine(fileUploadPath, "images", Path.GetFileName(backgroundPath));
                            _logger.LogWarning($"[GIFT CARD EMAIL] Alternative paths checked - Alt1: {altPath1} (exists: {File.Exists(altPath1)}), Alt2: {altPath2} (exists: {File.Exists(altPath2)})");
                        }
                    }
                    else
                    {
                        _logger.LogWarning($"[GIFT CARD EMAIL] FileUpload:Path configuration is empty, cannot load gift card background image");
                    }
                }
                else
                {
                    _logger.LogInformation("[GIFT CARD EMAIL] Background path from database is empty, will use default image");
                }
                
                // Fallback to default image if configured image not found
                if (string.IsNullOrEmpty(imagePath))
                {
                    var fileUploadPath = _configuration["FileUpload:Path"];
                    if (!string.IsNullOrEmpty(fileUploadPath))
                    {
                        var defaultImagePath = Path.Combine(fileUploadPath, "images", "mainImage.webp");
                        _logger.LogInformation($"[GIFT CARD EMAIL] Attempting to load default gift card background image from: {defaultImagePath}");
                        if (File.Exists(defaultImagePath))
                        {
                            imagePath = defaultImagePath;
                            _logger.LogInformation($"[GIFT CARD EMAIL] Using default gift card background image from: {defaultImagePath}");
                        }
                        else
                        {
                            _logger.LogWarning($"[GIFT CARD EMAIL] Default gift card background image not found at: {defaultImagePath}");
                        }
                    }
                }

                // Convert image to base64 data URI
                if (!string.IsNullOrEmpty(imagePath) && File.Exists(imagePath))
                {
                    var imageBytes = await File.ReadAllBytesAsync(imagePath);
                    var base64Image = Convert.ToBase64String(imageBytes);
                    var imageExtension = Path.GetExtension(imagePath).ToLowerInvariant().TrimStart('.');
                    var mimeType = imageExtension switch
                    {
                        "webp" => "image/webp",
                        "png" => "image/png",
                        "jpg" or "jpeg" => "image/jpeg",
                        "gif" => "image/gif",
                        _ => "image/webp"
                    };
                    backgroundImageDataUri = $"data:{mimeType};base64,{base64Image}";
                    _logger.LogInformation($"[GIFT CARD EMAIL] Successfully converted gift card background image to base64. Size: {imageBytes.Length} bytes, MIME type: {mimeType}, DataURI length: {backgroundImageDataUri.Length} chars");
                }
                else
                {
                    _logger.LogWarning($"[GIFT CARD EMAIL] Gift card background image path is empty or file does not exist. ImagePath: {imagePath ?? "null"}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[GIFT CARD EMAIL] Failed to load gift card background image for email. BackgroundPath from DB: {backgroundPath}, FileUploadPath: {_configuration["FileUpload:Path"]}");
                // Continue without background image if loading fails
            }
            
            _logger.LogInformation($"[GIFT CARD EMAIL] Background image processing complete. HasBackgroundImage: {!string.IsNullOrEmpty(backgroundImageDataUri)}, DataURI length: {backgroundImageDataUri?.Length ?? 0}");

            // Build the gift card HTML with proper email-compatible table structure
            string giftCardHtml = "";
            _logger.LogInformation($"[GIFT CARD EMAIL] Building gift card HTML. Has background image: {!string.IsNullOrEmpty(backgroundImageDataUri)}");
            
            if (!string.IsNullOrEmpty(backgroundImageDataUri))
            {
                _logger.LogInformation($"[GIFT CARD EMAIL] Using custom background image in email template");
                // Escape the data URI for use in HTML attributes (replace single quotes and ensure proper encoding)
                var escapedDataUri = backgroundImageDataUri.Replace("'", "&#39;");
                
                // Use table-based layout with embedded background image for maximum email compatibility
                // Using background attribute (better email support) and CSS as fallback
                giftCardHtml = $@"
            <table width='400' cellpadding='0' cellspacing='0' border='0' style='margin: 0 auto; border-radius: 12px; overflow: hidden; box-shadow: 0 4px 6px rgba(0, 0, 0, 0.1); border: 1px solid #e0e0e0; background-color: #f8f9fa;'>
                <tr>
                    <td background=""{backgroundImageDataUri}"" style=""background-image: url('{backgroundImageDataUri}'); background-size: cover; background-position: center; background-repeat: no-repeat; padding: 0; min-height: 280px;"">
                        <table width='100%' cellpadding='0' cellspacing='0' border='0' style='min-height: 280px;'>
                            <!-- Header -->
                            <tr>
                                <td style='padding: 16px; background: linear-gradient(45deg, rgba(0, 123, 255, 0.2), rgba(0, 86, 179, 0.25)), rgba(255, 255, 255, 0.5); border-bottom: 1px solid rgba(0, 123, 255, 0.2);'>
                                    <table width='100%' cellpadding='0' cellspacing='0' border='0'>
                                        <tr>
                                            <td style='font-size: 20px; color: #333; font-weight: bold; text-shadow: 1px 1px 2px rgba(255, 255, 255, 0.8);'>Dream Cleaning</td>
                                            <td align='right' style='font-size: 24px; font-weight: bold; color: #007bff; text-shadow: 1px 1px 2px rgba(255, 255, 255, 0.8);'>${amount:F2}</td>
                                        </tr>
                                    </table>
                                </td>
                            </tr>
                            <!-- Body -->
                            <tr>
                                <td>
                                    <table width='100%' cellpadding='0' cellspacing='0' border='0' style='height: 220px;'>
                                        <tr>
                                            <td width='30%' style='padding: 16px;'></td>
                                            <td width='70%' style='background-color: rgba(255, 255, 255, 0.74); padding: 16px; vertical-align: top;'>
                                                <p style='font-size: 14px; color: #333; margin: 0 0 8px 0;'>Dear {recipientName},</p>
                                                <div style='font-size: 13px; color: #666; line-height: 1.4; font-style: italic; word-wrap: break-word; margin-bottom: 16px;'>
                                                    <p style='margin: 0;'>{message}</p>
                                                </div>
                                                <table width='100%' cellpadding='0' cellspacing='0' border='0' style='margin-top: auto;'>
                                                    <tr>
                                                        <td align='center' style='font-family: monospace; font-size: 16px; color: #666; letter-spacing: 1px; padding: 10px; background-color: rgba(255, 255, 255, 0.8); border-radius: 4px;'>
                                                            {giftCardCode}
                                                        </td>
                                                    </tr>
                                                </table>
                                            </td>
                                        </tr>
                                    </table>
                                </td>
                            </tr>
                        </table>
                    </td>
                </tr>
            </table>";
            }
            else
            {
                _logger.LogInformation($"[GIFT CARD EMAIL] No background image available, using fallback template without background");
                // Fallback without background image
                giftCardHtml = $@"
            <table width='400' cellpadding='0' cellspacing='0' border='0' style='margin: 0 auto; border-radius: 12px; overflow: hidden; box-shadow: 0 4px 6px rgba(0, 0, 0, 0.1); border: 1px solid #e0e0e0; background-color: #f8f9fa;'>
                <tr>
                    <td style='padding: 16px; background: linear-gradient(45deg, rgba(0, 123, 255, 0.2), rgba(0, 86, 179, 0.25)), rgba(255, 255, 255, 0.5); border-bottom: 1px solid rgba(0, 123, 255, 0.2);'>
                        <table width='100%' cellpadding='0' cellspacing='0' border='0'>
                            <tr>
                                <td style='font-size: 20px; color: #333; font-weight: bold;'>Dream Cleaning</td>
                                <td align='right' style='font-size: 24px; font-weight: bold; color: #007bff;'>${amount:F2}</td>
                            </tr>
                        </table>
                    </td>
                </tr>
                <tr>
                    <td style='padding: 16px;'>
                        <p style='font-size: 14px; color: #333; margin: 0 0 8px 0;'>Dear {recipientName},</p>
                        <div style='font-size: 13px; color: #666; line-height: 1.4; font-style: italic; word-wrap: break-word; margin-bottom: 16px;'>
                            <p style='margin: 0;'>{message}</p>
                        </div>
                        <table width='100%' cellpadding='0' cellspacing='0' border='0'>
                            <tr>
                                <td align='center' style='font-family: monospace; font-size: 16px; color: #666; letter-spacing: 1px; padding: 10px; background-color: rgba(255, 255, 255, 0.8); border-radius: 4px;'>
                                    {giftCardCode}
                                </td>
                            </tr>
                        </table>
                    </td>
                </tr>
            </table>";
            }

            var body = $@"
<!DOCTYPE html>
<html>
<head>
    <meta charset='UTF-8'>
    <meta name='viewport' content='width=device-width, initial-scale=1.0'>
    <style>
        body {{
            font-family: Arial, sans-serif;
            background-color: #f4f4f4;
            margin: 0;
            padding: 0;
            -webkit-font-smoothing: antialiased;
            -moz-osx-font-smoothing: grayscale;
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
            margin: 20px 0;
        }}
        .instructions {{
            background: #e7f3ff;
            padding: 20px;
            border-radius: 8px;
            margin: 30px 0;
            text-align: left;
        }}
        .instructions h3 {{
            margin: 0 0 15px 0;
            color: #007bff;
            text-align: center;
        }}
        .instructions ol {{
            text-align: left;
            color: #666;
            padding-left: 20px;
        }}
        .instructions li {{
            margin-bottom: 8px;
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
        @media only screen and (max-width: 600px) {{
            .container {{
                padding: 10px;
            }}
            .gift-card-container table {{
                width: 100% !important;
                max-width: 350px !important;
            }}
        }}
    </style>
</head>
<body>
    <div class='container'>
        <div class='header'>
            <h1>🎁 You've Received a Gift Card!</h1>
            <p>From {senderName}</p>
        </div>
        
        <div class='content'>
            <h2 style='margin-top: 0;'>Hello, {recipientName}!</h2>
            <p style='font-size: 14px; color: #999; margin-top: 5px;'><a href='mailto:{senderEmail}' style='color: #007bff; text-decoration: none;'><b style='color:#000;'>Send a thank-you message to</b>: {senderEmail}</a></p>
            
            <div class='gift-card-container'>
                {giftCardHtml}
            </div>
            
            <div class='instructions'>
                <h3>How to Use Your Gift Card</h3>
                <ol>
                    <li>Visit <a href='{_configuration["Frontend:Url"]}'>{_configuration["Frontend:Url"]}</a></li>
                    <li>Fill up preferred cleaning services in booking form</li>
                    <li>Enter your gift card code <strong>{giftCardCode}</strong> at 'Promo Code/Gift Card' field</li>
                    <li>The gift card amount will be automatically applied</li>
                </ol>
                <p><strong>Important:</strong> Keep this email safe as it contains your gift card code. You can use this gift card for multiple orders until the balance is depleted.</p>
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
            <p>&copy; {DateTime.UtcNow.Year} Dream Cleaning. All rights reserved.</p>
        </div>
    </div>
</body>
</html>";

            _logger.LogInformation($"[GIFT CARD EMAIL] Email body constructed. Body length: {body.Length} chars, Gift card HTML length: {giftCardHtml.Length} chars");
            
            // Send email with inline image attachment if background image exists
            _logger.LogInformation($"[GIFT CARD EMAIL] Sending email to {recipientEmail}...");
            
            if (!string.IsNullOrEmpty(backgroundImageDataUri))
            {
                // Load the image file to attach as inline image
                string imagePath = null;
                try
                {
                    if (!string.IsNullOrEmpty(backgroundPath))
                    {
                        var fileUploadPath = _configuration["FileUpload:Path"];
                        if (!string.IsNullOrEmpty(fileUploadPath))
                        {
                            var normalizedPath = backgroundPath.TrimStart('/', '\\');
                            var pathParts = normalizedPath.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
                            imagePath = Path.Combine(new[] { fileUploadPath }.Concat(pathParts).ToArray());
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[GIFT CARD EMAIL] Failed to get image path for inline attachment");
                }

                if (!string.IsNullOrEmpty(imagePath) && File.Exists(imagePath))
                {
                    await SendEmailWithInlineImageAsync(recipientEmail, subject, body, imagePath, backgroundImageDataUri);
                }
                else
                {
                    // Fallback to regular email if image file not found
                    _logger.LogWarning($"[GIFT CARD EMAIL] Image file not found for inline attachment, sending email with data URI");
                    await SendEmailAsync(recipientEmail, subject, body);
                }
            }
            else
            {
                _logger.LogWarning($"[GIFT CARD EMAIL] WARNING: No background image - email will be sent without background!");
                await SendEmailAsync(recipientEmail, subject, body);
            }
            
            _logger.LogInformation($"[GIFT CARD EMAIL] Email sent successfully to {recipientEmail}");
        }

        private async Task SendEmailWithInlineImageAsync(string to, string subject, string html, string imagePath, string fallbackDataUri)
        {
            // Check if email sending is disabled in configuration
            var emailEnabled = _configuration.GetValue<bool>("Email:EnableEmailSending", true);

            if (!emailEnabled)
            {
                _logger.LogInformation($"Email sending is disabled. Would have sent email to {to} with subject: {subject}");
                return;
            }

            const int timeoutMs = 30000; // 30 second timeout

            try
            {
                var email = new MimeMessage();
                email.From.Add(new MailboxAddress(
                    _configuration["Email:FromName"],
                    _configuration["Email:FromAddress"]
                ));
                email.To.Add(MailboxAddress.Parse(to));
                email.Subject = subject;

                // Create multipart/related to include inline image
                var multipart = new Multipart("related");

                // Create HTML body part
                var htmlPart = new TextPart(TextFormat.Html) { Text = html };
                multipart.Add(htmlPart);

                // Create inline image attachment with Content-ID
                var imageBytes = await File.ReadAllBytesAsync(imagePath);
                var imageExtension = Path.GetExtension(imagePath).ToLowerInvariant().TrimStart('.');
                var mimeType = imageExtension switch
                {
                    "webp" => "image/webp",
                    "png" => "image/png",
                    "jpg" or "jpeg" => "image/jpeg",
                    "gif" => "image/gif",
                    _ => "image/webp"
                };

                var contentId = $"giftcard-bg-{Guid.NewGuid():N}@dreamcleaning";
                var imagePart = new MimePart(mimeType)
                {
                    Content = new MimeContent(new MemoryStream(imageBytes)),
                    ContentId = contentId,
                    ContentDisposition = new ContentDisposition(ContentDisposition.Inline),
                    FileName = Path.GetFileName(imagePath)
                };

                multipart.Add(imagePart);
                
                // Replace data URI with Content-ID reference in HTML before setting body
                var updatedHtml = html.Replace(fallbackDataUri, $"cid:{contentId}");
                htmlPart.Text = updatedHtml;
                
                email.Body = multipart;

                _logger.LogInformation($"[GIFT CARD EMAIL] Using inline image attachment with Content-ID: {contentId}");

                using var smtp = new SmtpClient();
                smtp.Timeout = timeoutMs;
                using var cts = new CancellationTokenSource(timeoutMs);

                await smtp.ConnectAsync(
                    _configuration["Email:SmtpHost"],
                    int.Parse(_configuration["Email:SmtpPort"]),
                    SecureSocketOptions.StartTls,
                    cts.Token
                );

                await smtp.AuthenticateAsync(
                    _configuration["Email:SmtpUser"],
                    _configuration["Email:SmtpPassword"],
                    cts.Token
                );

                await smtp.SendAsync(email, cts.Token);
                await smtp.DisconnectAsync(true, cts.Token);

                _logger.LogInformation($"Email with inline image sent successfully to {to}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to send email with inline image to {to}");
                // Fallback to regular email
                await SendEmailAsync(to, subject, html);
            }
        }

        public async Task SendGiftCardSenderConfirmationAsync(string senderEmail, string senderName,
            string recipientName, string recipientEmail, string giftCardCode,
            decimal amount, string message)
        {
            var subject = $"Gift Card Purchase Confirmation - Dream Cleaning";

            var body = $@"
    <!DOCTYPE html>
    <html>
    <head>
        <style>
            body {{
                font-family: Arial, sans-serif;
                color: #333;
                line-height: 1.6;
                max-width: 600px;
                margin: 0 auto;
            }}
            .container {{
                background-color: #f8f9fa;
                padding: 20px;
                border-radius: 10px;
                margin: 20px 0;
            }}
            .header {{
                background-color: #28a745;
                color: white;
                padding: 20px;
                border-radius: 10px 10px 0 0;
                text-align: center;
            }}
            .content {{
                background-color: white;
                padding: 30px;
                border-radius: 0 0 10px 10px;
            }}
            .gift-card-details {{
                background-color: #e8f5e9;
                padding: 20px;
                border-radius: 8px;
                margin: 20px 0;
                border-left: 4px solid #28a745;
            }}
            .code-display {{
                background-color: #f5f5f5;
                padding: 15px;
                border-radius: 5px;
                text-align: center;
                font-family: monospace;
                font-size: 18px;
                letter-spacing: 2px;
                margin: 15px 0;
                border: 2px dashed #28a745;
            }}
            .footer {{
                text-align: center;
                color: #666;
                font-size: 14px;
                margin-top: 30px;
                padding-top: 20px;
                border-top: 1px solid #eee;
            }}
        </style>
    </head>
    <body>
        <div class='container'>
            <div class='header'>
                <h2>Thank You for Your Gift Card Purchase!</h2>
            </div>
            
            <div class='content'>
                <p>Dear {senderName},</p>
                
                <p>Thank you for purchasing a Dream Cleaning gift card! Your thoughtful gift has been successfully processed and sent to <strong>{recipientName}</strong>.</p>
                
                <div class='gift-card-details'>
                    <h3 style='margin-top: 0;'>Gift Card Details:</h3>
                    <p><strong>Amount:</strong> ${amount:F2}</p>
                    <p><strong>Recipient:</strong> {recipientName} ({recipientEmail})</p>
                    <p><strong>Gift Card Code:</strong></p>
                    <div class='code-display'>{giftCardCode}</div>
                    {(!string.IsNullOrEmpty(message) ? $@"<p><strong>Your Message:</strong><br/><em>""{message}""</em></p>" : "")}
                </div>
                
                <p><strong>What happens next?</strong></p>
                <ul>
                    <li>{recipientName} has received an email with the gift card details</li>
                    <li>They can use the code <strong>{giftCardCode}</strong> when booking any Dream Cleaning service</li>
                    <li>The gift card never expires and can be used for multiple bookings until the balance is depleted</li>
                </ul>
                
                <p>Please save this email for your records. The gift card code above can be shared with the recipient if they haven't received their email.</p>
                
                <div class='footer'>
                    <p>If you have any questions about your gift card purchase, please contact us at<br/>
                    {_configuration["Email:FromAddress"]} or call (929) 930-1525</p>
                    <p>&copy; {DateTime.UtcNow.Year} Dream Cleaning. All rights reserved.</p>
                </div>
            </div>
        </div>
    </body>
    </html>
    ";

            await SendEmailAsync(senderEmail, subject, body);
        }

        public async Task SendContactFormEmailAsync(string to, string subject, string html)
        {
            await SendEmailAsync(to, subject, html);
        }

        public async Task SendCleanerAssignmentNotificationAsync(string email, string cleanerName, int orderId, bool sendCopyToAdmin = false)
        {
            // Find the order by ID to ensure we get the correct order
            var order = await _context.Orders
                .Include(o => o.ServiceType)
                .Include(o => o.OrderServices)
                    .ThenInclude(os => os.Service)
                .Include(o => o.OrderExtraServices)
                    .ThenInclude(oes => oes.ExtraService)
                .Include(o => o.OrderCleaners)
                    .ThenInclude(oc => oc.Cleaner)
                .FirstOrDefaultAsync(o => o.Id == orderId);

            if (order == null)
            {
                _logger.LogError($"Order {orderId} not found when sending cleaner assignment notification to {email}");
                return;
            }

            // Get cleaner-specific additional instructions
            var cleanerAdditionalInstructions = order.OrderCleaners
                .FirstOrDefault(oc => oc.Cleaner.Email == email)?.TipsForCleaner;

            // Check if there's a cleaners service to determine if we should divide duration
            bool hasCleanersService = order.OrderServices.Any(os =>
                os.Service.ServiceKey != null && os.Service.ServiceKey.ToLower().Contains("cleaner"));

            // Calculate duration per cleaner
            decimal durationPerCleaner = 0;
            string formattedDuration = "";

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

            // Build full address string
            var fullAddressParts = new List<string>();
            if (!string.IsNullOrEmpty(order.ServiceAddress))
                fullAddressParts.Add(order.ServiceAddress);
            if (!string.IsNullOrEmpty(order.AptSuite))
                fullAddressParts.Add(order.AptSuite);
            if (!string.IsNullOrEmpty(order.City))
                fullAddressParts.Add(order.City);
            if (!string.IsNullOrEmpty(order.State))
                fullAddressParts.Add(order.State);
            if (!string.IsNullOrEmpty(order.ZipCode))
                fullAddressParts.Add(order.ZipCode);

            var fullAddress = fullAddressParts.Any() 
                ? string.Join(", ", fullAddressParts) 
                : (order.ApartmentName ?? "Address provided separately");

            var subject = "New Cleaning Job Assignment - Dream Cleaning";
            var body = $@"
        <h2>Hi {cleanerName},</h2>
        <p>You have been assigned to a new cleaning job with complete details below:</p>
        
        <div style='background: #f8f9fa; padding: 20px; border-radius: 8px; margin: 20px 0; border-left: 4px solid #28a745;'>
            <h3>📋 Job Overview</h3>
            <p><strong>Order Number:</strong> #{order.Id}</p>
            <p><strong>Service Type:</strong> {order.ServiceType.Name}</p>
            <p><strong>Date:</strong> {order.ServiceDate:dddd, MMMM dd, yyyy}</p>
            <p><strong>Time:</strong> {FormatTimeForEmail(order.ServiceTime)}</p>
            <p><strong>Duration:</strong> {formattedDuration}{(hasCleanersService ? "" : " per cleaner")}</p>
            <p><strong>Team Size:</strong> {order.MaidsCount} cleaner(s)</p>
        </div>
        
        <div style='background: #e7f3ff; padding: 20px; border-radius: 8px; margin: 20px 0; border-left: 4px solid #007bff;'>
            <h3>👥 Client Information</h3>
            <p><strong>Client Name:</strong> {order.ContactFirstName} {order.ContactLastName}</p>
            <p><strong>Entry Method / How to get in:</strong> {(string.IsNullOrEmpty(order.EntryMethod) ? "To be confirmed" : order.EntryMethod)}</p>
        </div>
        
        <div style='background: #fff3cd; padding: 20px; border-radius: 8px; margin: 20px 0; border-left: 4px solid #ffc107;'>
            <h3>📍 Complete Service Address</h3>
            <p><strong>Address:</strong> {fullAddress}</p>
            {(!string.IsNullOrEmpty(order.ApartmentName) ? $"<p><strong>Apartment Name:</strong> {order.ApartmentName}</p>" : "")}
        </div>
        
        {(order.OrderServices.Any() ? $@"
        <div style='background: #f8f9fa; padding: 20px; border-radius: 8px; margin: 20px 0;'>
            <h3>🧹 Services to Perform</h3>
            <ul style='margin: 10px 0; padding-left: 20px;'>
                {string.Join("", order.OrderServices.Select(os => $"<li style='margin: 5px 0;'>{FormatServiceForEmail(os, order.Id)}</li>"))}
            </ul>
        </div>" : "")}
        
        {(order.OrderExtraServices.Any() ? $@"
        <div style='background: #f0f8ff; padding: 20px; border-radius: 8px; margin: 20px 0;'>
            <h3>✨ Extra Services</h3>
            <ul style='margin: 10px 0; padding-left: 20px;'>
                {string.Join("", order.OrderExtraServices.Select(oes =>
                            $"<li style='margin: 5px 0;'>{oes.ExtraService.Name} - Quantity: {oes.Quantity}" +
                            (oes.Hours > 0 ? $", Hours: {oes.Hours:0.#}" : "") + "</li>"))}
            </ul>
        </div>" : "")}

        {(order.Tips > 0 ? $@"
        <div style='background: #d4edda; padding: 20px; border-radius: 8px; margin: 20px 0; border-left: 4px solid #28a745;'>
            <h3>💰 Tips for Cleaning Team</h3>
            <p style='margin: 0; font-size: 1.2em; font-weight: bold; color: #155724; text-align: center;'>${order.Tips:F2}</p>
        </div>" : "")}
        
        {(!string.IsNullOrEmpty(order.SpecialInstructions) ? $@"
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

            // Optionally send one copy of the same email to one admin address (so admin gets exactly what the cleaner got, no second/different email)
            if (sendCopyToAdmin)
            {
                var adminCopyTo = "hello@dreamcleaningnearme.com";
                try
                {
                    await SendEmailAsync(adminCopyTo, subject, body);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Failed to send cleaner assignment copy to admin {adminCopyTo}");
                }
            }
        }

        public async Task SendAdminCleanerAssignmentNotificationAsync(string cleanerEmail, string cleanerName,
            DateTime serviceDate, string serviceTime, string formattedDuration, string fullAddress)
        {
            var subject = $"Cleaner Assignment Notification - {cleanerName}";
            var formattedTime = FormatTimeForEmail(TimeSpan.Parse(serviceTime));
            
            var body = $@"
        <h2>Cleaner Assignment Notification</h2>
        <p>A cleaner has been assigned to a cleaning job. Below are the details that were sent to the cleaner:</p>
        
        <div style='background: #f8f9fa; padding: 20px; border-radius: 8px; margin: 20px 0; border-left: 4px solid #007bff;'>
            <h3>👤 Cleaner Information</h3>
            <p><strong>Cleaner Name:</strong> {cleanerName}</p>
            <p><strong>Cleaner Email:</strong> {cleanerEmail}</p>
        </div>
        
        <div style='background: #e7f3ff; padding: 20px; border-radius: 8px; margin: 20px 0; border-left: 4px solid #007bff;'>
            <h3>📅 Scheduled Cleaning Details</h3>
            <p><strong>Date:</strong> {serviceDate:dddd, MMMM dd, yyyy}</p>
            <p><strong>Time:</strong> {formattedTime}</p>
            <p><strong>Duration (Hours sent to cleaner):</strong> {formattedDuration}</p>
        </div>
        
        <div style='background: #fff3cd; padding: 20px; border-radius: 8px; margin: 20px 0; border-left: 4px solid #ffc107;'>
            <h3>📍 Service Address</h3>
            <p><strong>Address:</strong> {fullAddress}</p>
        </div>
        
        <div style='background: #f8f9fa; padding: 20px; border-radius: 8px; margin: 20px 0; text-align: center;'>
            <p><em>This is an automated notification sent when a cleaner is assigned to an order.</em></p>
        </div>
        
        <br/>
        <p>Best regards,<br/>Dream Cleaning System</p>
    ";

            // Send to both admin email addresses
            var adminEmails = new[] { "hello@dreamcleaningnearme.com", "dreamcleaninginfos@gmail.com" };
            
            foreach (var adminEmail in adminEmails)
            {
                try
                {
                    await SendEmailAsync(adminEmail, subject, body);
                }
                catch (Exception ex)
                {
                    // Log error but continue to try sending to other admin emails
                    _logger.LogError(ex, $"Failed to send admin notification to {adminEmail}");
                }
            }
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
            // Round to nearest 15 minutes (same as frontend)
            var roundedMinutes = (int)Math.Round(minutes / 15.0) * 15;
            var hours = roundedMinutes / 60;
            var mins = roundedMinutes % 60;

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

        public async Task SendEmailChangeVerificationAsync(string newEmail, string firstName, string verificationLink, string currentEmail)
        {
            var subject = "Verify Your New Email Address - Dream Cleaning";
            var body = $@"
        <h2>Hi {firstName},</h2>
        <p>You have requested to change your email address for your Dream Cleaning account.</p>
        <p><strong>Current email:</strong> {currentEmail}</p>
        <p><strong>New email:</strong> {newEmail}</p>
        <p>To complete this change, please click the button below to verify your new email address:</p>
        <p style='margin: 30px 0;'>
            <a href='{verificationLink}' 
               style='background-color: #4CAF50; color: white; padding: 14px 20px; 
                      text-decoration: none; border-radius: 4px; display: inline-block;'>
                Verify New Email
            </a>
        </p>
        <p>Or copy and paste this link into your browser:</p>
        <p>{verificationLink}</p>
        <p><strong>Important:</strong> This link will expire in 1 hour for security reasons.</p>
        <p>If you didn't request this email change, please ignore this email and contact our support team immediately.</p>
        <br/>
        <p>Best regards,<br/>Dream Cleaning Team</p>
    ";

            await SendEmailAsync(newEmail, subject, body);
        }

        public async Task SendEmailChangeConfirmationAsync(string email, string firstName)
        {
            var subject = "Email Address Successfully Changed - Dream Cleaning";
            var body = $@"
        <h2>Hi {firstName},</h2>
        <p>Your email address has been successfully changed!</p>
        <p>You can now use this email address (<strong>{email}</strong>) to log in to your Dream Cleaning account.</p>
        <p>If you didn't make this change, please contact our support team immediately.</p>
        <br/>
        <p>For security reasons, all active sessions have been logged out. Please log in again with your new email address.</p>
        <br/>
        <p>Best regards,<br/>Dream Cleaning Team</p>
    ";

            await SendEmailAsync(email, subject, body);
        }

        public async Task SendCustomerReminderNotificationAsync(string email, string customerName,
            DateTime serviceDate, string serviceTime, string serviceTypeName, string address, bool isDaysBefore)
        {
            var timeFrame = isDaysBefore ? "in 2 days" : "in 2 hours";
            var subject = $"Upcoming Cleaning Service Reminder - Service {timeFrame}";

            var body = $@"
        <h2>Hi {customerName},</h2>
        <p>This is a friendly reminder that your cleaning service is scheduled for <strong>{timeFrame}</strong>:</p>
        
        <div style='background: #e8f5e9; padding: 20px; border-radius: 8px; margin: 20px 0; border-left: 4px solid #4caf50;'>
            <h3>Service Details:</h3>
            <p><strong>Service Type:</strong> {serviceTypeName}</p>
            <p><strong>Date:</strong> {serviceDate:dddd, MMMM dd, yyyy}</p>
            <p><strong>Time:</strong> {serviceTime}</p>
            <p><strong>Address:</strong> {address}</p>
        </div>
        
        {(isDaysBefore ?
                    @"<p>Please ensure that your home is ready for our cleaning team:</p>
            <ul style='margin-left: 20px;'>
                <li>Clear any personal items from surfaces that need cleaning</li>
                <li>Secure any fragile or valuable items</li>
                <li>Ensure our team has access to the property</li>
                <li>Have any special instructions ready</li>
            </ul>" :
                    @"<p>Our cleaning team will arrive soon! Please make sure:</p>
            <ul style='margin-left: 20px;'>
                <li>Someone is available to let the team in (if required)</li>
                <li>The property is accessible</li>
                <li>Any pets are secured</li>
            </ul>")}
        
        <p>If you need to make any changes or have questions, please contact us as soon as possible.</p>
        
        <div style='background: #f5f5f5; padding: 15px; border-radius: 8px; margin: 20px 0;'>
            <p><strong>Need to make changes?</strong></p>
            <p>Log in to your account to view or modify your order, or contact our support team.</p>
        </div>
        
        <br/>
        <p>Thank you for choosing Dream Cleaning!</p>
        <p>Best regards,<br/>Dream Cleaning Team</p>
    ";

            await SendEmailAsync(email, subject, body);
        }

        public async Task SendCustomerBookingConfirmationAsync(string email, string customerName,
            DateTime serviceDate, string serviceTime, string serviceTypeName, string address, int orderId)
        {
            var subject = "Booking Confirmed - Dream Cleaning Service Scheduled";

            var body = $@"
        <h2>Hi {customerName},</h2>
        <p>Thank you for choosing Dream Cleaning! Your booking has been confirmed and payment processed successfully.</p>
        
        <div style='background: #e3f2fd; padding: 20px; border-radius: 8px; margin: 20px 0; border-left: 4px solid #2196f3;'>
            <h3>Booking Details:</h3>
            <p><strong>Order Number:</strong> #{orderId}</p>
            <p><strong>Service Type:</strong> {serviceTypeName}</p>
            <p><strong>Date:</strong> {serviceDate:dddd, MMMM dd, yyyy}</p>
            <p><strong>Time:</strong> {serviceTime}</p>
            <p><strong>Address:</strong> {address}</p>
        </div>
        
        <div style='background: #fff3e0; padding: 15px; border-radius: 8px; margin: 20px 0;'>
            <h3>What's Next?</h3>
            <ul style='margin-left: 20px;'>
                <li>You'll receive reminder emails 2 days and 2 hours before your service</li>
                <li>Our professional cleaning team will be assigned to your order</li>
                <li>You can view and manage your booking in your account</li>
            </ul>
        </div>
        
        <div style='background: #f5f5f5; padding: 15px; border-radius: 8px; margin: 20px 0;'>
            <p><strong>Questions or need to make changes?</strong></p>
            <p>Log in to your account to view your order details or contact our support team.</p>
        </div>
        
        <br/>
        <p>We're excited to serve you!</p>
        <p>Best regards,<br/>Dream Cleaning Team</p>
    ";

            await SendEmailAsync(email, subject, body);
        }

        private void AddPhotoAttachments(MimeMessage email, List<PhotoUploadDto> photos)
        {
            if (photos == null || !photos.Any()) return;

            var multipart = new Multipart("mixed");

            // Add the HTML body first
            var bodyPart = (MimePart)email.Body;
            multipart.Add(bodyPart);

            // Add photo attachments
            int photoCount = 0;
            foreach (var photo in photos.Take(12)) // Limit to 12 photos
            {
                try
                {
                    photoCount++;
                    var attachment = new MimePart(photo.ContentType ?? "image/jpeg")
                    {
                        Content = new MimeContent(new MemoryStream(Convert.FromBase64String(photo.Base64Data))),
                        ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
                        ContentTransferEncoding = ContentEncoding.Base64,
                        FileName = string.IsNullOrEmpty(photo.FileName) ? $"photo_{photoCount}.jpg" : photo.FileName
                    };

                    multipart.Add(attachment);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Failed to attach photo: {photo.FileName}");
                }
            }

            email.Body = multipart;
        }

        public async Task SendCompanyBookingNotificationAsync(string contactFirstName, string contactLastName, string contactEmail, string contactPhone, DateTime serviceDate,
            string serviceTime, string serviceTypeName, string serviceAddress, string aptSuite, string city, string state, string zipCode, int orderId, List<PhotoUploadDto> uploadedPhotos = null)
        {
            var companyEmail = _configuration["Email:CompanyEmail"] ?? _configuration["Email:FromAddress"];
            var subject = $"New Booking Order #{orderId} - {contactFirstName} {contactLastName}";

            var photoInfo = "";
            if (uploadedPhotos != null && uploadedPhotos.Any())
            {
                photoInfo = $@"
                <div style='background: #e8f5e9; padding: 15px; border-radius: 8px; margin: 20px 0;'>
                    <p><strong>📷 Customer Photos:</strong></p>
                    <p>The customer has uploaded {uploadedPhotos.Count} photo(s) showing their cleaning needs. See attached files.</p>
                </div>";
            }

            var fullAddress = $"{serviceAddress}{(!string.IsNullOrEmpty(aptSuite) ? $", {aptSuite}" : "")}, {city}, {state} {zipCode}";

            var body = $@"
                <h2>New Booking Received</h2>
                
                <div style='background: #e3f2fd; padding: 20px; border-radius: 8px; margin: 20px 0; border-left: 4px solid #2196f3;'>
                    <h3>Customer Information:</h3>
                    <p><strong>Name:</strong> {contactFirstName} {contactLastName}</p>
                    <p><strong>Email:</strong> <a href='mailto:{contactEmail}'>{contactEmail}</a></p>
                    <p><strong>Phone:</strong> {contactPhone}</p>
                </div>
                
                <div style='background: #f5f5f5; padding: 20px; border-radius: 8px; margin: 20px 0;'>
                    <h3>Service Details:</h3>
                    <p><strong>Order Number:</strong> #{orderId}</p>
                    <p><strong>Service Type:</strong> {serviceTypeName}</p>
                    <p><strong>Date:</strong> {serviceDate:dddd, MMMM dd, yyyy}</p>
                    <p><strong>Time:</strong> {serviceTime}</p>
                </div>
                
                <div style='background: #fff3e0; padding: 20px; border-radius: 8px; margin: 20px 0;'>
                    <h3>Service Address:</h3>
                    <p>{fullAddress}</p>
                </div>
                
                {photoInfo}
                
                <p style='margin-top: 30px; color: #666; font-size: 14px;'>
                    <strong>Booking completed on:</strong> {DateTime.UtcNow:MMMM dd, yyyy at h:mm tt}
                </p>
            ";

            // Create the email message
            var mimeMessage = new MimeMessage();
            mimeMessage.From.Add(new MailboxAddress(
                _configuration["Email:FromName"],
                _configuration["Email:FromAddress"]
            ));
            mimeMessage.To.Add(MailboxAddress.Parse(companyEmail));
            mimeMessage.Subject = subject;
            mimeMessage.Body = new TextPart(TextFormat.Html) { Text = body };

            // Add photo attachments if any
            if (uploadedPhotos != null && uploadedPhotos.Any())
            {
                AddPhotoAttachments(mimeMessage, uploadedPhotos);
            }

            // Send the email using existing SendEmailAsync method
            const int timeoutMs = 30000;
            using var smtp = new SmtpClient();
            smtp.Timeout = timeoutMs;
            using var cts = new CancellationTokenSource(timeoutMs);

            await smtp.ConnectAsync(
                _configuration["Email:SmtpHost"],
                int.Parse(_configuration["Email:SmtpPort"]),
                SecureSocketOptions.StartTls,
                cts.Token
            );

            await smtp.AuthenticateAsync(
                _configuration["Email:SmtpUser"],
                _configuration["Email:SmtpPassword"],
                cts.Token
            );

            await smtp.SendAsync(mimeMessage, cts.Token);
            await smtp.DisconnectAsync(true, cts.Token);

            _logger.LogInformation($"Company notification email sent for order {orderId}");
        }

        public async Task SendPollSubmissionEmailWithPhotosAsync(string toEmail, string subject, string htmlBody, List<PhotoUploadDto> uploadedPhotos = null)
        {
            try
            {
                var email = new MimeMessage();
                email.From.Add(new MailboxAddress(
                    _configuration["Email:FromName"],
                    _configuration["Email:FromAddress"]
                ));
                email.To.Add(MailboxAddress.Parse(toEmail));
                email.Subject = subject;
                email.Body = new TextPart(TextFormat.Html) { Text = htmlBody };

                // Add photo attachments if any
                if (uploadedPhotos != null && uploadedPhotos.Any())
                {
                    AddPhotoAttachments(email, uploadedPhotos);
                }

                // Send using the existing SMTP logic
                const int timeoutMs = 30000;
                using var smtp = new SmtpClient();
                smtp.Timeout = timeoutMs;
                using var cts = new CancellationTokenSource(timeoutMs);

                await smtp.ConnectAsync(
                    _configuration["Email:SmtpHost"],
                    int.Parse(_configuration["Email:SmtpPort"]),
                    SecureSocketOptions.StartTls,
                    cts.Token
                );

                await smtp.AuthenticateAsync(
                    _configuration["Email:SmtpUser"],
                    _configuration["Email:SmtpPassword"],
                    cts.Token
                );

                await smtp.SendAsync(email, cts.Token);
                await smtp.DisconnectAsync(true, cts.Token);

                _logger.LogInformation($"Poll submission email sent successfully to {toEmail}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to send poll submission email to {toEmail}");
                throw;
            }
        }

        public async Task SendOrderUpdateNotificationAsync(int orderId, string customerEmail, decimal additionalAmount)
        {
            try
            {
                var companyEmail = _configuration["Email:CompanyEmail"] ?? "notifications@dreamcleaning.com";
                var subject = $"Order #{orderId} Updated";

                var body = $@"
            <html>
            <body style='font-family: Arial, sans-serif;'>
                <h3>Order Update Notification</h3>
                <p>Order #{orderId} has been updated.</p>
                <p><strong>Customer Email:</strong> {customerEmail}</p>
                <p><strong>Additional Amount:</strong> ${additionalAmount:F2}</p>
                <p><strong>Updated at:</strong> {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}</p>
            </body>
            </html>";

                await SendEmailAsync(companyEmail, subject, body);

                _logger.LogInformation($"Order update notification sent for Order #{orderId}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to send order update notification for Order #{orderId}");
                // Don't throw - we don't want email failures to break the order update
            }
        }

        public async Task SendPaymentReminderEmailAsync(string email, string customerName, decimal amount, int orderId, string orderLink)
        {
            try
            {
                var subject = $"Confirm Your Payment - Order #{orderId}";
                var amountFormatted = amount.ToString("C");

                var body = $@"
            <html>
            <head>
                <style>
                    body {{ font-family: Arial, sans-serif; line-height: 1.6; color: #333; }}
                    .container {{ max-width: 600px; margin: 0 auto; padding: 20px; }}
                    .header {{ background-color: #4CAF50; color: white; padding: 20px; text-align: center; border-radius: 5px 5px 0 0; }}
                    .content {{ background-color: #f9f9f9; padding: 30px; border-radius: 0 0 5px 5px; }}
                    .amount-box {{ background-color: #fff; border: 2px solid #4CAF50; border-radius: 5px; padding: 20px; text-align: center; margin: 20px 0; }}
                    .amount {{ font-size: 32px; font-weight: bold; color: #4CAF50; }}
                    .button {{ background-color: #4CAF50; color: white !important; padding: 14px 30px; text-decoration: none; border-radius: 4px; display: inline-block; margin: 20px 0; font-size: 16px; font-weight: bold; }}
                    .button a {{ color: white !important; }}
                    .footer {{ text-align: center; padding: 20px; color: #666; font-size: 12px; margin-top: 20px; }}
                </style>
            </head>
            <body>
                <div class='container'>
                    <div class='header'>
                        <h1 style='margin: 0;'>✨ Payment Confirmation Needed</h1>
                    </div>
                    <div class='content'>
                        <h2>Hi {customerName}!</h2>
                        <p>Great news! Your cleaning order has been created and is ready to be scheduled. To confirm your booking, please complete your payment.</p>
                        
                        <div class='amount-box'>
                            <div style='color: #666; font-size: 14px; margin-bottom: 5px;'>Amount Due</div>
                            <div class='amount'>{amountFormatted}</div>
                        </div>

                        <p style='text-align: center;'>
                            <a href='{orderLink}' class='button' style='color: white !important; text-decoration: none;'>Confirm Payment & Schedule Order</a>
                        </p>

                        <p style='font-size: 14px; color: #666;'>Or copy and paste this link into your browser:</p>
                        <p style='font-size: 12px; word-break: break-all; color: #007bff;'>{orderLink}</p>

                        <p style='margin-top: 30px; padding-top: 20px; border-top: 1px solid #e0e0e0;'>
                            <strong>Order Details:</strong><br/>
                            Order ID: #{orderId}<br/>
                            Amount: {amountFormatted}
                        </p>

                        <p style='color: #666; font-size: 14px; margin-top: 30px;'>
                            Your order will be scheduled once payment is confirmed. If you have any questions, feel free to reach out to us!
                        </p>
                    </div>
                    <div class='footer'>
                        <p>Thank you for choosing Dream Cleaning!</p>
                        <p>&copy; {DateTime.UtcNow.Year} Dream Cleaning. All rights reserved.</p>
                    </div>
                </div>
            </body>
            </html>";

                await SendEmailAsync(email, subject, body);
                _logger.LogInformation($"Payment reminder email sent successfully to {email} for Order #{orderId}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to send payment reminder email to {email} for Order #{orderId}");
                // Don't throw - we don't want email failures to break the booking creation
            }
        }
    }
}