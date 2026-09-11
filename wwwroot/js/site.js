window.videoDownloader = {
    logDownloadError: function (title, detail) {
        // Technical downloader details intentionally go to DevTools only.
        // They are never inserted into the DOM or shown in Download activity.
        console.groupCollapsed(`[Clip2Down] ${title || "Download error"}`);
        console.error(detail || "Unknown download error");
        console.groupEnd();
    },
    _sessionId: null,
    _sessionHandlerRegistered: false,
    _sessionCloseSent: false,

    readClipboard: async function () {
        if (!navigator.clipboard || !navigator.clipboard.readText) {
            throw new Error("Clipboard API is unavailable.");
        }

        return (await navigator.clipboard.readText()).trim();
    },

    registerSessionClose: function (sessionId) {
        this._sessionId = sessionId;
        this._sessionCloseSent = false;

        if (this._sessionHandlerRegistered) {
            return;
        }

        this._sessionHandlerRegistered = true;
        const api = this;

        window.addEventListener("pagehide", function (event) {
            // A page placed into the back-forward cache can be restored without
            // a reload. Keep its downloads alive in that case.
            if (event.persisted || api._sessionCloseSent || !api._sessionId) {
                return;
            }

            api._sessionCloseSent = true;
            const endpoint = `/api/sessions/${encodeURIComponent(api._sessionId)}/close`;
            const payload = new Blob([""], { type: "text/plain;charset=UTF-8" });

            if (navigator.sendBeacon && navigator.sendBeacon(endpoint, payload)) {
                return;
            }

            // keepalive is a best-effort fallback for browsers where sendBeacon
            // is unavailable or rejected.
            fetch(endpoint, {
                method: "POST",
                body: payload,
                credentials: "same-origin",
                keepalive: true,
                cache: "no-store"
            }).catch(function () {
                // Circuit disposal and the retention cleanup remain as fallback.
            });
        });
    },

    scrollToDownloadActivity: function (jobElementId) {
        const target = document.getElementById(jobElementId)
            || document.getElementById("download-activity");

        if (!target) {
            return;
        }

        const reduceMotion = window.matchMedia
            && window.matchMedia("(prefers-reduced-motion: reduce)").matches;

        window.requestAnimationFrame(function () {
            try {
                target.focus({ preventScroll: true });
            } catch {
                target.focus();
            }

            target.scrollIntoView({
                behavior: reduceMotion ? "auto" : "smooth",
                block: "start",
                inline: "nearest"
            });

            target.classList.remove("scroll-target-flash");
            void target.offsetWidth;
            target.classList.add("scroll-target-flash");

            window.setTimeout(function () {
                target.classList.remove("scroll-target-flash");
            }, 1800);
        });
    }
};

window.audioTimeline = {
    _previewUrls: [],

    revokeAllPreviews: function () {
        for (const url of this._previewUrls) {
            try { URL.revokeObjectURL(url); } catch { }
        }
        this._previewUrls = [];
    },

    loadSelectedFiles: async function (inputId) {
        const input = document.getElementById(inputId);
        if (!input || !input.files) {
            return [];
        }

        this.revokeAllPreviews();
        const files = Array.from(input.files);
        const api = this;

        const readMetadata = function (file) {
            return new Promise(function (resolve) {
                const url = URL.createObjectURL(file);
                api._previewUrls.push(url);
                const audio = document.createElement("audio");
                audio.preload = "metadata";

                let finished = false;
                const finish = function (duration) {
                    if (finished) return;
                    finished = true;
                    audio.onloadedmetadata = null;
                    audio.onerror = null;
                    audio.removeAttribute("src");
                    audio.load();
                    resolve({
                        url: url,
                        duration: Number.isFinite(duration) ? duration : 0
                    });
                };

                const timeout = window.setTimeout(function () {
                    finish(0);
                }, 8000);

                audio.onloadedmetadata = function () {
                    window.clearTimeout(timeout);
                    finish(audio.duration);
                };
                audio.onerror = function () {
                    window.clearTimeout(timeout);
                    finish(0);
                };
                audio.src = url;
            });
        };

        return await Promise.all(files.map(readMetadata));
    },

    getCurrentTime: function (audioId) {
        const audio = document.getElementById(audioId);
        return audio && Number.isFinite(audio.currentTime) ? audio.currentTime : 0;
    },

    playRange: async function (audioId, start, end) {
        const audio = document.getElementById(audioId);
        if (!audio) return;

        if (audio._clip2downRangeHandler) {
            audio.removeEventListener("timeupdate", audio._clip2downRangeHandler);
            audio._clip2downRangeHandler = null;
        }

        const safeStart = Math.max(0, Number(start) || 0);
        const safeEnd = Math.max(safeStart, Number(end) || safeStart);
        audio.pause();
        audio.currentTime = safeStart;

        const handler = function () {
            if (audio.currentTime >= safeEnd || audio.ended) {
                audio.pause();
                audio.removeEventListener("timeupdate", handler);
                audio._clip2downRangeHandler = null;
            }
        };
        audio._clip2downRangeHandler = handler;
        audio.addEventListener("timeupdate", handler);

        try {
            await audio.play();
        } catch {
            // Browser autoplay rules may require the user to press play directly.
        }
    }
};
