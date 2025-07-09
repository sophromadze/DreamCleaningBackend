using Microsoft.AspNetCore.Mvc;

namespace DreamCleaningBackend.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class LocationController : ControllerBase
    {
        // For now, these are hardcoded. In the future, you could move these to a database table
        private readonly List<string> _states = new List<string> { "New York" };
        private readonly Dictionary<string, List<string>> _citiesByState = new Dictionary<string, List<string>>
        {
            { "New York", new List<string> { "Manhattan", "Brooklyn", "Queens" } }
        };

        [HttpGet("states")]
        public ActionResult<List<string>> GetStates()
        {
            return Ok(_states);
        }

        [HttpGet("cities/{state}")]
        public ActionResult<List<string>> GetCities(string state)
        {
            if (_citiesByState.ContainsKey(state))
            {
                return Ok(_citiesByState[state]);
            }
            return Ok(new List<string>());
        }

        [HttpGet("cities")]
        public ActionResult<List<string>> GetAllCities()
        {
            var allCities = new List<string>();
            foreach (var cities in _citiesByState.Values)
            {
                allCities.AddRange(cities);
            }
            return Ok(allCities.Distinct().OrderBy(c => c).ToList());
        }
    }
}