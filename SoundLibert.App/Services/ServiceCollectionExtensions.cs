using Microsoft.Extensions.DependencyInjection;
using SoundLibert.Audio;
using SoundLibert.Domain.Interfaces;
using SoundLibert.Infra;
using SoundLibert.Infra.Genres;
using SoundLibert.Infra.InnerTube;
using SoundLibert.Infra.Lyrics;
using SoundLibert.Infra.SQLite;

namespace SoundLibert.App.Services;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSoundLibert(this IServiceCollection services)
    {
        var innerTubeClient = new InnerTubeClient();
        services.AddSingleton(innerTubeClient);
        services.AddSingleton<IMusicProvider>(new InnerTubeMusicProvider(innerTubeClient));
        services.AddSingleton<IAudioPlayer, NAudioPlayer>();
        services.AddSingleton<IMediaProbe, MediaFoundationProbe>();
        services.AddSingleton<IQueueService, QueueService>();
        services.AddSingleton<IMusicDatabase, SqliteMusicDatabase>();
        services.AddSingleton<ILyricsProvider, CompositeLyricsProvider>();
        services.AddSingleton<IGenreService, ITunesGenreService>();
        services.AddSingleton<IViewCountService, ViewCountService>();
        services.AddSingleton<IAccountService, AccountService>();
        services.AddSingleton<ILocalLibrary, LocalLibraryService>();
        services.AddSingleton<IDownloadService, DownloadService>();
        services.AddSingleton<ILikeService, LikeService>();
        services.AddSingleton<AppSettingsService>();
        services.AddSingleton<IAppSettings>(sp => sp.GetRequiredService<AppSettingsService>());
        services.AddSingleton<IAudioSettings>(sp => sp.GetRequiredService<AppSettingsService>());

        return services;
    }
}
