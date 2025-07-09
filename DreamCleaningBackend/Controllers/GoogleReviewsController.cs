using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace DreamCleaningBackend.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class GoogleReviewsController : ControllerBase
    {
        private readonly HttpClient _httpClient;
        private readonly IConfiguration _configuration;
        private readonly ILogger<GoogleReviewsController> _logger;

        public GoogleReviewsController(
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            ILogger<GoogleReviewsController> logger)
        {
            _httpClient = httpClientFactory.CreateClient();
            _configuration = configuration;
            _logger = logger;
        }

        [HttpGet("{placeId}")]
        public async Task<IActionResult> GetGoogleReviews(string placeId)
        {
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