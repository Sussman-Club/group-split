// Per-tab navigation memory. Only group list/activity routes participate.
const prefix = "groupsplit:navigation:";
function read(key) { try { return JSON.parse(sessionStorage.getItem(prefix + key)); } catch { return null; } }
function write(key, value) { try { sessionStorage.setItem(prefix + key, JSON.stringify(value)); } catch { } }
function supported(path) { return path === "/groups" || /^\/groups\/[^/]+\/activity$/.test(path); }
export function attach() {
    const path = location.pathname;
    const initial = location.pathname + location.search;
    const saved = read(initial);
    let restoring = supported(location.pathname) && saved > 0;
    let disposed = false;
    let frame;
    const deadline = performance.now() + 5000;
    function save() {
        if (restoring || location.pathname !== path || !supported(path)) return;
        const key = location.pathname + location.search;
        write(key, window.scrollY);
        write("last:" + location.pathname, key);
    }
    function restore() {
        if (!restoring || disposed) return;
        if (location.pathname + location.search !== initial) { restoring = false; return; }
        window.scrollTo({ top: saved, behavior: "instant" });
        if (Math.abs(window.scrollY - saved) < 2 || performance.now() > deadline) { restoring = false; return; }
        frame = requestAnimationFrame(restore);
    }
    function userInput() { restoring = false; cancelAnimationFrame(frame); }
    function rememberLink(event) {
        save();
        const link = event.target.closest?.("a[href]");
        if (!link || event.defaultPrevented || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey || event.button !== 0) return;
        const target = new URL(link.href, location.href);
        if (target.origin !== location.origin || !supported(target.pathname) || target.search || target.hash) return;
        const last = read("last:" + target.pathname);
        if (typeof last === "string" && last.startsWith(target.pathname + "?")) link.href = last;
    }
    window.addEventListener("scroll", save, { passive: true });
    window.addEventListener("pagehide", save);
    document.addEventListener("click", rememberLink, true);
    document.addEventListener("wheel", userInput, { passive: true });
    document.addEventListener("touchstart", userInput, { passive: true });
    document.addEventListener("keydown", userInput);
    if (restoring) frame = requestAnimationFrame(restore);
    return { dispose() {
        save(); disposed = true; cancelAnimationFrame(frame);
        window.removeEventListener("scroll", save);
        window.removeEventListener("pagehide", save);
        document.removeEventListener("click", rememberLink, true);
        document.removeEventListener("wheel", userInput);
        document.removeEventListener("touchstart", userInput);
        document.removeEventListener("keydown", userInput);
    } };
}
