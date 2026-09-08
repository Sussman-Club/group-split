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
    //
    // What is remembered per element is that identity, the final text it is
    // counting towards, and the last text this run itself wrote, because a count
    // writes part-way figures into the element and only the last frame writes the
    // real one. Every way out of a run therefore has to land on the final text: a
    // run that stopped part-way used to leave the part-way figure on screen for
    // good -- $0.99 where the data says $1.00 -- and Blazor never corrected it,
    // because as far as Blazor is concerned it already rendered $1.00 there and
    // nothing changed.
    //
    // Which is why the run only ever writes over its own part-way figures. It used
    // to write the text it remembered whatever was there, and the text it
    // remembered could already be stale: the attribute and the text are two edits,
    // and if the attribute arrives first the run reads the *previous* figure as its
    // final one and pastes it back 900ms later, over the figure the app had since
    // rendered. That is the "right numbers for a moment, then the old ones come
    // back" this file was reported for. A text change that is not this run's own is
    // the app rendering a newer figure, and it becomes the one to land on.
    const running = new WeakMap();

    // Writes a figure through the text node that is already there, rather than
    // through `textContent`, which throws that node away and puts a fresh one in
    // its place. Blazor keeps a reference to the node it rendered and writes its
    // next figure straight into that node -- so replacing it means every later
    // render lands on a node no longer in the document, and whatever this file
    // wrote last stays on screen for good. That is a figure from before the last
    // count sitting under a caption describing the new one, with `data-gs-count`
    // beside it already correct: the words moved and the number did not.
    function setText(el, text) {
        const node = el.firstChild;

        if (node !== null && node.nodeType === 3 && node.nextSibling === null) {
            node.data = text;
            return;
        }

        el.textContent = text;
    }

    function countUp(el) {
        const token = el.getAttribute("data-gs-count");
        if (token === null) return;

        const state = running.get(el);

        // Already counted to this figure. Put the final text back only if what is
        // there is this run's own part-way figure; anything else is the app's, and
        // newer than anything remembered here.
        if (state && state.token === token) {
            if (state.wrote !== null && el.textContent === state.wrote) setText(el, state.final);
            return;
        }

        // The text as rendered is the figure of record. Read before the first
        // frame can overwrite it, and kept, so a later frame or the backstop
        // below can put it back without having to work it out again -- and kept
        // up to date by `noteText` if the app renders a newer one mid-count.
        const final = el.textContent;
        const run = { token: token, final: final, wrote: null };
        running.set(el, run);

        const target = Math.abs(parseFloat(token));
        if (!isFinite(target) || target === 0 || reduced.matches) return;

        const match = final.match(/\d[\d.,]*/);
        if (!match) return;

        const head = final.slice(0, match.index);
        const tail = final.slice(match.index + match[0].length);
        const fraction = match[0].match(/[.,](\d+)$/);
        const decimals = fraction && fraction[1].length <= 2 ? fraction[1].length : 0;
        const grouped = /[.,]\d{3}/.test(match[0]);

        // The attribute and the text are two edits for one figure, and they do not
        // have to arrive together. When they disagree, the text on screen belongs to
        // the *previous* figure and the one for this attribute has not been rendered
        // yet -- so counting towards it would mean landing on a figure the app has
        // already replaced, which is the stale total this run must never leave
        // behind. The count is decoration and the figure is the point: this update
        // goes without its animation, and the text the app is about to render stands.
        const shown = Math.abs(parseFloat(match[0].replace(/,/g, "")));
        if (!isFinite(shown) || Math.abs(shown - target) > 0.5 / Math.pow(10, decimals)) return;

        const start = performance.now();
        const duration = 800;

        // Only ever replaces this run's own part-way figure. `run.final` is
        // whatever the app last rendered, which is not necessarily what it had
        // rendered when the run started.
        function land() {
            if (run.wrote === null || el.textContent === run.wrote) setText(el, run.final);
        }

        function settle() {
            if (el.getAttribute("data-gs-count") === token) land();
        }

        function frame(now) {
            // A newer figure has taken the element over. That run holds its own
            // final text and will land on it, so this one just stops.
            if (el.getAttribute("data-gs-count") !== token) return;

            const t = Math.min(1, (now - start) / duration);

            // Done, or detached mid-count. Either way the figure goes back to
            // the one the data gave, so a node Blazor reuses does not come back
            // still showing whatever fraction of it this run had reached.
            if (t >= 1 || !el.isConnected) {
                land();
                return;
            }

            const eased = 1 - Math.pow(1 - t, 4);
            const text = head + (target * eased).toLocaleString("en-US", {
                minimumFractionDigits: decimals,
                maximumFractionDigits: decimals,
                useGrouping: grouped
            }) + tail;

            setText(el, text);
            run.wrote = text;

            requestAnimationFrame(frame);
        }

        requestAnimationFrame(frame);

        // requestAnimationFrame is not a promise that the last frame ever
        // arrives: a backgrounded tab stops it outright and a phone throttles
        // it, both of which strand the count wherever it had got to. The figure
        // is the point and the count is decoration, so this makes sure the
        // figure lands whether or not the frames do.
        setTimeout(settle, duration + 100);
    }

    function scan(node) {
        if (!(node instanceof Element)) return;
        if (node.hasAttribute("data-gs-count")) countUp(node);
        for (const el of node.querySelectorAll("[data-gs-count]")) countUp(el);
    }

    // The app rendered a figure into an element a count is running on. The two
    // edits behind one figure -- the attribute and the text -- do not have to
    // arrive together, so this is how a run finds out that the text it read at the
    // start has been overtaken. Its own frames come through here too and are the
    // one text to ignore.
    function noteText(node) {
        const el = node instanceof Element ? node : node.parentElement;
        if (el === null || !el.hasAttribute("data-gs-count")) return;

        const run = running.get(el);
        if (run === undefined) return;

        const text = el.textContent;
        if (text !== run.wrote) run.final = text;
    }

    new MutationObserver(function (records) {
        for (const record of records) {
            if (record.type === "attributes") {
                countUp(record.target);
            } else if (record.type === "characterData") {
                noteText(record.target);
            } else {
                for (const node of record.addedNodes) scan(node);
            }
        }
    }).observe(root, {
        childList: true,
        subtree: true,
        attributes: true,
        attributeFilter: ["data-gs-count"],
        characterData: true
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

        // Hands the browser a file the app built in memory. The export is assembled
        // through the same HTTP client as everything else, because a plain download link
        // would go out without the bearer token and come back as a sign-in page named
        // .csv. The object URL is revoked on the next tick: revoking it synchronously
        // races the click in Safari and downloads nothing.
        download(filename, text) {
            const blob = new Blob([text], { type: "text/csv;charset=utf-8" });
            const url = URL.createObjectURL(blob);

            const link = document.createElement("a");
            link.href = url;
            link.download = filename;
            link.style.display = "none";

            document.body.appendChild(link);
            link.click();
            document.body.removeChild(link);

            setTimeout(() => URL.revokeObjectURL(url), 0);
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
