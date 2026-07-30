window.videoDownloader = {
    readClipboard: async function () {
        if (!navigator.clipboard || !navigator.clipboard.readText) {
            throw new Error("Clipboard API is unavailable.");
        }

        return (await navigator.clipboard.readText()).trim();
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
