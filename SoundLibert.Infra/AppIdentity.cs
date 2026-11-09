using System.Reflection;

namespace SoundLibert.Infra;

/// <summary>How this app names itself to the public APIs that ask clients to identify themselves.</summary>
internal static class AppIdentity
{
    public static string UserAgent { get; } =
        $"SoundLibert/{Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0"} (https://github.com/VasilKotik/SoundLibert)";
}
