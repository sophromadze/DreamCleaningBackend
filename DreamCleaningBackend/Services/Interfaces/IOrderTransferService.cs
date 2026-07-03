using DreamCleaningBackend.DTOs;

namespace DreamCleaningBackend.Services.Interfaces
{
    /// <summary>SuperAdmin-only order reassignment between user accounts, with exact undo.</summary>
    public interface IOrderTransferService
    {
        /// <summary>Moves the order (and everything it gave the source user — points, spent
        /// amount, first-time flag, photos, apartment) onto the target user. Throws
        /// InvalidOperationException with a user-readable message when the transfer is not allowed.</summary>
        Task<OrderTransferDto> TransferAsync(int orderId, int targetUserId, int superAdminId, string? notes);

        /// <summary>Reverts a transfer by applying the recorded snapshot/deltas back. Only the
        /// latest non-undone transfer of an order can be undone, and only while the order still
        /// belongs to the transfer's target user.</summary>
        Task<OrderTransferDto> UndoAsync(int transferId, int superAdminId);

        /// <summary>Transfer history for one order, newest first.</summary>
        Task<List<OrderTransferDto>> GetTransfersForOrderAsync(int orderId);
    }
}
