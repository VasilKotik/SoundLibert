namespace SoundLibert.Domain.Models;

public class StreamInfo
{
    public string Url { get; set; } = string.Empty;
    public int ExpiresInSeconds { get; set; }
    public string? MimeType { get; set; }
    public int Bitrate { get; set; }
    public double LoudnessDb { get; set; }
    public string? AudioQuality { get; set; }
    public string? ClientName { get; set; }
}
