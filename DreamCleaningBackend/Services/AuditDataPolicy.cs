using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DreamCleaningBackend.Services;

/// <summary>Credentials never belong in a restore snapshot or an audit API response.</summary>
public static class AuditDataPolicy
{
    public static bool IsSecret(string name)
    {
        var key = new string(name.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        return key.Contains("token") || key.Contains("password") || key.Contains("secret")
            || key.Contains("apikey") || key.Contains("authorization") || key.Contains("credential")
            || key.Contains("privatekey") || key.Contains("refreshsession") || key.Contains("loginotp") || key.Contains("twofactorpin") || key.Contains("2fapin")
            || key is "otp" or "otpcode" or "pinhash" or "privatekey" or "emailcodehash";
    }

    public static string? SanitizeJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return json;
        try { return Sanitize(JToken.Parse(json)).ToString(Formatting.None); }
        catch (JsonException) { return null; } // Unparseable snapshots must not leak raw credentials.
    }

    public static JToken Sanitize(JToken value)
    {
        var copy = value.DeepClone();
        if (copy is JObject obj)
        {
            // Settings payloads sometimes store the property name as data: { Key, Value }.
            var settingKey = (string?)(obj["Key"] ?? obj["key"] ?? obj["SettingKey"]);
            foreach (var property in obj.Properties().ToList())
            {
                if (IsSecret(property.Name) || (settingKey != null && IsSecret(settingKey)
                    && property.Name.Equals("Value", StringComparison.OrdinalIgnoreCase)))
                    property.Remove();
                else property.Value = Sanitize(property.Value);
            }
        }
        else if (copy is JArray array)
        {
            for (var i = 0; i < array.Count; i++) array[i] = Sanitize(array[i]);
        }
        else if (copy.Type == JTokenType.String)
        {
            var text = (string?)copy;
            if (text?.TrimStart().StartsWith('{') == true || text?.TrimStart().StartsWith('[') == true)
            {
                try { return new JValue(Sanitize(JToken.Parse(text)).ToString(Formatting.None)); }
                catch (JsonException) { }
            }
        }
        return copy;
    }
}
