using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using System.Text.Json;

namespace DreamCleaningBackend.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class GoogleReviewsController : ControllerBase
    {
        private const int CacheDurationHours = 168; // 7 days
        private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(CacheDurationHours);
        private const string NewPlacesFieldMask = "displayName,rating,userRatingCount,reviews";

        private readonly HttpClient _httpClient;
        private readonly IConfiguration _configuration;
        private readonly ILogger<GoogleReviewsController> _logger;
        private readonly IMemoryCache _cache;

        public GoogleReviewsController(
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            ILogger<GoogleReviewsController> logger,
            IMemoryCache cache)
        {
            _httpClient = httpClientFactory.CreateClient();
            _configuration = configuration;
            _logger = logger;
            _cache = cache;
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