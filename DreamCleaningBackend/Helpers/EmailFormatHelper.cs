using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace DreamCleaningBackend.Helpers
{
    /// <summary>
    /// Converts plain text to HTML for emails, preserving paragraph breaks:
    /// double newline = new paragraph (with spacing), single newline = &lt;br/&gt;.
    /// </summary>
    public static class EmailFormatHelper
    {
        public static string FormatEmailContentWithParagraphs(string? plainText)
        {
            if (string.IsNullOrEmpty(plainText)) return "";
            var t = plainText.Replace("\r\n", "\n").Replace("\r", "\n");
            t = WebUtility.HtmlEncode(t);
            var paragraphs = Regex.Split(t, @"\n{2,}");
            var sb = new StringBuilder();
            foreach (var p in paragraphs)
            {
                var trimmed = p.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;
                var inner = trimmed.Replace("\n", "<br/>");
                sb.Append("<p style='margin:0 0 1em 0;'>").Append(inner).Append("</p>");
            }
            var body = sb.Length > 0 ? sb.ToString() : "<p style='margin:0 0 1em 0;'></p>";
            return $@"<!DOCTYPE html><html><head><meta charset='UTF-8'/><style>body{{font-family:Arial,sans-serif;color:#333;line-height:1.5;}} p{{margin:0 0 1em 0;}}</style></head><body>{body}</body></html>";
        }
    }
}
