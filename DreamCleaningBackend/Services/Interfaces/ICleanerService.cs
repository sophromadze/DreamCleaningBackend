using DreamCleaningBackend.DTOs;

namespace DreamCleaningBackend.Services.Interfaces
{
    public interface ICleanerService
    {
        Task<List<CleanerCalendarDto>> GetCleanerCalendarAsync(int cleanerId);
        Task<CleanerOrderDetailDto> GetOrderDetailsForCleanerAsync(int orderId, int cleanerId);
        Task<List<AvailableCleanerDto>> GetAvailableCleanersAsync(DateTime serviceDate, string serviceTime);
        Task<bool> AssignCleanersToOrderAsync(AssignCleanersDto dto, int assignedBy);
        Task<bool> UnassignCleanerFromOrderAsync(int orderId, int cleanerId, int removedBy);
    }
}