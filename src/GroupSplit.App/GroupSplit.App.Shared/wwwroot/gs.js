// Group Split shell helpers. Deliberately small: the design system is
// CSS-first, and this only covers what CSS cannot do on its own.
(function () {
    const storageKey = "groupsplit.theme";
    const root = document.documentElement;

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
