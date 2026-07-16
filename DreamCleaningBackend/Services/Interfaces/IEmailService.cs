using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Models;

namespace DreamCleaningBackend.Services.Interfaces
{
    public interface IEmailService
    {
        Task SendEmailVerificationAsync(string email, string firstName, string verificationLink);
        Task SendPasswordResetAsync(string email, string firstName, string resetLink);
        Task SendAdminWelcomeEmailAsync(string email, string firstName, string? setPasswordLink = null);
        Task SendWelcomeEmailAsync(string email, string firstName);
        Task SendGiftCardNotificationAsync(string recipientEmail, string recipientName,
            string senderName, string giftCardCode, decimal amount, string message, string senderEmail);
        Task SendGiftCardSenderConfirmationAsync(string senderEmail, string senderName,
            string recipientName, string recipientEmail, string giftCardCode,
            decimal amount, string message);
        Task SendContactFormEmailAsync(string to, string subject, string html);
        Task SendCleanerAssignmentNotificationAsync(string email, string cleanerName, int orderId, bool sendCopyToAdmin = false);
        /// <summary>Build a compact SMS body containing the same assignment details as the email
        /// (type, beds/baths, duration, date+time, customer, address, entry, supplies, tips, special
        /// instructions). Used when a cleaner has no email on file — callers send the returned text
        /// via ISmsService.</summary>
        Task<string?> BuildCleanerAssignmentSmsBodyAsync(Cleaner cleaner, int orderId);
        Task SendAdminCleanerAssignmentNotificationAsync(string cleanerEmail, string cleanerName,
            DateTime serviceDate, string serviceTime, string formattedDuration, string fullAddress);
        Task SendCleanerReminderNotificationAsync(string email, string cleanerName,
            DateTime serviceDate, string serviceTime, string serviceTypeName, string address, bool isDayBefore,
            string? cleanerNationality = null);
        Task SendCleanerRemovalNotificationAsync(string email, string cleanerName,
            DateTime serviceDate, string serviceTime, string serviceTypeName);
        Task SendEmailChangeVerificationAsync(string newEmail, string firstName, string verificationLink, string currentEmail);
        Task SendEmailChangeConfirmationAsync(string email, string firstName);
        Task SendCustomerReminderNotificationAsync(string email, string customerName,
            DateTime serviceDate, string serviceTime, string serviceTypeName, string address, bool isDaysBefore);
        Task SendCustomerBookingConfirmationAsync(string email, string customerName,
            DateTime serviceDate, string serviceTime, string serviceTypeName, string address, int orderId,
            bool hasCleaningSupplies, bool isDeepCleaning, bool isCustomServiceType,
            string? floorTypes = null, string? floorTypeOther = null,
            bool paymentAlreadyProcessed = true);
        Task SendRealEmailVerificationCodeAsync(string email, string firstName, string code);
        Task SendAccountMergeConfirmationAsync(string email, string firstName, string code);
        Task SendEmailAsync(string to, string subject, string html);
        Task SendCompanyBookingNotificationAsync(string contactFirstName, string contactLastName, string contactEmail, string contactPhone, DateTime serviceDate,
            string serviceTime, string serviceTypeName, string serviceAddress, string aptSuite, string city, string state, string zipCode,
            int orderId, bool isCustomServiceType, string? serviceDescription, List<PhotoUploadDto> uploadedPhotos = null);
        Task SendPollSubmissionEmailWithPhotosAsync(string toEmail, string subject, string htmlBody, List<PhotoUploadDto> uploadedPhotos = null);
        Task SendOrderUpdateNotificationAsync(int orderId, string customerEmail, decimal additionalAmount);
        Task SendCompanyAdditionalPaymentReceivedAsync(int orderId, string customerEmail, string customerName, decimal amountPaid);
        Task SendPaymentReminderEmailAsync(string email, string customerName, decimal amount, int orderId, string orderLink);
        /// <summary>Notify customer that their order was updated and an additional payment is required. Includes payment link.</summary>
        Task SendAdditionalPaymentRequiredEmailAsync(string email, string customerName, decimal additionalAmount, int orderId, string paymentLink);
        /// <summary>Gentle reminder that the customer has an unpaid additional amount. Same styling as required email.</summary>
        Task SendAdditionalPaymentReminderEmailAsync(string email, string customerName, decimal additionalAmount, int orderId, string paymentLink);
        /// <summary>Notify the company about an order cancellation with reason, fee info, and user details.</summary>
        Task SendCancellationNotificationToCompanyAsync(int orderId, string userEmail, int userId, string reason, bool isLateCancellation, DateTime serviceDate, string serviceTime);
        /// <summary>Notify an assigned cleaner that their order has been cancelled.</summary>
        Task SendCancellationNotificationToCleanerAsync(string cleanerEmail, int orderId, DateTime serviceDate, string serviceTime, string fullAddress);
        /// <summary>Send a review request email to the customer after order completion.</summary>
        Task SendReviewRequestEmailAsync(string email, string customerName);
        /// <summary>Send a 6-digit login OTP to a user who has no password set.</summary>
        Task SendLoginOtpAsync(string email, string firstName, string code);

        // Loyalty re-engagement (Phase 4/5). Wording is gratitude-framed — never reveals the
        // inactivity trigger to the customer (spec section 6 framing rules).
        Task SendLoyaltyReminder30Async(string toEmail, string firstName);
        Task SendLoyaltyReminder60Async(string toEmail, string firstName, decimal percentage);
        Task SendLoyaltyReminder90Async(string toEmail, string firstName, decimal percentage);

        // Manual reminder for registered users who never ordered — "book your first cleaning"
        // copy instead of "we miss you". Discount % comes from the DB (SpecialOffers), never hardcoded.
        Task SendFirstBookingReminderAsync(string toEmail, string firstName, decimal? firstTimeDiscountPercentage);
    }
}
