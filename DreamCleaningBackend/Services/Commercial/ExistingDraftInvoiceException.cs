using DreamCleaningBackend.DTOs.Commercial;
using DreamCleaningBackend.Helpers.Commercial;
using DreamCleaningBackend.Models.Commercial;

namespace DreamCleaningBackend.Services.Commercial
{
    /// <summary>
    /// "Create Next Invoice" found an unsent DRAFT already sitting against this contract.
    ///
    /// A DISTINCT EXCEPTION, not an <see cref="InvoiceWorkflowException"/>, because the two need
    /// different answers. A workflow violation is a refusal an admin has to fix; this one is a
    /// redirection — the thing they asked for already exists, and the useful response is a link to
    /// it. It carries the draft's identity so the controller can return 409 with something the UI
    /// can turn into "A draft invoice already exists for this period. [Open Draft]" rather than a
    /// sentence with no button on it.
    ///
    /// It is not a hard wall: re-posting with <c>allowDuplicatePeriod</c> proceeds, exactly like
    /// the issued-invoice overlap guard beside it. There are legitimate reasons to want a second
    /// draft; there is no legitimate reason to make one by accident.
    /// </summary>
    public class ExistingDraftInvoiceException : Exception
    {
        public ExistingDraftInvoiceDto Draft { get; }

        private ExistingDraftInvoiceException(string message, ExistingDraftInvoiceDto draft)
            : base(message)
        {
            Draft = draft;
        }

        public static ExistingDraftInvoiceException For(CommercialInvoice draft, bool undated = false)
        {
            var period = ServiceDateFormatter.Describe(
                InvoiceService.ParseServiceDates(draft.ServiceDatesJson),
                draft.ServiceStartDate,
                draft.ServiceEndDate);

            var message = undated ? $"An undated draft ({draft.InvoiceNumber}) exists. Its billing period is unknown. Open it to review, or explicitly continue with the intended period."
                : period.HasValue
                ? $"A draft invoice ({draft.InvoiceNumber}) already exists for {period.Text}."
                : $"A draft invoice ({draft.InvoiceNumber}) already exists for this contract.";

            return new ExistingDraftInvoiceException(message, new ExistingDraftInvoiceDto
            {
                IsUndated = undated,
                InvoiceId = draft.Id,
                InvoiceNumber = draft.InvoiceNumber,
                ServiceStartDate = draft.ServiceStartDate,
                ServiceEndDate = draft.ServiceEndDate,
                ServicePeriodText = period.HasValue ? period.Text : null,
                Total = draft.Total,
                CreatedAt = draft.CreatedAt,
                Message = message
            });
        }
    }
}
