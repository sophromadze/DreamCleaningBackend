using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services.Interfaces;

namespace DreamCleaningBackend.Services
{
    public class BubbleRewardsSettingsService : IBubbleRewardsSettingsService
    {
        private readonly ApplicationDbContext _context;
        private readonly IMemoryCache _cache;
        private readonly ILogger<BubbleRewardsSettingsService> _logger;
        private const string CacheKeyPrefix = "BubbleSettings_";
        private const string AllSettingsCacheKey = "BubbleSettings_All";
        private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

        public BubbleRewardsSettingsService(
            ApplicationDbContext context,
            IMemoryCache cache,
            ILogger<BubbleRewardsSettingsService> logger)
        {
            _context = context;
            _cache = cache;
            _logger = logger;
        }

        public async Task<string> GetSetting(string key, string defaultValue = "")
        {
            var cacheKey = CacheKeyPrefix + key;
            if (_cache.TryGetValue(cacheKey, out string? cached) && cached != null)
                return cached;

            var setting = await _context.BubbleRewardsSettings
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.SettingKey == key);

            var value = setting?.SettingValue ?? defaultValue;
            _cache.Set(cacheKey, value, CacheDuration);
            return value;
        }

        public async Task<T> GetSetting<T>(string key, T defaultValue = default!)
        {
            var raw = await GetSetting(key, string.Empty);
            if (string.IsNullOrEmpty(raw)) return defaultValue;

            try
            {
                if (typeof(T) == typeof(bool))
                    return (T)(object)bool.Parse(raw);
                if (typeof(T) == typeof(int))
                    return (T)(object)int.Parse(raw);
                if (typeof(T) == typeof(decimal))
                    return (T)(object)decimal.Parse(raw);
                if (typeof(T) == typeof(double))
                    return (T)(object)double.Parse(raw);
                return (T)(object)raw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse setting {Key} as {Type}", key, typeof(T).Name);
                return defaultValue;
            }
        }

        public async Task SetSetting(string key, string value)
        {
            var setting = await _context.BubbleRewardsSettings
                .FirstOrDefaultAsync(s => s.SettingKey == key);

            if (setting == null)
            {
                _logger.LogWarning("Attempted to update non-existent setting: {Key}", key);
                return;
            }

            setting.SettingValue = value;
            setting.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            // Invalidate caches
            _cache.Remove(CacheKeyPrefix + key);
            _cache.Remove(AllSettingsCacheKey);
        }

        public async Task<List<BubbleRewardsSettingDto>> GetAllSettings()
        {
            if (_cache.TryGetValue(AllSettingsCacheKey, out List<BubbleRewardsSettingDto>? cached) && cached != null)
                return cached;

            var settings = await _context.BubbleRewardsSettings
                .AsNoTracking()
                .OrderBy(s => s.Category)
                .ThenBy(s => s.SettingKey)
                .Select(s => new BubbleRewardsSettingDto
                {
                    Id = s.Id,
                    SettingKey = s.SettingKey,
                    SettingValue = s.SettingValue,
                    Description = s.Description,
                    Category = s.Category,
                    UpdatedAt = s.UpdatedAt
                })
                .ToListAsync();

            _cache.Set(AllSettingsCacheKey, settings, CacheDuration);
            return settings;
        }

        public async Task<List<BubbleRewardsSettingDto>> GetSettingsByCategory(string category)
        {
            var all = await GetAllSettings();
            return all.Where(s => s.Category.Equals(category, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        public async Task BulkUpdateSettings(List<BulkUpdateSettingDto> updates)
        {
            var keys = updates.Select(u => u.Key).ToList();
            var settings = await _context.BubbleRewardsSettings
                .Where(s => keys.Contains(s.SettingKey))
                .ToListAsync();

            foreach (var update in updates)
            {
                var setting = settings.FirstOrDefault(s => s.SettingKey == update.Key);
                if (setting != null)
                {
                    setting.SettingValue = update.Value;
                    setting.UpdatedAt = DateTime.UtcNow;
                }
            }

            await _context.SaveChangesAsync();
            InvalidateCache();
        }

        public void InvalidateCache()
        {
            _cache.Remove(AllSettingsCacheKey);
            // Individual keys will expire naturally or get refreshed on next read
        }
    }
}
