namespace DreamCleaningBackend.Services
{
    /// <summary>
    /// Thrown when cleaners cannot be assigned to an order because of a hard scheduling
    /// conflict (the 1-hour-gap rule). The message is safe to surface to admins.
    /// </summary>
    public class CleanerAssignmentException : Exception
    {
        public CleanerAssignmentException(string message) : base(message) { }
    }
}
