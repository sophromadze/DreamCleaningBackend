using Microsoft.EntityFrameworkCore;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services.Interfaces;

namespace DreamCleaningBackend.Services
{
    public class ReferralService : IReferralService
    {
        private readonly ApplicationDbContext _context;
        private readonly IBubbleRewardsSettingsService _settings;
        private readonly ILogger<ReferralService> _logger;

        public ReferralService(
            ApplicationDbContext context,
            IBubbleRewardsSettingsService settings,
            ILogger<ReferralService> logger)
        {
            _context = context;
            _settings = settings;
            _logger = logger;
        }

        public async Task<ReferralValidationResult> ValidateCode(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
                return new ReferralValidationResult { Valid = false, Message = "No code provided." };

            var referrer = await _context.Users
                .AsNoTracking()
                .FirstOrDefaultAsync(u => u.ReferralCode == code.ToUpper() && !u.IsDeleted);

            if (referrer == null)
                return new ReferralValidationResult { Valid = false, Message = "Invalid referral code." };

            return new ReferralValidationResult
            {
                Valid = true,
                ReferrerName = referrer.FirstName
            };
        }

        public async Task ProcessReferralRegistration(int newUserId, string referralCode)
        {
            var referralEnabled = await _settings.GetSetting<bool>("ReferralEnabled", true);
            if (!referralEnabled) return;

            var newUser = await _context.Users.FindAsync(newUserId);
            if (newUser == null) return;

            var referrer = await _context.Users
                .FirstOrDefaultAsync(u => u.ReferralCode == referralCode.ToUpper() && !u.IsDeleted);

            if (referrer == null || referrer.Id == newUserId) return;

            // Check if this user was already referred
            var existing = await _context.Referrals
                .AnyAsync(r => r.ReferredUserId == newUserId);
            if (existing) return;

            newUser.ReferredByUserId = referrer.Id;

            var referral = new Referral
            {
                ReferrerUserId = referrer.Id,
                ReferredUserId = newUserId,
                Status = "Registered",
                CreatedAt = DateTime.UtcNow
            };

            _context.Referrals.Add(referral);
            await _context.SaveChangesAsync();

            // Registration bonus for referrer (disabled by default)
            var regBonusEnabled = await _settings.GetSetting<bool>("ReferralRegistrationBonusEnabled", false);
            if (regBonusEnabled)
            {
                var bonusPoints = await _settings.GetSetting<int>("ReferralRegistrationBonusPoints", 50);
                if (bonusPoints > 0)
                {
                    referrer.BubblePoints += bonusPoints;
                    var history = new BubblePointsHistory
                    {
                        UserId = referrer.Id,
                        Points = bonusPoints,
                        Type = "ReferralRegistration",
                        Description = $"{newUser.FirstName} signed up with your referral code!",
                        CreatedAt = DateTime.UtcNow
                    };
                    _context.BubblePointsHistories.Add(history);
                    referral.RegistrationBonusGiven = true;
                    await _context.SaveChangesAsync();
                }
            }

            // Extra points for the new user who signed up with a referral link (separate from welcome bonus)
            var pointsSystemOn = await _settings.GetSetting<bool>("PointsSystemEnabled", true);
            var newUserBonusEnabled = await _settings.GetSetting<bool>("ReferralNewUserBonusEnabled", true);
            var newUserBonusPts = await _settings.GetSetting<int>("ReferralNewUserBonusPoints", 50);
            if (pointsSystemOn && newUserBonusEnabled && newUserBonusPts > 0)
            {
                var already = await _context.BubblePointsHistories
                    .AnyAsync(h => h.UserId == newUserId && h.Type == "ReferralNewUserBonus");
                if (!already)
                {
                    newUser = await _context.Users.FindAsync(newUserId);
                    if (newUser != null)
                    {
                        newUser.BubblePoints += newUserBonusPts;
                        _context.BubblePointsHistories.Add(new BubblePointsHistory
                        {
                            UserId = newUserId,
                            Points = newUserBonusPts,
                            Type = "ReferralNewUserBonus",
                            Description = "Signed up with a friend's referral link!",
                            CreatedAt = DateTime.UtcNow
                        });
                        await _context.SaveChangesAsync();
                    }
                }
            }
        }

        public async Task ProcessReferralOrderCompletion(int userId)
        {
            var referralEnabled = await _settings.GetSetting<bool>("ReferralEnabled", true);
            var orderBonusEnabled = await _settings.GetSetting<bool>("ReferralOrderCompletedBonusEnabled", true);
            if (!referralEnabled || !orderBonusEnabled) return;

            var referral = await _context.Referrals
                .Include(r => r.Referred)
                .FirstOrDefaultAsync(r => r.ReferredUserId == userId && !r.OrderBonusGiven);

            if (referral == null) return;

            // Check if referred user has any completed orders (this is their first)
            var completedOrderCount = await _context.Orders
                .CountAsync(o => o.UserId == userId && o.Status == "Done");

            if (completedOrderCount != 1) return; // Only trigger on the first completed order

            var creditAmount = await _settings.GetSetting<decimal>("ReferralOrderCompletedCreditAmount", 10);

            var referrer = await _context.Users.FindAsync(referral.ReferrerUserId);
            if (referrer == null) return;

            referrer.BubbleCredits += creditAmount;
            referral.OrderBonusGiven = true;
            referral.Status = "OrderCompleted";
            referral.CompletedAt = DateTime.UtcNow;

            var referred = referral.Referred;
            var history = new BubblePointsHistory
            {
                UserId = referrer.Id,
                Points = 0,
                Type = "ReferralOrderCompleted",
                Description = $"${creditAmount} reward — {referred?.FirstName ?? "Your referral"} completed their first order!",
                CreatedAt = DateTime.UtcNow
            };
            _context.BubblePointsHistories.Add(history);
            await _context.SaveChangesAsync();
        }

        public async Task<List<ReferralDto>> GetMyReferrals(int userId)
        {
            return await _context.Referrals
                .Include(r => r.Referred)
                .Where(r => r.ReferrerUserId == userId)
                .OrderByDescending(r => r.CreatedAt)
                .Select(r => new ReferralDto
                {
                    Id = r.Id,
                    ReferredUserId = r.ReferredUserId,
                    ReferredUserName = r.Referred.FirstName + " " + r.Referred.LastName,
                    ReferredUserEmail = r.Referred.Email,
                    Status = r.Status,
                    RegistrationBonusGiven = r.RegistrationBonusGiven,
                    OrderBonusGiven = r.OrderBonusGiven,
                    CreatedAt = r.CreatedAt,
                    CompletedAt = r.CompletedAt
                })
                .ToListAsync();
        }

        public async Task<string> GenerateReferralCode(int userId)
        {
            var chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
            var random = new Random();
            string code;
            int attempts = 0;

            do
            {
                var suffix = new string(Enumerable.Repeat(chars, 5)
                    .Select(s => s[random.Next(s.Length)]).ToArray());
                code = $"DREAM-{suffix}";
                attempts++;
                if (attempts > 50) break;
            }
            while (await _context.Users.AnyAsync(u => u.ReferralCode == code));

            var user = await _context.Users.FindAsync(userId);
            if (user != null)
            {
                user.ReferralCode = code;
                await _context.SaveChangesAsync();
            }

            return code;
        }
    }
}
