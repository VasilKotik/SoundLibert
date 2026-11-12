using Microsoft.Extensions.DependencyInjection;
using SoundLibert.Domain.Interfaces;
using SoundLibert.Infra;
using SoundLibert.Infra.AiMusic;
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
        // The catalogue, with AI-generated acts kept out of what is recommended (search and
        // what the listener opens stay whole).
        var catalogue = new InnerTubeMusicProvider(innerTubeClient);
        services.AddSingleton<IAiMusicFilter, AiMusicFilter>();
        services.AddSingleton<IMusicProvider>(sp => new RecommendationFilter(catalogue, sp.GetRequiredService<IAiMusicFilter>()));
        // The player and the decoder probe belong to each platform (NAudio and Media
        // Foundation on Windows, Media3 on Android): the app registers its own.
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
        services.AddSingleton<IAiMusicSettings>(sp => sp.GetRequiredService<AppSettingsService>());

        return services;
    }
}
