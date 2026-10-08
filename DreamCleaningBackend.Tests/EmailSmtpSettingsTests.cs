using DreamCleaningBackend.Data;
using DreamCleaningBackend.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// EVERY SEND PATH READS THE Email:* SETTINGS THROUGH ONE HELPER (2026-10, MailKit 4.18).
    ///
    /// Five send paths used to read the SMTP host, port, user, password and sender inline, and a
    /// missing value reached MailKit as null — an ArgumentNullException from deep inside the
    /// connect call that named nothing. They now share one reader, so the error names the key.
    /// </summary>
    public class EmailSmtpSettingsTests
    {
        [Theory]
        [InlineData("Email:FromAddress")]
        [InlineData("Email:SmtpHost")]
        [InlineData("Email:SmtpPort")]
        public async Task AMissingSettingIsNamedInTheFailure(string missingKey)
        {
            var settings = new Dictionary<string, string?>
            {
                ["Email:EnableEmailSending"] = "true",
                ["Email:FromName"] = "Dream Cleaning",
                ["Email:FromAddress"] = "hello@example.invalid",
                // A closed local port: if the reader let a value through, the test would fail on
                // a refused connection instead of on the missing setting it is about.
                ["Email:SmtpHost"] = "127.0.0.1",
                ["Email:SmtpPort"] = "9",
                ["Email:SmtpUser"] = "user",
                ["Email:SmtpPassword"] = "password"
            };
            settings[missingKey] = null;

            var service = NewService(settings);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.SendEmailAsync("customer@example.com", "Subject", "<p>Body</p>"));
            Assert.Contains(missingKey, error.Message);
        }

        [Fact]
        public async Task DisabledSendingNeverReadsTheSmtpSettings()
        {
            var service = NewService(new Dictionary<string, string?> { ["Email:EnableEmailSending"] = "false" });

            await service.SendEmailAsync("customer@example.com", "Subject", "<p>Body</p>");
        }

        private static EmailService NewService(Dictionary<string, string?> settings)
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
            var context = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase("email-settings-" + Guid.NewGuid())
                .Options);
            return new EmailService(configuration, NullLogger<EmailService>.Instance, context);
        }
    }
}
