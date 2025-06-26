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
    }
}
