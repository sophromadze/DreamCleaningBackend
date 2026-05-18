namespace DreamCleaningBackend.Services
{
    // Thrown by SmsService when RingCentral rejects the destination number as invalid (error
    // code CMN-414 / "InvalidParameter" on to.phoneNumber). Callers should treat this as a
    // user-facing "no SMS could be sent — the number on file is bad" rather than a server error.
    public class InvalidPhoneNumberException : Exception
    {
        public string PhoneNumber { get; }

        public InvalidPhoneNumberException(string phoneNumber, string message, Exception? inner = null)
            : base(message, inner)
        {
            PhoneNumber = phoneNumber;
        }
    }
}
