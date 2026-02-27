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

            if (_cache.TryGetValue(cacheKey, out string? cachedContent))
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

                var url = $"https://maps.googleapis.com/maps/api/place/details/json?" +
                         $"place_id={placeId}" +
                         $"&fields=name,formatted_address,geometry,rating,reviews,user_ratings_total" +
                         $"&key={apiKey}";

                var response = await _httpClient.GetAsync(url);

                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    var jsonDocument = JsonDocument.Parse(content);

                    if (jsonDocument.RootElement.TryGetProperty("error_message", out var errorMessage))
                    {
                        _logger.LogError($"Google Maps API error: {errorMessage.GetString()}");
                        return BadRequest(new { error = errorMessage.GetString() });
                    }

                    _cache.Set(cacheKey, content, new MemoryCacheEntryOptions().SetAbsoluteExpiration(CacheDuration));

                    return Ok(jsonDocument.RootElement);
                }
                else
                {
                    _logger.LogError($"Google Maps API request failed with status: {response.StatusCode}");
                    return StatusCode((int)response.StatusCode, new { error = "Failed to fetch reviews from Google" });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching Google reviews");
                return StatusCode(500, new { error = "An error occurred while fetching reviews" });
            }
        }
    }
}