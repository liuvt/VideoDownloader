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
