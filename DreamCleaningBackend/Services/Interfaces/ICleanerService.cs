using DreamCleaningBackend.DTOs;

namespace DreamCleaningBackend.Services.Interfaces
{
    public interface ICleanerService
    {
        Task<List<AvailableCleanerDto>> GetAvailableCleanersAsync(DateTime serviceDate, string serviceTime);
        Task<bool> AssignCleanersToOrderAsync(AssignCleanersDto dto, int assignedBy);
        Task<bool> UnassignCleanerFromOrderAsync(int orderId, int cleanerId, int removedBy);

        /// <summary>
        /// Sends assignment emails only to cleaners on this order who have not been emailed yet, then sets AssignmentNotificationSentAt.
        /// </summary>
        Task<SendCleanerAssignmentMailsResultDto?> SendPendingCleanerAssignmentMailsAsync(int orderId);

        /// <summary>
        /// Re-sends the assignment email to one specific cleaner, resets reminder logs for that cleaner+order,
        /// and restarts that cleaner's reminder flow from scratch.
        /// </summary>
        Task<SendCleanerAssignmentMailsResultDto?> ResendCleanerAssignmentMailAsync(int orderId, int cleanerId);
    }
}
