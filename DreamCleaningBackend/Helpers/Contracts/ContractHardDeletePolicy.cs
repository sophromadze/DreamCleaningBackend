using DreamCleaningBackend.Models.Contracts;

namespace DreamCleaningBackend.Helpers.Contracts
{
    /// <summary>
    /// The facts a permanent-delete decision is made on. Passed as a struct rather than read off
    /// the entity so the policy stays pure and every rule below is asserted without a database.
    /// </summary>
    public readonly struct ContractDeletionFacts
    {
        /// <summary>How many <see cref="ContractSignature"/> rows exist across every version.</summary>
        public int SignatureCount { get; init; }

        /// <summary>Commercial invoices whose <c>ContractId</c> points at this contract.</summary>
        public int LinkedInvoiceCount { get; init; }

        /// <summary>Recurring invoice templates billing against this contract.</summary>
        public int LinkedRecurringTemplateCount { get; init; }

        /// <summary>Contracts produced from this one by "Duplicate as new contract" / "Amend".</summary>
        public int DerivedContractCount { get; init; }

        public ContractStatus Status { get; init; }
    }

    /// <summary>
    /// WHETHER A CONTRACT MAY BE DESTROYED, as opposed to archived.
    ///
    /// The whole point of the feature is to let an admin clear away test and mistaken contracts
    /// without keeping them forever - and the whole risk of the feature is that the same button
    /// destroys a signed agreement or orphans a bill somebody has already sent. So this file
    /// answers one question, in one place, and BOTH callers use it: the admin endpoint, and the
    /// retention sweep that purges archived contracts on a timer.
    ///
    /// Sharing it with the sweep is not tidiness. Before this existed the sweep hard-deleted
    /// ANYTHING that had been archived for six months, executed agreements included - a silent,
    /// unattended destruction of exactly the records that must survive. The archive action now
    /// promises the document is preserved, and this policy is what makes that promise true.
    ///
    /// THE TEST IS EVIDENCE, NOT VOCABULARY. A signature is the legal artefact, so the gate is
    /// "has anybody signed anything", never "is the status one of the late-looking ones". A
    /// contract can sit in AwaitingSignatures having been sent to a client who never opened it,
    /// and that is a test contract somebody should be able to clear away; a PartiallySigned one
    /// carries a real person's mark and never is.
    /// </summary>
    public static class ContractHardDeletePolicy
    {
        /// <summary>
        /// Null when the contract may be permanently deleted; otherwise the sentence to show the
        /// admin and to return from the API.
        ///
        /// A reason string rather than a bool because every one of these is actionable - the admin
        /// is being told which other record to deal with first, or that archiving is the answer.
        /// </summary>
        public static string? DescribeBlocker(ContractDeletionFacts facts)
        {
            // Evidence first: a captured signature outranks everything, including a status that
            // has since moved on.
            if (facts.SignatureCount > 0)
            {
                return facts.SignatureCount == 1
                    ? "This contract carries a signature and cannot be permanently deleted. Archive it instead."
                    : $"This contract carries {facts.SignatureCount} signatures and cannot be permanently deleted. Archive it instead.";
            }

            // An executed contract with no signature rows should not exist, but if the two ever
            // disagree the status is not the half to trust silently.
            if (facts.Status is ContractStatus.FullySigned or ContractStatus.Completed)
                return "An executed contract cannot be permanently deleted. Archive it instead.";

            // Orphan guards. Both of these FKs are SetNull, so deleting the contract would not
            // fail - it would quietly detach a real financial document from the agreement it was
            // raised under, which is worse than refusing.
            if (facts.LinkedInvoiceCount > 0)
            {
                return facts.LinkedInvoiceCount == 1
                    ? "An invoice was raised against this contract. Delete or void that invoice first, or archive this contract instead."
                    : $"{facts.LinkedInvoiceCount} invoices were raised against this contract. Delete or void those invoices first, or archive this contract instead.";
            }

            if (facts.LinkedRecurringTemplateCount > 0)
                return "A recurring billing template bills against this contract. Remove that template first, or archive this contract instead.";

            // DuplicatedFromContractId is a plain column with no foreign key, so this one cannot
            // fail at the database either - it would leave an amendment pointing at a contract
            // nobody can open, and "amended from" is the sentence that explains why the amendment
            // says what it says.
            if (facts.DerivedContractCount > 0)
            {
                return facts.DerivedContractCount == 1
                    ? "Another contract was created from this one as a duplicate or amendment. Archive this contract instead."
                    : $"{facts.DerivedContractCount} contracts were created from this one as duplicates or amendments. Archive this contract instead.";
            }

            return null;
        }

        /// <summary>Convenience for a caller that only needs the yes/no.</summary>
        public static bool CanHardDelete(ContractDeletionFacts facts) =>
            DescribeBlocker(facts) == null;
    }
}
