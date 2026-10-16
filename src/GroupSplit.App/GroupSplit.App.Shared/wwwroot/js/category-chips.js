const observers = new WeakMap();

function layout(row) {
    const chips = [...row.querySelectorAll(".gs-category-leader")];
    const more = row.querySelector(".gs-category-toggle");
    const label = more.querySelector(".gs-category-more");
    const gap = parseFloat(getComputedStyle(row).columnGap) || 0;
    more.hidden = false;
    chips.forEach(chip => { chip.hidden = false; });
    // Reserve the widest overflow label before fitting, so the last chip never
    // displaces the button that makes the remaining categories reachable.
    label.textContent = "+" + chips.length + " more";
    const reserve = more.getBoundingClientRect().width + gap;
    const widths = chips.map(chip => chip.getBoundingClientRect().width);
    const available = row.clientWidth;
    const total = widths.reduce((sum, width) => sum + width, 0) + gap * Math.max(0, chips.length - 1);
    let visible = chips.length;
    if (total > available) {
        let used = 0;
        visible = 0;
        for (const width of widths) {
            const next = used + (visible ? gap : 0) + width;
            if (next + reserve > available) break;
            used = next;
            visible++;
        }
    }
    chips.forEach((chip, index) => { chip.hidden = index >= visible; });
    const hidden = chips.length - visible;
    more.hidden = hidden === 0;
    label.textContent = "+" + hidden + " more";
}

export function fit(row) {
    if (!observers.has(row)) {
        const observer = new ResizeObserver(() => layout(row));
        observer.observe(row);
        observers.set(row, observer);
        // Font loading can change chip widths without changing the row width.
        document.fonts.ready.then(() => {
            if (observers.has(row)) layout(row);
        });
    }
    layout(row);
}

export function disconnect(row) {
    observers.get(row)?.disconnect();
    observers.delete(row);
}
