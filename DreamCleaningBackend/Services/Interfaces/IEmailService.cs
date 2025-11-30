using DreamCleaningBackend.DTOs;

namespace DreamCleaningBackend.Services.Interfaces
{
    public interface IEmailService
    {
        Task SendEmailVerificationAsync(string email, string firstName, string verificationLink);
        Task SendPasswordResetAsync(string email, string firstName, string resetLink);
        Task SendWelcomeEmailAsync(string email, string firstName);
        Task SendGiftCardNotificationAsync(string recipientEmail, string recipientName,
            string senderName, string giftCardCode, decimal amount, string message, string senderEmail);
        Task SendGiftCardSenderConfirmationAsync(string senderEmail, string senderName,
            string recipientName, string recipientEmail, string giftCardCode,
            decimal amount, string message);
        Task SendContactFormEmailAsync(string to, string subject, string html);
        Task SendCleanerAssignmentNotificationAsync(string email, string cleanerName,
            DateTime serviceDate, string serviceTime, string serviceTypeName, string address);
        Task SendAdminCleanerAssignmentNotificationAsync(string cleanerEmail, string cleanerName,
            DateTime serviceDate, string serviceTime, string formattedDuration, string fullAddress);
        Task SendCleanerReminderNotificationAsync(string email, string cleanerName,
            DateTime serviceDate, string serviceTime, string serviceTypeName, string address, bool isDayBefore);
        Task SendCleanerRemovalNotificationAsync(string email, string cleanerName,
            DateTime serviceDate, string serviceTime, string serviceTypeName);
        Task SendEmailChangeVerificationAsync(string newEmail, string firstName, string verificationLink, string currentEmail);
        Task SendEmailChangeConfirmationAsync(string email, string firstName);
        Task SendCustomerReminderNotificationAsync(string email, string customerName,
            DateTime serviceDate, string serviceTime, string serviceTypeName, string address, bool isDaysBefore);
        Task SendCustomerBookingConfirmationAsync(string email, string customerName,
            DateTime serviceDate, string serviceTime, string serviceTypeName, string address, int orderId);
        Task SendEmailAsync(string to, string subject, string html);
        Task SendCompanyBookingNotificationAsync(string contactFirstName, string contactLastName, string contactEmail, string contactPhone, DateTime serviceDate,
            string serviceTime, string serviceTypeName, string serviceAddress, string aptSuite, string city, string state, string zipCode, int orderId, List<PhotoUploadDto> uploadedPhotos = null);
        Task SendPollSubmissionEmailWithPhotosAsync(string toEmail, string subject, string htmlBody, List<PhotoUploadDto> uploadedPhotos = null);
        Task SendOrderUpdateNotificationAsync(int orderId, string customerEmail, decimal additionalAmount);
    }
}
