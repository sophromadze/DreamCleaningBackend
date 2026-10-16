using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DreamCleaningBackend.Services.AdminAgent
{
    /// <summary>
    /// The second formatter of the shared tool core (2026-10): turns a tool result the panel
    /// agent would receive into the compact shape the Claude connector returns. It changes the
    /// PRESENTATION only — every figure is the one the canonical tool computed:
    /// <list type="bullet">
    /// <item>the long <c>definition</c> paragraph becomes its short name ("Statistics Net Income",
    /// "Admins-tab net" …). The full wording is one <c>describe_data</c> call away, so a money
    /// answer still names its definition without paying for the paragraph on every call;</item>
    /// <item>top-level echoes of the input (date range, grouping, filters …) are dropped — the
    /// caller already knows what it asked for;</item>
    /// <item>empty strings are dropped (nulls already are);</item>
    /// <item>non-ASCII text (Georgian names) is written as-is instead of \uXXXX escapes.</item>
    /// </list>
    /// The panel format never passes through here, so its output stays byte-identical.
    /// </summary>
    public static class AgentToolCompactFormatter
    {
        private static readonly string[] EchoKeys =
            { "date_range", "grouped_by", "period", "filters", "admin_id", "includes_upcoming" };

        private static readonly JsonSerializerOptions Output = new()
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = false
        };

        public static string Format(string content, string? definitionName)
        {
            JsonNode? root;
            try
            {
                root = JsonNode.Parse(content);
            }
            catch (JsonException)
            {
                // A plain-text result (or one the panel formatter cut off) passes through untouched.
                return content;
            }
            if (root == null) return content;

            if (root is JsonObject obj)
            {
                foreach (var key in EchoKeys)
                    obj.Remove(key);
                if (!string.IsNullOrWhiteSpace(definitionName))
                {
                    // Re-inserted FIRST, so the definition leads the result.
                    obj.Remove("definition");
                    var rest = obj.ToList();
                    obj.Clear();
                    obj["definition"] = definitionName;
                    foreach (var (k, v) in rest)
                        obj[k] = v;
                }
            }

            DropEmptyStrings(root);
            return root.ToJsonString(Output);
        }

        private static void DropEmptyStrings(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject o:
                    foreach (var key in o.Where(p => p.Value is JsonValue v && v.TryGetValue<string>(out var s) && s.Length == 0)
                                         .Select(p => p.Key).ToList())
                        o.Remove(key);
                    foreach (var (_, child) in o)
                        DropEmptyStrings(child);
                    break;
                case JsonArray a:
                    foreach (var child in a)
                        DropEmptyStrings(child);
                    break;
            }
        }
    }
}
