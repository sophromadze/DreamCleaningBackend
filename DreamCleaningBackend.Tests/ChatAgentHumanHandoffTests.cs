using System.Text.RegularExpressions;
using DreamCleaningBackend.Services;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// "HIRE US" vs "HIRE ME", and the visitor's route to a real person.
    ///
    /// <para>
    /// 2026-09, from a real transcript. A visitor opened with "Hey I need cleaning job and my
    /// husband". The agent asked the right clarifying question — and then accepted the reply
    /// "Cleaning job" as meaning a booking, and went on to ask how many bedrooms, bathrooms and
    /// square feet the home was. The visitor's next message, "Do you have on cash" (i.e. do you
    /// pay cash), was answered with "I'm not sure what you mean" plus our phone number and an
    /// offer to price their home. Two people looking for work were run through a booking funnel
    /// and then handed a dead end.
    /// </para>
    ///
    /// <para>
    /// Three things came out of it, and these tests hold all three in place. The prompt has to
    /// name the actual signals (a cleaning JOB, a partner named as a second worker, asking what
    /// WE pay) rather than a generic "watch for employment intent"; an answer that merely echoes
    /// the ambiguous wording must not close the question; and the visitor needs a button that
    /// reaches a human WITHOUT the assistant having to agree — which is why the handoff path
    /// must never depend on Anthropic.
    /// </para>
    ///
    /// <para>
    /// The prompt assertions are deliberately about WORDING. The prompt is the whole
    /// implementation of this behaviour, so "the rule is still in there" is the only thing
    /// there is to assert; an edit that drops one of these sentences is the regression.
    /// </para>
    /// </summary>
    public class ChatAgentHumanHandoffTests
    {
        private static string Prompt => ChatAgentSystemPrompt.Default;

        // ===== The classification the transcript got wrong =====

        [Fact]
        public void APhraseLikeCleaningJobIsNamedAsAnEmploymentSignal_NotABooking()
        {
            // The whole misread turned on this one phrase, so a generic "watch for job
            // seekers" is not enough — the prompt has to say what to do with these words.
            Assert.Contains("\"cleaning job\"", Prompt);
            Assert.Contains("only a job-seeker asks for A CLEANING JOB", Prompt);
        }

        [Fact]
        public void APartnerNamedAlongsideTheRequestIsAnEmploymentSignal()
        {
            // "and my husband" was in the very first message and carried no weight at all.
            Assert.Contains("me and my husband", Prompt);
            Assert.Contains("two people offering to work", Prompt);
        }

        [Fact]
        public void AskingWhatWePayIsDistinguishedFromAskingWhatTheyPay()
        {
            // "Do you have on cash" is only ambiguous if you ignore which way the money is
            // going. Both directions have to be spelled out or the distinction isn't usable.
            Assert.Contains("do you pay cash", Prompt);
            Assert.Contains("how much per hour", Prompt);
            Assert.Contains("can I pay in cash?", Prompt);
        }

        [Fact]
        public void AnAnswerThatEchoesTheAmbiguousWordingDoesNotSettleTheQuestion()
        {
            // The visitor answered "Cleaning job" and that was taken as a booking.
            Assert.Contains("AN ECHO IS NOT AN ANSWER", Prompt);
            Assert.Contains("ask again with the two explicit options", Prompt);
        }

        [Fact]
        public void ALaterJobSignalReopensTheClassification()
        {
            // "Do you have on cash" arrived AFTER booking details had started being collected.
            // Without this rule the agent has no licence to go back on its own reading.
            Assert.Contains("RE-CLASSIFY AT ANY POINT", Prompt);
            Assert.Contains("Never keep gathering estimate details from someone who has begun asking about pay", Prompt);
        }

        [Fact]
        public void TheBookOrJobForkGetsButtons_ButAJobSeekerStillGetsNoPriceAndNoServiceChips()
        {
            // The ban on present_choices for a suspected job-seeker used to be absolute, which
            // left the disambiguation itself stuck in prose — exactly the format that produced
            // the echoed answer. The exception is scoped to that one two-option fork.
            Assert.Contains("the ONLY present_choices call permitted for a suspected job-seeker", Prompt);
            Assert.Contains("Apply for a job with your team", Prompt);

            Assert.Contains("NEVER call calculate_price_estimate for a suspected job-seeker", Prompt);
            Assert.Contains("never offer service-type chips to one", Prompt);
        }

        // ===== The dead-end reply =====

        [Fact]
        public void AMisunderstoodMessageIsAnsweredWithABestReading_NotWithThePhoneNumber()
        {
            Assert.Contains("WHEN YOU DON'T UNDERSTAND A MESSAGE", Prompt);
            Assert.Contains("Offer your best reading back as a short yes/no question", Prompt);
            Assert.Contains("are you asking whether we pay our cleaners in cash?", Prompt);
        }

        [Fact]
        public void TheAgentIsToldNotToPickWhicheverReadingKeepsBookingMoving()
        {
            // The failure mode isn't only "said the wrong thing" — it's that every ambiguity
            // was resolved in the direction of an estimate.
            Assert.Contains("never silently pick whichever interpretation keeps the booking flow moving", Prompt);
        }

        // ===== The button that doesn't need the assistant's permission =====

        [Fact]
        public void TheWidgetCanReachAHumanThroughItsOwnEndpoint()
        {
            var controller = ReadBackendFile("Controllers", "ChatController.cs");

            Assert.Contains("[HttpPost(\"request-human\")]", controller);
            Assert.Contains("RequestHumanAsync", controller);
        }

        [Fact]
        public void TheHandoffPathNeverAsksAnthropicAnything()
        {
            // This is the escape hatch from an assistant that is misreading the visitor, so it
            // has to work when the assistant doesn't. Reading the method body is the point:
            // an AI call added here would only show up in production, on the worst day.
            var body = ExtractMethodBody(
                ReadBackendFile("Services", "ChatAgentService.cs"),
                "public async Task<ChatMessageResponseDto> RequestHumanAsync");

            Assert.DoesNotContain("_anthropic", StripComments(body));
            Assert.Contains("EscalateSessionAsync", body);
        }

        [Fact]
        public void AskingForAHumanTwiceDoesNotHandOffTwice()
        {
            // A second Telegram topic (and a second escalation email) for one conversation is
            // how the team ends up replying into a thread the visitor isn't reading.
            var body = ExtractMethodBody(
                ReadBackendFile("Services", "ChatAgentService.cs"),
                "public async Task<ChatMessageResponseDto> RequestHumanAsync");

            Assert.Contains("ChatSessionStatus.EscalatedToHuman", body);
        }

        // ===== The guest email, which now has somewhere to go mid-conversation =====

        [Fact]
        public void AGuestEmailCanArriveAfterTheConversationHasStarted()
        {
            // The field used to hide itself the moment the first message was sent, so the only
            // window to fill it in was before anyone had said anything. Now it has a button,
            // which means the address can turn up at any point and needs its own endpoint.
            var controller = ReadBackendFile("Controllers", "ChatController.cs");

            Assert.Contains("[HttpPost(\"session/{sessionId:guid}/guest-email\")]", controller);
            Assert.Contains("SetGuestEmailAsync", controller);
        }

        [Fact]
        public void ARejectedEmailNamesTheMistake_AsEverywhereElseAnAddressIsTyped()
        {
            // Same rule as the admin panel (see EmailAddressValidatorTests): the rejection has
            // to say what is wrong with what they typed, in the { message } shape the frontend
            // reads. A bare [EmailAddress] attribute produces ValidationProblemDetails, which
            // has no message property at all.
            var controller = ReadBackendFile("Controllers", "ChatController.cs");

            Assert.Contains("EmailAddressValidator.DescribeProblem", controller);
            Assert.Contains("BadRequest(new { message = problem })", controller);
        }

        // ===== Helpers =====

        /// <summary>
        /// Returns the source of the method whose signature starts with <paramref name="signature"/>,
        /// by brace matching from its opening brace.
        /// </summary>
        private static string ExtractMethodBody(string source, string signature)
        {
            var start = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(start >= 0, $"Could not find `{signature}` — has it been renamed?");

            var open = source.IndexOf('{', start);
            Assert.True(open > 0, $"Could not find the body of `{signature}`.");

            var depth = 0;
            for (var i = open; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}')
                {
                    depth--;
                    if (depth == 0)
                        return source[start..(i + 1)];
                }
            }

            Assert.Fail($"Unbalanced braces while reading the body of `{signature}`.");
            return string.Empty;
        }

        private static string ReadBackendFile(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "DreamCleaningBackend")))
                dir = dir.Parent;

            Assert.NotNull(dir);

            var path = Path.Combine(
                new[] { dir!.FullName, "DreamCleaningBackend" }.Concat(parts).ToArray());

            Assert.True(File.Exists(path), $"{path} was not found.");
            return File.ReadAllText(path);
        }

        private static string StripComments(string source)
        {
            var withoutBlock = Regex.Replace(source, @"/\*.*?\*/", "", RegexOptions.Singleline);
            return Regex.Replace(withoutBlock, @"//.*?$", "", RegexOptions.Multiline);
        }
    }
}
