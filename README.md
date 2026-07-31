# Clip2Down — Blazor Server Video Downloader

A .NET 9 Blazor Server application that downloads accessible YouTube, Facebook, TikTok, Instagram and X (Twitter) media through `yt-dlp` and uses FFmpeg for merging and audio conversion.

> Download only media you own, media you are authorized to save, or content distributed under a compatible license. The application does not bypass DRM, paywalls or platform access controls.

## What changed in this US/Los Angeles edition

- Entire public interface rewritten in US English.
- Distinct developer-tool visual direction inspired by GitHub Primer rather than a generic SaaS landing page.
- Local system font stack matching GitHub Primer: Mona Sans when available, followed by Apple/Windows system fonts.
- Monospace utility labels and terminal-style download preview.
- Responsive layout for desktop and mobile.
- SEO content for common US search intent: YouTube, Facebook, TikTok, Instagram and X video downloader search intent, plus YouTube to MP3, Shorts and Reels.
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

## Production SEO configuration

Set the real HTTPS origin before deployment. Do not leave `BaseUrl` empty behind a reverse proxy unless forwarded headers are configured correctly.

```json
{
  "Site": {
    "Name": "Clip2Down",
    "BaseUrl": "https://your-domain.com",
    "Description": "Download public YouTube, Facebook, TikTok, Instagram and X videos as MP4 or MP3 with a fast online downloader for US users.",
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
- Review each supported platform, copyright and local legal requirements before opening the service publicly.


## Release

```
dotnet publish .\VideoDownloader.Blazor.csproj -c Release -o .\publish
```

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

## Supported platform hosts

The URL allowlist accepts YouTube, Facebook, TikTok, Instagram and X/Twitter domains. Actual extraction depends on the installed yt-dlp version and whether the supplied media is publicly accessible.

SEO landing routes included in `sitemap.xml`:

- `/youtube-video-downloader`
- `/facebook-video-downloader`
- `/tiktok-video-downloader`
- `/instagram-video-downloader`
- `/twitter-video-downloader`
