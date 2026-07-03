using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services.Interfaces;

namespace DreamCleaningBackend.Services
{
    /// <summary>
    /// Everything captured before a transfer runs, serialized into OrderTransfer.SnapshotJson.
    /// Undo applies DELTAS back (subtract what was added, re-add what was removed) rather than
    /// blindly restoring absolute balances, so unrelated activity that happened between transfer
    /// and undo (e.g. the target earning points on another order) is never clobbered.
    /// </summary>
    public class OrderTransferSnapshot
    {
        // Order fields as they were BEFORE the transfer
        public int OrderUserId { get; set; }
        public int? OrderApartmentId { get; set; }
        public string? OrderApartmentName { get; set; }
        public string OrderContactFirstName { get; set; } = "";
        public string OrderContactLastName { get; set; } = "";
        public string OrderContactEmail { get; set; } = "";
        public string OrderContactPhone { get; set; } = "";

        // Reward movement actually applied (0 when the order wasn't completed yet)
        public int PointsMoved { get; set; }
        public decimal SpentAmountMoved { get; set; }
        public bool StreakDecrementedOnSource { get; set; }
        public bool StreakIncrementedOnTarget { get; set; }

        // FirstTimeOrder flags BEFORE the transfer
        public bool SourceFirstTimeOrderBefore { get; set; }
        public bool TargetFirstTimeOrderBefore { get; set; }

        // Ids of BubblePointsHistory rows moved from source to target
        public List<int> MovedPointsHistoryIds { get; set; } = new();

        // Ids of UserCleaningPhoto rows moved from source to target
        public List<int> MovedPhotoIds { get; set; } = new();

        // Apartment created on the target during transfer (null when an existing one matched)
        public int? CreatedApartmentId { get; set; }
    }

    public class OrderTransferService : IOrderTransferService
    {
        private readonly ApplicationDbContext _context;
        private readonly IAuditService _auditService;
        private readonly ILogger<OrderTransferService> _logger;

        public OrderTransferService(ApplicationDbContext context, IAuditService auditService,
            ILogger<OrderTransferService> logger)
        {
            _context = context;
            _auditService = auditService;
            _logger = logger;
        }

        public async Task<OrderTransferDto> TransferAsync(int orderId, int targetUserId, int superAdminId, string? notes)
        {
            var order = await _context.Orders
                .Include(o => o.Apartment)
                .FirstOrDefaultAsync(o => o.Id == orderId)
                ?? throw new InvalidOperationException("Order not found.");

            var sourceUser = await _context.Users.FindAsync(order.UserId)
                ?? throw new InvalidOperationException("The order's current user no longer exists.");
            var targetUser = await _context.Users.FindAsync(targetUserId)
                ?? throw new InvalidOperationException("Target user not found.");

            if (targetUser.Id == sourceUser.Id)
                throw new InvalidOperationException("The order already belongs to this user.");
            if (targetUser.IsDeleted || !targetUser.IsActive)
                throw new InvalidOperationException("Target user is deleted or blocked.");
            if (targetUser.Role != UserRole.Customer)
                throw new InvalidOperationException("Orders can only be transferred to Customer accounts.");

            // Keep the audit copy BEFORE mutating (generic Order update log shows the owner change).
            var originalOrderForAudit = new Order
            {
                Id = order.Id,
                UserId = order.UserId,
                ApartmentId = order.ApartmentId,
                ApartmentName = order.ApartmentName,
                ContactFirstName = order.ContactFirstName,
                ContactLastName = order.ContactLastName,
                ContactEmail = order.ContactEmail,
                ContactPhone = order.ContactPhone,
                Status = order.Status,
                Total = order.Total
            };

            var snapshot = new OrderTransferSnapshot
            {
                OrderUserId = order.UserId,
                OrderApartmentId = order.ApartmentId,
                OrderApartmentName = order.ApartmentName,
                OrderContactFirstName = order.ContactFirstName,
                OrderContactLastName = order.ContactLastName,
                OrderContactEmail = order.ContactEmail,
                OrderContactPhone = order.ContactPhone,
                SourceFirstTimeOrderBefore = sourceUser.FirstTimeOrder,
                TargetFirstTimeOrderBefore = targetUser.FirstTimeOrder
            };

            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                // ── 1. Order ownership + contact fields ──
                // Contact info becomes the NEW owner's data outright — nothing from the source
                // account (typically a staff/private account) may remain on the order. Blank
                // target fields go blank rather than keeping the source's values; the snapshot
                // preserves the originals for undo.
                order.UserId = targetUser.Id;
                order.ContactFirstName = targetUser.FirstName ?? "";
                order.ContactLastName = targetUser.LastName ?? "";
                // No-email accounts must never leak their internal placeholder onto an order.
                order.ContactEmail = targetUser.IsNoEmailUser || NoEmailHelper.IsPlaceholder(targetUser.Email)
                    ? ""
                    : targetUser.Email;
                order.ContactPhone = targetUser.Phone ?? "";
                order.UpdatedAt = DateTime.UtcNow;

                // ── 2. Apartment: give the target user the service address ──
                // Reuse an existing apartment when the address already matches; otherwise copy
                // the source apartment (or synthesize one from the order's frozen address).
                var existingMatch = await _context.Apartments.FirstOrDefaultAsync(a =>
                    a.UserId == targetUser.Id && a.IsActive &&
                    a.Address == order.ServiceAddress &&
                    (a.AptSuite ?? "") == (order.AptSuite ?? "") &&
                    a.PostalCode == order.ZipCode);

                if (existingMatch != null)
                {
                    order.ApartmentId = existingMatch.Id;
                    order.ApartmentName = existingMatch.Name;
                }
                else
                {
                    var sourceApartment = order.ApartmentId.HasValue
                        ? await _context.Apartments.FindAsync(order.ApartmentId.Value)
                        : null;

                    var newApartment = new Apartment
                    {
                        UserId = targetUser.Id,
                        Name = sourceApartment?.Name ?? order.ApartmentName ?? "Home",
                        Address = sourceApartment?.Address ?? order.ServiceAddress,
                        AptSuite = sourceApartment?.AptSuite ?? order.AptSuite,
                        City = sourceApartment?.City ?? order.City,
                        State = sourceApartment?.State ?? order.State,
                        PostalCode = sourceApartment?.PostalCode ?? order.ZipCode,
                        SpecialInstructions = sourceApartment?.SpecialInstructions,
                        IsActive = true,
                        CreatedAt = DateTime.UtcNow
                    };
                    _context.Apartments.Add(newApartment);
                    await _context.SaveChangesAsync(); // need the new id
                    snapshot.CreatedApartmentId = newApartment.Id;
                    order.ApartmentId = newApartment.Id;
                    order.ApartmentName = newApartment.Name;
                }

                // ── 3. Cleaning photos attached to this order ──
                var photos = await _context.UserCleaningPhotos
                    .Where(p => p.OrderId == orderId && p.UserId == sourceUser.Id)
                    .ToListAsync();
                foreach (var photo in photos)
                {
                    photo.UserId = targetUser.Id;
                    snapshot.MovedPhotoIds.Add(photo.Id);
                }

                // ── 4. Bubble points earned from this order ──
                // Positive rows only: redemptions (negative rows) were the SOURCE user spending
                // their own points on the order and stay where they are.
                var earnedEntries = await _context.BubblePointsHistories
                    .Where(h => h.OrderId == orderId && h.UserId == sourceUser.Id && h.Points > 0)
                    .ToListAsync();

                var pointsMoved = earnedEntries.Sum(e => e.Points);
                if (pointsMoved > 0)
                {
                    foreach (var entry in earnedEntries)
                    {
                        entry.UserId = targetUser.Id;
                        snapshot.MovedPointsHistoryIds.Add(entry.Id);
                    }
                    sourceUser.BubblePoints = Math.Max(0, sourceUser.BubblePoints - pointsMoved);
                    targetUser.BubblePoints += pointsMoved;
                    snapshot.PointsMoved = pointsMoved;
                }

                // ── 5. Total-spent (tier) + streak: only when the completed order actually
                // contributed to the source's totals (mirrors ProcessOrderCompletion/Reverse). ──
                if (order.Status == "Done" || pointsMoved > 0)
                {
                    var spentToMove = Math.Min(order.Total, sourceUser.TotalSpentAmount);
                    sourceUser.TotalSpentAmount -= spentToMove;
                    targetUser.TotalSpentAmount += spentToMove;
                    snapshot.SpentAmountMoved = spentToMove;

                    if (sourceUser.ConsecutiveOrderCount > 0)
                    {
                        sourceUser.ConsecutiveOrderCount--;
                        snapshot.StreakDecrementedOnSource = true;
                    }
                    targetUser.ConsecutiveOrderCount++;
                    snapshot.StreakIncrementedOnTarget = true;
                }

                // ── 6. First-time flags ──
                // Target now owns an order → no longer a first-time customer (unless cancelled).
                if (order.Status != "Cancelled" && targetUser.FirstTimeOrder)
                    targetUser.FirstTimeOrder = false;
                // Source gets first-time back only when this was their sole remaining order.
                var sourceHasOtherOrders = await _context.Orders
                    .AnyAsync(o => o.UserId == sourceUser.Id && o.Id != orderId && o.Status != "Cancelled");
                if (!sourceHasOtherOrders && !sourceUser.FirstTimeOrder)
                    sourceUser.FirstTimeOrder = true;

                sourceUser.UpdatedAt = DateTime.UtcNow;
                targetUser.UpdatedAt = DateTime.UtcNow;

                var transfer = new OrderTransfer
                {
                    OrderId = orderId,
                    FromUserId = sourceUser.Id,
                    ToUserId = targetUser.Id,
                    TransferredByUserId = superAdminId,
                    Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
                    SnapshotJson = JsonConvert.SerializeObject(snapshot),
                    CreatedAt = DateTime.UtcNow
                };
                _context.OrderTransfers.Add(transfer);

                await _context.SaveChangesAsync();
                await transaction.CommitAsync();

                try
                {
                    await _auditService.LogCreateAsync(transfer);
                    await _auditService.LogUpdateAsync(originalOrderForAudit, order);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Audit logging failed for order transfer {TransferId}", transfer.Id);
                }

                _logger.LogInformation(
                    "Order {OrderId} transferred from user {FromUserId} to user {ToUserId} by SuperAdmin {AdminId} (points moved: {Points}, spent moved: {Spent})",
                    orderId, sourceUser.Id, targetUser.Id, superAdminId, snapshot.PointsMoved, snapshot.SpentAmountMoved);

                return await ToDtoAsync(transfer);
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }

        public async Task<OrderTransferDto> UndoAsync(int transferId, int superAdminId)
        {
            var transfer = await _context.OrderTransfers.FindAsync(transferId)
                ?? throw new InvalidOperationException("Transfer record not found.");

            if (transfer.IsUndone)
                throw new InvalidOperationException("This transfer has already been undone.");

            var newerTransfer = await _context.OrderTransfers
                .AnyAsync(t => t.OrderId == transfer.OrderId && t.Id > transfer.Id && !t.IsUndone);
            if (newerTransfer)
                throw new InvalidOperationException("A newer transfer exists for this order — undo that one first.");

            var order = await _context.Orders.FirstOrDefaultAsync(o => o.Id == transfer.OrderId)
                ?? throw new InvalidOperationException("The order no longer exists.");
            if (order.UserId != transfer.ToUserId)
                throw new InvalidOperationException("The order no longer belongs to the transfer's target user — undo is not safe.");

            var snapshot = JsonConvert.DeserializeObject<OrderTransferSnapshot>(transfer.SnapshotJson)
                ?? throw new InvalidOperationException("The transfer snapshot could not be read.");

            var sourceUser = await _context.Users.FindAsync(transfer.FromUserId)
                ?? throw new InvalidOperationException("The original user no longer exists.");
            var targetUser = await _context.Users.FindAsync(transfer.ToUserId)
                ?? throw new InvalidOperationException("The target user no longer exists.");

            var originalOrderForAudit = new Order
            {
                Id = order.Id,
                UserId = order.UserId,
                ApartmentId = order.ApartmentId,
                ApartmentName = order.ApartmentName,
                ContactFirstName = order.ContactFirstName,
                ContactLastName = order.ContactLastName,
                ContactEmail = order.ContactEmail,
                ContactPhone = order.ContactPhone,
                Status = order.Status,
                Total = order.Total
            };

            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                // ── Order fields back to their exact pre-transfer values ──
                order.UserId = snapshot.OrderUserId;
                order.ApartmentId = snapshot.OrderApartmentId;
                order.ApartmentName = snapshot.OrderApartmentName;
                order.ContactFirstName = snapshot.OrderContactFirstName;
                order.ContactLastName = snapshot.OrderContactLastName;
                order.ContactEmail = snapshot.OrderContactEmail;
                order.ContactPhone = snapshot.OrderContactPhone;
                order.UpdatedAt = DateTime.UtcNow;

                // ── Photos back to the source user ──
                if (snapshot.MovedPhotoIds.Count > 0)
                {
                    var photos = await _context.UserCleaningPhotos
                        .Where(p => snapshot.MovedPhotoIds.Contains(p.Id))
                        .ToListAsync();
                    foreach (var photo in photos)
                        photo.UserId = transfer.FromUserId;
                }

                // ── Points rows + balances (delta-based, floored at zero) ──
                if (snapshot.MovedPointsHistoryIds.Count > 0)
                {
                    var entries = await _context.BubblePointsHistories
                        .Where(h => snapshot.MovedPointsHistoryIds.Contains(h.Id))
                        .ToListAsync();
                    foreach (var entry in entries)
                        entry.UserId = transfer.FromUserId;
                }
                if (snapshot.PointsMoved > 0)
                {
                    targetUser.BubblePoints = Math.Max(0, targetUser.BubblePoints - snapshot.PointsMoved);
                    sourceUser.BubblePoints += snapshot.PointsMoved;
                }
                if (snapshot.SpentAmountMoved > 0)
                {
                    targetUser.TotalSpentAmount = Math.Max(0, targetUser.TotalSpentAmount - snapshot.SpentAmountMoved);
                    sourceUser.TotalSpentAmount += snapshot.SpentAmountMoved;
                }
                if (snapshot.StreakDecrementedOnSource)
                    sourceUser.ConsecutiveOrderCount++;
                if (snapshot.StreakIncrementedOnTarget)
                    targetUser.ConsecutiveOrderCount = Math.Max(0, targetUser.ConsecutiveOrderCount - 1);

                // ── First-time flags back to their pre-transfer values ──
                sourceUser.FirstTimeOrder = snapshot.SourceFirstTimeOrderBefore;
                targetUser.FirstTimeOrder = snapshot.TargetFirstTimeOrderBefore;

                // ── Apartment created during the transfer: remove it if nothing else uses it ──
                if (snapshot.CreatedApartmentId.HasValue)
                {
                    var createdApartment = await _context.Apartments.FindAsync(snapshot.CreatedApartmentId.Value);
                    if (createdApartment != null)
                    {
                        var usedByOtherOrders = await _context.Orders
                            .AnyAsync(o => o.ApartmentId == createdApartment.Id && o.Id != order.Id);
                        if (usedByOtherOrders)
                            createdApartment.IsActive = false; // keep the row, hide it
                        else
                            _context.Apartments.Remove(createdApartment);
                    }
                }

                sourceUser.UpdatedAt = DateTime.UtcNow;
                targetUser.UpdatedAt = DateTime.UtcNow;

                transfer.IsUndone = true;
                transfer.UndoneAt = DateTime.UtcNow;
                transfer.UndoneByUserId = superAdminId;

                await _context.SaveChangesAsync();
                await transaction.CommitAsync();

                try
                {
                    await _auditService.LogUpdateAsync(originalOrderForAudit, order);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Audit logging failed for order transfer undo {TransferId}", transfer.Id);
                }

                _logger.LogInformation(
                    "Order transfer {TransferId} (order {OrderId}) undone by SuperAdmin {AdminId}",
                    transfer.Id, transfer.OrderId, superAdminId);

                return await ToDtoAsync(transfer);
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }

        public async Task<List<OrderTransferDto>> GetTransfersForOrderAsync(int orderId)
        {
            var transfers = await _context.OrderTransfers
                .Where(t => t.OrderId == orderId)
                .OrderByDescending(t => t.Id)
                .ToListAsync();

            var result = new List<OrderTransferDto>();
            foreach (var t in transfers)
                result.Add(await ToDtoAsync(t));
            return result;
        }

        private async Task<OrderTransferDto> ToDtoAsync(OrderTransfer transfer)
        {
            // Names resolved fresh so the panel shows current display names.
            var userIds = new[] { transfer.FromUserId, transfer.ToUserId, transfer.TransferredByUserId, transfer.UndoneByUserId ?? 0 };
            var users = await _context.Users
                .Where(u => userIds.Contains(u.Id))
                .Select(u => new { u.Id, u.FirstName, u.LastName, u.IsNoEmailUser, u.Email })
                .ToListAsync();

            string NameOf(int id)
            {
                var u = users.FirstOrDefault(x => x.Id == id);
                return u == null ? $"User #{id}" : $"{u.FirstName} {u.LastName}".Trim();
            }

            var snapshot = JsonConvert.DeserializeObject<OrderTransferSnapshot>(transfer.SnapshotJson);

            return new OrderTransferDto
            {
                Id = transfer.Id,
                OrderId = transfer.OrderId,
                FromUserId = transfer.FromUserId,
                FromUserName = NameOf(transfer.FromUserId),
                ToUserId = transfer.ToUserId,
                ToUserName = NameOf(transfer.ToUserId),
                TransferredByUserId = transfer.TransferredByUserId,
                TransferredByName = NameOf(transfer.TransferredByUserId),
                Notes = transfer.Notes,
                CreatedAt = transfer.CreatedAt,
                IsUndone = transfer.IsUndone,
                UndoneAt = transfer.UndoneAt,
                UndoneByName = transfer.UndoneByUserId.HasValue ? NameOf(transfer.UndoneByUserId.Value) : null,
                PointsMoved = snapshot?.PointsMoved ?? 0,
                SpentAmountMoved = snapshot?.SpentAmountMoved ?? 0,
                PhotosMoved = snapshot?.MovedPhotoIds.Count ?? 0
            };
        }
    }
}
