namespace SoundLibert.Domain.Interfaces;

/// <summary>
/// User audio preferences (normalisation + equalizer), shared between the
/// settings screen and the playback pipeline so a change applies immediately
/// and survives across tracks.
/// </summary>
public interface IAudioSettings
{
    bool NormalizeVolume { get; set; }
    double BassDb { get; set; }
    double MidDb { get; set; }
    double TrebleDb { get; set; }
}
