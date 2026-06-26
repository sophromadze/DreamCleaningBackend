using DreamCleaningBackend.Data;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models;
using Microsoft.EntityFrameworkCore;

namespace DreamCleaningBackend.Services.CallTracking
{
    /// <summary>
    /// Polls the configured <see cref="ICallProvider"/> on an interval, upserts completed calls
    /// into <see cref="CallRecord"/> (deduped on Provider+ExternalId), links inbound calls to CRM
    /// leads, and advances a per-provider watermark (<see cref="CallSyncState"/>). On failure it
    /// leaves the watermark untouched so the next run retries the same window. Provider-neutral —
    /// it never references RingCentral directly.
    /// </summary>
    public class CallLogSyncService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ICallProvider _provider;
        private readonly IConfiguration _configuration;
        private readonly ILogger<CallLogSyncService> _logger;

        // Absorbs the provider's ingestion delay (RingCentral lags ~15–30s) plus clock skew.
        // Dedupe on (Provider, ExternalId) cleans up the resulting overlap.
        private static readonly TimeSpan OverlapBuffer = TimeSpan.FromMinutes(5);

        // First-ever run (no watermark yet) backfills this far so we don't pull the whole history.
        private static readonly TimeSpan InitialBackfill = TimeSpan.FromDays(7);

        public CallLogSyncService(
            IServiceScopeFactory scopeFactory,
            ICallProvider provider,
            IConfiguration configuration,
            ILogger<CallLogSyncService> logger)
        {
            _scopeFactory = scopeFactory;
            _provider = provider;
            _configuration = configuration;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (_provider.IsEnabled())
                        await RunSyncAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Never let one bad run kill the service; watermark is only advanced on success.
                    _logger.LogError(ex, "Call-log sync run failed for provider {Provider}.", _provider.ProviderName);
                }

                await Task.Delay(GetInterval(), stoppingToken);
            }
        }

        private TimeSpan GetInterval()
        {
            var minutes = _configuration.GetValue<int>("RingCentral:CallLogSyncIntervalMinutes", 30);
            if (minutes < 1) minutes = 30;
            return TimeSpan.FromMinutes(minutes);
        }

        private async Task RunSyncAsync(CancellationToken ct)
        {
            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var now = DateTime.UtcNow;

            var state = await context.CallSyncStates
                .FirstOrDefaultAsync(s => s.Provider == _provider.ProviderName, ct);

            var fromUtc = state != null
                ? state.LastSyncedUtc - OverlapBuffer
                : now - InitialBackfill;

            var calls = await _provider.FetchCallsAsync(fromUtc, now, ct);

            // Load cleaner phones once per run for classification (not a query per call).
            var cleanerPhoneMap = await CallClassifier.LoadCleanerPhoneMapAsync(context, ct);
            var adNumberDigits = CallClassifier.ResolveAdNumberDigits(_configuration);

            int inserted = 0, skipped = 0;
            foreach (var dto in calls)
            {
                ct.ThrowIfCancellationRequested();

                var exists = await context.CallRecords.AnyAsync(
                    c => c.Provider == dto.Provider && c.ExternalId == dto.ExternalId, ct);
                if (exists)
                {
                    skipped++;
                    continue;
                }

                var record = new CallRecord
                {
                    Provider = dto.Provider,
                    ExternalId = dto.ExternalId,
                    SessionId = dto.SessionId,
                    Direction = dto.Direction,
                    Result = dto.Result,
                    FromNumber = dto.FromNumber,
                    FromName = dto.FromName,
                    ToNumber = dto.ToNumber,
                    StartTimeUtc = dto.StartTimeUtc,
                    DurationSeconds = dto.DurationSeconds,
                    RecordingUrl = dto.RecordingUrl,
                    CreatedAt = DateTime.UtcNow
                };

                // Link to a CRM lead (and add a Call activity) for inbound calls before saving,
                // so the new record persists with its LeadId in one go.
                await LinkToLeadAsync(context, record, ct);

                // Classify after linking so it can read the resolved LeadId (Customer vs Spam).
                CallClassifier.Classify(record, cleanerPhoneMap, adNumberDigits);

                context.CallRecords.Add(record);
                inserted++;
            }

            if (inserted > 0)
                await context.SaveChangesAsync(ct);

            // Advance the watermark only on a fully successful run.
            if (state == null)
            {
                context.CallSyncStates.Add(new CallSyncState
                {
                    Provider = _provider.ProviderName,
                    LastSyncedUtc = now
                });
            }
            else
            {
                state.LastSyncedUtc = now;
            }
            await context.SaveChangesAsync(ct);

            _logger.LogInformation(
                "Call-log sync ({Provider}): fetched {Fetched}, inserted {Inserted}, skipped {Skipped}. Window {From:o}..{To:o}.",
                _provider.ProviderName, calls.Count, inserted, skipped, fromUtc, now);
        }

        /// <summary>
        /// Phase 4 — match an inbound call to an existing lead by phone, set the FK and append a
        /// "Call" activity to that lead's timeline. Optionally creates a lead from the caller when
        /// RingCentral:AutoCreateLeadsFromCalls is enabled (off by default to avoid lead spam).
        /// Only ever called for not-yet-persisted records, so each call yields at most one activity.
        /// </summary>
        private async Task LinkToLeadAsync(ApplicationDbContext context, CallRecord record, CancellationToken ct)
        {
            if (!string.Equals(record.Direction, "Inbound", StringComparison.OrdinalIgnoreCase))
                return;

            var e164 = SmsService.NormalizePhoneToE164(record.FromNumber);
            if (string.IsNullOrEmpty(e164))
                return;

            // Leads store a bare 10-digit phone (Lead.Phone setter via PhoneHelper), so match on
            // the same normalized form derived from the E.164 number.
            var digits = PhoneHelper.NormalizeToDigits(e164);
            if (string.IsNullOrEmpty(digits))
                return;

            var lead = await context.Leads.FirstOrDefaultAsync(l => l.Phone == digits, ct);

            var answered = string.Equals(record.Result, "Accepted", StringComparison.OrdinalIgnoreCase);

            if (lead == null)
            {
                var autoCreate = _configuration.GetValue<bool>("RingCentral:AutoCreateLeadsFromCalls", false);
                if (!autoCreate || !answered)
                    return;

                lead = BuildLeadFromCall(record, digits);
                context.Leads.Add(lead);
                // Save so the new lead has an Id before we attach the FK + activity.
                await context.SaveChangesAsync(ct);
            }

            record.LeadId = lead.Id;

            var activity = new LeadActivity
            {
                LeadId = lead.Id,
                Type = LeadActivityType.Call,
                Content = BuildCallSummary(record),
                AdminId = null,
                AdminName = "System (Call Tracking)",
                CreatedAt = record.StartTimeUtc
            };
            context.LeadActivities.Add(activity);

            // Touch the lead so it surfaces in recency-sorted views.
            lead.LastActivityAt = DateTime.UtcNow;
            lead.UpdatedAt = DateTime.UtcNow;
        }

        private Lead BuildLeadFromCall(CallRecord record, string digits)
        {
            // Use the caller-ID name when the provider supplied one; otherwise leave names blank.
            string? first = null, last = null;
            if (!string.IsNullOrWhiteSpace(record.FromName))
            {
                var parts = record.FromName.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                first = parts.Length > 0 ? parts[0] : null;
                last = parts.Length > 1 ? parts[1] : null;
            }

            return new Lead
            {
                FirstName = first,
                LastName = last,
                Phone = digits,
                Source = LeadSource.Manual,
                Stage = LeadStage.New,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                LastActivityAt = DateTime.UtcNow
            };
        }

        private static string BuildCallSummary(CallRecord record)
        {
            var direction = string.Equals(record.Direction, "Outbound", StringComparison.OrdinalIgnoreCase)
                ? "Outbound call"
                : "Inbound call";
            return $"{direction} · {FormatDuration(record.DurationSeconds)} · {record.Result}";
        }

        // Format seconds as "Xm Ys" (e.g. 134s -> "2m 14s").
        private static string FormatDuration(int totalSeconds)
        {
            if (totalSeconds < 0) totalSeconds = 0;
            var minutes = totalSeconds / 60;
            var seconds = totalSeconds % 60;
            return $"{minutes}m {seconds}s";
        }
    }
}
