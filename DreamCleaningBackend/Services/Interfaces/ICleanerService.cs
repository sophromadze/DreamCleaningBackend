using DreamCleaningBackend.DTOs;

namespace DreamCleaningBackend.Services.Interfaces
{
    public interface ICleanerService
    {
        Task<List<CleanerCalendarDto>> GetCleanerCalendarAsync(int userId, string userRole, DateTime startDate, DateTime endDate);
        Task<CleanerOrderDetailDto> GetOrderDetailsForCleanerAsync(int orderId, int userId, string userRole);
        Task<List<AvailableCleanerDto>> GetAvailableCleanersAsync(DateTime serviceDate, string serviceTime);
        Task<bool> AssignCleanersToOrderAsync(AssignCleanersDto dto, int assignedBy);
        Task<bool> UnassignCleanerFromOrderAsync(int orderId, int cleanerId, int removedBy);
    }
}