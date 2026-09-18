namespace DreamCleaningBackend.Services
{
    /// <summary>
    /// Thrown by <c>AuthService.CreateOrGetGuestUserAsync</c> when the contact email on a guest
    /// booking belongs to an account the anonymous caller may not be signed into — see
    /// <see cref="Helpers.GuestAccountClaimPolicy"/>.
    ///
    /// Its own type, rather than a bare Exception, so prepare-payment can answer with a clean
    /// "please sign in" that the customer can act on. Falling into that endpoint's generic catch
    /// would render it as "Failed to prepare payment: ..." — which reads as our fault and tells
    /// the customer nothing about what to do next.
    /// </summary>
    public class GuestAccountRequiresLoginException : Exception
    {
        /// <summary>Stable discriminator the frontend keys on, so the wording stays free to change.</summary>
        public const string Code = "account_exists";

        public GuestAccountRequiresLoginException(string message) : base(message)
        {
        }
    }
}
