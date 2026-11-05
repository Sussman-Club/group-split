export function enablePdfPan(viewport) {
    let drag = null;
    const down = event => {
        if (event.pointerType !== 'mouse' || event.button !== 0 ||
            (viewport.scrollWidth <= viewport.clientWidth && viewport.scrollHeight <= viewport.clientHeight)) return;
        drag = { id: event.pointerId, x: event.clientX, y: event.clientY,
            left: viewport.scrollLeft, top: viewport.scrollTop };
        viewport.setPointerCapture(event.pointerId);
        viewport.classList.add('is-panning');
        event.preventDefault();
    };
    const move = event => {
        if (!drag || event.pointerId !== drag.id) return;
        viewport.scrollLeft = drag.left - (event.clientX - drag.x);
        viewport.scrollTop = drag.top - (event.clientY - drag.y);
    };
    const end = event => {
        if (!drag || event.pointerId !== drag.id) return;
        drag = null;
        viewport.classList.remove('is-panning');
        if (viewport.hasPointerCapture(event.pointerId)) viewport.releasePointerCapture(event.pointerId);
    };
    viewport.addEventListener('pointerdown', down);
    viewport.addEventListener('pointermove', move);
    viewport.addEventListener('pointerup', end);
    viewport.addEventListener('pointercancel', end);
    viewport.addEventListener('lostpointercapture', end);
    return () => {
        if (drag && viewport.hasPointerCapture(drag.id)) viewport.releasePointerCapture(drag.id);
        drag = null;
        viewport.classList.remove('is-panning');
        viewport.removeEventListener('pointerdown', down);
        viewport.removeEventListener('pointermove', move);
        viewport.removeEventListener('pointerup', end);
        viewport.removeEventListener('pointercancel', end);
        viewport.removeEventListener('lostpointercapture', end);
    };
}
