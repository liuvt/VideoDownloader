# Clip2Down

Supported download inputs include YouTube, Facebook, TikTok, Instagram, X (Twitter), Reddit and Threads public links. Reddit uses the bundled yt-dlp extractor; Threads uses a public-page media resolver before the normal yt-dlp download pipeline. — Blazor Server Video Downloader

A .NET 8 Blazor Server application that downloads accessible YouTube and Facebook media through `yt-dlp` and uses FFmpeg for merging and audio conversion.

> Download only media you own, media you are authorized to save, or content distributed under a compatible license. The application does not bypass DRM, paywalls or platform access controls.

## What changed in this US/Los Angeles edition

- Entire public interface rewritten in US English.
- Distinct developer-tool visual direction inspired by GitHub Primer rather than a generic SaaS landing page.
- Local system font stack matching GitHub Primer: Mona Sans when available, followed by Apple/Windows system fonts.
- Monospace utility labels and terminal-style download preview.
- Responsive layout for desktop and mobile.
- SEO content for common US search intent: YouTube video downloader, Facebook video downloader, YouTube to MP3, YouTube Shorts downloader and Facebook Reels downloader.
- `en-US` HTML language, Open Graph locale and web manifest.
- Canonical URL and `hreflang="en-US"`.
- JSON-LD for `WebSite`, `WebApplication` and visible `FAQPage` content.
- US service area in structured data without pretending to be a physical local business.
- `robots.txt`, `sitemap.xml`, no-index headers on temporary download endpoints and a 1200×630 social image.

SEO cannot guarantee a number-one ranking. Search visibility also depends on domain authority, useful original content, crawlability, real-world performance, backlinks, competition and compliance with Google and platform policies.

## Windows setup

Place the required executables in the local `Tools` directory:

```text
Tools/
├── yt-dlp.exe
├── ffmpeg.exe
└── ffprobe.exe

dotnet publish .\VideoDownloader.Blazor.csproj -c Release -o .\publish
```

Download `yt-dlp.exe` from the official yt-dlp release page. Install FFmpeg with Winget or place the FFmpeg executables in `Tools`.

```powershell
cd D:\Dev\VideoDownloader

New-Item -ItemType Directory -Force .\Tools | Out-Null

Invoke-WebRequest `
  -Uri "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe" `
  -OutFile ".\Tools\yt-dlp.exe"

winget install --id Gyan.FFmpeg -e

.\Tools\yt-dlp.exe --version
ffmpeg -version

dotnet restore
dotnet watch
```

The service now resolves a relative `Downloader:YtDlpPath` against the project content root first, so `Tools\\yt-dlp.exe` works even when the process launch directory changes.

When FFmpeg is stored in `Tools` instead of the Windows PATH, add that directory before running the app:

```powershell
$env:Path = "$(Resolve-Path .\Tools);$env:Path"
dotnet watch
```

## Ubuntu setup

```bash
sudo apt update
sudo apt install -y ffmpeg python3
sudo wget https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp \
  -O /usr/local/bin/yt-dlp
sudo chmod a+rx /usr/local/bin/yt-dlp
```

Override the Windows development path on Linux:

```bash
export Downloader__YtDlpPath=/usr/local/bin/yt-dlp
dotnet run --urls http://0.0.0.0:4123
```


## YouTube HTTP 403 compatibility (August 2026)

YouTube now uses JavaScript challenges and increasingly enforces Proof of Origin (PO) tokens for media requests. A format may appear in `yt-dlp --dump-single-json` but still return `HTTP Error 403: Forbidden` when the Google Video Server URL is fetched.

This project therefore defaults to a conservative anonymous YouTube mode:

- `youtube:player_client=web_safari`
- only HLS (`m3u8`) YouTube formats are rendered in the quality picker
- the exact analyzed format ID is still used for the first download attempt
- on a YouTube 403, the worker performs one fresh HLS retry at the **same requested height**; it never silently downgrades 1080p to 720p
- `--remote-components ejs:github` is enabled for YouTube challenge scripts
- Deno is auto-discovered by yt-dlp; if Node is installed, Clip2Down automatically enables it with `--js-runtimes node`

Keep yt-dlp current. On Windows:

```powershell
.\Tools\yt-dlp.exe -U
.\Tools\yt-dlp.exe --version
```

For current YouTube extraction, install either Deno 2.3+ (recommended by yt-dlp) or Node 22+. After installing the runtime, restart the ASP.NET process so the updated `PATH` is visible.

On Linux, update the server binary before restarting the service:

```bash
sudo /usr/local/bin/yt-dlp -U
/usr/local/bin/yt-dlp --version
node --version || deno --version
sudo systemctl restart videodownloader
```

The relevant settings are:

```json
{
  "Downloader": {
    "YouTubeCompatibilityMode": true,
    "YouTubePlayerClient": "web_safari",
    "YouTubeSafeFormatsOnly": true,
    "YouTubeEnableRemoteEjs": true,
    "YouTubeJavaScriptRuntime": ""
  }
}
```

`YouTubeJavaScriptRuntime` may be set to `"node"` or `"deno"` explicitly. Leave it empty to let yt-dlp discover Deno and let Clip2Down enable Node automatically when present.

For a high-volume production downloader, HLS compatibility mode is a fallback rather than a permanent guarantee. YouTube's current yt-dlp guidance recommends a PO Token Provider plugin with the `mweb` client when token enforcement affects the required formats. Do not hard-code a manually copied token because modern PO tokens can be bound to individual videos and expire.

## Production SEO configuration

Set the real HTTPS origin before deployment. Do not leave `BaseUrl` empty behind a reverse proxy unless forwarded headers are configured correctly.

```json
{
  "Site": {
    "Name": "Clip2Down",
    "BaseUrl": "https://your-domain.com",
    "Description": "Download public YouTube videos, YouTube Shorts, Facebook videos and Reels as MP4 or MP3 with a fast online video downloader built for US users.",
    "Language": "en-US"
  }
}
```

Or use environment variables:

```bash
export Site__BaseUrl=https://your-domain.com
export Site__Language=en-US
```

After deployment:

1. Verify `/robots.txt` and `/sitemap.xml` use the production HTTPS domain.
2. Add the domain property to Google Search Console.
3. Submit `/sitemap.xml` and request indexing for the home page.
4. Test structured data and inspect the rendered HTML source.
5. Measure Core Web Vitals with real field data.
6. Build useful supporting pages only when they provide unique, helpful content; do not create thin location or keyword doorway pages.

## Downloader configuration

```json
{
  "Downloader": {
    "YtDlpPath": "Tools\\yt-dlp.exe",
    "DownloadRoot": "App_Data/downloads",
    "CookiesFile": "",
    "RetentionHours": 6,
    "MaxQueueLength": 20,
    "MaxVideoSize": "2G"
  }
}
```

For private or login-required media, configure a protected Netscape cookie file on the server. Never allow public users to upload account cookies through the website.

## Production hardening checklist

- Add authentication or per-user download ownership.
- Add ASP.NET Core rate limiting by IP and user.
- Apply bandwidth, file-size, duration and daily quota limits.
- Store jobs in a database when history must survive restarts.
- Run downloads in a dedicated worker for multi-instance deployment.
- Keep `yt-dlp` updated because source platforms change frequently.
- Review YouTube, Facebook, copyright and local legal requirements before opening the service publicly.


## Fix Reconnect blazorserver “Rejoining the server…”.

1. 
```C#
<!-- Reconnect element hiden -->
    <div id="components-reconnect-modal"
        class="components-reconnect-hide"
        aria-hidden="true"></div>
    <script src="~/js/blazor-reconnect.js" asp-append-version="true"></script>

    <script src="js/site.js" asp-append-version="true"></script>
    <script src="_framework/blazor.server.js"></script>

    <script>
    Blazor.start({
        configureSignalR: function (builder) {
            // allow mobile delay or timeout
            builder.withServerTimeout(60000);
            builder.withKeepAliveInterval(15000);
        },

        reconnectionOptions: {
            maxRetries: 20,

            retryIntervalMilliseconds: function (previousAttempts) {
                const delays = [
                    0,
                    500,
                    1000,
                    2000,
                    3000,
                    5000,
                    10000,
                    15000,
                    30000
                ];

                return delays[
                    Math.min(previousAttempts, delays.length - 1)
                ];
            }
        }
    });
```

2. Tạo wwwroot/js/blazor-reconnect.js

```Js
(function () {
    "use strict";

    let reconnecting = false;
    let reloadScheduled = false;

    function getReconnectModal() {
        return document.getElementById("components-reconnect-modal");
    }

    function isDisconnected() {
        const modal = getReconnectModal();

        if (!modal) {
            return false;
        }

        return (
            modal.classList.contains("components-reconnect-show") ||
            modal.classList.contains("components-reconnect-retrying") ||
            modal.classList.contains("components-reconnect-failed") ||
            modal.classList.contains("components-reconnect-rejected")
        );
    }

    function scheduleReload(delay = 1200) {
        if (reloadScheduled) {
            return;
        }

        reloadScheduled = true;

        window.setTimeout(function () {
            window.location.reload();
        }, delay);
    }

    async function reconnectOrReload() {
        if (reconnecting || !isDisconnected()) {
            return;
        }

        if (!window.Blazor || typeof window.Blazor.reconnect !== "function") {
            scheduleReload();
            return;
        }

        reconnecting = true;

        try {
            const connected = await window.Blazor.reconnect();

            // false nghĩa là server không còn circuit cũ.
            if (connected === false) {
                scheduleReload();
            }
        } catch (error) {
            console.debug("Blazor reconnect failed:", error);
            scheduleReload();
        } finally {
            reconnecting = false;
        }
    }

    function handleReconnectStateChanged(event) {
        const state = event.detail?.state;

        switch (state) {
            case "failed":
                reconnectOrReload();
                break;

            case "rejected":
                // Circuit đã hết hạn hoặc server vừa restart.
                scheduleReload(300);
                break;

            case "hide":
                reconnecting = false;
                reloadScheduled = false;
                break;
        }
    }

    function initialize() {
        const modal = getReconnectModal();

        if (modal) {
            modal.addEventListener(
                "components-reconnect-state-changed",
                handleReconnectStateChanged
            );
        }

        // Khi người dùng chuyển về tab.
        document.addEventListener("visibilitychange", function () {
            if (document.visibilityState === "visible") {
                window.setTimeout(reconnectOrReload, 150);
            }
        });

        // Một số trình duyệt mobile dùng pageshow khi khôi phục tab.
        window.addEventListener("pageshow", function () {
            window.setTimeout(reconnectOrReload, 150);
        });

        window.addEventListener("online", function () {
            window.setTimeout(reconnectOrReload, 150);
        });

        window.addEventListener("focus", function () {
            window.setTimeout(reconnectOrReload, 150);
        });
    }

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", initialize);
    } else {
        initialize();
    }
})();
```

3. Ẩn popup bằng CSS

```Css
/* Không hiển thị popup Rejoining the server */
#components-reconnect-modal,
#components-reconnect-modal.components-reconnect-show,
#components-reconnect-modal.components-reconnect-retrying,
#components-reconnect-modal.components-reconnect-failed,
#components-reconnect-modal.components-reconnect-rejected,
#components-reconnect-modal.components-reconnect-paused,
#components-reconnect-modal.components-reconnect-hide {
    display: none !important;
    visibility: hidden !important;
    opacity: 0 !important;
    pointer-events: none !important;
}
```

4. Sửa Program.cs

```C#
builder.Services
    .AddServerSideBlazor(options =>
    {
        // Giữ trạng thái circuit lâu hơn khi người dùng chuyển tab.
        options.DisconnectedCircuitRetentionPeriod =
            TimeSpan.FromMinutes(15);

        // Số circuit bị ngắt tối đa được giữ lại.
        options.DisconnectedCircuitMaxRetained = 200;

        options.JSInteropDefaultCallTimeout =
            TimeSpan.FromMinutes(2);
    })
    .AddHubOptions(options =>
    {
        // Server đợi client lâu hơn trước khi xác định đã mất kết nối.
        options.ClientTimeoutInterval =
            TimeSpan.FromSeconds(60);

        options.HandshakeTimeout =
            TimeSpan.FromSeconds(30);

        // Phải khớp với client: 15 giây.
        options.KeepAliveInterval =
            TimeSpan.FromSeconds(15);
    });
```
## Delete temporary data when a browser session closes

This build isolates download jobs by browser tab. When the user closes or leaves the downloader page, the browser sends a best-effort close signal to the server. The server cancels active `yt-dlp` work immediately and schedules the session directory for deletion.

```json
{
  "Downloader": {
    "DeleteOnSessionClose": true,
    "SessionCloseGraceSeconds": 60,
    "CleanupIntervalSeconds": 30,
    "RetentionHours": 6
  }
}
```

The short grace period allows an already-started file response to finish. `RetentionHours` remains a safety net for abrupt browser crashes, network failures and orphaned directories after an application restart.


## Download error handling

Download failures are intentionally sanitized in the page UI. Users see only:

`Unable to download this video. Please try another video or try again later.`

The original downloader/yt-dlp error is kept server-side on the active job and forwarded once to the browser DevTools console through `videoDownloader.logDownloadError`. It is never rendered into the Download activity HTML. Server-side exceptions continue to be written through ASP.NET Core `ILogger`.


## SEO trending upgrade (2026-08-20)
- Keyword-focused but natural home title/H1 for online video downloader intent.
- Unique platform titles/descriptions for YouTube, Facebook, TikTok, Instagram, X/Twitter, Reddit and Threads.
- Added long-tail search-intent links (Shorts, Reels, MP4, MP3) without meta-keyword stuffing.
- Added Organization, ItemList and BreadcrumbList JSON-LD to improve entity/page relationships.
- Preserved canonical, robots, Open Graph, Twitter cards, sitemap and indexable server-prerendered content.
- SEO copy avoids claiming guaranteed rankings; validate performance in Google Search Console and use query groups/trending-up data to refine content over time.


## Available-format detection

The home page no longer exposes fixed 360p/480p/720p/1080p selectors. After a supported URL is pasted, `YtDlpService.AnalyzeAsync` reads the concrete formats reported by yt-dlp and the UI renders only those options. The analyzer groups the resolutions actually reported by yt-dlp, while downloads re-select a live stream by resolution instead of depending on a transient platform `format_id`. This avoids requesting a resolution that the source never exposed and reduces failures when IDs change between analysis and download. MP3 is rendered only when an audio stream is reported. Technical analyzer/downloader errors stay out of the HTML UI and are logged through the existing browser-console/server logging path.

## YouTube format stability fix (2026-08-20)

The YouTube path now follows the same extraction strategy that succeeds on the Linux server:

- no forced `web_safari` player client;
- no HLS-only format filtering;
- EJS remote components enabled;
- Deno selected as the JavaScript runtime;
- the analyzer still renders only resolutions actually reported by yt-dlp;
- downloads use a fresh resolution-based selector instead of relying only on transient format IDs such as `137+251`;
- HTTP 403 or `Requested format is not available` triggers one fresh retry at the same requested resolution.

Recommended production settings:

```json
{
  "Downloader": {
    "YtDlpPath": "/usr/local/bin/yt-dlp",
    "DownloadRoot": "App_Data/downloads",
    "YouTubeCompatibilityMode": true,
    "YouTubePlayerClient": "",
    "YouTubeSafeFormatsOnly": false,
    "YouTubeEnableRemoteEjs": true,
    "YouTubeJavaScriptRuntime": "deno"
  }
}
```

Ensure the service user can write the download directory:

```bash
sudo mkdir -p /www/wwwroot/VideoDownloader.Blazor/App_Data/downloads
sudo chown -R www-data:www-data /www/wwwroot/VideoDownloader.Blazor/App_Data
sudo chmod -R 775 /www/wwwroot/VideoDownloader.Blazor/App_Data
```

## Production reliability update (2026-08-20)

This build hardens the shared downloader pipeline for Linux production:

- `appsettings.Production.json` points to `/usr/local/bin/yt-dlp`.
- The default `appsettings.json` uses `yt-dlp` so local PATH resolution works cross-platform.
- yt-dlp is auto-discovered from `/usr/local/bin/yt-dlp`, `/usr/bin/yt-dlp`, PATH, or the local `Tools` directory.
- Analyze runs with `--format all --skip-download` so it can inspect the full format inventory instead of depending on the default selected format.
- Download selectors are resolution-based and can re-select a live stream if transient platform format IDs change between analysis and download.
- yt-dlp/Deno cache is redirected to `App_Data/cache`, with automatic `--no-cache-dir` fallback if the cache directory is not writable.
- `deploy/prepare-runtime-directories.sh` creates and validates writable download/cache directories for `www-data`.

### Linux deploy checks

```bash
cd /www/wwwroot/VideoDownloader.Blazor
sudo bash deploy/prepare-runtime-directories.sh
sudo cp deploy/videodownloader.service /etc/systemd/system/videodownloader.service
sudo systemctl daemon-reload
sudo systemctl restart videodownloader
sudo systemctl status videodownloader --no-pager -l
```

Verify the exact service user can analyze YouTube:

```bash
sudo -u www-data /usr/local/bin/yt-dlp \
  --js-runtimes deno \
  --remote-components ejs:github \
  --skip-download --dump-single-json --format all \
  "https://www.youtube.com/watch?v=xKwKzBP5w6Q" >/tmp/clip2down-test.json
```


## Social platform resilience update (2026-08-21)

This build consolidates Facebook, Instagram and Threads handling so one platform's authentication/rate-limit behavior does not break the shared download pipeline.

- **Facebook Story / Reel:** Story share tokens are decoded to the underlying media id and numeric `/reel/<id>` URLs are normalized to yt-dlp's `facebook:<id>` extractor input. This prevents GenericIE from following Story URLs into `facebook.com/login.php`. For Story/Reel/share URLs, `cookies/facebook.txt` is preferred on the first request when the file exists; other Facebook URLs can still fall back to the cookie once after an anonymous login-gate failure.
- **Instagram 429:** if `cookies/instagram.txt` exists, authenticated analysis is used first instead of making an anonymous probe first. Successful analysis is cached (default 15 minutes), only one Instagram analysis runs concurrently per app instance, and a 429 starts a configurable cooldown (default 10 minutes). `InstagramProxy` can be configured when the deployment has an approved alternate egress/proxy. A 429 from Instagram is an upstream IP/session block; the application can reduce duplicate requests but cannot manufacture access when Instagram rejects the server IP.
- **Threads:** Threads still uses the custom resolver because yt-dlp has no native Threads extractor. The resolver reads current `data-sjs` JSON, matches `thread_items[*].post.code`, extracts `video_versions`/compatible direct media fields, and only accepts real Meta CDN URLs (`fbcdn.net` / `cdninstagram.com`). It can use `cookies/threads.txt`, caches a successful direct URL between Analyze and Download (default 10 minutes), and enters a short cooldown after 403/429. Threads `/media` HTML endpoints are never passed to yt-dlp as direct video URLs.
- Raw extractor errors remain in browser DevTools/server logs only; the page continues to show the generic user-facing error message.

Recommended production settings are already present in `appsettings.Production.json`:

```json
{
  "Downloader": {
    "InstagramCookiesFile": "cookies/instagram.txt",
    "FacebookCookiesFile": "cookies/facebook.txt",
    "ThreadsCookiesFile": "cookies/threads.txt",
    "InstagramPreferCookies": true,
    "FacebookPreferCookies": true,
    "AnalysisCacheMinutes": 15,
    "InstagramRateLimitCooldownSeconds": 600,
    "ThreadsResolveCacheMinutes": 10,
    "ThreadsRateLimitCooldownSeconds": 300,
    "InstagramProxy": "",
    "FacebookProxy": ""
  }
}
```

Cookie files are optional. When used, keep them outside source control and restrict access:

```bash
sudo chown www-data:www-data /www/wwwroot/VideoDownloader.Blazor/cookies/*.txt
sudo chmod 600 /www/wwwroot/VideoDownloader.Blazor/cookies/*.txt
```

After copying the new build to Linux, run:

```bash
cd /www/wwwroot/VideoDownloader.Blazor
sudo bash deploy/prepare-runtime-directories.sh
sudo systemctl restart videodownloader
sudo systemctl status videodownloader --no-pager -l
```
