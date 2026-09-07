namespace DreamCleaningBackend.Models
{
    /// <summary>
    /// Company officer title on a staff account. A THIRD, independent dimension alongside
    /// <see cref="UserRole"/> (what the app lets you do) and <see cref="AdminPosition"/>
    /// (which side of the per-order bonus you earn).
    ///
    /// It exists because the Contracts module needed an authority model that the general role
    /// hierarchy could not express: there, a CEO and a CTO outrank a SuperAdmin, and a SuperAdmin
    /// with no title is treated as an ordinary manager. Everywhere ELSE in the app this column is
    /// inert — SuperAdmin continues to mean exactly what it always meant, and nothing outside
    /// Contracts (and the OrgTitle assignment endpoint itself) reads this value.
    ///
    /// Non-nullable with a None default, following <see cref="AdminPosition"/>: a nullable "unset"
    /// would need a fallback rule at every read site, and None is the right answer for every
    /// account that existed before titles did.
    /// </summary>
    public enum OrgTitle
    {
        None = 0,
        CEO = 1,
        CTO = 2
    }
}
