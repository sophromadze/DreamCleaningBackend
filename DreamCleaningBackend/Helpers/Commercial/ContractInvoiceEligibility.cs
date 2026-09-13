using DreamCleaningBackend.Models.Contracts;

namespace DreamCleaningBackend.Helpers.Commercial
{
    /// <summary>
    /// MAY THIS CONTRACT RAISE AN INVOICE? One answer, shared by the endpoint and the button.
    ///
    /// Hiding a button in Angular is presentation; this is the control. Both sides read the same
    /// rule — the frontend through <c>ContractListItemDto.CanCreateNextInvoice</c>, the backend by
    /// calling <see cref="Check"/> before anything is written — so a contract that cannot be
    /// invoiced cannot be invoiced by anyone who knows the URL either.
    ///
    /// <b>Only a contract that actually binds somebody may be billed against.</b> Everything up to
    /// and including <see cref="ContractStatus.PartiallySigned"/> is a document still being agreed:
    /// a draft, a preview nobody has seen, one sent back for revision, or one that half the
    /// signatories have signed. Invoicing against any of those bills a client for terms they have
    /// not accepted. Voided and Expired are the other end — the agreement is over.
    ///
    /// That leaves <see cref="ContractStatus.FullySigned"/> and <see cref="ContractStatus.Completed"/>,
    /// which is the existing lifecycle's own definition of "executed". <b>No new statuses were
    /// invented</b> and none were repurposed.
    ///
    /// <b>Having no previous invoice is NOT a reason to refuse.</b> A signed contract with nothing
    /// billed yet is precisely the case that needs its first draft, built from the contract's own
    /// pricing — see <c>RecurringInvoiceService.ApplyContractPricing</c>.
    /// </summary>
    public static class ContractInvoiceEligibility
    {
        /// <summary>The statuses that represent an executed, in-force agreement.</summary>
        public static readonly IReadOnlyList<ContractStatus> InvoiceableStatuses = new[]
        {
            ContractStatus.FullySigned,
            ContractStatus.Completed
        };

        /// <summary>Null when the contract may raise an invoice; otherwise the reason it may not,
        /// worded for an admin rather than for a log.</summary>
        public static string? Check(ContractStatus status, bool isHidden)
        {
            if (isHidden)
                return "This contract has been deleted. Restore it before invoicing against it.";

            if (InvoiceableStatuses.Contains(status)) return null;

            return status switch
            {
                ContractStatus.Draft or ContractStatus.PreviewGenerated =>
                    "This contract is still a draft. It has to be signed before it can be invoiced.",

                ContractStatus.AwaitingClientReview or ContractStatus.NeedsRevision =>
                    "This contract is still being agreed with the client, so there is nothing to "
                    + "invoice against yet.",

                ContractStatus.ReadyForSignature or ContractStatus.AwaitingSignatures
                    or ContractStatus.PartiallySigned =>
                    "This contract has not been fully signed yet. Invoicing against it would bill "
                    + "the client for terms they have not accepted.",

                ContractStatus.Voided =>
                    "This contract was voided and cannot be invoiced against.",

                ContractStatus.Expired =>
                    "This contract has expired. Renew it before invoicing against it.",

                _ => "This contract is not in a state that can be invoiced."
            };
        }

        public static bool CanCreateNextInvoice(ContractStatus status, bool isHidden) =>
            Check(status, isHidden) == null;
    }
}
