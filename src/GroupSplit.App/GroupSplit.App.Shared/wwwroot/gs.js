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

    // -------------------------------------------------- before the runtime --

    // The page is server-rendered and interactive only once the WebAssembly
    // runtime has downloaded. The two pieces of chrome that need no data,
    // the theme switcher and the sidebar collapse, work from here in the
    // meantime; Blazor picks up whatever state they left when it takes over.
    const railKey = "groupsplit.rail";

    function read(key) {
        try { return localStorage.getItem(key); } catch { return null; }
    }

    function write(key, value) {
        try { localStorage.setItem(key, value); } catch { /* blocked storage */ }
    }

    document.addEventListener("click", function (event) {
        if (root.hasAttribute("data-gs-ready")) return;

        const seg = event.target.closest(".gs-seg-btn[data-mode]");
        if (seg) {
            const mode = seg.dataset.mode;
            write(storageKey, mode);
            root.dataset.theme = resolve(mode) ? "dark" : "light";
            const group = seg.closest(".gs-seg");
            const buttons = [...group.querySelectorAll(".gs-seg-btn")];
            buttons.forEach(b => b.classList.toggle("active", b === seg));
            group.style.setProperty("--gs-seg-i", String(buttons.indexOf(seg)));
            return;
        }

        if (event.target.closest(".gs-sidebar-toggle")) {
            const shell = document.querySelector(".gs-shell");
            if (!shell) return;
            const rail = shell.classList.toggle("is-rail");
            write(railKey, rail ? "1" : "0");
        }
    });

    function applyRail() {
        const shell = document.querySelector(".gs-shell");
        if (shell && read(railKey) === "1") shell.classList.add("is-rail");
    }

    if (document.body) applyRail(); else document.addEventListener("DOMContentLoaded", applyRail);

    window.gs = {
        setTheme(isDark) {
            root.dataset.theme = isDark ? "dark" : "light";
        },

        // Called once Blazor has rendered interactively: hands the chrome over
        // and hides the boot indicator.
        ready() {
            root.setAttribute("data-gs-ready", "");
        },

        rail() {
            return read(railKey) === "1";
        },

        setRail(value) {
            write(railKey, value ? "1" : "0");
        },

        // The browser's offset from UTC, in minutes, with the sign .NET uses: +60 for
        // Lisbon in summer, -240 for New York. JavaScript's own getTimezoneOffset() is the
        // other way round (minutes *behind* UTC), hence the negation. This is the one
        // source of "where is the person" for every date the app shows or sends.
        tzOffsetMinutes() {
            return -new Date().getTimezoneOffset();
        },

        // ------------------------------------------------------- plaid --

        // Plaid Link runs in the browser because it has to: the person's bank
        // credentials are typed into Plaid's own frame and never reach this
        // origin. What comes back is a one-time public token, useless without
        // the server's credentials, which the server exchanges for a lasting one.
        plaid: {
            // Fetched the first time somebody links a bank rather than on every
            // page load, so a person who never links one never pays for it.
            script: null,

            load() {
                if (this.script) return this.script;

                this.script = new Promise((resolve, reject) => {
                    if (window.Plaid) return resolve();

                    const el = document.createElement("script");
                    el.src = "https://cdn.plaid.com/link/v2/stable/link-initialize.js";
                    el.onload = () => resolve();
                    el.onerror = () => reject(new Error("Plaid Link could not be loaded."));
                    document.head.appendChild(el);
                });

                return this.script;
            },

            async open(token, callback) {
                await this.load();

                // onSuccess and onExit are exclusive and one of them always runs,
                // so the .NET side is always answered and never waits forever.
                const handler = window.Plaid.create({
                    token: token,
                    onSuccess(publicToken) {
                        callback.invokeMethodAsync("OnSuccess", publicToken);
                    },
                    onExit(error) {
                        callback.invokeMethodAsync("OnExit", error ? error.error_code : null);
                    }
                });

                handler.open();
            }
        }
    };
})();
