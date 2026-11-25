(function () {
    "use strict";

    const BOOT_KEY = "ge_portal_boot_v1";
    const loaderEl = document.getElementById("portalLoader");
    const radarEl = document.getElementById("portalLoaderRadar");
    const tilesEl = document.getElementById("portalLoaderTiles");
    const navLoadEl = document.getElementById("portalNavLoad");

    const LOADER_MSGS = [
        "Connecting to biometric devices…",
        "Syncing attendance data…",
        "Almost ready…",
    ];

    let welcomeRunning = false;
    let navLoaderDisabled = false;
    let loaderTimer = null;
    let loaderProgress = 0;

    function unlockScroll() {
        document.documentElement.classList.remove("portal-no-scroll");
    }

    function hideOverlay(el) {
        if (!el) return;
        el.hidden = true;
        el.classList.remove("is-leaving", "is-active");
        el.setAttribute("aria-hidden", "true");
    }

    function fadeOut(el, ms, done) {
        if (!el) return done?.();
        el.classList.add("is-leaving");
        setTimeout(() => {
            hideOverlay(el);
            done?.();
        }, ms || 500);
    }

    function stopLoaderAnimation() {
        if (loaderTimer) {
            clearInterval(loaderTimer);
            loaderTimer = null;
        }
        loaderProgress = 0;
    }

    function pickLoaderVariant() {
        return Math.random() < 0.5 ? "radar" : "tiles";
    }

    function applyLoaderProgress(p) {
        const m = LOADER_MSGS[p < 35 ? 0 : p < 75 ? 1 : 2];
        if (!loaderEl) return;
        loaderEl.querySelectorAll(".portal-loader-pm").forEach((e) => {
            e.textContent = m;
        });
        loaderEl.querySelectorAll(".portal-loader-pt").forEach((e) => {
            e.textContent = `${Math.floor(p)}%`;
        });
        loaderEl.querySelectorAll(".portal-loader-nd").forEach((e, i) => {
            e.classList.toggle("done", p >= (i + 1) * 30);
        });
        loaderEl.querySelectorAll(".portal-loader-tile").forEach((e, i) => {
            const fill = e.querySelector("i");
            const f = Math.max(0, Math.min(100, ((p - i * 22) / 40) * 100));
            if (fill) fill.style.height = `${f}%`;
            e.classList.toggle("on", f > 55);
        });
    }

    function showLoader(onDone) {
        if (!loaderEl) {
            onDone?.();
            return;
        }

        stopLoaderAnimation();
        const variant = pickLoaderVariant();
        loaderEl.dataset.variant = variant;
        loaderEl.hidden = false;
        loaderEl.setAttribute("aria-hidden", "false");
        loaderEl.classList.remove("is-leaving");

        if (radarEl) radarEl.hidden = variant !== "radar";
        if (tilesEl) tilesEl.hidden = variant !== "tiles";

        document.documentElement.classList.add("portal-no-scroll");
        loaderProgress = 0;
        applyLoaderProgress(0);

        let hold = false;
        loaderTimer = setInterval(() => {
            if (hold) return;
            loaderProgress += loaderProgress < 90 ? 1.4 : 0.4;
            if (loaderProgress >= 100) {
                loaderProgress = 100;
                hold = true;
                applyLoaderProgress(100);
                stopLoaderAnimation();
                setTimeout(() => {
                    fadeOut(loaderEl, 450, () => {
                        unlockScroll();
                        onDone?.();
                    });
                }, 700);
                return;
            }
            applyLoaderProgress(loaderProgress);
        }, 60);
    }

    function forceDismissAll() {
        stopLoaderAnimation();
        hideOverlay(loaderEl);
        hideOverlay(navLoadEl);
        welcomeRunning = false;
        navLoaderDisabled = false;
        unlockScroll();
    }

    function runBoot(onDone) {
        if (!loaderEl || localStorage.getItem(BOOT_KEY)) {
            onDone?.();
            return;
        }
        showLoader(() => {
            localStorage.setItem(BOOT_KEY, "1");
            onDone?.();
        });
    }

    function runWelcome() {
        if (!loaderEl || document.body.dataset.welcome !== "true") {
            return;
        }
        welcomeRunning = true;
        navLoaderDisabled = true;
        showLoader(() => {
            welcomeRunning = false;
            navLoaderDisabled = false;
        });
    }

    function showNavLoader() {
        if (!navLoadEl || welcomeRunning || navLoaderDisabled) return;
        navLoadEl.hidden = false;
        navLoadEl.classList.add("is-active");
    }

    function hideNavLoader() {
        if (!navLoadEl) return;
        navLoadEl.classList.remove("is-active");
        setTimeout(() => hideOverlay(navLoadEl), 350);
    }

    function wireNav() {
        document.querySelectorAll(".portal-nav-link[href]").forEach((a) => {
            a.addEventListener("click", (e) => {
                if (welcomeRunning || navLoaderDisabled) return;
                const href = a.getAttribute("href");
                if (!href || href.startsWith("#") || a.classList.contains("active")) return;
                if (e.metaKey || e.ctrlKey || e.shiftKey || e.altKey) return;
                showNavLoader();
            });
        });
        window.addEventListener("pageshow", (ev) => {
            if (ev.persisted) hideNavLoader();
        });
        hideNavLoader();
    }

    function init() {
        hideOverlay(loaderEl);
        hideOverlay(navLoadEl);

        runBoot(() => runWelcome());
        wireNav();

        setTimeout(forceDismissAll, 12000);
    }

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", init);
    } else {
        init();
    }
})();