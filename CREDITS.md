# Credits & Third-Party Notices

SoundLibert is licensed under the **GNU General Public License v3.0** (see `LICENSE`).
It reuses concepts and — in places — logic ported from other free software. Our
thanks to everyone below.

## Special thanks

### kiku-jw — https://github.com/kiku-jw
Helped with the synced lyrics: getting every line to land in time with the
recording that plays.

### Velune — https://github.com/nikhilvishwakarma00/Velune
Velune is a Kotlin/Android YouTube Music client licensed under **GPL-3.0**. Its
source files credit Kòi Natsuko (koiverse), and it builds on
[ArchiveTune](https://github.com/koiverse/ArchiveTune), Metrolist and InnerTune.
Our thanks go to the people behind all of them. SoundLibert is a from-scratch C#
desktop application, but several subsystems were studied and adapted from Velune:

- the InnerTube request model, client definitions and fallback strategy;
- stream resolution, `cver` patching and Range-probe validation;
- proof-of-origin (`poToken`) generation and visitor data bootstrap;
- the `next` / automix flow behind radio and autoplay;
- LRCLIB lyrics lookup and duration-based matching;
- the Google login concept (WebView sign-in → cookies + `DATASYNC_ID`).

Because Velune is GPL-3.0, SoundLibert is GPL-3.0 as well.

### LRCLIB — https://lrclib.net
Free, community-run lyrics API used for synchronized lyrics (fallback).

### iTunes Search API — https://performance-partners.apple.com/search-api
Apple's public search API, used for the genre chips in playlists. SoundLibert is
not affiliated with Apple.

### Lyrics on YouTube Music
Timed lyrics are read from YouTube Music, where they are provided by its lyrics
partners (e.g. LyricFind); they belong to their respective rights holders.

### spotify-ai-blocker (CennoxX) — https://github.com/CennoxX/spotify-ai-blocker
The "Don't recommend AI-generated music" setting reads this project's community
list of AI-generated artists (`SpotifyAiArtists.csv`; the project is MIT),
downloaded once a day and kept on the device; only the names are used. As its
README states, the list draws on:

- [loved.music AI Artists](https://github.com/gounetmusic/loved-music-ai-artists) — CC BY 4.0;
- [Zoundhub](https://github.com/xoundbyte/zoundhub) (formerly Soul Over AI) — CC BY 4.0;
- [Spotify AI Blocklist](https://github.com/eye-wave/spotify-ai-blocklist) — GPL-3.0.

SoundLibert matches the names against what is recommended (Home, charts, radio,
"you might also like"); search, albums, playlists and the library stay whole. A
few names on the list that also belong to real artists are never matched by the
list alone, and the listener's own word ("AI artist" / "Not AI" in a menu) wins.

## Libraries

| Library | License | Use |
|---------|---------|-----|
| Avalonia UI | MIT | Cross-platform desktop UI framework |
| Avalonia.Controls.WebView | MIT | Embedded web view (Google sign-in) |
| CommunityToolkit.Mvvm | MIT | MVVM source generators |
| NAudio | MIT | Audio decoding/playback (Windows) |
| Microsoft.Data.Sqlite | MIT | Local database |
| Microsoft.Extensions.DependencyInjection | MIT | Dependency injection |
| LibVLCSharp | LGPL-2.1 | .NET bindings for libvlc (Video mode / Canvas) |
| libvlc (VideoLAN) | LGPL-2.1-or-later | Native video engine (`libvlc/win-x64`, shipped next to the exe) |
| Velopack | MIT | Installer and automatic updates (GitHub Releases) |
| SkiaSharp / HarfBuzzSharp | MIT | Rendering (via Avalonia) |
| Inter (font) | SIL OFL 1.1 | UI typeface |
| Lucide icons (geometry adapted, portions from Feather) | ISC / MIT | The app's icon set (`Controls/IconSet.cs`) |

## LGPL notice (libvlc / LibVLCSharp)
SoundLibert **links against libvlc dynamically** (the `libvlc.dll` and its plugins
are loaded at runtime from the `libvlc/win-x64` folder next to the executable;
nothing is statically linked into SoundLibert's own binary). This keeps the
LGPL-2.1 obligation satisfiable: the library is not modified and can be replaced
by the user with their own compatible build of libvlc. Sources for libvlc and
LibVLCSharp are available from VideoLAN (https://www.videolan.org) and
https://code.videolan.org/videolan/LibVLCSharp under the LGPL-2.1.

## YouTube Music / YouTube
SoundLibert is an unofficial client and is **not affiliated with, endorsed by, or
sponsored by YouTube or Google**. "YouTube" and "YouTube Music" are trademarks of
Google LLC. All audio is streamed from YouTube's public endpoints and remains the
property of its respective rights holders. Users are responsible for complying
with YouTube's Terms of Service.

Icons/logos in `SoundLibert.UI/Assets` were provided by the project owner.

## Lucide

The icons in `SoundLibert.UI/Controls/IconSet.cs` are drawn in the construction of
Lucide (https://lucide.dev) and many adapt its path data.

ISC License. Copyright (c) for portions of Lucide are held by Cole Bemis 2013-2022 as
part of Feather (MIT). All other copyright (c) for Lucide are held by Lucide
Contributors 2022.

Permission to use, copy, modify, and/or distribute this software for any purpose with
or without fee is hereby granted, provided that the above copyright notice and this
permission notice appear in all copies.

THE SOFTWARE IS PROVIDED "AS IS" AND THE AUTHOR DISCLAIMS ALL WARRANTIES WITH REGARD
TO THIS SOFTWARE INCLUDING ALL IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS. IN
NO EVENT SHALL THE AUTHOR BE LIABLE FOR ANY SPECIAL, DIRECT, INDIRECT, OR CONSEQUENTIAL
DAMAGES OR ANY DAMAGES WHATSOEVER RESULTING FROM LOSS OF USE, DATA OR PROFITS, WHETHER
IN AN ACTION OF CONTRACT, NEGLIGENCE OR OTHER TORTIOUS ACTION, ARISING OUT OF OR IN
CONNECTION WITH THE USE OR PERFORMANCE OF THIS SOFTWARE.
