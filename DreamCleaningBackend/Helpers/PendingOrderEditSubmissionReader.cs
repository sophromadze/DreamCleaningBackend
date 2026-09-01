using System.Text.Json;
using DreamCleaningBackend.DTOs;

namespace DreamCleaningBackend.Helpers
{
    /// <summary>
    /// Reads the body of <c>POST api/admin/orders/{orderId}/pending-edit</c>.
    ///
    /// TWO accepted shapes, and the older one still works. Before 2026-08-31 the body WAS the
    /// <see cref="SuperAdminUpdateOrderDto"/>; it is now a wrapper carrying the same DTO under
    /// "changes" plus the submit-time field payload and a reason. A body with no "changes" key is
    /// read the old way, so a stale client (or a direct API caller) submits a request with no
    /// readable payload rather than being rejected — the same shape as the legacy rows already in
    /// the table.
    ///
    /// The input is a System.Text.Json <see cref="JsonElement"/>, NOT a Newtonsoft JObject.
    /// Program.cs registers only the System.Text.Json input formatter (no AddNewtonsoftJson, and
    /// no Microsoft.AspNetCore.Mvc.NewtonsoftJson package), so a JObject action parameter could
    /// never be model-bound: [ApiController] answered every submission with an automatic 400
    /// before the handler ran. Newtonsoft stays in use as a LIBRARY for the JSON that gets stored
    /// — it just cannot appear on an action signature. Guarded by RequestBodyBindingTests.
    /// </summary>
    public static class PendingOrderEditSubmissionReader
    {
        /// <summary>Column width of PendingOrderEdit.Reason. Enforced here because the body binds
        /// as a JsonElement, so MVC model validation never sees the wrapper's [StringLength].</summary>
        public const int MaxReasonLength = 1000;

        private static readonly JsonSerializerOptions Options = new()
        {
            PropertyNameCaseInsensitive = true
        };

        /// <summary>
        /// Parses either accepted shape. Returns false when the body carries no proposed changes
        /// at all — an empty body, a non-object, or a payload the DTO cannot be read out of.
        /// </summary>
        public static bool TryRead(JsonElement body, out SubmitPendingOrderEditDto submission)
        {
            submission = new SubmitPendingOrderEditDto();

            if (body.ValueKind != JsonValueKind.Object)
                return false;

            var raw = body.GetRawText();

            try
            {
                if (body.TryGetProperty("changes", out _) || body.TryGetProperty("Changes", out _))
                {
                    submission = JsonSerializer.Deserialize<SubmitPendingOrderEditDto>(raw, Options)
                                 ?? new SubmitPendingOrderEditDto();
                }
                else
                {
                    submission = new SubmitPendingOrderEditDto
                    {
                        Changes = JsonSerializer.Deserialize<SuperAdminUpdateOrderDto>(raw, Options)
                    };
                }
            }
            catch (JsonException)
            {
                // A body we cannot read is reported as "no proposed changes" rather than as a
                // server error — the caller gets the same actionable 400 either way.
                submission = new SubmitPendingOrderEditDto();
                return false;
            }

            submission.Reason = NormalizeReason(submission.Reason);
            return submission.Changes != null;
        }

        /// <summary>Trimmed, null when blank, and truncated to the column width.</summary>
        public static string? NormalizeReason(string? reason)
        {
            if (string.IsNullOrWhiteSpace(reason)) return null;
            var trimmed = reason.Trim();
            return trimmed.Length > MaxReasonLength ? trimmed.Substring(0, MaxReasonLength) : trimmed;
        }
    }
}
