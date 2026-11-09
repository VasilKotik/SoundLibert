using NAudio.Wave;
using SoundLibert.Domain.Interfaces;

namespace SoundLibert.Audio;

/// <summary>
/// Reads local media metadata with NAudio's Media Foundation reader (Windows).
/// Returns 0 on unsupported platforms/files rather than throwing.
/// </summary>
public sealed class MediaFoundationProbe : IMediaProbe
{
    public int GetDurationSeconds(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return 0;

        try
        {
            // A single native reader, or the file stays open until the GC runs.
            using var reader = new MediaFoundationReader(path,
                new MediaFoundationReader.MediaFoundationReaderSettings { SingleReaderObject = true });
            return (int)Math.Round(reader.TotalTime.TotalSeconds);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[LocalProbe] {Path.GetFileName(path)}: {ex.Message}");
            return 0;
        }
    }
}
