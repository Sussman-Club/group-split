// Group Split shell helpers. Deliberately small: the design system is
// CSS-first, and this only covers what CSS cannot do on its own.
(function () {
    const storageKey = "groupsplit.theme";
    const root = document.documentElement;
    const reduced = window.matchMedia("(prefers-reduced-motion: reduce)");

    // ------------------------------------------------------------ theme --

    // Stamp the stored theme before Blazor boots so a dark-mode user never
    // sees a cream flash while the circuit or the runtime comes up.
    function resolve(mode) {
        if (mode === "Dark") return true;
        if (mode === "Light") return false;
        return window.matchMedia("(prefers-color-scheme: dark)").matches;
    }

    let stored = null;
    try {
        stored = localStorage.getItem(storageKey);
    } catch {
        // Storage can be blocked; the system preference is a fine answer.
    }

    root.dataset.theme = resolve(stored) ? "dark" : "light";

    // --------------------------------------------------------- counters --

    // An element with `data-gs-count` already holds its final, formatted text.
    // The attribute is the animation's target and its identity: the digits
    // inside the text are counted up to it, and if the attribute changes while
    // a run is in flight (a different group was picked) that run stops rather
    // than pasting a stale figure over fresh data.
    const running = new WeakMap();

    function countUp(el) {
        const token = el.getAttribute("data-gs-count");
        if (token === null || running.get(el) === token) return;
        running.set(el, token);

        const target = Math.abs(parseFloat(token));
        if (!isFinite(target) || target === 0 || reduced.matches) return;

        const final = el.textContent;
        const match = final.match(/\d[\d.,]*/);
        if (!match) return;

        const head = final.slice(0, match.index);
        const tail = final.slice(match.index + match[0].length);
        const fraction = match[0].match(/[.,](\d+)$/);
        const decimals = fraction && fraction[1].length <= 2 ? fraction[1].length : 0;
        const grouped = /[.,]\d{3}/.test(match[0]);
        const start = performance.now();
        const duration = 800;

        function frame(now) {
            if (el.getAttribute("data-gs-count") !== token || !el.isConnected) return;

            const t = Math.min(1, (now - start) / duration);
            if (t >= 1) {
                el.textContent = final;
                return;
            }

            const eased = 1 - Math.pow(1 - t, 4);
            el.textContent = head + (target * eased).toLocaleString("en-US", {
                minimumFractionDigits: decimals,
                maximumFractionDigits: decimals,
                useGrouping: grouped
            }) + tail;

            requestAnimationFrame(frame);
        }

        requestAnimationFrame(frame);
    }

    function scan(node) {
        if (!(node instanceof Element)) return;
        if (node.hasAttribute("data-gs-count")) countUp(node);
        for (const el of node.querySelectorAll("[data-gs-count]")) countUp(el);
    }

    new MutationObserver(function (records) {
        for (const record of records) {
            if (record.type === "attributes") {
                countUp(record.target);
            } else {
                for (const node of record.addedNodes) scan(node);
            }
        }
    }).observe(root, {
        childList: true,
        subtree: true,
        attributes: true,
        attributeFilter: ["data-gs-count"]
    });

    if (document.body) {
        scan(document.body);
    } else {
        document.addEventListener("DOMContentLoaded", () => scan(document.body));
    }

    window.gs = {
        setTheme(isDark) {
            root.dataset.theme = isDark ? "dark" : "light";
        }
    };
})();
