using System.Text.RegularExpressions;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace DreamCleaningBackend.Services
{
    // Hourly background worker for the loyalty re-engagement system. Pattern mirrors
    // CustomerNotificationService (hourly loop, scope per cycle, exponential backoff on
    // consecutive errors). Distinct behavior:
    //
    //   - The dispatch body only runs when the current time in America/New_York is between
    //     11:00 and 11:59. Cron fires every hour but most ticks are no-ops; this keeps the
    //     daily reminder send predictable for customers while still letting the service
    //     recover within an hour if it misses one tick (e.g. process restart at 11:30).
    //
    //   - Eligibility, idempotency, and the activation/upgrade decision all happen here.
    //     LoyaltyDiscountService owns the state mutation + audit log so the same code path
    //     is used whether the change is driven by the worker or by an admin.
    //
    //   - Settings are read via IBubbleRewardsSettingsService at the start of each cycle.
    //     That service has its own 5-min in-memory cache, so admin edits propagate within
    //     5 min cache TTL × 1 worker tick = effectively within the same hour.
    public class LoyaltyReengagementService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<LoyaltyReengagementService> _logger;
        private readonly IHostEnvironment _hostEnvironment;
        private readonly IConfiguration _configuration;
        private int _consecutiveErrors = 0;
        // Dev-only one-shot bypass for the 11am NY gate — true after we've consumed the flag
        // once in this process lifetime. See ConsumeDevForceRunFlag for the full rules.
        private bool _forceRunConsumed = false;
        private const int MAX_CONSECUTIVE_ERRORS = 5;

        // 7-day catch-up window: if cron misses the exact day boundary (process down at 11am
        // that day), the user is still eligible the next time the service runs as long as
        // they haven't crossed the next milestone yet. Idempotency is enforced by NotificationLog
        // so a user can't get the same reminder twice.
        private const int WINDOW_DAYS = 7;

        // Booking-form E.164 regex (per spec section 4.2 step 7). Loose enough to allow US +1
        // 10-digit raw numbers (PhoneHelper normalizes those) but blocks obviously broken values
        // like "+11234567890" that RingCentral rejects with CMN-414.
        private static readonly Regex E164Regex = new(@"^\+?[1-9]\d{9,14}$", RegexOptions.Compiled);

        public LoyaltyReengagementService(
            IServiceProvider serviceProvider,
            ILogger<LoyaltyReengagementService> logger,
            IHostEnvironment hostEnvironment,
            IConfiguration configuration)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
            _hostEnvironment = hostEnvironment;
            _configuration = configuration;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Match CustomerNotificationService: 30-second warmup so we don't fight other
            // startup work for DB connections.
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RunCycleAsync(stoppingToken);
                    _consecutiveErrors = 0;
                }
                catch (Exception ex)
                {
                    _consecutiveErrors++;
                    _logger.LogError(ex, "Error in LoyaltyReengagementService cycle (attempt {Attempt})", _consecutiveErrors);
                    if (_consecutiveErrors >= MAX_CONSECUTIVE_ERRORS)
                    {
                        _logger.LogCritical("Too many consecutive errors in LoyaltyReengagementService. Stopping service.");
                        break;
                    }
                }

                try
                {
                    var delay = _consecutiveErrors > 0
                        ? TimeSpan.FromMinutes(5 * _consecutiveErrors)
                        : TimeSpan.FromHours(1);
                    await Task.Delay(delay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("LoyaltyReengagementService cancellation requested");
                    break;
                }
            }

            _logger.LogInformation("LoyaltyReengagementService stopped");
        }

        private async Task RunCycleAsync(CancellationToken cancellationToken)
        {
            // Dev-only one-shot bypass — consumed at most once per process lifetime. See
            // ConsumeDevForceRunFlag for the full rules.
            var forceThisCycle = ConsumeDevForceRunFlag();

            if (!forceThisCycle && !IsWithinDispatchHour())
            {
                // Quiet log so prod doesn't drown in "skipping" lines. Verbose level for
                // operators who turn on Debug to inspect the schedule.
                _logger.LogDebug("Loyalty reengagement: outside 11:00-11:59 America/New_York window; skipping");
                return;
            }

            if (forceThisCycle)
            {
                _logger.LogWarning("Loyalty reengagement: dev-only LOYALTY_FORCE_RUN flag consumed — bypassing 11am NY gate for this cycle only. Restart the host with the flag set to trigger again.");
            }

            using var scope = _serviceProvider.CreateScope();
            var settings = scope.ServiceProvider.GetRequiredService<IBubbleRewardsSettingsService>();
            var enabled = await settings.GetSetting<bool>("LoyaltyDiscountEnabled", true);
            if (!enabled)
            {
                _logger.LogInformation("Loyalty reengagement: disabled via setting; cycle no-op");
                return;
            }

            // Load all thresholds once per cycle.
            var day60Pct = await settings.GetSetting<decimal>("LoyaltyDay60Percentage", 10m);
            var day90Pct = await settings.GetSetting<decimal>("LoyaltyDay90Percentage", 15m);
            var day30 = await settings.GetSetting<int>("DaysUntilFirstReminder", 30);
            var day60 = await settings.GetSetting<int>("DaysUntilDiscountActivation", 60);
            var day90 = await settings.GetSetting<int>("DaysUntilDiscountUpgrade", 90);
            var minCooldown = await settings.GetSetting<int>("MinDaysFromLastUseBeforeReActivation", 30);

            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var emailService = scope.ServiceProvider.GetRequiredService<IEmailService>();
            var smsService = scope.ServiceProvider.GetRequiredService<ISmsService>();
            var auditService = scope.ServiceProvider.GetRequiredService<IAuditService>();

            // Eligibility query — filtered server-side. We pad each boundary by 1 day to
            // absorb microsecond-level drift between when a user's LastCompletedOrderDate
            // was stamped and when we sample `now` (and to avoid the previous .Date-truncation
            // bug that floored the latest boundary to 00:00 and dropped users seeded at any
            // non-midnight time on the exact day-30 anniversary). The in-memory branch logic
            // in ProcessCandidateAsync narrows back to the spec's exact day-count windows
            // using calendar-day arithmetic, so loading a handful of slightly-too-recent or
            // slightly-too-old rows is harmless.
            var now = DateTime.UtcNow;
            var latestEligibleLastOrderUtc = now.AddDays(-(day30 - 1));
            var earliestEligibleLastOrderUtc = now.AddDays(-(day90 + WINDOW_DAYS));

            // Future-order exclusion: users with any upcoming engaged-status order (Active /
            // Pending / Confirmed, ServiceDate >= today) are clearly engaged — skip them.
            var todayDate = now.Date;
            var engagedStatuses = new[] { "Active", "Pending", "Confirmed" };

            var candidates = await context.Users
                .Where(u =>
                    u.IsActive &&
                    !u.IsDeleted &&
                    u.LastCompletedOrderDate != null &&
                    u.LastCompletedOrderDate <= latestEligibleLastOrderUtc &&
                    u.LastCompletedOrderDate >= earliestEligibleLastOrderUtc &&
                    !context.Orders.Any(o =>
                        o.UserId == u.Id &&
                        engagedStatuses.Contains(o.Status) &&
                        o.ServiceDate >= todayDate))
                .Select(u => new CandidateRow
                {
                    UserId = u.Id,
                    FirstName = u.FirstName,
                    Email = u.Email,
                    Phone = u.Phone,
                    LastCompletedOrderDate = u.LastCompletedOrderDate!.Value,
                    CanReceiveEmails = u.CanReceiveEmails,
                    CanReceiveMessages = u.CanReceiveMessages,
                    LoyaltyDiscountPercentage = u.LoyaltyDiscountPercentage,
                    LoyaltyDiscountIsManualOverride = u.LoyaltyDiscountIsManualOverride,
                    LoyaltyDiscountActivatedAt = u.LoyaltyDiscountActivatedAt,
                    LoyaltyDiscountLastUsedAt = u.LoyaltyDiscountLastUsedAt,
                })
                .ToListAsync(cancellationToken);

            _logger.LogInformation("Loyalty reengagement: {Count} eligible candidates this cycle", candidates.Count);

            foreach (var c in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await ProcessCandidateAsync(c, day30, day60, day90, day60Pct, day90Pct, minCooldown, now,
                        context, emailService, smsService, auditService);
                }
                catch (Exception ex)
                {
                    // Per-user failures must not stop the cycle (spec section 4.2 step 8).
                    _logger.LogError(ex, "Loyalty reengagement: failed to process user {UserId}; continuing", c.UserId);
                }
            }
        }

        private async Task ProcessCandidateAsync(
            CandidateRow c,
            int day30, int day60, int day90,
            decimal day60Pct, decimal day90Pct,
            int minCooldown,
            DateTime now,
            ApplicationDbContext context,
            IEmailService emailService,
            ISmsService smsService,
            IAuditService auditService)
        {
            var daysSinceLastOrder = (int)Math.Floor((now.Date - c.LastCompletedOrderDate.Date).TotalDays);

            // Re-activation cooldown: even if days-since-last-order is past day30, we wait
            // until N days after LastUsedAt before re-entering the cycle. Per spec section 2.3.
            if (c.LoyaltyDiscountLastUsedAt.HasValue)
            {
                var daysSinceLastUse = (now.Date - c.LoyaltyDiscountLastUsedAt.Value.Date).TotalDays;
                if (daysSinceLastUse < minCooldown)
                {
                    _logger.LogDebug("User {UserId}: skipped (cooldown — {Days} days since last use, need {Min})",
                        c.UserId, daysSinceLastUse, minCooldown);
                    return;
                }
            }

            // Which milestone window does this user fall into right now? Branches are
            // mutually exclusive — the largest window wins so a user who somehow lands on
            // both 60 AND 90 gets the 90 send (idempotency guards both).
            int? milestoneDays = null;
            string? milestoneType = null;
            decimal? milestonePctForSend = null;
            bool isActivation = false;
            bool isUpgrade = false;

            if (daysSinceLastOrder >= day90 && daysSinceLastOrder < day90 + WINDOW_DAYS)
            {
                milestoneDays = day90;
                milestoneType = NotificationTypes.LoyaltyReminder90;
                milestonePctForSend = day90Pct;
                isUpgrade = true;
            }
            else if (daysSinceLastOrder >= day60 && daysSinceLastOrder < day60 + WINDOW_DAYS)
            {
                milestoneDays = day60;
                milestoneType = NotificationTypes.LoyaltyReminder60;
                milestonePctForSend = day60Pct;
                isActivation = true;
            }
            else if (daysSinceLastOrder >= day30 && daysSinceLastOrder < day30 + WINDOW_DAYS)
            {
                milestoneDays = day30;
                milestoneType = NotificationTypes.LoyaltyReminder30;
            }

            if (milestoneType == null)
            {
                _logger.LogDebug("User {UserId}: no active milestone at {Days} days inactive", c.UserId, daysSinceLastOrder);
                return;
            }

            // Idempotency: skip if we've already logged a reminder of this type for this user
            // since their last completed order. Combined with NotificationLog write below, this
            // guarantees at-most-once delivery per cycle.
            var alreadySent = await context.NotificationLogs
                .AnyAsync(nl =>
                    nl.CustomerId == c.UserId &&
                    nl.NotificationType == milestoneType &&
                    nl.SentAt >= c.LastCompletedOrderDate);
            if (alreadySent)
            {
                _logger.LogDebug("User {UserId}: {Type} already sent this cycle", c.UserId, milestoneType);
                return;
            }

            // Manual-override users must not receive the discount-content reminders (60/90).
            // Spec section 2.5: "no reminder sends about the discount itself" — the auto template
            // would contradict whatever percentage the admin set. The 30-day reminder is plain
            // "we miss you" copy with no discount content, so it still goes through.
            //
            // We DON'T insert a NotificationLog row for this skip — see the readout's flagged
            // decision. Keeping the log absent lets a later admin-clear allow the next cycle to
            // re-evaluate cleanly; writing it would silently block the user from ever receiving
            // the 60/90 send even after the override is removed.
            if ((isActivation || isUpgrade) && c.LoyaltyDiscountIsManualOverride)
            {
                _logger.LogInformation("User {UserId}: skipped {Type} (manual override active, percentage={Pct})",
                    c.UserId, milestoneType, c.LoyaltyDiscountPercentage);
                return;
            }

            // ─── Account-state mutation (discount activation/upgrade) ───
            // Happens BEFORE communication attempts: per spec section 8 case 3, the discount is
            // account state, not communication. CanReceive flags + invalid phone don't block it.

            if (isActivation && !c.LoyaltyDiscountIsManualOverride && c.LoyaltyDiscountPercentage == 0m)
            {
                var user = await context.Users.FirstOrDefaultAsync(u => u.Id == c.UserId);
                if (user != null)
                {
                    var oldPct = user.LoyaltyDiscountPercentage;
                    var oldOverride = user.LoyaltyDiscountIsManualOverride;
                    var oldActivatedAt = user.LoyaltyDiscountActivatedAt;
                    var oldLastUsedAt = user.LoyaltyDiscountLastUsedAt;

                    user.LoyaltyDiscountPercentage = day60Pct;
                    user.LoyaltyDiscountActivatedAt = now;
                    user.LoyaltyDiscountIsManualOverride = false;
                    await context.SaveChangesAsync();

                    await auditService.LogLoyaltyDiscountChangeAsync(
                        user.Id, "LoyaltyAutoActivated",
                        oldPct, oldOverride, oldActivatedAt, oldLastUsedAt,
                        user.LoyaltyDiscountPercentage, user.LoyaltyDiscountIsManualOverride,
                        user.LoyaltyDiscountActivatedAt, user.LoyaltyDiscountLastUsedAt,
                        adminUserId: null);

                    _logger.LogInformation("User {UserId}: loyalty auto-activated at {Pct}%", c.UserId, day60Pct);
                    // Refresh local snapshot so subsequent send uses the new percentage.
                    c.LoyaltyDiscountPercentage = day60Pct;
                }
            }
            else if (isUpgrade && !c.LoyaltyDiscountIsManualOverride && c.LoyaltyDiscountPercentage == day60Pct)
            {
                var user = await context.Users.FirstOrDefaultAsync(u => u.Id == c.UserId);
                if (user != null)
                {
                    var oldPct = user.LoyaltyDiscountPercentage;
                    var oldOverride = user.LoyaltyDiscountIsManualOverride;
                    var oldActivatedAt = user.LoyaltyDiscountActivatedAt;
                    var oldLastUsedAt = user.LoyaltyDiscountLastUsedAt;

                    user.LoyaltyDiscountPercentage = day90Pct;
                    await context.SaveChangesAsync();

                    await auditService.LogLoyaltyDiscountChangeAsync(
                        user.Id, "LoyaltyAutoUpgraded",
                        oldPct, oldOverride, oldActivatedAt, oldLastUsedAt,
                        user.LoyaltyDiscountPercentage, user.LoyaltyDiscountIsManualOverride,
                        user.LoyaltyDiscountActivatedAt, user.LoyaltyDiscountLastUsedAt,
                        adminUserId: null);

                    _logger.LogInformation("User {UserId}: loyalty auto-upgraded to {Pct}%", c.UserId, day90Pct);
                    c.LoyaltyDiscountPercentage = day90Pct;
                }
            }

            // ─── Communication ───
            // Insert NotificationLog FIRST so idempotency is preserved even if email/SMS fails.
            // The log row is the boundary: "we attempted this milestone for this user once".
            var logRow = new NotificationLog
            {
                OrderId = null,
                CleanerId = null,
                CustomerId = c.UserId,
                NotificationType = milestoneType,
                SentAt = now,
            };
            context.NotificationLogs.Add(logRow);
            await context.SaveChangesAsync();

            // Email: respects CanReceiveEmails. A missing Email field is unusual but possible
            // for Apple/Google accounts that haven't set one — skip if empty.
            if (c.CanReceiveEmails && !string.IsNullOrWhiteSpace(c.Email))
            {
                try
                {
                    if (milestoneType == NotificationTypes.LoyaltyReminder30)
                        await emailService.SendLoyaltyReminder30Async(c.Email, c.FirstName);
                    else if (milestoneType == NotificationTypes.LoyaltyReminder60)
                        await emailService.SendLoyaltyReminder60Async(c.Email, c.FirstName, milestonePctForSend!.Value);
                    else if (milestoneType == NotificationTypes.LoyaltyReminder90)
                        await emailService.SendLoyaltyReminder90Async(c.Email, c.FirstName, milestonePctForSend!.Value);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Loyalty {Type} email failed for user {UserId}; SMS attempt will continue", milestoneType, c.UserId);
                }
            }

            // SMS: respects CanReceiveMessages + phone presence + E.164 validity + SMS enabled.
            // RingCentral CMN-414 (invalid number) is caught by SmsService and surfaced as
            // InvalidPhoneNumberException, which we treat as a soft skip — log and move on.
            if (c.CanReceiveMessages && !string.IsNullOrWhiteSpace(c.Phone) && smsService.IsSmsEnabled())
            {
                var normalized = SmsService.NormalizePhoneToE164(c.Phone);
                if (string.IsNullOrEmpty(normalized) || !E164Regex.IsMatch(normalized))
                {
                    _logger.LogWarning("Loyalty {Type} SMS skipped for user {UserId}: phone failed E.164 validation", milestoneType, c.UserId);
                }
                else
                {
                    try
                    {
                        if (milestoneType == NotificationTypes.LoyaltyReminder30)
                            await smsService.SendLoyaltyReminder30SmsAsync(normalized, c.FirstName);
                        else if (milestoneType == NotificationTypes.LoyaltyReminder60)
                            await smsService.SendLoyaltyReminder60SmsAsync(normalized, c.FirstName, milestonePctForSend!.Value);
                        else if (milestoneType == NotificationTypes.LoyaltyReminder90)
                            await smsService.SendLoyaltyReminder90SmsAsync(normalized, c.FirstName, milestonePctForSend!.Value);
                    }
                    catch (InvalidPhoneNumberException)
                    {
                        _logger.LogWarning("Loyalty {Type} SMS skipped for user {UserId}: RingCentral rejected the number", milestoneType, c.UserId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Loyalty {Type} SMS failed for user {UserId}", milestoneType, c.UserId);
                    }
                }
            }
        }

        // Dev-only one-shot bypass for the 11am NY dispatch gate. Hard-gated behind
        // ASPNETCORE_ENVIRONMENT=Development so a production host can never trigger this
        // even if someone accidentally sets the env var or appsettings key in prod config.
        //
        // Recognised inputs (either suffices, "true" or "1", case-insensitive):
        //   - Environment variable: LOYALTY_FORCE_RUN
        //   - Configuration key:    Loyalty:ForceRun  (e.g. in appsettings.Development.json)
        //
        // Workflow:
        //   1. Dev sets LOYALTY_FORCE_RUN=true in their shell (or the appsettings key).
        //   2. Dev starts (or restarts) the backend.
        //   3. After the 30-second warmup the first cycle runs, this method returns true,
        //      the cycle bypasses the 11am NY gate, and the env var is actively unset so
        //      a subsequent cycle in this same process won't see it again.
        //   4. To trigger again, dev re-sets the env var and restarts the host.
        //
        // The appsettings path is "consumed" via the in-memory _forceRunConsumed flag —
        // restarting the host re-arms it (resetting the in-memory flag). We deliberately
        // don't mutate appsettings.json from inside the process.
        private bool ConsumeDevForceRunFlag()
        {
            if (!_hostEnvironment.IsDevelopment()) return false;
            if (_forceRunConsumed) return false;

            var envVar = Environment.GetEnvironmentVariable("LOYALTY_FORCE_RUN");
            var configVal = _configuration["Loyalty:ForceRun"];

            static bool IsTruthy(string? s) =>
                string.Equals(s, "true", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s, "1", StringComparison.Ordinal);

            if (!IsTruthy(envVar) && !IsTruthy(configVal)) return false;

            _forceRunConsumed = true;
            // Actively clear the env var so this process can't accidentally re-fire on
            // a subsequent cycle. SetEnvironmentVariable(name, null) removes it.
            Environment.SetEnvironmentVariable("LOYALTY_FORCE_RUN", null);
            return true;
        }

        // 11:00–11:59 America/New_York gate. We accept either of two TimeZoneInfo IDs for
        // cross-platform safety: the IANA name works on Linux/macOS, the Windows name on
        // legacy Windows hosts. The IANA "America/New_York" is preferred on modern .NET 8.
        private static readonly Lazy<TimeZoneInfo> NyTimeZone = new(() =>
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById("America/New_York"); }
            catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"); }
        });

        private static bool IsWithinDispatchHour()
        {
            var nyNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, NyTimeZone.Value);
            return nyNow.Hour == 11;
        }

        // Snapshot DTO so the eligibility SELECT stays a single SQL roundtrip and we don't
        // hold thousands of User entities tracked by EF for the duration of the loop. The
        // per-user mutation block re-loads the specific User row when it needs to write.
        private sealed class CandidateRow
        {
            public int UserId { get; init; }
            public string FirstName { get; init; } = string.Empty;
            public string Email { get; init; } = string.Empty;
            public string? Phone { get; init; }
            public DateTime LastCompletedOrderDate { get; init; }
            public bool CanReceiveEmails { get; init; }
            public bool CanReceiveMessages { get; init; }
            public decimal LoyaltyDiscountPercentage { get; set; }
            public bool LoyaltyDiscountIsManualOverride { get; init; }
            public DateTime? LoyaltyDiscountActivatedAt { get; init; }
            public DateTime? LoyaltyDiscountLastUsedAt { get; init; }
        }
    }
}
