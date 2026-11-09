namespace SoundLibert.Domain.Models;

/// <summary>
/// Volume normalisation from YouTube's per-track <c>loudnessDb</c>: how many dB a
/// track is louder (positive) or quieter (negative) than YouTube's target, the
/// "content loudness" of its Stats for nerds. A track 3.6 dB too loud plays at
/// 10^(-3.6/20) ≈ 66%, exactly as on YouTube.
/// </summary>
public static class LoudnessNormalization
{
    private const double MinimumGain = 0.25;

    /// <summary>
    /// Gain for a track. Only ever attenuates: a quiet track stays as it is,
    /// because boosting it above full scale would clip.
    /// </summary>
    public static double GainFor(double loudnessDb)
        => loudnessDb <= 0 ? 1.0 : Math.Clamp(Math.Pow(10, -loudnessDb / 20.0), MinimumGain, 1.0);
}
