namespace SoundLibert.Domain.Interfaces;

public interface ILyricsProvider
{
    string Name { get; }
    bool IsEnabled { get; }
    Task<string?> GetLyricsAsync(string videoId, string title, string artist, string? album, int durationSeconds, CancellationToken ct = default);

    /// <summary>
    /// Lyrics timed for exactly this video id (no duration matching), or null.
    /// Used to upgrade cached lyrics whose timing came from a looser match.
    /// </summary>
    Task<string?> GetExactTimedLyricsAsync(string videoId, CancellationToken ct = default)
        => Task.FromResult<string?>(null);
}
