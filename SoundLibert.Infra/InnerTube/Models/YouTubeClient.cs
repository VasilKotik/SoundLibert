namespace SoundLibert.Infra.InnerTube.Models;

public class YouTubeClient
{
    public string ClientName { get; init; } = string.Empty;
    public string ClientVersion { get; init; } = string.Empty;
    public string ClientId { get; init; } = string.Empty;
    public string UserAgent { get; init; } = string.Empty;
    public bool LoginSupported { get; init; }
    public bool UseSignatureTimestamp { get; init; }
    public bool IsEmbedded { get; init; }
    public string? OsName { get; init; }
    public string? OsVersion { get; init; }
    public string? DeviceMake { get; init; }
    public string? DeviceModel { get; init; }
    public string? AndroidSdkVersion { get; init; }

    public const string OriginYouTubeMusic = "https://music.youtube.com";
    public const string RefererYouTubeMusic = OriginYouTubeMusic + "/";
    public const string ApiUrlYouTubeMusic = OriginYouTubeMusic + "/youtubei/v1/";

    public static readonly YouTubeClient WEB = new()
    {
        ClientName = "WEB",
        ClientVersion = "2.20260114.00.00",
        ClientId = "1",
        UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36"
    };

    public static readonly YouTubeClient WEB_REMIX = new()
    {
        ClientName = "WEB_REMIX",
        ClientVersion = "1.20260114.01.00",
        ClientId = "67",
        UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36",
        LoginSupported = true,
        UseSignatureTimestamp = true
    };

    public static readonly YouTubeClient IOS = new()
    {
        ClientName = "IOS",
        ClientVersion = "19.29.1",
        ClientId = "5",
        UserAgent = "com.google.ios.youtube/19.29.1 (iPhone16,2; U; CPU iOS 17_5_1 like Mac OS X;)",
        // NOTE: kept in sync with Velune — IOS sends only osVersion (with build number), no device fields.
        OsVersion = "17.5.1.21F90"
    };

    public static readonly YouTubeClient ANDROID_MUSIC = new()
    {
        ClientName = "ANDROID_MUSIC",
        ClientVersion = "7.27.52",
        ClientId = "21",
        UserAgent = "com.google.android.apps.youtube.music/7.27.52 (Linux; U; Android 15; en_US; Pixel 9 Pro; Build/AP4A.250205.002; Cronet/132.0.6834.79) gzip",
        LoginSupported = true,
        UseSignatureTimestamp = true,
        OsName = "Android",
        OsVersion = "15",
        DeviceMake = "Google",
        DeviceModel = "Pixel 9 Pro",
        AndroidSdkVersion = "35"
    };

    public static readonly YouTubeClient ANDROID = new()
    {
        ClientName = "ANDROID",
        ClientVersion = "21.10.38",
        ClientId = "3",
        UserAgent = "com.google.android.youtube/21.10.38 (Linux; U; Android 15; en_US; Pixel 9 Pro; Build/AP4A.250205.002; Cronet/132.0.6834.79) gzip",
        LoginSupported = true,
        UseSignatureTimestamp = true,
        OsName = "Android",
        OsVersion = "15",
        DeviceMake = "Google",
        DeviceModel = "Pixel 9 Pro",
        AndroidSdkVersion = "35"
    };

    public static readonly YouTubeClient ANDROID_CREATOR = new()
    {
        ClientName = "ANDROID_CREATOR",
        ClientVersion = "23.47.101",
        ClientId = "14",
        // NOTE: kept in sync with Velune — Creator uses "Pixel 9 Pro Fold" + AP3A build.
        UserAgent = "com.google.android.apps.youtube.creator/23.47.101 (Linux; U; Android 15; en_US; Pixel 9 Pro Fold; Build/AP3A.241005.015.A2; Cronet/132.0.6779.0)",
        LoginSupported = true,
        UseSignatureTimestamp = true,
        OsName = "Android",
        OsVersion = "15",
        DeviceMake = "Google",
        DeviceModel = "Pixel 9 Pro Fold",
        AndroidSdkVersion = "35"
    };

    public static readonly YouTubeClient IOS_MUSIC = new()
    {
        ClientName = "IOS_MUSIC",
        ClientVersion = "7.27.0",
        ClientId = "26",
        UserAgent = "com.google.ios.youtubemusic/7.27.0 (iPhone16,2; U; CPU iOS 17_5_1 like Mac OS X;)",
        OsName = "iOS",
        // NOTE: kept in sync with Velune — build suffix matters.
        OsVersion = "17.5.1.21F90",
        DeviceMake = "Apple",
        DeviceModel = "iPhone16,2"
    };

    public static readonly YouTubeClient ANDROID_TESTSUITE = new()
    {
        ClientName = "ANDROID_TESTSUITE",
        ClientVersion = "1.9",
        ClientId = "30",
        UserAgent = "com.google.android.youtube/1.9 (Linux; U; Android 15; en_US; Pixel 9 Pro; Build/AP4A.250205.002)",
        OsName = "Android",
        OsVersion = "15",
        DeviceMake = "Google",
        DeviceModel = "Pixel 9 Pro",
        AndroidSdkVersion = "35"
    };

    public static readonly YouTubeClient ANDROID_VR_NO_AUTH = new()
    {
        ClientName = "ANDROID_VR",
        ClientVersion = "1.37",
        ClientId = "28",
        // NOTE: kept in sync with Velune.
        UserAgent = "com.google.android.apps.youtube.vr.oculus/1.37 (Linux; U; Android 12; en_US; Quest 3; Build/SQ3A.220605.009.A1; Cronet/107.0.5284.2)",
        OsName = "Android",
        OsVersion = "12",
        DeviceMake = "Oculus",
        DeviceModel = "Quest 3",
        AndroidSdkVersion = "32"
    };

    public static readonly YouTubeClient TVHTML5_SIMPLY_EMBEDDED_PLAYER = new()
    {
        ClientName = "TVHTML5_SIMPLY_EMBEDDED_PLAYER",
        ClientVersion = "2.0",
        ClientId = "85",
        UserAgent = "Mozilla/5.0 (PlayStation; PlayStation 4/12.02) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/15.4 Safari/605.1.15",
        LoginSupported = true,
        IsEmbedded = true,
        UseSignatureTimestamp = true
    };

    /// <summary>
    /// Primary anonymous stream client (verified 2026-09-13): the only client
    /// returning direct (non-ciphered) audio URLs without login.
    /// Kept in sync with Velune's VISIONOS definition.
    /// </summary>
    public static readonly YouTubeClient VISIONOS = new()
    {
        ClientName = "VISIONOS",
        ClientVersion = "0.1",
        ClientId = "101",
        UserAgent = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Safari/605.1.15",
        OsName = "visionOS",
        OsVersion = "1.3.21O771",
        DeviceMake = "Apple",
        DeviceModel = "RealityDevice14,1"
    };

    public static readonly YouTubeClient MWEB = new()
    {
        ClientName = "MWEB",
        ClientVersion = "2.20260114.00.00",
        ClientId = "2",
        UserAgent = "Mozilla/5.0 (Linux; Android 15; Pixel 9 Pro) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Mobile Safari/537.36"
    };

    public static readonly YouTubeClient TVHTML5 = new()
    {
        ClientName = "TVHTML5",
        ClientVersion = "7.20260114.00.00",
        ClientId = "7",
        UserAgent = "Mozilla/5.0 (PlayStation; PlayStation 4/12.02) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/15.4 Safari/605.1.15"
    };

    public static readonly YouTubeClient WEB_CREATOR = new()
    {
        ClientName = "WEB_CREATOR",
        ClientVersion = "1.20260114.00.00",
        ClientId = "62",
        UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36"
    };

    public static readonly YouTubeClient ANDROID_UNPLUGGED = new()
    {
        ClientName = "ANDROID_UNPLUGGED",
        ClientVersion = "8.49.0",
        ClientId = "29",
        UserAgent = "com.google.android.apps.youtube.unplugged/8.49.0 (Linux; U; Android 15; en_US; Pixel 9 Pro; Build/AP4A.250205.002; Cronet/132.0.6834.79) gzip",
        LoginSupported = true,
        UseSignatureTimestamp = true,
        OsName = "Android",
        OsVersion = "15",
        DeviceMake = "Google",
        DeviceModel = "Pixel 9 Pro",
        AndroidSdkVersion = "35"
    };

    public static readonly YouTubeClient IPADOS = new()
    {
        ClientName = "IOS",
        ClientVersion = "19.22.3",
        ClientId = "5",
        UserAgent = "com.google.ios.youtube/19.22.3 (iPad7,6; U; CPU iPadOS 17_7_10 like Mac OS X; en-US)",
        OsName = "iPadOS",
        OsVersion = "17.7.10.21H450",
        DeviceMake = "Apple",
        DeviceModel = "iPad7,6"
    };
}
