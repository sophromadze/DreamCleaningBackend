using DreamCleaningBackend.DTOs;

namespace DreamCleaningBackend.Services.Interfaces
{
    public interface IBubbleRewardsSettingsService
    {
        Task<string> GetSetting(string key, string defaultValue = "");
        Task<T> GetSetting<T>(string key, T defaultValue = default!);
        Task SetSetting(string key, string value);
        Task<List<BubbleRewardsSettingDto>> GetAllSettings();
        Task<List<BubbleRewardsSettingDto>> GetSettingsByCategory(string category);
        Task BulkUpdateSettings(List<BulkUpdateSettingDto> updates);
        void InvalidateCache();
    }
}
