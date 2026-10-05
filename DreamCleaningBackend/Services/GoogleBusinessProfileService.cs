using System.Net.Http.Headers;
using System.Text.Json;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace DreamCleaningBackend.Services
{
    public interface IGoogleBusinessProfileService
    {
        /// <summary>True only when ClientId, ClientSecret, RefreshToken, AccountId and LocationId are all configured.</summary>
        bool IsConfigured { get; }

        /// <summary>
        /// Pulls every review from the Google Business Profile API and reconciles them into the
        /// GoogleReviews table (upsert by Google review id, delete reviews no longer on Google),
        /// and stores Google's totalReviewCount / averageRating in GoogleReviewSyncState.
        /// Throws <see cref="GoogleReviewSyncException"/> on any failure; nothing is written then.
        /// </summary>
        Task<GoogleReviewSyncResult> SyncReviewsAsync(CancellationToken cancellationToken = default);
    }

    public sealed record GoogleReviewSyncResult(int StoredCount, int TotalReviewCount, double AverageRating);

    /// <summary>A failed review sync. <see cref="IsInvalidGrant"/> means the refresh token is expired or revoked.</summary>
    public class GoogleReviewSyncException : Exception
    {
        public bool IsInvalidGrant { get; }

        public GoogleReviewSyncException(string message, bool isInvalidGrant = false, Exception? inner = null)
            : base(message, inner)
        {
            IsInvalidGrant = isInvalidGrant;
        }
    }

    public class GoogleBusinessProfileService : IGoogleBusinessProfileService
    {
        // Named HttpClient registered in Program.cs with an IPv4-forced handler (VPS has IPv6 disabled).
        public const string HttpClientName = "GoogleBusinessProfile";

        // IMemoryCache key for the full non-hidden review snapshot served by GoogleReviewsController.
        // Evicted here after a successful sync so a fresh pull shows up without waiting out the TTL.
        public const string AllReviewsCacheKey = "GoogleReviews:All";

        // IMemoryCache key for the count/rating served by GoogleReviewsController's /stats endpoint.
        public const string StatsCacheKey = "GoogleReviews:Stats";

        private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
        // v4 is the only Business Profile API version that exposes reviews.
        private const string ReviewsApiBase = "https://mybusiness.googleapis.com/v4";
        private const int PageSize = 50; // API max per page.

        private readonly IServiceProvider _serviceProvider;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConfiguration _configuration;
        private readonly ILogger<GoogleBusinessProfileService> _logger;
        private readonly IMemoryCache _cache;

        public GoogleBusinessProfileService(
            IServiceProvider serviceProvider,
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            ILogger<GoogleBusinessProfileService> logger,
            IMemoryCache cache)
        {
            _serviceProvider = serviceProvider;
            _httpClientFactory = httpClientFactory;
            _configuration = configuration;
            _logger = logger;
            _cache = cache;
        }

        private string? ClientId => _configuration["GoogleBusinessProfile:ClientId"];
        private string? ClientSecret => _configuration["GoogleBusinessProfile:ClientSecret"];
        private string? RefreshToken => _configuration["GoogleBusinessProfile:RefreshToken"];
        private string? AccountId => _configuration["GoogleBusinessProfile:AccountId"];
        private string? LocationId => _configuration["GoogleBusinessProfile:LocationId"];

        public bool IsConfigured =>
            !string.IsNullOrWhiteSpace(ClientId) &&
            !string.IsNullOrWhiteSpace(ClientSecret) &&
            !string.IsNullOrWhiteSpace(RefreshToken) &&
            !string.IsNullOrWhiteSpace(AccountId) &&
            !string.IsNullOrWhiteSpace(LocationId);

        public async Task<GoogleReviewSyncResult> SyncReviewsAsync(CancellationToken cancellationToken = default)
        {
            if (!IsConfigured)
                throw new InvalidOperationException("GoogleBusinessProfile is not configured.");

            var accessToken = await GetAccessTokenAsync(cancellationToken);

            var fetched = await FetchAllReviewsAsync(accessToken, cancellationToken);
            _logger.LogInformation(
                "Fetched {Count} reviews from Google Business Profile (Google total {Total}, average {Average}).",
                fetched.Reviews.Count, fetched.TotalReviewCount, fetched.AverageRating);

            // A business with reviews never legitimately lists zero of them; reconciling an empty
            // list would delete every stored review.
            if (fetched.Reviews.Count == 0 && fetched.TotalReviewCount > 0)
                throw new GoogleReviewSyncException(
                    $"Google reported {fetched.TotalReviewCount} reviews but returned none; refusing to reconcile.");

            var stored = await ReconcileAsync(fetched, cancellationToken);
            return new GoogleReviewSyncResult(stored, fetched.TotalReviewCount, fetched.AverageRating);
        }

        /// <summary>Exchanges the long-lived refresh token for a short-lived access token.</summary>
        private async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);

            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = ClientId!,
                ["client_secret"] = ClientSecret!,
                ["refresh_token"] = RefreshToken!,
                ["grant_type"] = "refresh_token"
            });

            using var response = await client.PostAsync(TokenEndpoint, content, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                // invalid_grant = the refresh token is expired or revoked and must be regenerated.
                var isInvalidGrant = ReadOAuthError(body) == "invalid_grant";
                throw new GoogleReviewSyncException(
                    $"Google token exchange failed ({(int)response.StatusCode} {response.StatusCode}): {body}",
                    isInvalidGrant);
            }

            using var doc = JsonDocument.Parse(body);
            var token = doc.RootElement.TryGetProperty("access_token", out var tokenEl)
                ? tokenEl.GetString()
                : null;

            if (string.IsNullOrEmpty(token))
                throw new GoogleReviewSyncException("Google token exchange returned no access_token.");

            return token;
        }

        private static string? ReadOAuthError(string body)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                return doc.RootElement.TryGetProperty("error", out var errEl) && errEl.ValueKind == JsonValueKind.String
                    ? errEl.GetString()
                    : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private sealed record FetchedReviews(List<GoogleReview> Reviews, int TotalReviewCount, double AverageRating);

        /// <summary>
        /// Pages through the reviews endpoint and returns every review plus Google's aggregate.
        /// Any failed page throws — a partial list must never reach <see cref="ReconcileAsync"/>,
        /// which would delete every review on the pages that were not fetched.
        /// </summary>
        private async Task<FetchedReviews> FetchAllReviewsAsync(string accessToken, CancellationToken cancellationToken)
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            var results = new List<GoogleReview>();
            int? totalReviewCount = null;
            double? averageRating = null;
            string? pageToken = null;
            var now = DateTime.UtcNow;

            do
            {
                var url = $"{ReviewsApiBase}/accounts/{AccountId}/locations/{LocationId}/reviews?pageSize={PageSize}";
                if (!string.IsNullOrEmpty(pageToken))
                    url += $"&pageToken={Uri.EscapeDataString(pageToken)}";

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

                using var response = await client.SendAsync(request, cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);

                if (!response.IsSuccessStatusCode)
                    throw new GoogleReviewSyncException(
                        $"Google reviews request failed ({(int)response.StatusCode} {response.StatusCode}): {body}");

                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;

                // averageRating / totalReviewCount come back on every page; keep the first seen.
                if (totalReviewCount == null &&
                    root.TryGetProperty("totalReviewCount", out var totalEl) && totalEl.TryGetInt32(out var total))
                    totalReviewCount = total;
                if (averageRating == null &&
                    root.TryGetProperty("averageRating", out var avgEl) && avgEl.TryGetDouble(out var avg))
                    averageRating = avg;

                if (root.TryGetProperty("reviews", out var reviewsEl) && reviewsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var r in reviewsEl.EnumerateArray())
                    {
                        var mapped = MapReview(r, now);
                        if (mapped != null)
                            results.Add(mapped);
                    }
                }

                pageToken = root.TryGetProperty("nextPageToken", out var tokenEl)
                    ? tokenEl.GetString()
                    : null;
            }
            while (!string.IsNullOrEmpty(pageToken) && !cancellationToken.IsCancellationRequested);

            cancellationToken.ThrowIfCancellationRequested();

            // Google omits both fields for a location with no reviews.
            return new FetchedReviews(results, totalReviewCount ?? 0, averageRating ?? 0);
        }

        private static GoogleReview? MapReview(JsonElement r, DateTime now)
        {
            var reviewId = r.TryGetProperty("reviewId", out var idEl) ? idEl.GetString() : null;
            if (string.IsNullOrEmpty(reviewId))
                return null;

            string? authorName = null;
            string? photoUrl = null;
            if (r.TryGetProperty("reviewer", out var reviewer))
            {
                authorName = reviewer.TryGetProperty("displayName", out var dn) ? dn.GetString() : null;
                photoUrl = reviewer.TryGetProperty("profilePhotoUrl", out var pp) ? pp.GetString() : null;
            }

            var rating = r.TryGetProperty("starRating", out var sr) ? StarRatingToInt(sr.GetString()) : 0;
            var text = r.TryGetProperty("comment", out var c) ? c.GetString() : null;

            string? replyText = null;
            if (r.TryGetProperty("reviewReply", out var reply) &&
                reply.TryGetProperty("comment", out var rc))
            {
                replyText = rc.GetString();
            }

            var createTime = r.TryGetProperty("createTime", out var ct) ? ParseTime(ct.GetString()) : now;
            var updateTime = r.TryGetProperty("updateTime", out var ut) ? ParseTime(ut.GetString()) : createTime;

            return new GoogleReview
            {
                ReviewId = reviewId,
                AuthorName = authorName ?? "Google user",
                ProfilePhotoUrl = photoUrl,
                Rating = rating,
                Text = text,
                ReplyText = replyText,
                CreateTime = createTime,
                UpdateTime = updateTime,
                LastSyncedAt = now
            };
        }

        private static int StarRatingToInt(string? starRating) => starRating switch
        {
            "ONE" => 1,
            "TWO" => 2,
            "THREE" => 3,
            "FOUR" => 4,
            "FIVE" => 5,
            _ => 0
        };

        private static DateTime ParseTime(string? value)
        {
            if (string.IsNullOrEmpty(value))
                return DateTime.UtcNow;
            return DateTimeOffset.Parse(value, null, System.Globalization.DateTimeStyles.AssumeUniversal)
                .UtcDateTime;
        }

        /// <summary>
        /// Upserts fetched reviews by Google review id (edits update the existing row, nothing
        /// duplicates), deletes any stored review no longer present on Google, and records the
        /// Google aggregate plus a clean sync state — all in one SaveChanges.
        /// </summary>
        private async Task<int> ReconcileAsync(FetchedReviews fetched, CancellationToken cancellationToken)
        {
            using var scope = _serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var existing = await context.GoogleReviews.ToListAsync(cancellationToken);
            var existingById = existing.ToDictionary(e => e.ReviewId);
            // Google review ids are unique, but guard anyway so a duplicate in the feed can't
            // make EF track two entities with the same key.
            var fetchedById = new Dictionary<string, GoogleReview>();
            foreach (var review in fetched.Reviews)
                fetchedById[review.ReviewId] = review;

            // Insert / update.
            foreach (var review in fetchedById.Values)
            {
                if (existingById.TryGetValue(review.ReviewId, out var current))
                {
                    current.AuthorName = review.AuthorName;
                    current.ProfilePhotoUrl = review.ProfilePhotoUrl;
                    current.Rating = review.Rating;
                    current.Text = review.Text;
                    current.ReplyText = review.ReplyText;
                    current.CreateTime = review.CreateTime;
                    current.UpdateTime = review.UpdateTime;
                    current.LastSyncedAt = review.LastSyncedAt;
                    // IsHidden is admin-owned — intentionally left untouched.
                }
                else
                {
                    context.GoogleReviews.Add(review);
                }
            }

            // Delete reviews removed on Google.
            var toRemove = existing.Where(e => !fetchedById.ContainsKey(e.ReviewId)).ToList();
            if (toRemove.Count > 0)
            {
                context.GoogleReviews.RemoveRange(toRemove);
                _logger.LogInformation("Removing {Count} reviews no longer present on Google.", toRemove.Count);
            }

            // Google's aggregate + a clean sync state. A success ends any failure streak and
            // re-arms the failure alert.
            var now = DateTime.UtcNow;
            var state = await context.GoogleReviewSyncStates
                .FirstOrDefaultAsync(s => s.Id == GoogleReviewSyncState.SingletonId, cancellationToken);
            if (state == null)
            {
                state = new GoogleReviewSyncState();
                context.GoogleReviewSyncStates.Add(state);
            }
            state.TotalReviewCount = fetched.TotalReviewCount;
            state.AverageRating = fetched.AverageRating;
            state.LastSuccessAt = now;
            state.LastAttemptAt = now;
            state.LastError = null;
            state.LastErrorAt = null;
            state.ConsecutiveFailures = 0;
            state.AlertSentAt = null;

            await context.SaveChangesAsync(cancellationToken);

            // Drop the cached snapshot + stats so GoogleReviewsController rebuilds them from the
            // fresh DB state right away instead of waiting out the 7-day TTL.
            _cache.Remove(AllReviewsCacheKey);
            _cache.Remove(StatsCacheKey);

            return fetchedById.Count;
        }
    }
}
