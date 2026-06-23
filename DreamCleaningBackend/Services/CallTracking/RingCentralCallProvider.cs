using System.Globalization;
using RingCentral;

namespace DreamCleaningBackend.Services.CallTracking
{
    /// <summary>
    /// RingCentral adapter for <see cref="ICallProvider"/>. Pulls the account-level company
    /// call log (/restapi/v1.0/account/~/call-log) via the typed SDK and maps it to the neutral
    /// <see cref="CallRecordDto"/>. This is the ONLY RingCentral-specific file in the call-tracking
    /// stack — swap it out to change providers.
    ///
    /// Auth mirrors <see cref="SmsService.GetRingCentralClientAsync"/> (JWT authorize, always
    /// Revoke in finally). The Call Log API is a "Heavy" usage-plan resource, so we cap pages
    /// per run and use perPage=100.
    /// </summary>
    public class RingCentralCallProvider : ICallProvider
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<RingCentralCallProvider> _logger;

        // Safety cap so a misconfigured/huge window can't hammer the Heavy-rated Call Log API.
        private const int MaxPagesPerRun = 50;
        private const int PerPage = 100;

        // Call Log is a "Heavy" rate-limited resource — pause between page requests to stay
        // within RingCentral's per-minute limits when a window spans many pages.
        private static readonly TimeSpan InterPageDelay = TimeSpan.FromMilliseconds(1000);

        public RingCentralCallProvider(IConfiguration configuration, ILogger<RingCentralCallProvider> logger)
        {
            _configuration = configuration;
            _logger = logger;
        }

        public string ProviderName => "RingCentral";

        public bool IsEnabled()
        {
            if (!_configuration.GetValue<bool>("RingCentral:EnableCallLogSync", false))
                return false;
            var clientId = _configuration["RingCentral:ClientId"];
            var clientSecret = _configuration["RingCentral:ClientSecret"];
            var server = _configuration["RingCentral:ServerUrl"];
            var jwt = _configuration["RingCentral:JwtToken"];
            return !string.IsNullOrWhiteSpace(clientId)
                && !string.IsNullOrWhiteSpace(clientSecret)
                && !string.IsNullOrWhiteSpace(server)
                && !string.IsNullOrWhiteSpace(jwt);
        }

        public async Task<IReadOnlyList<CallRecordDto>> FetchCallsAsync(
            DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
        {
            if (!IsEnabled())
                return Array.Empty<CallRecordDto>();

            var results = new List<CallRecordDto>();
            RestClient? rc = null;
            try
            {
                rc = await GetRingCentralClientAsync();

                var completed = false;
                for (var page = 1; page <= MaxPagesPerRun; page++)
                {
                    ct.ThrowIfCancellationRequested();

                    // Throttle between pages (not before the first) for the Heavy-rated resource.
                    if (page > 1)
                        await Task.Delay(InterPageDelay, ct);

                    // The window + filters are re-applied on every page; only `page` advances.
                    var parameters = new ReadCompanyCallLogParameters
                    {
                        dateFrom = ToIso(fromUtc),
                        dateTo = ToIso(toUtc),
                        view = "Detailed",
                        direction = new[] { "Inbound" },
                        type = new[] { "Voice" },
                        perPage = PerPage,
                        page = page
                    };

                    var response = await rc.Restapi().Account().CallLog().List(parameters);
                    var records = response?.records ?? Array.Empty<CallLogRecord>();

                    foreach (var record in records)
                    {
                        var dto = MapRecord(record);
                        if (dto != null)
                            results.Add(dto);
                    }

                    // An empty page means there's nothing further in the window.
                    if (records.Length == 0)
                    {
                        completed = true;
                        break;
                    }

                    // Prefer the server's paging metadata; advance until we've read the last page.
                    var totalPages = response?.paging?.totalPages;
                    if (totalPages.HasValue)
                    {
                        if (page >= totalPages.Value)
                        {
                            completed = true;
                            break;
                        }
                    }
                    // No paging metadata: stop once a page comes back not full (the last page).
                    else if (records.Length < PerPage)
                    {
                        completed = true;
                        break;
                    }
                }

                if (!completed)
                {
                    _logger.LogWarning(
                        "RingCentral call-log fetch hit the {Cap}-page safety cap for window {From:o}..{To:o}; older calls in this range may be unfetched. Narrow the sync window or raise the cap.",
                        MaxPagesPerRun, fromUtc, toUtc);
                }
            }
            finally
            {
                if (rc != null)
                {
                    try { await rc.Revoke(); } catch (Exception ex) { _logger.LogDebug(ex, "Revoke after call-log fetch"); }
                }
            }

            return results;
        }

        private CallRecordDto? MapRecord(CallLogRecord record)
        {
            if (string.IsNullOrWhiteSpace(record.id))
                return null;

            return new CallRecordDto
            {
                Provider = ProviderName,
                ExternalId = record.id,
                SessionId = record.sessionId,
                Direction = record.direction ?? "Inbound",
                Result = record.result ?? "Unknown",
                FromNumber = record.from?.phoneNumber,
                FromName = record.from?.name,
                ToNumber = record.to?.phoneNumber,
                StartTimeUtc = ParseStartTimeUtc(record.startTime),
                DurationSeconds = (int)(record.duration ?? 0),
                RecordingUrl = record.recording?.contentUri
            };
        }

        // RingCentral returns startTime as ISO 8601 with an offset (e.g. 2026-06-23T10:15:00.000-04:00).
        // Normalize to UTC; fall back to "now" only if the field is unparseable so we never drop a record.
        private static DateTime ParseStartTimeUtc(string? startTime)
        {
            if (!string.IsNullOrWhiteSpace(startTime) &&
                DateTimeOffset.TryParse(startTime, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto))
            {
                return dto.UtcDateTime;
            }
            return DateTime.UtcNow;
        }

        private static string ToIso(DateTime utc) =>
            DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

        // Mirrors SmsService.GetRingCentralClientAsync — same JWT authorize flow.
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
    }
}
