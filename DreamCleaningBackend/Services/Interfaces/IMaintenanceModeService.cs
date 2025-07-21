using DreamCleaningBackend.DTOs;

namespace DreamCleaningBackend.Services.Interfaces
{
    public interface IMaintenanceModeService
    {
        Task<MaintenanceModeDto> GetMaintenanceModeStatus();
        Task<MaintenanceModeDto> ToggleMaintenanceMode(ToggleMaintenanceModeDto dto, string startedBy);
        Task<bool> IsMaintenanceModeEnabled();
    }
} 