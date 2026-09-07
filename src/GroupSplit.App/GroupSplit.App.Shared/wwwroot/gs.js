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

    // A figure marked `gs-figure` rises from zero to the figure it is showing.
    //
    // The number it counts to is read from the element's own text, and that text
    // is the whole of its input: there is no attribute alongside it that could
    // disagree with what was rendered. The components keep it that way -- each
    // keys its element on the figure, so a figure that *changes* arrives as a new
    // element rather than as new text in the old one. A node's text therefore
    // never changes for as long as the node lives, and reading it once, at the
    // start, is reading the figure of record.
    //
    // That invariant is the fix for #193 (and #174 before it). This used to take
    // its target from a `data-gs-count` attribute, which made one figure two
    // edits to one element -- and the two did not have to arrive together. When
    // the attribute came first, the run read the *previous* figure as the text to
    // land on and pasted it back up to 900ms later, over the figure the app had
    // since rendered: above a filtered list, the all-time total returning to the
    // card and staying there. There is no second edit to get out of order now,
    // and a run that finds itself on a detached node simply stops, because the
    // figure it was counting belongs to an element that no longer exists.
    const counted = new WeakSet();

    const duration = 800;

    function countUp(el) {
        // One run per element, for the lifetime of the element. Re-scanning one --
        // an ancestor re-inserted, a row moved -- must not restart the count.
        if (counted.has(el)) return;
        counted.add(el);

        // The text as rendered is the figure of record: what the count lands on,
        // and what it is counting towards.
        const final = el.textContent;
        const match = final.match(/\d[\d,.]*/);
        if (match === null || reduced.matches) return;

        const digits = match[0];
        const target = parseFloat(digits.replace(/,/g, ""));

        // Nothing to count: zero is already on screen, and a figure that does not
        // parse is not a number this should be touching.
        if (!isFinite(target) || target === 0) return;

        // Everything around the digits is kept and put back on every frame, so a
        // count runs inside "$1,234.56" or "($5.00)" without losing the currency,
        // the sign or the brackets a negative is rendered in.
        const head = final.slice(0, match.index);
        const tail = final.slice(match.index + digits.length);
        const fraction = digits.match(/\.(\d+)$/);
        const decimals = fraction ? Math.min(fraction[1].length, 2) : 0;

        // Grouped if the final figure is. Below a thousand there is no separator
        // to place, and asking for one would render "1,0" on the way to "999".
        const grouped = digits.includes(",");

        // en-US to match MoneyExtensions, which pins the culture for the same
        // reason: the browser's own locale would flip the separators on hydration.
        const shown = value => head + value.toLocaleString("en-US", {
            minimumFractionDigits: decimals,
            maximumFractionDigits: decimals,
            useGrouping: grouped
        }) + tail;

        const start = performance.now();

        function frame(now) {
            // Detached: the figure changed, so Blazor replaced this element with
            // one holding the new figure, and that element is counting itself.
            // There is nothing to put back on a node that is no longer anywhere.
            if (!el.isConnected) return;

            const t = Math.min(1, (now - start) / duration);

            if (t >= 1) {
                el.textContent = final;
                return;
            }

            el.textContent = shown(target * (1 - Math.pow(1 - t, 4)));

            requestAnimationFrame(frame);
        }

        requestAnimationFrame(frame);

        // requestAnimationFrame is not a promise that the last frame arrives: a
        // backgrounded tab stops it outright and a phone throttles it, both of
        // which would strand the count part-way. The figure is the point and the
        // count is decoration, so the figure lands whether or not the frames do.
        setTimeout(function () {
            if (el.isConnected) el.textContent = final;
        }, duration + 100);
    }

    function scan(node) {
        if (!(node instanceof Element)) return;
        if (node.classList.contains("gs-figure")) countUp(node);
        for (const el of node.querySelectorAll(".gs-figure")) countUp(el);
    }

    // Insertions only. A figure never changes in place -- see the note above --
    // so there is no attribute or text mutation left worth watching for.
    new MutationObserver(function (records) {
        for (const record of records)
            for (const node of record.addedNodes) scan(node);
    }).observe(root, { childList: true, subtree: true });

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

        // Puts text on the clipboard, and says whether it got there. The async
        // API is unavailable outside a secure context and can be refused by
        // permission, so the old selection-and-execCommand route stays as a
        // fallback -- a join link nobody can copy is a join link nobody shares.
        // Answering false rather than throwing lets the caller show the text to
        // be copied by hand instead of an error about the clipboard.
        async copy(text) {
            try {
                if (navigator.clipboard && window.isSecureContext) {
                    await navigator.clipboard.writeText(text);
                    return true;
                }
            } catch {
                // Fall through: refused, or no permission.
            }

            try {
                const el = document.createElement("textarea");
                el.value = text;
                el.setAttribute("readonly", "");
                el.style.position = "fixed";
                el.style.opacity = "0";
                document.body.appendChild(el);
                el.select();

                const copied = document.execCommand("copy");
                document.body.removeChild(el);

                return copied;
            } catch {
                return false;
            }
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
