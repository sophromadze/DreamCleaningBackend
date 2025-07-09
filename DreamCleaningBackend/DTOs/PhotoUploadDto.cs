namespace DreamCleaningBackend.DTOs
{
    public class PhotoUploadDto
    {
        public string FileName { get; set; }
        public string Base64Data { get; set; }
        public string ContentType { get; set; }
    }
}
