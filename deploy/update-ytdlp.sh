#!/usr/bin/env bash
set -euo pipefail

YTDLP_PATH="${YTDLP_PATH:-/usr/local/bin/yt-dlp}"

# Use the Linux standalone nightly build. Unlike the generic zipapp, this build
# bundles curl_cffi so TikTok browser impersonation targets are actually usable.
echo "Installing/updating yt-dlp Linux nightly at ${YTDLP_PATH}..."
sudo curl -L --fail --silent --show-error \
  https://github.com/yt-dlp/yt-dlp-nightly-builds/releases/latest/download/yt-dlp_linux \
  -o "${YTDLP_PATH}"
sudo chmod 755 "${YTDLP_PATH}"

"${YTDLP_PATH}" --version
"${YTDLP_PATH}" --list-impersonate-targets | sed -n '1,20p'

if command -v deno >/dev/null 2>&1; then
  deno --version
elif command -v node >/dev/null 2>&1; then
  node --version
  echo "Clip2Down will automatically pass --js-runtimes node to yt-dlp when configured."
else
  echo "WARNING: install Deno 2.3+ or Node 22+ for current YouTube extraction." >&2
fi
