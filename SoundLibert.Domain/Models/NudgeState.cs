namespace SoundLibert.Domain.Models;

/// <summary>
/// How long and how often the app has been used, and what it has already asked of the
/// listener: a star on GitHub, asked gently and rarely.
/// </summary>
public sealed class NudgeState
{
    public DateTime? FirstLaunchUtc { get; set; }
    public int Launches { get; set; }
    public int StarAsks { get; set; }
    public DateTime? LastStarAskUtc { get; set; }

    /// <summary>They went to give the star: never ask again.</summary>
    public bool StarGiven { get; set; }

    /// <summary>
    /// Only someone who has come back: five starts over three days at least, twice at most,
    /// three weeks apart, and never after they went to give it.
    /// </summary>
    public bool ShouldAskForStar(DateTime nowUtc) =>
        !StarGiven && StarAsks < 2 && Launches >= 5 &&
        FirstLaunchUtc is { } first && nowUtc - first >= TimeSpan.FromDays(3) &&
        (LastStarAskUtc is not { } last || nowUtc - last >= TimeSpan.FromDays(21));
}
