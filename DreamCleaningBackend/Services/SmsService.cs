using DreamCleaningBackend.Services.Interfaces;
using RingCentral;

namespace DreamCleaningBackend.Services
{
    public class SmsService : ISmsService
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<SmsService> _logger;

        public SmsService(IConfiguration configuration, ILogger<SmsService> logger)
        {
            _configuration = configuration;
            _logger = logger;
        }

        public bool IsSmsEnabled()
        {
            var enabled = _configuration.GetValue<bool>("RingCentral:EnableSmsSending", false);
            if (!enabled) return false;
            var from = _configuration["RingCentral:FromNumber"];
            if (string.IsNullOrWhiteSpace(from)) return false;
            var clientId = _configuration["RingCentral:ClientId"];
            var clientSecret = _configuration["RingCentral:ClientSecret"];
            var server = _configuration["RingCentral:ServerUrl"];
            if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret) || string.IsNullOrWhiteSpace(server))
                return false;
            var jwt = _configuration["RingCentral:JwtToken"];
            return !string.IsNullOrWhiteSpace(jwt);
        }

        private async Task<RestClient> GetRingCentralClientAsync()
        {
            var clientId = _configuration["RingCentral:ClientId"];
            var clientSecret = _configuration["RingCentral:ClientSecret"];
            var server = _configuration["RingCentral:ServerUrl"] ?? "https://platform.ringcentral.com";

            var rc = new RestClient(clientId, clientSecret, server);

            var jwt = _configuration["RingCentral:JwtToken"];
            if (string.IsNullOrWhiteSpace(jwt))
                throw new InvalidOperationException("RingCentral:JwtToken must be set. Generate a JWT in RingCentral Developer Portal for your app.");
            await rc.Authorize(jwt);

            return rc;
        }

        public async Task SendSmsAsync(string toNumber, string message)
        {
            if (!IsSmsEnabled())
            {
                _logger.LogInformation("SMS sending is disabled or not configured. Would have sent to {To}: {Msg}", toNumber, message?.Length > 50 ? message[..50] + "..." : message);
                return;
            }

            var normalized = NormalizePhoneToE164(toNumber);
            if (string.IsNullOrEmpty(normalized))
            {
                _logger.LogWarning("Cannot send SMS: invalid or empty phone number: {Phone}", toNumber);
                return;
            }

            // SMS max 160 chars for single segment; RingCentral may concatenate. Truncate to avoid huge costs.
            var text = message?.Length > 1600 ? message[..1600] + "..." : (message ?? "");

            RestClient? rc = null;
            try
            {
                rc = await GetRingCentralClientAsync();
                var fromNumber = _configuration["RingCentral:FromNumber"];

                var parameters = new CreateSMSMessage
                {
                    from = new MessageStoreCallerInfoRequest { phoneNumber = fromNumber },
                    to = new[] { new MessageStoreCallerInfoRequest { phoneNumber = normalized } },
                    text = text
                };

                await rc.Restapi().Account().Extension().Sms().Post(parameters);
                _logger.LogInformation("SMS sent successfully to {To}", normalized);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send SMS to {To}", toNumber);
                throw;
            }
            finally
            {
                if (rc != null)
                {
                    try { await rc.Revoke(); } catch (Exception ex) { _logger.LogDebug(ex, "Revoke after SMS"); }
                }
            }
        }

        public async Task SendBookingConfirmationSmsAsync(string phoneNumber, string customerName, DateTime serviceDate, string serviceTime,
            bool hasCleaningSupplies, bool isDeepCleaning, bool isCustomServiceType)
        {
            var firstName = customerName.Split(' ').FirstOrDefault() ?? customerName;

            // Custom service types don't use the regular cleaning-supplies workflow.
            if (isCustomServiceType)
            {
                hasCleaningSupplies = true;
            }

            // Always required items (always included, even if cleaning supplies selected)
            var items = new List<string>
            {
                "Paper towels",
                "Garbage bags",
                "Broom or vacuum cleaner"
            };

            // If cleaning supplies were NOT selected, customer must also have these items ready.
            if (!hasCleaningSupplies)
            {
                var zep = isDeepCleaning
                    ? "Zep liquids: Green, Floor (or similar), Oven Cleaner (or similar)"
                    : "Zep liquids: Green, Floor (or similar)";

                items.Add(zep);
                items.Add("Windex liquid (or similar)");
                items.Add("Cleaning cloths, Sponge and Mop");
            }

            var msg =
                $"Hi {firstName}! Your Dream Cleaning appointment is confirmed for {serviceDate:MMM dd} at {serviceTime}." +
                $"\nPlease provide the following items:" +
                $"\n- {string.Join("\n- ", items)}" +
                $"\nAll changes, requests, and concerns must go through Dream Cleaning. Do not make any arrangements directly with your cleaner." +
                $"\nBy booking with us, you agree to our Privacy Policy: https://dreamcleaningnearme.com/privacy-policy. We're committed to keeping your information safe." +
                $"\nReply STOP to opt-out.";
            await SendSmsAsync(phoneNumber, msg);
        }

        public async Task SendCleanerAssignmentSmsAsync(string phoneNumber, string cleanerName, DateTime serviceDate, string address)
        {
            var msg = $"Hi {cleanerName}! You've been assigned a cleaning job on {serviceDate:MMM dd} at {address}. Check your dashboard for details.";
            await SendSmsAsync(phoneNumber, msg);
        }

        public async Task SendPaymentReminderSmsAsync(string phoneNumber, string customerName, decimal amount, int orderId, string orderLink)
        {
            // Create professional SMS message
            var amountFormatted = amount.ToString("C");
            // Extract first name only for greeting
            var firstName = customerName.Split(' ').FirstOrDefault() ?? customerName;
            var msg = $"Hi {firstName}, thank you for choosing Dream Cleaning! To finalize order #{orderId}, please confirm your payment of {amountFormatted} here: {orderLink}\nWe look forward to making your space shine!";
            await SendSmsAsync(phoneNumber, msg);
        }

        public async Task SendAdditionalPaymentRequiredSmsAsync(string phoneNumber, string customerName, decimal additionalAmount, int orderId, string paymentLink)
        {
            var amountFormatted = additionalAmount.ToString("C");
            var firstName = customerName.Split(' ').FirstOrDefault() ?? customerName;
            var msg = $"Hi {firstName}, your order #{orderId} was updated. An additional payment of {amountFormatted} is required. Pay here: {paymentLink}";
            await SendSmsAsync(phoneNumber, msg);
        }

        public async Task SendAdditionalPaymentReminderSmsAsync(string phoneNumber, string customerName, decimal additionalAmount, int orderId, string paymentLink)
        {
            var amountFormatted = additionalAmount.ToString("C");
            var firstName = customerName.Split(' ').FirstOrDefault() ?? customerName;
            var msg = $"Hi {firstName}, friendly reminder: you have an unpaid amount of {amountFormatted} for order #{orderId}. Pay here when ready: {paymentLink}";
            await SendSmsAsync(phoneNumber, msg);
        }

        public async Task SendReviewRequestSmsAsync(string phoneNumber, string customerName)
        {
            var firstName = customerName.Split(' ').FirstOrDefault() ?? customerName;
            var msg = $"Hi {firstName}! Thank you so much for choosing Dream Cleaning — we hope your space feels fresh and spotless! ✨ If you're happy with the service, we'd truly appreciate a quick review. It only takes a moment and means the world to our small team!\n\nhttps://g.page/r/CSmN7-QdmiyoEAI/review\n\nThank you and have a wonderful day! 😊";
            await SendSmsAsync(phoneNumber, msg);
        }

        /// <summary>
        /// Normalize US/NA phone to E.164 (e.g. +19295551234). Handles 10-digit, 11-digit with leading 1, or already E.164.
        /// </summary>
        public static string? NormalizePhoneToE164(string? phone)
        {
            if (string.IsNullOrWhiteSpace(phone)) return null;
            var digits = new string(phone.Where(char.IsDigit).ToArray());
            if (digits.Length == 10)
                return "+1" + digits;
            if (digits.Length == 11 && digits.StartsWith('1'))
                return "+" + digits;
            if (digits.Length >= 10 && digits.Length <= 15)
                return "+" + digits;
            return null;
        }
    }
}
