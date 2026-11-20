// Dragging the grip selects every row between where the drag started and the row under the
// pointer, so a fast drag that skips rows between two pointer events still selects them.
export function attach(root, receiver) {
    let drag = null;
    const rows = () => [...root.querySelectorAll('[data-selection-id]')];
    function preview(range) {
        root.querySelectorAll('.gs-drag-preview').forEach(row => { if (!range.includes(row)) row.classList.remove('gs-drag-preview'); });
        range.forEach(row => row.classList.add('gs-drag-preview'));
    }
    function down(event) {
        const handle = event.target.closest('[data-select-handle]');
        if (!handle || !root.contains(handle) || (event.pointerType === 'mouse' && event.button !== 0)) return;
        const row = handle.closest('[data-selection-id]');
        if (!row) return;
        event.preventDefault();
        drag = { pointer: event.pointerId, start: row, range: [row] };
        preview(drag.range);
    }
    function move(event) {
        if (!drag || event.pointerId !== drag.pointer) return;
        event.preventDefault();
        const row = document.elementFromPoint(event.clientX, event.clientY)?.closest('[data-selection-id]');
        if (!row || !root.contains(row)) return;
        const all = rows();
        const [from, to] = [all.indexOf(drag.start), all.indexOf(row)].sort((a, b) => a - b);
        if (from < 0) return;
        drag.range = all.slice(from, to + 1);
        preview(drag.range);
    }
    function finish(event) {
        if (!drag || event.pointerId !== drag.pointer) return;
        const ids = drag.range.map(row => row.dataset.selectionId);
        drag = null;
        preview([]);
        if (event.type !== 'pointercancel') receiver.invokeMethodAsync('SelectDraggedRows', ids);
    }
    root.addEventListener('pointerdown', down);
    document.addEventListener('pointermove', move, { passive: false });
    document.addEventListener('pointerup', finish);
    document.addEventListener('pointercancel', finish);
    return { dispose() { root.removeEventListener('pointerdown', down); document.removeEventListener('pointermove', move); document.removeEventListener('pointerup', finish); document.removeEventListener('pointercancel', finish); } };
}
