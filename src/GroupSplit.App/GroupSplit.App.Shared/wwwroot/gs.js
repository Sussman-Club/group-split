// Group Split shell helper. Deliberately tiny: the design system is CSS-first,
// and this only covers what CSS cannot do on its own.
(function () {
    const storageKey = "groupsplit.theme";
    const root = document.documentElement;

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

    window.gs = {
        setTheme(isDark) {
            root.dataset.theme = isDark ? "dark" : "light";
        }
    };
})();
