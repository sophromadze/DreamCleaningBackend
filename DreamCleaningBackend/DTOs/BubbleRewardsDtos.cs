namespace DreamCleaningBackend.DTOs
{
    public class BubbleRewardsSettingDto
    {
        public int Id { get; set; }
        public string SettingKey { get; set; } = string.Empty;
        public string SettingValue { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string Category { get; set; } = string.Empty;
        public DateTime UpdatedAt { get; set; }
    }

    public class UpdateSettingDto
    {
        public string Value { get; set; } = string.Empty;
    }

    public class BulkUpdateSettingDto
    {
        public string Key { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
    }

    public class RedemptionOptionDto
    {
        public int Points { get; set; }
        public decimal DollarValue { get; set; }
        public bool Available { get; set; }
    }

    public class RewardsGuideDto
    {
        public decimal PointsPerDollar { get; set; }
        public decimal BubbleMultiplier { get; set; }
        public decimal SuperBubbleMultiplier { get; set; }
        public decimal UltraBubbleMultiplier { get; set; }
        public decimal TierSuperBubbleMinSpent { get; set; }
        public decimal TierUltraBubbleMinSpent { get; set; }
        public bool WelcomeBonusEnabled { get; set; }
        public int WelcomeBonusPoints { get; set; }
        public bool RecurringBonusEnabled { get; set; }
        public decimal RecurringBonusPercent { get; set; }
        public bool NextOrderBoosterEnabled { get; set; }
        public int NextOrderBoosterDays { get; set; }
        public decimal NextOrderBoosterPercent { get; set; }
        public bool StreakEnabled { get; set; }
        public int Streak3Bonus { get; set; }
        public int Streak6Bonus { get; set; }
        public bool ReviewBonusEnabled { get; set; }
        public int ReviewBonusPoints { get; set; }
        public bool ReferralEnabled { get; set; }
        public bool ReferralRegistrationBonusEnabled { get; set; }
        public int ReferralRegistrationBonusPoints { get; set; }
        public bool ReferralNewUserBonusEnabled { get; set; }
        public int ReferralNewUserBonusPoints { get; set; }
        public decimal ReferralOrderCreditAmount { get; set; }
    }

    public class BubbleRewardsSummaryDto
    {
        public int CurrentPoints { get; set; }
        public decimal BubbleCredits { get; set; }
        public string Tier { get; set; } = "Bubble";
        public string TierEmoji { get; set; } = "🫧";
        public decimal TierProgressPercent { get; set; }
        public string? NextTierName { get; set; }
        public decimal AmountToNextTier { get; set; }
        public decimal TotalSpentAmount { get; set; }
        public List<RedemptionOptionDto> AvailableRedemptions { get; set; } = new();
        public int StreakCount { get; set; }
        public string ReferralCode { get; set; } = string.Empty;
        public string ShareUrl { get; set; } = string.Empty;
        public int TotalEarned { get; set; }
        public int TotalRedeemed { get; set; }
        public bool PointsSystemEnabled { get; set; }
        public bool ReferralRegistrationBonusEnabled { get; set; }
        public RewardsGuideDto Guide { get; set; } = new();
    }

    public class HeaderSummaryDto
    {
        public int Points { get; set; }
        public string Tier { get; set; } = "Bubble";
        public string TierEmoji { get; set; } = "🫧";
        public decimal Credits { get; set; }
        public bool PointsSystemEnabled { get; set; }
        public decimal TierProgressPercent { get; set; }
        public string? NextTierName { get; set; }
    }

    public class BubblePointsHistoryDto
    {
        public int Id { get; set; }
        public int Points { get; set; }
        public string Type { get; set; } = string.Empty;
        public string? Description { get; set; }
        public int? OrderId { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    public class RedeemPointsDto
    {
        public int Points { get; set; }
        public int OrderId { get; set; }
    }

    public class RedemptionResultDto
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public decimal CreditApplied { get; set; }
        public int RemainingPoints { get; set; }
    }

    public class AdjustPointsDto
    {
        public int Points { get; set; }
        public string Description { get; set; } = string.Empty;
    }

    public class ResetBubblePointsDto
    {
        /// <summary>If null, resets all users. If set, resets only that user.</summary>
        public int? UserId { get; set; }
    }

    public class GrantCreditDto
    {
        public decimal Amount { get; set; }
        public string Description { get; set; } = string.Empty;
    }

    public class AddReferralDto
    {
        public string Email { get; set; } = string.Empty;
    }

    public class ReferralDto
    {
        public int Id { get; set; }
        public int ReferredUserId { get; set; }
        public string ReferredUserName { get; set; } = string.Empty;
        public string ReferredUserEmail { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public bool RegistrationBonusGiven { get; set; }
        public bool OrderBonusGiven { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
    }

    public class ReferralValidationResult
    {
        public bool Valid { get; set; }
        public string? ReferrerName { get; set; }
        public string? Message { get; set; }
    }

    public class ValidateReferralCodeDto
    {
        public string Code { get; set; } = string.Empty;
    }

    public class PagedResult<T>
    {
        public List<T> Items { get; set; } = new();
        public int TotalCount { get; set; }
        public int Page { get; set; }
        public int PageSize { get; set; }
        public int TotalPages => (int)Math.Ceiling((double)TotalCount / PageSize);
    }

    public class RewardsStatsDto
    {
        public long TotalPointsIssued { get; set; }
        public long TotalPointsRedeemed { get; set; }
        public decimal TotalCreditsIssued { get; set; }
        public int ActiveUsersWithPoints { get; set; }
        public int BubbleTierCount { get; set; }
        public int SuperBubbleTierCount { get; set; }
        public int UltraBubbleTierCount { get; set; }
        public int TotalReferrals { get; set; }
        public int CompletedReferrals { get; set; }
    }

    public class AdminUserRewardsSummaryDto : BubbleRewardsSummaryDto
    {
        public int UserId { get; set; }
        public string UserName { get; set; } = string.Empty;
        public string UserEmail { get; set; } = string.Empty;
        public List<BubblePointsHistoryDto> PointsHistory { get; set; } = new();
        public List<ReferralDto> Referrals { get; set; } = new();
        public string? ReferredByName { get; set; }
    }
}
