import { getDocument, GlobalWorkerOptions } from "../vendor/pdfjs/pdf.min.mjs";
GlobalWorkerOptions.workerSrc = new URL("../vendor/pdfjs/pdf.worker.min.mjs", import.meta.url).href;
const viewers = new WeakMap();

export async function mount(host, url, labels) {
    await dispose(host);
    const state = { page: 1, zoom: 1, revision: 0, disposed: false, queue: Promise.resolve() };
    viewers.set(host, state);
    const canvas = host.querySelector("canvas");
    const status = host.querySelector("[data-pdf-status]");
    const pageLabel = host.querySelector("[data-pdf-page]");
    const viewportHost = host.querySelector(".gs-pdf-viewport");
    const buttons = [...host.querySelectorAll("button[data-pdf-action]")];
    function controls() {
        for (const button of buttons) {
            const action = button.dataset.pdfAction;
            button.disabled = !state.pdf || (action === "previous" && state.page <= 1) ||
                (action === "next" && state.page >= state.pdf.numPages) ||
                (action === "out" && state.zoom <= .75) || (action === "in" && state.zoom >= 2.5);
        }
        if (state.pdf) pageLabel.textContent = labels.page.replace("{0}", state.page).replace("{1}", state.pdf.numPages);
    }
    function showError(error) {
        if (state.disposed || error?.name === "RenderingCancelledException") return;
        status.hidden = false;
        status.textContent = error?.name === "PasswordException"
            ? labels.password
            : labels.failed;
        canvas.hidden = true;
        pageLabel.textContent = labels.unavailable;
    }
    function render() {
        const revision = ++state.revision;
        state.renderTask?.cancel();
        state.queue = state.queue.catch(() => {}).then(async () => {
            if (state.disposed || !state.pdf || revision !== state.revision) return;
            const page = await state.pdf.getPage(state.page);
            if (state.disposed || revision !== state.revision) return;
            const base = page.getViewport({ scale: 1 });
            const width = Math.max(1, viewportHost.clientWidth - 16);
            const viewport = page.getViewport({ scale: width / base.width * state.zoom });
            // Bound mobile canvas memory without changing the original document.
            const ratio = Math.min(window.devicePixelRatio || 1, 2,
                Math.sqrt(8000000 / (viewport.width * viewport.height)),
                16384 / Math.max(viewport.width, viewport.height));
            canvas.width = Math.max(1, Math.floor(viewport.width * ratio));
            canvas.height = Math.max(1, Math.floor(viewport.height * ratio));
            canvas.style.width = `${Math.floor(viewport.width)}px`;
            canvas.style.height = `${Math.floor(viewport.height)}px`;
            canvas.hidden = false;
            canvas.setAttribute("aria-label", labels.pdfPage.replace("{0}", state.page).replace("{1}", state.pdf.numPages));
            status.hidden = false;
            status.textContent = labels.rendering;
            state.renderTask = page.render({ canvasContext: canvas.getContext("2d"), viewport,
                transform: [ratio, 0, 0, ratio, 0, 0] });
            await state.renderTask.promise;
            if (state.disposed || revision !== state.revision) return;
            state.renderTask = null;
            status.hidden = true;
            controls();
        }).catch(showError);
        return state.queue;
    }
    state.click = event => {
        const button = event.target.closest("button[data-pdf-action]");
        if (!button || !state.pdf || button.disabled) return;
        switch (button.dataset.pdfAction) {
            case "previous": state.page--; break;
            case "next": state.page++; break;
            case "in": state.zoom = Math.min(2.5, state.zoom + .25); break;
            case "out": state.zoom = Math.max(.75, state.zoom - .25); break;
            case "fit": state.zoom = 1; break;
        }
        controls();
        void render();
    };
    host.addEventListener("click", state.click);
    let knownWidth = viewportHost.clientWidth;
    state.observer = new ResizeObserver(() => {
        if (Math.abs(viewportHost.clientWidth - knownWidth) < 1) return;
        knownWidth = viewportHost.clientWidth;
        if (state.pdf && !state.disposed) void render();
    });
    state.observer.observe(viewportHost);
    try {
        state.loadingTask = getDocument({ url, withCredentials: true, isEvalSupported: false,
            cMapUrl: new URL("../vendor/pdfjs/cmaps/", import.meta.url).href, cMapPacked: true,
            standardFontDataUrl: new URL("../vendor/pdfjs/standard_fonts/", import.meta.url).href,
            wasmUrl: new URL("../vendor/pdfjs/wasm/", import.meta.url).href });
        state.pdf = await state.loadingTask.promise;
        if (state.disposed) return;
        controls();
        await render();
    } catch (error) { showError(error); }
}

export async function dispose(host) {
    const state = viewers.get(host);
    if (!state) return;
    viewers.delete(host);
    state.disposed = true;
    state.revision++;
    state.observer?.disconnect();
    host.removeEventListener("click", state.click);
    state.renderTask?.cancel();
    try { await state.loadingTask?.destroy(); } catch { /* The document may still be loading. */ }
    await state.queue.catch(() => {});
}
