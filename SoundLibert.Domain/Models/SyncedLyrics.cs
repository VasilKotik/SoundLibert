namespace SoundLibert.Domain.Models;

/// <summary>
/// How precisely a lyrics payload is timed. The UI must handle all three:
/// <see cref="None"/> renders as static text, <see cref="Line"/> highlights
/// the active line, <see cref="Word"/> additionally tracks the active word.
/// Word timing is never faked — without provider timestamps the level stays
/// at <see cref="Line"/> (or <see cref="None"/>).
/// </summary>
public enum LyricsSyncType
{
    None,
    Line,
    Word
}

/// <summary>One word with its own timing (only present when the provider supplies it).</summary>
public sealed class SyncedLyricWord
{
    public TimeSpan Start { get; init; }
    public TimeSpan End { get; init; }
    public string Text { get; init; } = string.Empty;
}

/// <summary>
/// One lyrics line. <see cref="End"/> is explicit when the provider gives it,
/// otherwise derived from the next line's start (last line: track length when
/// known, else its own start).
/// </summary>
public sealed class SyncedLyricLine
{
    public TimeSpan Start { get; init; }
    public TimeSpan End { get; init; }
    public string Text { get; init; } = string.Empty;
    public IReadOnlyList<SyncedLyricWord> Words { get; init; } = Array.Empty<SyncedLyricWord>();

    public bool HasWords => Words.Count > 0;
}

/// <summary>
/// Provider-agnostic lyrics: every source (LRC, timed API payloads, plain
/// text) normalizes into this shape before reaching playback/UI code.
/// Immutable once built, so it can be shared across threads.
/// </summary>
public sealed class SyncedLyrics
{
    public LyricsSyncType SyncType { get; init; } = LyricsSyncType.None;
    public string? Language { get; init; }
    public IReadOnlyList<SyncedLyricLine> Lines { get; init; } = Array.Empty<SyncedLyricLine>();

    public bool HasLines => Lines.Count > 0;

    public static SyncedLyrics Empty { get; } = new();
}
