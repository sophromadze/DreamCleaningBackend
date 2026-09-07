using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Helpers.Contracts;
using DreamCleaningBackend.Models.Contracts;
using DreamCleaningBackend.Services.Contracts;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// THE CLIENT EDIT GATE.
    ///
    /// The client review page lets a commercial counterparty change their own details before
    /// signing. Two very different things can happen, and confusing them is the failure this
    /// suite guards:
    ///
    ///   personal correction  - a name typo, a title, an email, a phone. Patched into the current
    ///                          version; the client carries straight on to signature.
    ///   contract modification - the legal entity name or the company address. Produces a NEW
    ///                          version, drops the contract to NeedsRevision, and cannot be signed
    ///                          until an admin re-approves it.
    ///
    /// Getting this wrong in the lenient direction is the expensive one: it would let a client
    /// change who the agreement is with, and where notice is served, inside a version an admin had
    /// already approved.
    /// </summary>
    public class ContractClientEditPolicyTests
    {
        private static ContractSnapshot Snapshot() => new()
        {
            Client = new ClientSnapshot
            {
                LegalEntityName = "Chick Tastic LLC",
                EntityType = "a limited liability company",
                PrincipalAddress = "1569 Flatbush Ave.",
                City = "Brooklyn",
                State = "NY",
                Zip = "11210",
                NoticeEmail = "ap@chicktastic.example",
                Phone = "7325471819"
            },
            ClientSigner = new SignerSnapshot
            {
                FirstName = "Natalie",
                LastName = "Finkels",
                Title = null,
                Email = "natalie@chicktastic.example"
            }
        };

        private static ClientReviewInfoDto MatchingDto(ContractSnapshot s) => new()
        {
            CompanyLegalName = s.Client.LegalEntityName,
            FirstName = s.ClientSigner.FirstName,
            LastName = s.ClientSigner.LastName,
            Title = s.ClientSigner.Title,
            Email = s.ClientSigner.Email,
            Phone = s.ClientSigner.Phone,
            CompanyAddress = s.Client.PrincipalAddress,
            City = s.Client.City,
            State = s.Client.State,
            Zip = s.Client.Zip
        };

        // ── personal corrections ───────────────────────────────────────────────

        [Fact]
        public void FixingANameTypoIsNotAContractModification()
        {
            var snapshot = Snapshot();
            var dto = MatchingDto(snapshot);
            dto.LastName = "Finkelstein";

            Assert.False(ContractClientEditPolicy.IsContractModification(snapshot, dto));
        }

        [Fact]
        public void FillingInATitleIsNotAContractModification()
        {
            var snapshot = Snapshot();
            var dto = MatchingDto(snapshot);
            dto.Title = "Owner";

            Assert.Empty(ContractClientEditPolicy.DetectModifications(snapshot, dto));
        }

        [Fact]
        public void CorrectingEmailOrPhoneIsNotAContractModification()
        {
            var snapshot = Snapshot();
            var dto = MatchingDto(snapshot);
            dto.Email = "natalie.f@chicktastic.example";
            dto.Phone = "7325471820";

            Assert.Empty(ContractClientEditPolicy.DetectModifications(snapshot, dto));
        }

        [Fact]
        public void ACorrectedEmailAndPhoneFollowThroughToTheNoticeBlock()
        {
            // Section 32 serves notice on the address in the document. A client who corrects their
            // email and is then served at the old one is a real problem, not a cosmetic one.
            var snapshot = Snapshot();
            var dto = MatchingDto(snapshot);
            dto.Email = "new@chicktastic.example";
            dto.Phone = "9175550000";

            ContractClientEditPolicy.ApplyPersonalFields(snapshot, dto);

            Assert.Equal("new@chicktastic.example", snapshot.ClientSigner.Email);
            Assert.Equal("new@chicktastic.example", snapshot.Client.NoticeEmail);
            Assert.Equal("9175550000", snapshot.Client.Phone);
        }

        // ── contract modifications ─────────────────────────────────────────────

        [Fact]
        public void ChangingTheLegalEntityNameIsAContractModification()
        {
            var snapshot = Snapshot();
            var dto = MatchingDto(snapshot);
            dto.CompanyLegalName = "Chick Tastic Holdings LLC";

            var changes = ContractClientEditPolicy.DetectModifications(snapshot, dto);
            Assert.Contains("Legal entity name", changes);
        }

        [Fact]
        public void ChangingTheCompanyAddressIsAContractModification()
        {
            var snapshot = Snapshot();
            var dto = MatchingDto(snapshot);
            dto.CompanyAddress = "2000 Kings Highway";

            Assert.Contains("Company address", ContractClientEditPolicy.DetectModifications(snapshot, dto));
        }

        [Theory]
        [InlineData("Queens", null, null, "Company city")]
        [InlineData(null, "NJ", null, "Company state")]
        [InlineData(null, null, "11215", "Company ZIP")]
        public void EachPartOfTheCompanyAddressCountsOnItsOwn(
            string? city, string? state, string? zip, string expected)
        {
            var snapshot = Snapshot();
            var dto = MatchingDto(snapshot);
            if (city != null) dto.City = city;
            if (state != null) dto.State = state;
            if (zip != null) dto.Zip = zip;

            Assert.Contains(expected, ContractClientEditPolicy.DetectModifications(snapshot, dto));
        }

        [Fact]
        public void SeveralCompanyChangesAtOnceAreAllReported()
        {
            // The revision-request email lists what changed, so the admin knows what to check.
            var snapshot = Snapshot();
            var dto = MatchingDto(snapshot);
            dto.CompanyLegalName = "Chick Tastic Holdings LLC";
            dto.CompanyAddress = "2000 Kings Highway";
            dto.Zip = "11229";

            var changes = ContractClientEditPolicy.DetectModifications(snapshot, dto);
            Assert.Equal(3, changes.Count);
        }

        // ── the no-op cases ────────────────────────────────────────────────────

        [Fact]
        public void SubmittingTheFormUnchangedChangesNothing()
        {
            var snapshot = Snapshot();
            Assert.Empty(ContractClientEditPolicy.DetectModifications(snapshot, MatchingDto(snapshot)));
        }

        [Theory]
        [InlineData("chick tastic llc")]
        [InlineData("CHICK TASTIC LLC")]
        [InlineData("  Chick Tastic LLC  ")]
        public void RetypingTheSameValueDifferentlyIsNotARenegotiation(string retyped)
        {
            // Casing and stray whitespace must not cost the client a whole approval cycle.
            var snapshot = Snapshot();
            var dto = MatchingDto(snapshot);
            dto.CompanyLegalName = retyped;

            Assert.Empty(ContractClientEditPolicy.DetectModifications(snapshot, dto));
        }

        [Fact]
        public void CompanyFieldsAreOnlyEverAppliedDeliberately()
        {
            // ApplyPersonalFields must not touch the company — otherwise a "personal only" edit
            // would quietly rewrite the counterparty in an already-approved version.
            var snapshot = Snapshot();
            var dto = MatchingDto(snapshot);
            dto.CompanyLegalName = "Something Else LLC";
            dto.CompanyAddress = "Elsewhere";

            ContractClientEditPolicy.ApplyPersonalFields(snapshot, dto);

            Assert.Equal("Chick Tastic LLC", snapshot.Client.LegalEntityName);
            Assert.Equal("1569 Flatbush Ave.", snapshot.Client.PrincipalAddress);
        }
    }

    /// <summary>
    /// THE LOCK.
    ///
    /// "Once a contract reaches ReadyForSignature it is locked" is the rule that keeps a signature
    /// meaningful: the document somebody signed can never be edited underneath them, only
    /// superseded by a new version they must sign again.
    /// </summary>
    public class ContractLockingTests
    {
        [Theory]
        [InlineData(ContractStatus.Draft)]
        [InlineData(ContractStatus.PreviewGenerated)]
        [InlineData(ContractStatus.AwaitingClientReview)]
        [InlineData(ContractStatus.NeedsRevision)]
        public void AContractBeforeSigningIsEditable(ContractStatus status)
        {
            Assert.True(ContractService.CanEdit(status));
        }

        [Theory]
        [InlineData(ContractStatus.ReadyForSignature)]
        [InlineData(ContractStatus.AwaitingSignatures)]
        [InlineData(ContractStatus.PartiallySigned)]
        [InlineData(ContractStatus.FullySigned)]
        [InlineData(ContractStatus.Completed)]
        [InlineData(ContractStatus.Voided)]
        [InlineData(ContractStatus.Expired)]
        public void EverythingFromReadyForSignatureOnIsLocked(ContractStatus status)
        {
            Assert.False(ContractService.CanEdit(status));
        }

        [Theory]
        [InlineData(ContractStatus.PartiallySigned)]
        [InlineData(ContractStatus.FullySigned)]
        [InlineData(ContractStatus.Completed)]
        [InlineData(ContractStatus.Voided)]
        public void OnceAnybodyHasSignedTheClientCanNoLongerEditFromTheReviewPage(ContractStatus status)
        {
            Assert.False(ContractClientEditPolicy.ClientMayEdit(status));
        }

        [Fact]
        public void EveryStatusHasAHumanReadableLabel()
        {
            // The label is what an admin and a client both read, so a raw enum name leaking into
            // the UI ("AwaitingClientReview") is a rough edge on a legal document. Single-word
            // statuses like Draft legitimately equal their enum name; multi-word ones must not.
            foreach (var status in Enum.GetValues<ContractStatus>())
            {
                var label = ContractService.StatusLabel(status);
                Assert.False(string.IsNullOrWhiteSpace(label));

                var name = status.ToString();
                var isMultiWord = name.Skip(1).Any(char.IsUpper);
                if (isMultiWord) Assert.Contains(' ', label);
            }
        }
    }

    /// <summary>
    /// Filenames and tokens. Small surface, but both are handed to people: the executed PDF is
    /// filed by name, and the tokens are the entire authorization on the client-facing pages.
    /// </summary>
    public class ContractIdentityTests
    {
        [Fact]
        public void TheExecutedFilenameFollowsTheAgreedShape()
        {
            Assert.Equal(
                "DC-2026-0001-chick-tastic-llc-Executed.pdf",
                ContractNotificationService.ExecutedFileName("DC-2026-0001", "Chick Tastic LLC"));
        }

        [Fact]
        public void TokensAreLongAndUnguessable()
        {
            var a = ContractService.GenerateToken();
            var b = ContractService.GenerateToken();

            // 24 random bytes -> 48 hex characters, the same shape as the payment-link token.
            Assert.Equal(48, a.Length);
            Assert.NotEqual(a, b);
            Assert.Matches("^[0-9a-f]+$", a);
        }
    }
}
