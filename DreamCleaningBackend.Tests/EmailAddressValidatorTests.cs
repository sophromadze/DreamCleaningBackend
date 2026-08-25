using DreamCleaningBackend.Helpers;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// EMAIL ERRORS MUST NAME THE MISTAKE.
    ///
    /// 2026-08: an admin registered a customer and typed an address with no '@'. It was correctly
    /// rejected — but the rejection came from [ApiController] automatic model validation, whose
    /// ValidationProblemDetails body has an "errors" dictionary and NO "message" property. The
    /// admin panel read only err.error.message, so the admin was shown
    ///
    ///     Http failure response for https://dreamcleaningnyc.com/api/admin/users/register: 400
    ///
    /// and had no way to see the missing character. AdminUsersController.RegisterUser now
    /// validates the format itself and answers with the usual BadRequest(new { message = ... }).
    ///
    /// These tests assert the WORDING, not just the verdict — a generic "invalid email address"
    /// would leave the admin exactly as stuck as the 400 did.
    ///
    /// Mirrored by DreamCleaningNG/src/app/utils/email.utils.spec.ts. Change both together.
    /// </summary>
    public class EmailAddressValidatorTests
    {
        [Theory]
        [InlineData("john@example.com")]
        [InlineData("JOHN.DOE+tag@sub.example.co.uk")]
        [InlineData("a_b-c@example.io")]
        [InlineData("  spaced@example.com  ")]   // surrounding whitespace is trimmed, not an error
        public void AcceptsOrdinaryAddresses(string email)
        {
            Assert.Null(EmailAddressValidator.DescribeProblem(email));
            Assert.True(EmailAddressValidator.IsValid(email));
        }

        [Fact]
        public void NamesTheMissingAtSymbol_TheMistakeTheAdminActuallyMade()
        {
            AssertProblemContains("johnexample.com", "@", "missing", EmailAddressValidator.Example);
        }

        [Theory]
        [InlineData("johnexample.com")]
        [InlineData("john@@example.com")]
        [InlineData("@example.com")]
        [InlineData("john@")]
        [InlineData("john@example")]
        [InlineData("jo hn@example.com")]
        public void EveryRejectionShowsTheAdminWhatAValidAddressLooksLike(string email)
        {
            AssertProblemContains(email, EmailAddressValidator.Example);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void ReportsAnEmptyAddressAsRequired_NotAsMalformed(string? email)
        {
            Assert.Equal("Email address is required.", EmailAddressValidator.DescribeProblem(email));
        }

        [Fact]
        public void CountsDuplicateAtSymbols()
        {
            AssertProblemContains("john@@example.com", "2 \"@\" symbols");
        }

        [Fact]
        public void NamesAMissingLocalPart()
        {
            AssertProblemContains("@example.com", "before the \"@\"");
        }

        [Fact]
        public void NamesAMissingDomain()
        {
            AssertProblemContains("john@", "after the \"@\"");
        }

        [Fact]
        public void NamesADomainWithNoEnding()
        {
            AssertProblemContains("john@example", "example", ".com");
        }

        [Theory]
        [InlineData("john@example..com")]
        [InlineData("john@.example.com")]
        [InlineData("john@example.com.")]
        public void RejectsMalformedDotsInTheDomain(string email)
        {
            AssertProblemContains(email, "check the dots");
        }

        [Theory]
        [InlineData("john@example.c")]
        [InlineData("john@example.123")]
        public void RejectsAnInvalidDomainEnding(string email)
        {
            AssertProblemContains(email, "domain ending");
        }

        [Fact]
        public void RejectsSpacesAnywhereInTheAddress()
        {
            AssertProblemContains("jo hn@example.com", "spaces");
        }

        [Theory]
        [InlineData("john<doe@example.com")]
        [InlineData("john,doe@example.com")]
        public void RejectsCharactersThatCannotAppearInAnAddress(string email)
        {
            AssertProblemContains(email, "not allowed");
        }

        /// <summary>
        /// The no-email placeholder domain is .invalid, which is a perfectly well-FORMED address —
        /// NoEmailHelper is what decides it isn't sendable. This validator must not second-guess it,
        /// or the cash-customer branch of RegisterUser would start rejecting its own placeholders.
        /// </summary>
        [Fact]
        public void AcceptsTheNoEmailPlaceholderShape()
        {
            Assert.Null(EmailAddressValidator.DescribeProblem(NoEmailHelper.GeneratePlaceholder()));
        }

        /// <summary>Asserts the address is rejected AND that the message names each expected fragment.</summary>
        private static void AssertProblemContains(string? email, params string[] expectedFragments)
        {
            var problem = EmailAddressValidator.DescribeProblem(email);

            Assert.NotNull(problem);
            foreach (var fragment in expectedFragments)
                Assert.Contains(fragment, problem);
        }
    }
}
