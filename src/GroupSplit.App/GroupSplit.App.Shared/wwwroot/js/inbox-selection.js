export function attach(root, receiver) {
    let drag = null;
    function down(event) {
        const handle = event.target.closest('[data-select-handle]');
        if (!handle || !root.contains(handle) || (event.pointerType === 'mouse' && event.button !== 0)) return;
        event.preventDefault();
        const row = handle.closest('[data-selection-id]');
        if (!row) return;
        drag = { pointer: event.pointerId, ids: new Set([row.dataset.selectionId]) };
        row.classList.add('gs-drag-preview');
    }
    function move(event) {
        if (!drag || event.pointerId !== drag.pointer) return;
        event.preventDefault();
        const row = document.elementFromPoint(event.clientX, event.clientY)?.closest('[data-selection-id]');
        if (row && root.contains(row)) { drag.ids.add(row.dataset.selectionId); row.classList.add('gs-drag-preview'); }
    }
    function finish(event) {
        if (!drag || event.pointerId !== drag.pointer) return;
        const ids = [...drag.ids];
        drag = null;
        root.querySelectorAll('.gs-drag-preview').forEach(row => row.classList.remove('gs-drag-preview'));
        if (event.type !== 'pointercancel') receiver.invokeMethodAsync('SelectDraggedRows', ids);
    }
    root.addEventListener('pointerdown', down);
    document.addEventListener('pointermove', move, { passive: false });
    document.addEventListener('pointerup', finish);
    document.addEventListener('pointercancel', finish);
    return { dispose() { root.removeEventListener('pointerdown', down); document.removeEventListener('pointermove', move); document.removeEventListener('pointerup', finish); document.removeEventListener('pointercancel', finish); } };
}
