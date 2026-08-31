#!/usr/bin/env bash
set -euo pipefail

YTDLP_PATH="${YTDLP_PATH:-/usr/local/bin/yt-dlp}"

echo "Updating yt-dlp at ${YTDLP_PATH}..."
sudo curl -L --fail --silent --show-error \
  https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp \
  -o "${YTDLP_PATH}"
sudo chmod a+rx "${YTDLP_PATH}"
"${YTDLP_PATH}" --version

if command -v deno >/dev/null 2>&1; then
  deno --version
elif command -v node >/dev/null 2>&1; then
  node --version
  echo "Clip2Down will automatically pass --js-runtimes node to yt-dlp."
else
  echo "WARNING: install Deno 2.3+ or Node 22+ for current YouTube extraction." >&2
fi
