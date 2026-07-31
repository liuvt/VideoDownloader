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