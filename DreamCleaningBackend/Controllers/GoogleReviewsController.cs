using DreamCleaningBackend.Data;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using System.Text.RegularExpressions;

namespace DreamCleaningBackend.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class GoogleReviewsController : ControllerBase
    {
        private const int CacheDurationHours = 168; // 7 days
        private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(CacheDurationHours);
        private const int DefaultPageSize = 9;

        private readonly IMemoryCache _cache;
        private readonly ApplicationDbContext _context;

        public GoogleReviewsController(
            IMemoryCache cache,
            ApplicationDbContext context)
        {
            _cache = cache;
            _context = context;
        }

        /// <summary>
        /// The single source of truth for the review count and rating shown anywhere on the site
        /// (hero badge, /reviews header, service pages, JSON-LD AggregateRating): Google's own
        /// totalReviewCount / averageRating, stored by GoogleReviewSyncService. Until the first
        /// successful sync has stored them, falls back to the count/average of the non-hidden
        /// GoogleReviews rows. Cached 7 days; evicted by GoogleBusinessProfileService after each
        /// successful sync.
        /// </summary>
        [HttpGet("stats")]
        public async Task<IActionResult> GetStats()
        {
            var stats = await GetStatsSnapshotAsync();
            return Ok(new
            {
                rating = stats.Rating,
                total = stats.Total,
                last_synced_at = stats.LastSyncedAt
            });
        }

        /// <summary>
        /// Returns a page of the reviews persisted from the Google Business Profile API (synced by
        /// GoogleReviewSyncService), in the same legacy shape the frontend already consumes.
        /// Hidden reviews are excluded. The full non-hidden set is loaded into IMemoryCache once
        /// (7-day TTL, evicted by GoogleBusinessProfileService after each successful sync), so
        /// "Load More" paging slices the cached list without touching the DB or Google.
        /// rating / user_ratings_total are the same Google aggregate as /stats, never the row count.
        /// </summary>
        [HttpGet("all")]
        public async Task<IActionResult> GetAllReviews([FromQuery] int page = 1, [FromQuery] int pageSize = DefaultPageSize)
        {
            if (page < 1) page = 1;
            if (pageSize < 1) pageSize = DefaultPageSize;

            var snapshot = await GetAllReviewsSnapshotAsync();
            var stats = await GetStatsSnapshotAsync();

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

            // Paging runs over the displayable (text-bearing) list; the headline total/rating
            // are the Google aggregate, so has_more must use the displayable count.
            var hasMore = (long)page * pageSize < snapshot.Reviews.Count;

            var result = new
            {
                result = new
                {
                    name = "Dream Cleaning",
                    rating = stats.Rating,
                    user_ratings_total = stats.Total,
                    reviews = pageItems,
                    page,
                    page_size = pageSize,
                    has_more = hasMore
                }
            };

            return Ok(result);
        }

        /// <summary>
        /// Builds (or returns from cache) the count/rating served by /stats and /all.
        /// Cached under <see cref="GoogleBusinessProfileService.StatsCacheKey"/>.
        /// </summary>
        private async Task<StatsSnapshot> GetStatsSnapshotAsync()
        {
            if (_cache.TryGetValue(GoogleBusinessProfileService.StatsCacheKey, out StatsSnapshot? cached) && cached != null)
            {
                return cached;
            }

            var state = await _context.GoogleReviewSyncStates
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == GoogleReviewSyncState.SingletonId);

            StatsSnapshot snapshot;
            if (state?.TotalReviewCount != null && state.AverageRating != null)
            {
                snapshot = new StatsSnapshot
                {
                    Rating = Math.Round(state.AverageRating.Value, 1),
                    Total = state.TotalReviewCount.Value,
                    LastSyncedAt = state.LastSuccessAt
                };
            }
            else
            {
                // No successful sync has stored Google's aggregate yet.
                var ratings = await _context.GoogleReviews
                    .Where(r => !r.IsHidden)
                    .Select(r => r.Rating)
                    .ToListAsync();
                snapshot = new StatsSnapshot
                {
                    Rating = ratings.Count > 0 ? Math.Round(ratings.Average(), 1) : 0,
                    Total = ratings.Count,
                    LastSyncedAt = state?.LastSuccessAt
                };
            }

            _cache.Set(GoogleBusinessProfileService.StatsCacheKey, snapshot,
                new MemoryCacheEntryOptions().SetAbsoluteExpiration(CacheDuration));

            return snapshot;
        }

        /// <summary>
        /// Builds (or returns from cache) the displayable review list used to serve pages.
        /// Cached under <see cref="GoogleBusinessProfileService.AllReviewsCacheKey"/> for 7 days;
        /// the sync service evicts it after a successful pull.
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
                // restricted content like prices are hidden).
                Reviews = reviews
                    .Where(r => IsDisplayableReviewText(r.Text))
                    .Select(r => new SnapshotReview
                    {
                        AuthorName = r.AuthorName,
                        ProfilePhotoUrl = r.ProfilePhotoUrl ?? "",
                        Rating = r.Rating,
                        Text = r.Text ?? "",
                        Time = new DateTimeOffset(DateTime.SpecifyKind(r.CreateTime, DateTimeKind.Utc)).ToUnixTimeSeconds()
                    }).ToList()
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

        /// <summary>The displayable review set, cached as one unit.</summary>
        private sealed class AllReviewsSnapshot
        {
            public List<SnapshotReview> Reviews { get; init; } = new();
        }

        /// <summary>The Google aggregate (or the pre-first-sync DB fallback), cached as one unit.</summary>
        private sealed class StatsSnapshot
        {
            public double Rating { get; init; }
            public int Total { get; init; }
            public DateTime? LastSyncedAt { get; init; }
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
    }
}
