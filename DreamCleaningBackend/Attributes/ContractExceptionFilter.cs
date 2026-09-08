using DreamCleaningBackend.Services.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace DreamCleaningBackend.Attributes
{
    /// <summary>
    /// Turns the Contracts module's two domain exceptions into the right HTTP status, so every
    /// controller action can just call the policy and get on with its job instead of wrapping
    /// each one in the same try/catch.
    ///
    /// The distinction matters and is the reason this exists: a workflow violation ("generate a
    /// preview first") is a 400 the caller can fix, while a permission refusal is a 403. Returning
    /// 400 for both would let an unauthorized caller mistake a refusal for a bad request, and
    /// would make the UI unable to tell "you can't do this" from "you did this wrong".
    ///
    /// Registered globally in Program.cs; harmless for controllers that never throw either type.
    /// </summary>
    public class ContractExceptionFilter : IExceptionFilter
    {
        public void OnException(ExceptionContext context)
        {
            switch (context.Exception)
            {
                case ContractForbiddenException forbidden:
                    context.Result = new ObjectResult(new { message = forbidden.Message })
                    {
                        StatusCode = StatusCodes.Status403Forbidden
                    };
                    context.ExceptionHandled = true;
                    break;

                case ContractWorkflowException workflow:
                    context.Result = new BadRequestObjectResult(new { message = workflow.Message });
                    context.ExceptionHandled = true;
                    break;

                // Commercial invoicing shares this filter rather than registering a second one:
                // its workflow violations are the same kind of thing ("send this invoice before
                // recording a payment against it") and want the same 400 with a message an admin
                // can act on. Permission refusals there are attribute-driven, so there is no
                // invoice equivalent of ContractForbiddenException to map.
                case Services.Commercial.InvoiceWorkflowException invoice:
                    context.Result = new BadRequestObjectResult(new { message = invoice.Message });
                    context.ExceptionHandled = true;
                    break;
            }
        }
    }
}
