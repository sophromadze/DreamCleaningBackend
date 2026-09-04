using DreamCleaningBackend.Data;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace DreamCleaningBackend.Services
{
    /// <inheritdoc />
    public class CleanerAccountService : ICleanerAccountService
    {
        private readonly ApplicationDbContext _context;
        private readonly ILogger<CleanerAccountService> _logger;

        public CleanerAccountService(ApplicationDbContext context, ILogger<CleanerAccountService> logger)
        {
            _context = context;
            _logger = logger;
        }

        public async Task<int?> ApplyCleanerRoleIfEmailMatchesAsync(User user)
        {
            if (!CleanerAccountLink.CanAutoAssignCleanerRole(user.Role))
                return null;

            var email = CleanerAccountLink.NormalizeEmail(user.Email);
            if (email == null) return null;

            // Lowest id wins a contested email, matching LeadCustomerMatcher's tie-break. Two
            // cleaner rows can genuinely share an address (a duplicate an admin has not merged
            // yet), and picking the older one is at least stable across re-runs.
            var cleanerId = await _context.Cleaners
                .Where(c => c.Email != null && c.Email.ToLower() == email)
                .OrderBy(c => c.Id)
                .Select(c => (int?)c.Id)
                .FirstOrDefaultAsync();

            if (cleanerId == null) return null;

            user.Role = UserRole.Cleaner;
            _logger.LogInformation(
                "Registration matched cleaner record {CleanerId} - account created on the Cleaner role", cleanerId);
            return cleanerId;
        }

        public async Task LinkOnRegistrationAsync(int cleanerId, int userId)
        {
            var cleaner = await _context.Cleaners.FirstOrDefaultAsync(c => c.Id == cleanerId);
            if (cleaner == null) return;

            // Already claimed by another account: leave it alone. The role has still been assigned,
            // so the person reaches the portal and an admin can re-point the link from the Cleaner
            // Accounts tab - which is better than stealing a link that somebody set deliberately.
            if (cleaner.UserId.HasValue && cleaner.UserId.Value != userId)
            {
                _logger.LogWarning(
                    "Cleaner {CleanerId} is already linked to user {ExistingUserId}; leaving the link untouched for new user {UserId}",
                    cleanerId, cleaner.UserId.Value, userId);
                return;
            }

            if (cleaner.UserId == userId) return;

            // The account might already back a different cleaner (an admin linked it, then that
            // person registered a second time under the same address). The UserId index is unique,
            // so the old link has to go first or the insert fails.
            var previous = await _context.Cleaners.Where(c => c.UserId == userId && c.Id != cleanerId).ToListAsync();
            foreach (var stale in previous)
            {
                stale.UserId = null;
                stale.UpdatedAt = DateTime.UtcNow;
            }

            cleaner.UserId = userId;
            cleaner.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
        }

        public async Task<int?> ResolveCleanerIdForUserAsync(int userId)
        {
            var linked = await _context.Cleaners
                .Where(c => c.UserId == userId)
                .Select(c => (int?)c.Id)
                .FirstOrDefaultAsync();

            if (linked != null) return linked;

            // Nothing linked: fall back to the email the account signs in with. This is what makes
            // an account that predates the link work at all, and it is deliberately a FALLBACK -
            // an explicit link always wins, so correcting a cleaner's email can never re-point
            // somebody whose link was set on purpose.
            var user = await _context.Users
                .Where(u => u.Id == userId)
                .Select(u => new { u.Email, u.IsNoEmailUser })
                .FirstOrDefaultAsync();

            if (user == null || user.IsNoEmailUser) return null;

            var email = CleanerAccountLink.NormalizeEmail(user.Email);
            if (email == null) return null;

            // Only unlinked cleaner rows are eligible here. A row already pointing at a different
            // account belongs to that account, whatever its email column says.
            return await _context.Cleaners
                .Where(c => c.UserId == null && c.Email != null && c.Email.ToLower() == email)
                .OrderBy(c => c.Id)
                .Select(c => (int?)c.Id)
                .FirstOrDefaultAsync();
        }
    }
}
