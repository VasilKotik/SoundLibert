namespace SoundLibert.Domain.Interfaces;

/// <summary>What the listener chose about AI-generated music: the switch, and their word about artists.</summary>
public interface IAiMusicSettings
{
    /// <summary>Keeps AI-generated music out of recommendations (search still finds it).</summary>
    bool HideAiMusic { get; set; }

    /// <summary>What the listener said about artists, by normalized name: AI (true) or not (false).</summary>
    IReadOnlyDictionary<string, bool> AiArtistMarks { get; }

    /// <summary>Records (or with null forgets) the listener's word about an artist.</summary>
    void SetAiArtistMark(string normalizedName, bool? isAi);
}
