#!/usr/bin/env bash
set -euo pipefail

APP_ROOT="${1:-/www/wwwroot/VideoDownloader.Blazor}"
APP_USER="${APP_USER:-www-data}"
APP_GROUP="${APP_GROUP:-www-data}"

mkdir -p \
  "${APP_ROOT}/App_Data/downloads" \
  "${APP_ROOT}/App_Data/cache/deno" \
  "${APP_ROOT}/cookies"

chown -R "${APP_USER}:${APP_GROUP}" "${APP_ROOT}/App_Data"
chmod -R 775 "${APP_ROOT}/App_Data"
chown "${APP_USER}:${APP_GROUP}" "${APP_ROOT}/cookies"
chmod 750 "${APP_ROOT}/cookies"

for cookie_file in "${APP_ROOT}/cookies/instagram.txt" "${APP_ROOT}/cookies/facebook.txt" "${APP_ROOT}/cookies/threads.txt"; do
  if [ -f "${cookie_file}" ]; then
    chown "${APP_USER}:${APP_GROUP}" "${cookie_file}"
    chmod 600 "${cookie_file}"
  fi
done

# Verify that the same account used by systemd can actually create files.
sudo -u "${APP_USER}" touch "${APP_ROOT}/App_Data/downloads/.write-test"
rm -f "${APP_ROOT}/App_Data/downloads/.write-test"

if [ -x /usr/local/bin/yt-dlp ]; then
  YTDLP=/usr/local/bin/yt-dlp
elif [ -x /usr/bin/yt-dlp ]; then
  YTDLP=/usr/bin/yt-dlp
elif command -v yt-dlp >/dev/null 2>&1; then
  YTDLP="$(command -v yt-dlp)"
else
  echo "ERROR: yt-dlp was not found." >&2
  exit 1
fi

echo "yt-dlp: ${YTDLP}"
sudo -u "${APP_USER}" "${YTDLP}" --version

if command -v deno >/dev/null 2>&1; then
  echo "Deno: $(deno --version | head -n1)"
elif command -v node >/dev/null 2>&1; then
  echo "Node: $(node --version)"
else
  echo "WARNING: Deno/Node was not found. YouTube extraction may fail." >&2
fi

echo "Runtime directories are ready for ${APP_USER}."
