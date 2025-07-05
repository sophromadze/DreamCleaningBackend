namespace DreamCleaningBackend.Services.Interfaces
{
    public interface IEmailService
    {
        Task SendEmailVerificationAsync(string email, string firstName, string verificationLink);
        Task SendPasswordResetAsync(string email, string firstName, string resetLink);
        Task SendWelcomeEmailAsync(string email, string firstName);
        Task SendGiftCardNotificationAsync(string recipientEmail, string recipientName,
            string senderName, string giftCardCode, decimal amount, string message, string senderEmail);
        Task SendContactFormEmailAsync(string to, string subject, string html);
        Task SendCleanerAssignmentNotificationAsync(string email, string cleanerName,
            DateTime serviceDate, string serviceTime, string serviceTypeName, string address);
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
    }
}
