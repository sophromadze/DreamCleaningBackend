using System.Reflection;
using System.Text.Json;
using DreamCleaningBackend.Helpers;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// WHAT A REQUEST BODY MAY BE BOUND TO, and how the pending-edit submission is read.
    ///
    /// Program.cs registers AddControllers().AddJsonOptions(...) and nothing else — there is no
    /// AddNewtonsoftJson() and no Microsoft.AspNetCore.Mvc.NewtonsoftJson package — so
    /// System.Text.Json is the ONLY input formatter the app has. A [FromBody] parameter typed as
    /// a Newtonsoft type (JObject / JToken / JArray) therefore can never be model-bound, and
    /// [ApiController] answers with an automatic 400 before the action body ever runs.
    ///
    /// That is exactly what happened to POST orders/{orderId}/pending-edit: every submission an
    /// admin made came back 400 Bad Request, and nothing in the handler was reached, so nothing
    /// was logged. Newtonsoft is still fine as a LIBRARY (JsonConvert is used all over for stored
    /// JSON) — it just cannot appear on an action signature.
    /// </summary>
    public class RequestBodyBindingTests
    {
        [Fact]
        public void NoActionBindsARequestBodyToANewtonsoftType()
        {
            var offenders = new List<string>();

            var controllers = typeof(DreamCleaningBackend.Controllers.AdminOrdersController).Assembly
                .GetTypes()
                .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract);

            foreach (var controller in controllers)
            {
                var methods = controller.GetMethods(
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

                foreach (var method in methods)
                {
                    foreach (var parameter in method.GetParameters())
                    {
                        var ns = parameter.ParameterType.Namespace ?? "";
                        if (!ns.StartsWith("Newtonsoft.Json", StringComparison.Ordinal)) continue;
                        if (parameter.GetCustomAttribute<FromBodyAttribute>() == null) continue;

                        offenders.Add($"{controller.Name}.{method.Name}" +
                                      $"({parameter.ParameterType.Name} {parameter.Name})");
                    }
                }
            }

            Assert.True(
                offenders.Count == 0,
                "These actions bind a request body to a Newtonsoft type, which the app's only " +
                "input formatter (System.Text.Json) cannot produce — every call returns 400: " +
                string.Join(", ", offenders));
        }

        private static JsonElement Body(string json) => JsonDocument.Parse(json).RootElement;

        [Fact]
        public void TheWrappedSubmitBody_IsRead()
        {
            // The shape admin.service.ts posts today.
            var body = Body("""
            {
              "changes": { "subTotal": 275.55, "tax": 24.46, "total": 300.01, "contactFirstName": "Ana" },
              "fieldChanges": [
                { "field": "Total", "current": "$280.00", "proposed": "$300.01",
                  "difference": "+$20.01", "emphasised": true }
              ],
              "reason": "customer added a fridge"
            }
            """);

            Assert.True(PendingOrderEditSubmissionReader.TryRead(body, out var submission));

            Assert.NotNull(submission.Changes);
            Assert.Equal("Ana", submission.Changes!.ContactFirstName);
            Assert.Equal(275.55m, submission.Changes.SubTotal);
            Assert.Equal("customer added a fridge", submission.Reason);

            var change = Assert.Single(submission.FieldChanges!);
            Assert.Equal("Total", change.Field);
            Assert.Equal("+$20.01", change.Difference);
            Assert.True(change.Emphasised);
        }

        [Fact]
        public void TheLegacyBareBody_StillReadsAsTheUpdateDto()
        {
            // Every caller posted this shape before 2026-08-31, and a stale client still can.
            var body = Body("""{ "subTotal": 120.00, "contactFirstName": "Ana" }""");

            Assert.True(PendingOrderEditSubmissionReader.TryRead(body, out var submission));

            Assert.NotNull(submission.Changes);
            Assert.Equal("Ana", submission.Changes!.ContactFirstName);
            Assert.Equal(120.00m, submission.Changes.SubTotal);
            Assert.Null(submission.FieldChanges);
            Assert.Null(submission.Reason);
        }

        [Fact]
        public void PropertyNamesAreReadCaseInsensitively()
        {
            var body = Body("""{ "Changes": { "ContactFirstName": "Ana" }, "Reason": "  spaced  " }""");

            Assert.True(PendingOrderEditSubmissionReader.TryRead(body, out var submission));
            Assert.Equal("Ana", submission.Changes!.ContactFirstName);
            Assert.Equal("spaced", submission.Reason);
        }

        [Fact]
        public void AnEmptyOrUnreadableBody_IsReportedAsNoChanges()
        {
            Assert.False(PendingOrderEditSubmissionReader.TryRead(default, out _));
            Assert.False(PendingOrderEditSubmissionReader.TryRead(Body("null"), out _));
            Assert.False(PendingOrderEditSubmissionReader.TryRead(Body("[]"), out _));
            // "changes" present but not an object — read as a failure, never as a server error.
            Assert.False(PendingOrderEditSubmissionReader.TryRead(Body("""{ "changes": 5 }"""), out _));
        }

        [Fact]
        public void AnEmptyObject_KeepsTheLegacyTolerance()
        {
            // {} has no "changes" key, so it is read the old way and yields an all-null update
            // DTO — a request with nothing in it, exactly as before 2026-08-31. Deliberately not
            // tightened here: the fix was the binding, and legacy rows of this shape already exist.
            Assert.True(PendingOrderEditSubmissionReader.TryRead(Body("{}"), out var submission));
            Assert.NotNull(submission.Changes);
            Assert.Null(submission.Changes!.ContactFirstName);
            Assert.Null(submission.Changes.SubTotal);
        }

        [Fact]
        public void ABlankReasonIsNull_AndAnOverlongOneIsCappedToTheColumnWidth()
        {
            Assert.Null(PendingOrderEditSubmissionReader.NormalizeReason("   "));
            Assert.Null(PendingOrderEditSubmissionReader.NormalizeReason(null));

            var tooLong = new string('x', PendingOrderEditSubmissionReader.MaxReasonLength + 500);
            var capped = PendingOrderEditSubmissionReader.NormalizeReason(tooLong);

            // PendingOrderEdit.Reason is varchar(1000); the body binds as a JsonElement, so model
            // validation never sees the wrapper's [StringLength] and the INSERT would fail.
            Assert.Equal(PendingOrderEditSubmissionReader.MaxReasonLength, capped!.Length);
        }
    }
}
