using System.Net.Http.Headers;
using System.Text.Json;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.Models;
using Microsoft.EntityFrameworkCore;

namespace DreamCleaningBackend.Services
{
    public interface IGoogleBusinessProfileService
    {
        /// <summary>True only when ClientId, ClientSecret, RefreshToken, AccountId and LocationId are all configured.</summary>
        bool IsConfigured { get; }

        /// <summary>
        /// Pulls every review from the Google Business Profile API and reconciles them into the
        /// GoogleReviews table: inserts new reviews, updates changed ones, deletes reviews that no
        /// longer exist on Google. Returns the number of reviews now stored.
        /// </summary>
        Task<int> SyncReviewsAsync(CancellationToken cancellationToken = default);
    }

    public class GoogleBusinessProfileService : IGoogleBusinessProfileService
    {
        // Named HttpClient registered in Program.cs with an IPv4-forced handler (VPS has IPv6 disabled).
        public const string HttpClientName = "GoogleBusinessProfile";

        private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
        // v4 is the only Business Profile API version that exposes reviews.
        private const string ReviewsApiBase = "https://mybusiness.googleapis.com/v4";
        private const int PageSize = 50; // API max per page.

        private readonly IServiceProvider _serviceProvider;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConfiguration _configuration;
        private readonly ILogger<GoogleBusinessProfileService> _logger;

        public GoogleBusinessProfileService(
            IServiceProvider serviceProvider,
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            ILogger<GoogleBusinessProfileService> logger)
        {
            _serviceProvider = serviceProvider;
            _httpClientFactory = httpClientFactory;
            _configuration = configuration;
            _logger = logger;
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

        public async Task<int> SyncReviewsAsync(CancellationToken cancellationToken = default)
        {
            if (!IsConfigured)
            {
                _logger.LogInformation("GoogleBusinessProfile is not configured; skipping review sync.");
                return 0;
            }

            var accessToken = await GetAccessTokenAsync(cancellationToken);
            if (string.IsNullOrEmpty(accessToken))
            {
                _logger.LogError("Could not obtain a Google Business Profile access token; skipping review sync.");
                return 0;
            }

            var fetched = await FetchAllReviewsAsync(accessToken, cancellationToken);
            _logger.LogInformation("Fetched {Count} reviews from Google Business Profile.", fetched.Count);

            return await ReconcileAsync(fetched, cancellationToken);
        }

        /// <summary>Exchanges the long-lived refresh token for a short-lived access token.</summary>
        private async Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken)
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
                _logger.LogError("Google token exchange failed ({Status}): {Body}", response.StatusCode, body);
                return null;
            }

            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("access_token", out var tokenEl)
                ? tokenEl.GetString()
                : null;
        }

        /// <summary>Pages through the reviews endpoint and returns every review.</summary>
        private async Task<List<GoogleReview>> FetchAllReviewsAsync(string accessToken, CancellationToken cancellationToken)
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            var results = new List<GoogleReview>();
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
                {
                    _logger.LogError("Google reviews request failed ({Status}): {Body}", response.StatusCode, body);
                    break;
                }

                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;

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

            return results;
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

        /// <summary>Upserts fetched reviews and deletes any stored review no longer present on Google.</summary>
        private async Task<int> ReconcileAsync(List<GoogleReview> fetched, CancellationToken cancellationToken)
        {
            using var scope = _serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var existing = await context.GoogleReviews.ToListAsync(cancellationToken);
            var existingById = existing.ToDictionary(e => e.ReviewId);
            var fetchedIds = new HashSet<string>(fetched.Select(f => f.ReviewId));

            // Insert / update.
            foreach (var review in fetched)
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
            var toRemove = existing.Where(e => !fetchedIds.Contains(e.ReviewId)).ToList();
            if (toRemove.Count > 0)
            {
                context.GoogleReviews.RemoveRange(toRemove);
                _logger.LogInformation("Removing {Count} reviews no longer present on Google.", toRemove.Count);
            }

            await context.SaveChangesAsync(cancellationToken);
            return fetched.Count;
        }
    }
}
