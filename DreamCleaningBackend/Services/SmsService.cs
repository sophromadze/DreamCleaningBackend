using DreamCleaningBackend.Data;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using RingCentral;

namespace DreamCleaningBackend.Services
{
    public class SmsService : ISmsService
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<SmsService> _logger;
        private readonly ApplicationDbContext _context;

        public SmsService(IConfiguration configuration, ILogger<SmsService> logger, ApplicationDbContext context)
        {
            _configuration = configuration;
            _logger = logger;
            _context = context;
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

            if (await IsBlockedUserPhoneAsync(toNumber))
            {
                _logger.LogWarning("Suppressing SMS to blocked user phone {To}", toNumber);
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
            catch (RestException rcEx) when (IsInvalidPhoneNumberError(rcEx))
            {
                // RingCentral rejected the destination number (CMN-414 / InvalidParameter on
                // to.phoneNumber). This is a data problem on our side, not a transient failure
                // and not worth alerting on — surface it as a typed exception so admin-triggered
                // sends can show a clean "this number is invalid, no SMS sent" message instead
                // of leaking the raw RingCentral payload to the user.
                _logger.LogWarning(rcEx, "RingCentral rejected phone number {To} as invalid; SMS not sent", toNumber);
                throw new InvalidPhoneNumberException(toNumber, "Phone number is invalid; no SMS was sent.", rcEx);
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

        /// <summary>
        /// Blocked (deactivated) users must receive no SMS from us at all until an admin unblocks
        /// them. User.Phone is stored in assorted formats, so both sides are normalized to bare
        /// digits before comparing. The blocked set is tiny, so pulling it into memory is cheap.
        /// </summary>
        private async Task<bool> IsBlockedUserPhoneAsync(string toNumber)
        {
            var target = PhoneHelper.NormalizeToDigits(toNumber);
            if (target == null) return false;
            try
            {
                var blockedPhones = await _context.Users
                    .Where(u => !u.IsActive && !u.IsDeleted && u.Phone != null && u.Phone != "")
                    .Select(u => u.Phone)
                    .ToListAsync();
                // Also cover the contact phone on blocked users' orders — reminder flows
                // address the order contact, which can differ from the profile phone.
                var blockedOrderPhones = await _context.Orders
                    .Where(o => !o.User.IsActive && !o.User.IsDeleted
                        && o.ContactPhone != null && o.ContactPhone != "")
                    .Select(o => o.ContactPhone)
                    .Distinct()
                    .ToListAsync();
                return blockedPhones.Concat(blockedOrderPhones)
                    .Any(p => PhoneHelper.NormalizeToDigits(p) == target);
            }
            catch (Exception ex)
            {
                // A lookup failure must not take the SMS pipeline down with it.
                _logger.LogError(ex, "Blocked-user phone check failed for {Phone}; allowing send", toNumber);
                return false;
            }
        }

        // Recognise the RingCentral "invalid destination phone number" response. Their error
        // bodies aren't typed beyond the JSON, so we match the documented errorCode CMN-414 as
        // well as the parameterName hint, against whichever piece of the exception carries the
        // payload across SDK versions.
        private static bool IsInvalidPhoneNumberError(RestException ex)
        {
            var payload = (ex.Message ?? string.Empty) + " " + (ex.InnerException?.Message ?? string.Empty);
            if (payload.Contains("CMN-414", StringComparison.OrdinalIgnoreCase)) return true;
            if (payload.Contains("to.phoneNumber", StringComparison.OrdinalIgnoreCase) &&
                payload.Contains("InvalidParameter", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public async Task SendBookingConfirmationSmsAsync(string phoneNumber, string customerName, DateTime serviceDate, string serviceTime,
            SupplyChecklistFacts supplyChecklist, bool isUpdate = false)
        {
            var firstName = customerName.Split(' ').FirstOrDefault() ?? customerName;

            var items = CustomerSupplyChecklist.BuildItems(supplyChecklist);

            // isUpdate: re-send after an admin changed the order. Same body, but the lead line
            // says "updated" so it doesn't read as a duplicate of the original confirmation.
            var leadLine = isUpdate
                ? $"Hi {firstName}! Your Dream Cleaning appointment has been updated and is now confirmed for {serviceDate:MMM dd} at {serviceTime}."
                : $"Hi {firstName}! Your Dream Cleaning appointment is confirmed for {serviceDate:MMM dd} at {serviceTime}.";

            // Cleaning Supplies + Cleaning Essentials + Vacuum Cleaner together leave the customer
            // nothing to prepare. A "Please provide the following items:" header with no items
            // under it reads as a broken message, so that case says the good news instead.
            var checklistLines = items.Count == 0
                ? "\nEverything is covered - we bring all the supplies, so there is nothing for you to provide."
                : "\nPlease provide the following items:" + $"\n- {string.Join("\n- ", items)}";

            var msg =
                leadLine +
                checklistLines +
                $"\nAll changes, requests, and concerns must go through Dream Cleaning. Do not make any arrangements directly with your cleaner." +
                $"\nBy booking with us, you agree to our Privacy Policy: https://dreamcleaningnyc.com/privacy-policy. We're committed to keeping your information safe." +
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

        // Loyalty re-engagement SMS templates — copy is verbatim from spec section 6 so the
        // gratitude framing isn't accidentally paraphrased. Single-segment-targeted lengths.
        public async Task SendLoyaltyReminder30SmsAsync(string phone, string firstName)
        {
            var msg = $"Hi {firstName}! It's been a while since your last clean — your home deserves another sparkle. Book today: dreamcleaningnyc.com Reply STOP to opt out.";
            await SendSmsAsync(phone, msg);
        }

        public async Task SendLoyaltyReminder60SmsAsync(string phone, string firstName, decimal percentage)
        {
            var pct = percentage.ToString("0.##");
            var msg = $"Hi {firstName}! As a thank-you from Dream Cleaning, we've added {pct}% off to your account — applies automatically at checkout. dreamcleaningnyc.com STOP to opt out.";
            await SendSmsAsync(phone, msg);
        }

        public async Task SendLoyaltyReminder90SmsAsync(string phone, string firstName, decimal percentage)
        {
            var pct = percentage.ToString("0.##");
            var msg = $"Hi {firstName}! Your account discount has been bumped up to {pct}% as our way of saying thanks. Book anytime: dreamcleaningnyc.com STOP to opt out.";
            await SendSmsAsync(phone, msg);
        }

        /// <param name="firstTimeDiscountLabel">Live label from the DB ("10%" / "$25"); null drops
        /// the discount clause. Must stay in sync with the email version — never a hardcoded default.</param>
        public async Task SendFirstBookingReminderSmsAsync(string phone, string firstName, string? firstTimeDiscountLabel)
        {
            var msg = !string.IsNullOrWhiteSpace(firstTimeDiscountLabel)
                ? $"Hi {firstName}! Your home's first Dream Cleaning sparkle is waiting — first-time customers get {firstTimeDiscountLabel} off. Book today: dreamcleaningnyc.com Reply STOP to opt out."
                : $"Hi {firstName}! Your home's first Dream Cleaning sparkle is waiting. Book today: dreamcleaningnyc.com Reply STOP to opt out.";
            await SendSmsAsync(phone, msg);
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
