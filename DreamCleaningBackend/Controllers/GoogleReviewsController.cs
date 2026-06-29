using DreamCleaningBackend.Data;
using DreamCleaningBackend.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DreamCleaningBackend.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class GoogleReviewsController : ControllerBase
    {
        private const int CacheDurationHours = 168; // 7 days
        private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(CacheDurationHours);
        private const string NewPlacesFieldMask = "displayName,rating,userRatingCount,reviews";
        private const int DefaultPageSize = 9;

        private readonly HttpClient _httpClient;
        private readonly IConfiguration _configuration;
        private readonly ILogger<GoogleReviewsController> _logger;
        private readonly IMemoryCache _cache;
        private readonly ApplicationDbContext _context;

        public GoogleReviewsController(
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            ILogger<GoogleReviewsController> logger,
            IMemoryCache cache,
            ApplicationDbContext context)
        {
            _httpClient = httpClientFactory.CreateClient();
            _configuration = configuration;
            _logger = logger;
            _cache = cache;
            _context = context;
        }

        /// <summary>
        /// Returns a page of the reviews persisted from the Google Business Profile API (synced by
        /// GoogleReviewSyncService), in the same legacy shape the frontend already consumes.
        /// Hidden reviews are excluded. The full non-hidden set is loaded into IMemoryCache once
        /// (7-day TTL, evicted by GoogleBusinessProfileService after each successful sync), so
        /// "Load More" paging slices the cached list without touching the DB or Google.
        /// Returns an empty review list when none are stored yet, so the frontend can fall back
        /// to the 5-review Places endpoint.
        /// </summary>
        [HttpGet("all")]
        public async Task<IActionResult> GetAllReviews([FromQuery] int page = 1, [FromQuery] int pageSize = DefaultPageSize)
        {
            if (page < 1) page = 1;
            if (pageSize < 1) pageSize = DefaultPageSize;

            var snapshot = await GetAllReviewsSnapshotAsync();

            var pageItems = snapshot.Reviews
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(r => new
                {
                    author_name = r.AuthorName,
                    profile_photo_url = r.ProfilePhotoUrl,
                    rating = r.Rating,
                    text = r.Text,
                    time = r.Time
                })
                .ToList();

            // Paging runs over the displayable (text-bearing) list; the headline total/rating below
            // stay the true Google aggregate, so has_more must use the displayable count.
            var hasMore = (long)page * pageSize < snapshot.Reviews.Count;

            var result = new
            {
                result = new
                {
                    name = "Dream Cleaning",
                    rating = snapshot.OverallRating,
                    user_ratings_total = snapshot.Total,
                    reviews = pageItems,
                    page,
                    page_size = pageSize,
                    has_more = hasMore
                }
            };

            return Ok(result);
        }

        /// <summary>
        /// Builds (or returns from cache) the full non-hidden review snapshot used to serve pages.
        /// Cached under <see cref="GoogleBusinessProfileService.AllReviewsCacheKey"/> for the same
        /// 7-day window as the Places endpoint; the sync service evicts it after a successful pull.
        /// </summary>
        private async Task<AllReviewsSnapshot> GetAllReviewsSnapshotAsync()
        {
            if (_cache.TryGetValue(GoogleBusinessProfileService.AllReviewsCacheKey, out AllReviewsSnapshot? cached) && cached != null)
            {
                return cached;
            }

            var reviews = await _context.GoogleReviews
                .Where(r => !r.IsHidden)
                .OrderByDescending(r => r.CreateTime)
                .ToListAsync();

            var snapshot = new AllReviewsSnapshot
            {
                // Grid shows only reviews with displayable text (star-only ratings and reviews with
                // restricted content like prices are hidden). Total/rating below stay the true
                // Google aggregate over every non-hidden review.
                Reviews = reviews
                    .Where(r => IsDisplayableReviewText(r.Text))
                    .Select(r => new SnapshotReview
                    {
                        AuthorName = r.AuthorName,
                        ProfilePhotoUrl = r.ProfilePhotoUrl ?? "",
                        Rating = r.Rating,
                        Text = r.Text ?? "",
                        Time = new DateTimeOffset(DateTime.SpecifyKind(r.CreateTime, DateTimeKind.Utc)).ToUnixTimeSeconds()
                    }).ToList(),
                OverallRating = reviews.Count > 0 ? Math.Round(reviews.Average(r => r.Rating), 1) : 0,
                Total = reviews.Count
            };

            _cache.Set(GoogleBusinessProfileService.AllReviewsCacheKey, snapshot,
                new MemoryCacheEntryOptions().SetAbsoluteExpiration(CacheDuration));

            return snapshot;
        }

        /// <summary>Cached, pre-mapped review used to serve pages without re-querying the DB.</summary>
        private sealed class SnapshotReview
        {
            public string AuthorName { get; init; } = string.Empty;
            public string ProfilePhotoUrl { get; init; } = string.Empty;
            public double Rating { get; init; }
            public string Text { get; init; } = string.Empty;
            public long Time { get; init; }
        }

        /// <summary>The full non-hidden review set plus totals, cached as one unit.</summary>
        private sealed class AllReviewsSnapshot
        {
            public List<SnapshotReview> Reviews { get; init; } = new();
            public double OverallRating { get; init; }
            public int Total { get; init; }
        }

        // Matches a price reference like "$50", "$ 50", "50 dollars", "20usd".
        private static readonly Regex RestrictedContentRegex =
            new(@"\$\s?\d|\b\d+\s?(?:dollars?|usd)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// A review is shown only when it has real written text and no restricted content
        /// (e.g. a price). Star-only ratings and price-mentioning reviews are hidden from the grid.
        /// </summary>
        private static bool IsDisplayableReviewText(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;
            return !RestrictedContentRegex.IsMatch(text);
        }

        [HttpGet("{placeId}")]
        public async Task<IActionResult> GetGoogleReviews(string placeId)
        {
            var cacheKey = $"GoogleReviews:{placeId}";

            if (_cache.TryGetValue(cacheKey, out string? cachedContent) && cachedContent != null)
            {
                return Content(cachedContent, "application/json");
            }

            try
            {
                var apiKey = _configuration["GoogleMaps:ApiKey"];

                if (string.IsNullOrEmpty(apiKey))
                {
                    _logger.LogError("Google Maps API key is not configured");
                    return StatusCode(500, new { error = "API key not configured" });
                }

                var url = $"https://places.googleapis.com/v1/places/{placeId}";
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("X-Goog-Api-Key", apiKey);
                request.Headers.Add("X-Goog-FieldMask", NewPlacesFieldMask);

                var response = await _httpClient.SendAsync(request);

                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    var legacyJson = MapNewPlacesResponseToLegacy(content);
                    if (legacyJson == null)
                    {
                        _logger.LogError("Failed to map Places API (New) response");
                        return StatusCode(500, new { error = "An error occurred while processing reviews" });
                    }

                    _cache.Set(cacheKey, legacyJson, new MemoryCacheEntryOptions().SetAbsoluteExpiration(CacheDuration));

                    return Content(legacyJson, "application/json");
                }
                else
                {
                    var errorBody = await response.Content.ReadAsStringAsync();
                    _logger.LogError("Places API (New) request failed with status: {StatusCode}, body: {Body}", response.StatusCode, errorBody);
                    return StatusCode((int)response.StatusCode, new { error = "Failed to fetch reviews from Google" });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching Google reviews");
                return StatusCode(500, new { error = "An error occurred while fetching reviews" });
            }
        }

        /// <summary>
        /// Maps Places API (New) response to the legacy shape expected by the frontend.
        /// </summary>
        private static string? MapNewPlacesResponseToLegacy(string newApiJson)
        {
            try
            {
                using var doc = JsonDocument.Parse(newApiJson);
                var root = doc.RootElement;

                var name = GetString(root, "displayName", "text") ?? "";
                var rating = GetDouble(root, "rating");
                var userRatingCount = GetInt32(root, "userRatingCount");

                var reviews = new List<object>();
                if (root.TryGetProperty("reviews", out var reviewsEl) && reviewsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var r in reviewsEl.EnumerateArray())
                    {
                        var authorName = GetString(r, "authorAttribution", "displayName");
                        var profilePhotoUrl = GetString(r, "authorAttribution", "photoUri");
                        var reviewRating = GetDouble(r, "rating");
                        var text = GetString(r, "text", "text");
                        var publishTime = GetString(r, "publishTime");
                        var timeSeconds = ParsePublishTimeToUnixSeconds(publishTime);

                        // Same display rule as the /all grid: skip star-only and price-mentioning reviews.
                        if (!IsDisplayableReviewText(text))
                            continue;

                        reviews.Add(new
                        {
                            author_name = authorName ?? "",
                            profile_photo_url = profilePhotoUrl ?? "",
                            rating = reviewRating ?? 0,
                            text = text ?? "",
                            time = timeSeconds
                        });
                    }
                }

                var result = new
                {
                    result = new
                    {
                        name,
                        rating = rating ?? 0,
                        user_ratings_total = userRatingCount ?? 0,
                        reviews
                    }
                };

                return JsonSerializer.Serialize(result);
            }
            catch
            {
                return null;
            }
        }

        private static string? GetString(JsonElement parent, string prop1, string? prop2 = null)
        {
            if (!parent.TryGetProperty(prop1, out var el))
                return null;
            if (prop2 != null)
            {
                if (!el.TryGetProperty(prop2, out var inner))
                    return null;
                return inner.GetString();
            }
            return el.GetString();
        }

        private static double? GetDouble(JsonElement parent, string prop)
        {
            if (!parent.TryGetProperty(prop, out var el))
                return null;
            if (el.TryGetDouble(out var d))
                return d;
            return null;
        }

        private static int? GetInt32(JsonElement parent, string prop)
        {
            if (!parent.TryGetProperty(prop, out var el))
                return null;
            if (el.TryGetInt32(out var i))
                return i;
            return null;
        }

        private static long ParsePublishTimeToUnixSeconds(string? publishTime)
        {
            if (string.IsNullOrEmpty(publishTime))
                return 0;
            try
            {
                var dt = DateTimeOffset.Parse(publishTime, null, System.Globalization.DateTimeStyles.AssumeUniversal);
                return dt.ToUnixTimeSeconds();
            }
            catch
            {
                return 0;
            }
        }
    }
}