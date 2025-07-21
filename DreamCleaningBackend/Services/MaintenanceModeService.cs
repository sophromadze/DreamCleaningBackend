using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace DreamCleaningBackend.Services
{
    public class MaintenanceModeService : IMaintenanceModeService
    {
        private readonly ApplicationDbContext _context;

        public MaintenanceModeService(ApplicationDbContext context)
        {
            _context = context;
        }

        public async Task<MaintenanceModeDto> GetMaintenanceModeStatus()
        {
            var maintenanceMode = await _context.MaintenanceModes
                .OrderByDescending(m => m.CreatedAt)
                .FirstOrDefaultAsync();

            if (maintenanceMode == null)
            {
                return new MaintenanceModeDto
                {
                    IsEnabled = false,
                    Message = null,
                    StartedAt = null,
                    EndedAt = null,
                    StartedBy = null
                };
            }

            return new MaintenanceModeDto
            {
                IsEnabled = maintenanceMode.IsEnabled,
                Message = maintenanceMode.Message,
                StartedAt = maintenanceMode.StartedAt,
                EndedAt = maintenanceMode.EndedAt,
                StartedBy = maintenanceMode.StartedBy
            };
        }

        public async Task<MaintenanceModeDto> ToggleMaintenanceMode(ToggleMaintenanceModeDto dto, string startedBy)
        {
            var maintenanceMode = new MaintenanceMode
            {
                IsEnabled = dto.IsEnabled,
                Message = dto.Message,
                StartedBy = startedBy,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            if (dto.IsEnabled)
            {
                maintenanceMode.StartedAt = DateTime.UtcNow;
            }
            else
            {
                maintenanceMode.EndedAt = DateTime.UtcNow;
            }

            _context.MaintenanceModes.Add(maintenanceMode);
            await _context.SaveChangesAsync();

            return new MaintenanceModeDto
            {
                IsEnabled = maintenanceMode.IsEnabled,
                Message = maintenanceMode.Message,
                StartedAt = maintenanceMode.StartedAt,
                EndedAt = maintenanceMode.EndedAt,
                StartedBy = maintenanceMode.StartedBy
            };
        }

        public async Task<bool> IsMaintenanceModeEnabled()
        {
            var maintenanceMode = await _context.MaintenanceModes
                .OrderByDescending(m => m.CreatedAt)
                .FirstOrDefaultAsync();

            return maintenanceMode?.IsEnabled ?? false;
        }
    }
} 