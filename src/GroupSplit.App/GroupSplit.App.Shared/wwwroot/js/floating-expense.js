const subscriptions = new WeakMap();

export function observe(button) {
    if (!(button instanceof HTMLElement) || !button.isConnected) return;
    if (subscriptions.has(button)) return;
    const phone = matchMedia("(max-width: 820px), (max-width: 900px) and (max-height: 500px)");
    const positions = new WeakMap();
    let timer;
    function restore() {
        button.classList.remove("is-scrolling");
    }
    function onScroll(event) {
        const target = event.target === document ? document.scrollingElement : event.target;
        if (!target || typeof target.scrollTop !== "number") return;
        const current = target.scrollTop;
        const previous = positions.get(target) ?? 0;
        positions.set(target, current);
        if (!phone.matches) return;
        if (Math.abs(current - previous) < 2) return;
        button.classList.toggle("is-scrolling", current > previous && current > 24);
        clearTimeout(timer);
        timer = setTimeout(restore, 900);
    }
    // Capture catches both window scrolling and the app's nested content scroller.
    document.addEventListener("scroll", onScroll, { capture: true, passive: true });
    phone.addEventListener("change", restore);
    subscriptions.set(button, () => {
        document.removeEventListener("scroll", onScroll, true);
        phone.removeEventListener("change", restore);
        clearTimeout(timer);
    });
}

export function disconnect(button) {
    subscriptions.get(button)?.();
    subscriptions.delete(button);
}
