namespace DreamCleaningBackend.Models
{
    public enum UserRole
    {
        Customer = 0,
        SuperAdmin = 1,
        Admin = 2,
        Moderator = 3,

        /// <summary>
        /// A cleaner's LOGIN ACCOUNT. Deliberately NOT the cleaner record itself - the person is a
        /// row in the Cleaners table (see Models/Cleaner.cs) and orders are assigned to that row,
        /// never to a User. This role only says "this account belongs to a cleaner", which routes
        /// them to the read-only cleaner portal and keeps them out of the customer and admin views.
        ///
        /// Cleaner is NOT a staff role in the permission sense: PermissionService grants it nothing,
        /// and TwoFactorService.RequiresTwoFactor deliberately excludes it (a cleaner reading their
        /// own schedule on a phone must not be forced through a PIN meant for people who can move
        /// money). Value 4 is new - no existing row holds it.
        /// </summary>
        Cleaner = 4,
    }
}
