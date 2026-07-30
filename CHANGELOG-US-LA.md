# US/Los Angeles SEO & UI Revision

## Interface

- Rebranded the public experience as Clip2Down.
- Replaced the generic blue SaaS layout with a GitHub-inspired developer utility design.
- Added GitHub Primer-compatible system and monospace font stacks without shipping font files.
- Added a terminal-style hero, command-like URL input, editorial search-intent board and distinctive dark workflow section.
- Rebuilt mobile breakpoints and all public text in US English.

## SEO

- Updated title, description, canonical, Open Graph and Twitter metadata.
- Switched language and locale signals to `en-US` / `en_US`.
- Added visible content for primary US search intents without hidden keyword blocks.
- Added WebSite, WebApplication and FAQPage JSON-LD.
- Added United States area served without false physical LocalBusiness information.
- Updated sitemap, robots and `X-Robots-Tag` handling for temporary download files.
- Replaced social sharing image and app icons.

## Runtime

- Default Windows executable path is now `Tools\\yt-dlp.exe`.
- Relative yt-dlp paths are resolved against the application content root.
- User-facing validation, progress status and runtime errors are now English.

## Auto-scroll to active download

- After a valid download request is queued, the page now scrolls smoothly to the exact job in **Active session / Download activity**.
- The newly created job receives keyboard focus and a short highlight animation.
- Reduced-motion browser preferences are respected.
