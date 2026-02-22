using Microsoft.EntityFrameworkCore;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services.Interfaces;

namespace DreamCleaningBackend.Services
{
    public class AccountMergeService : IAccountMergeService
    {
        private readonly ApplicationDbContext _context;
        private readonly IAuthService _authService;
        private readonly ILogger<AccountMergeService> _logger;

        private readonly IEmailService _emailService;

        public AccountMergeService(ApplicationDbContext context, IAuthService authService, IEmailService emailService, ILogger<AccountMergeService> logger)
        {
            _context = context;
            _authService = authService;
            _emailService = emailService;
            _logger = logger;
        }

        public async Task ResendMergeCodeAsync(int newAccountId)
        {
            var request = await _context.AccountMergeRequests
                .Include(r => r.OldAccount)
                .Where(r => r.NewAccountId == newAccountId && r.Status == AccountMergeRequestStatus.Pending && r.ExpiresAt > DateTime.UtcNow)
                .OrderByDescending(r => r.CreatedAt)
                .FirstOrDefaultAsync();
            if (request == null)
                throw new Exception("No pending merge request found or it has expired.");
            var code = new Random().Next(100000, 999999).ToString();
            request.VerificationCode = code;
            request.ExpiresAt = DateTime.UtcNow.AddMinutes(30);
            await _context.SaveChangesAsync();
            await _emailService.SendAccountMergeConfirmationAsync(request.VerifiedRealEmail, request.OldAccount.FirstName, code);
        }

        public async Task<MergeResultDto> ConfirmAndMergeAsync(int newAccountId, string verificationMethod, string verificationToken)
        {
            var request = await _context.AccountMergeRequests
                .Include(r => r.OldAccount)
                .Include(r => r.NewAccount)
                .Where(r => r.NewAccountId == newAccountId && r.Status == AccountMergeRequestStatus.Pending && r.ExpiresAt > DateTime.UtcNow)
                .OrderByDescending(r => r.CreatedAt)
                .FirstOrDefaultAsync();
            if (request == null)
                throw new Exception("No pending merge request found or it has expired. Please start the verification process again.");

            if (!verificationMethod.Equals("email", StringComparison.OrdinalIgnoreCase))
                throw new Exception("Invalid verification method. Use the 6-digit code sent to your email.");

            if (request.VerificationCode != verificationToken.Trim())
                throw new Exception("Invalid merge confirmation code.");

            request.Status = AccountMergeRequestStatus.Verified;
            await _context.SaveChangesAsync();

            var mergeResult = await MergeAccountsAsync(request.NewAccountId, request.OldAccountId, request.VerifiedRealEmail);
            request.Status = AccountMergeRequestStatus.Merged;
            await _context.SaveChangesAsync();

            var authResponse = await _authService.RefreshUserToken(mergeResult.newAccountId);
            return new MergeResultDto
            {
                MergedData = new MergeDataDto
                {
                    OrdersTransferred = mergeResult.ordersTransferred,
                    AddressesTransferred = mergeResult.addressesTransferred,
                    SubscriptionTransferred = mergeResult.subscriptionTransferred
                },
                NewToken = authResponse.Token,
                RefreshToken = authResponse.RefreshToken,
                User = authResponse.User
            };
        }

        /// <summary>Performs the data merge in a single transaction. Returns counts and the new account id.</summary>
        internal async Task<(int newAccountId, int ordersTransferred, int addressesTransferred, bool subscriptionTransferred)> MergeAccountsAsync(int newAccountId, int oldAccountId, string verifiedRealEmail)
        {
            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                var oldAccount = await _context.Users
                    .Include(u => u.UserSpecialOffers)
                    .FirstOrDefaultAsync(u => u.Id == oldAccountId);
                var newAccount = await _context.Users
                    .Include(u => u.UserSpecialOffers)
                    .FirstOrDefaultAsync(u => u.Id == newAccountId);
                if (oldAccount == null || newAccount == null)
                    throw new Exception("Account not found.");

                // 1. Orders
                var ordersCount = await _context.Orders.Where(o => o.UserId == oldAccountId).ExecuteUpdateAsync(s => s.SetProperty(o => o.UserId, newAccountId));

                // 2. Apartments (addresses) — move then deduplicate
                var apartmentsMoved = await _context.Apartments.Where(a => a.UserId == oldAccountId).ToListAsync();
                var addressesTransferred = apartmentsMoved.Count;
                foreach (var apt in apartmentsMoved)
                    apt.UserId = newAccountId;
                await _context.SaveChangesAsync();

                var newAccountApartments = await _context.Apartments.Where(a => a.UserId == newAccountId).ToListAsync();
                var toRemove = new List<Apartment>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var apt in newAccountApartments.OrderBy(a => a.Id))
                {
                    var key = $"{apt.Address?.Trim().ToLowerInvariant()}|{apt.City?.Trim().ToLowerInvariant()}|{apt.State?.Trim().ToLowerInvariant()}|{apt.PostalCode?.Trim().ToLowerInvariant()}";
                    if (seen.Contains(key))
                        toRemove.Add(apt);
                    else
                        seen.Add(key);
                }
                _context.Apartments.RemoveRange(toRemove);
                await _context.SaveChangesAsync();
                addressesTransferred -= toRemove.Count;

                // 3. Subscription — transfer from Old to New when Old has active; if both active keep later expiration
                bool subscriptionTransferred = false;
                var oldActive = oldAccount.SubscriptionExpiryDate.HasValue && oldAccount.SubscriptionExpiryDate.Value > DateTime.UtcNow;
                var newActive = newAccount.SubscriptionExpiryDate.HasValue && newAccount.SubscriptionExpiryDate.Value > DateTime.UtcNow;
                if (oldActive && !newActive)
                {
                    newAccount.SubscriptionId = oldAccount.SubscriptionId;
                    newAccount.SubscriptionStartDate = oldAccount.SubscriptionStartDate;
                    newAccount.SubscriptionExpiryDate = oldAccount.SubscriptionExpiryDate;
                    subscriptionTransferred = true;
                }
                else if (oldActive && newActive && oldAccount.SubscriptionExpiryDate!.Value > newAccount.SubscriptionExpiryDate!.Value)
                {
                    newAccount.SubscriptionId = oldAccount.SubscriptionId;
                    newAccount.SubscriptionStartDate = oldAccount.SubscriptionStartDate;
                    newAccount.SubscriptionExpiryDate = oldAccount.SubscriptionExpiryDate;
                    subscriptionTransferred = true;
                }

                // 4. Google — link Old's Google to New if Old has Google
                if (oldAccount.AuthProvider == "Google" && !string.IsNullOrEmpty(oldAccount.ExternalAuthId))
                {
                    newAccount.ExternalAuthId = oldAccount.ExternalAuthId;
                }

                // 5. UserSpecialOffers — move Old's to New, merge duplicates by more restrictive
                var oldOffers = await _context.UserSpecialOffers.Where(uso => uso.UserId == oldAccountId).ToListAsync();
                var newOffers = await _context.UserSpecialOffers.Where(uso => uso.UserId == newAccountId).ToListAsync();
                var newBySpecial = newOffers.ToDictionary(uso => uso.SpecialOfferId);
                foreach (var oldUso in oldOffers)
                {
                    if (newBySpecial.TryGetValue(oldUso.SpecialOfferId, out var existing))
                    {
                        existing.IsUsed = existing.IsUsed || oldUso.IsUsed;
                        if (oldUso.UsedAt.HasValue && (!existing.UsedAt.HasValue || oldUso.UsedAt < existing.UsedAt))
                            existing.UsedAt = oldUso.UsedAt;
                        existing.UsedOnOrderId = existing.UsedOnOrderId ?? oldUso.UsedOnOrderId;
                        _context.UserSpecialOffers.Remove(oldUso);
                    }
                    else
                    {
                        oldUso.UserId = newAccountId;
                        newBySpecial[oldUso.SpecialOfferId] = oldUso;
                    }
                }
                await _context.SaveChangesAsync();

                // 6. FirstTimeOrder — more restrictive
                if (!oldAccount.FirstTimeOrder)
                    newAccount.FirstTimeOrder = false;

                // 6b. Transfer profile data from Old (existing account) to New (Apple temp) — Old has real name, phone, role
                if (!string.IsNullOrWhiteSpace(oldAccount.FirstName))
                    newAccount.FirstName = oldAccount.FirstName;
                if (!string.IsNullOrWhiteSpace(oldAccount.LastName))
                    newAccount.LastName = oldAccount.LastName;
                if (!string.IsNullOrWhiteSpace(oldAccount.Phone))
                    newAccount.Phone = oldAccount.Phone;
                if (oldAccount.Role != UserRole.Customer)
                    newAccount.Role = oldAccount.Role;
                if (!string.IsNullOrEmpty(oldAccount.ProfilePictureUrl))
                    newAccount.ProfilePictureUrl = oldAccount.ProfilePictureUrl;
                newAccount.CanReceiveCommunications = oldAccount.CanReceiveCommunications;
                newAccount.CanReceiveEmails = oldAccount.CanReceiveEmails;
                newAccount.CanReceiveMessages = oldAccount.CanReceiveMessages;

                // 6c. Transfer password for local login — so user can sign in with email/password after merge
                if (!string.IsNullOrEmpty(oldAccount.PasswordHash) && !string.IsNullOrEmpty(oldAccount.PasswordSalt))
                {
                    newAccount.PasswordHash = oldAccount.PasswordHash;
                    newAccount.PasswordSalt = oldAccount.PasswordSalt;
                    newAccount.AuthProvider = "Local"; // Required for email/password login (Login checks AuthProvider == "Local")
                }

                // 7. GiftCardUsage (UserId)
                await _context.GiftCardUsages.Where(g => g.UserId == oldAccountId).ExecuteUpdateAsync(s => s.SetProperty(g => g.UserId, newAccountId));

                // 8. PollSubmission
                await _context.PollSubmissions.Where(p => p.UserId == oldAccountId).ExecuteUpdateAsync(s => s.SetProperty(p => p.UserId, newAccountId));

                // 9. NotificationLog (CustomerId)
                await _context.NotificationLogs.Where(n => n.CustomerId == oldAccountId).ExecuteUpdateAsync(s => s.SetProperty(n => n.CustomerId, newAccountId));

                // 10. AuditLog (UserId) — optional, move for consistency
                await _context.AuditLogs.Where(a => a.UserId == oldAccountId).ExecuteUpdateAsync(s => s.SetProperty(a => a.UserId, newAccountId));

                // 11. Soft-delete Old account and clear its email first (avoids unique Email constraint when we assign verified email to New)
                oldAccount.Email = $"merged-{oldAccountId}@deleted.local";
                oldAccount.IsDeleted = true;
                oldAccount.DeletedAt = DateTime.UtcNow;
                oldAccount.DeletedReason = $"Merged into account {newAccountId}";
                oldAccount.UpdatedAt = DateTime.UtcNow;

                // 12. Update New account email and flags
                newAccount.Email = verifiedRealEmail;
                newAccount.RequiresRealEmail = false;
                newAccount.IsEmailVerified = true;
                newAccount.UpdatedAt = DateTime.UtcNow;

                await _context.SaveChangesAsync();
                await transaction.CommitAsync();

                _logger.LogInformation("Accounts merged: Old {OldId} into New {NewId}, orders={Orders}, addresses={Addresses}, subscription={Sub}",
                    oldAccountId, newAccountId, ordersCount, addressesTransferred, subscriptionTransferred);

                var netAddresses = apartmentsMoved.Count - toRemove.Count;
                return (newAccountId, ordersCount, netAddresses, subscriptionTransferred);
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }
    }
}
