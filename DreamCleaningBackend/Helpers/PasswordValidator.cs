using System.Text.RegularExpressions;

namespace DreamCleaningBackend.Helpers
{
    public static class PasswordValidator
    {
        public static bool IsValidPassword(string password, out string errorMessage)
        {
            errorMessage = string.Empty;

            if (string.IsNullOrEmpty(password))
            {
                errorMessage = "Password is required";
                return false;
            }

            // Check minimum length
            if (password.Length < 8)
            {
                errorMessage = "Password must be at least 8 characters long";
                return false;
            }

            // Check for at least one letter
            if (!Regex.IsMatch(password, @"[a-zA-Z]"))
            {
                errorMessage = "Password must contain at least one letter";
                return false;
            }

            // Check for at least one number
            if (!Regex.IsMatch(password, @"\d"))
            {
                errorMessage = "Password must contain at least one number";
                return false;
            }

            // Check for at least one uppercase letter
            if (!Regex.IsMatch(password, @"[A-Z]"))
            {
                errorMessage = "Password must contain at least one uppercase letter";
                return false;
            }

            return true;
        }

        public static string GetPasswordRequirementsMessage()
        {
            return "Password must be at least 8 characters long and contain at least one uppercase letter, one lowercase letter, and one number.";
        }
    }
}