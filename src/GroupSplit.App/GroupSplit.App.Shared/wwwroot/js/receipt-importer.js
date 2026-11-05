export function openReceiptImporter() {
    return new Promise(resolve => {
        let timer;
        let dialog;
        let frame;
        const cleanup = () => {
            clearTimeout(timer);
            window.removeEventListener('groupsplit:receipt-importer-opened', onOpened);
            window.removeEventListener('groupsplit:receipt-importer-window-opened', onWindowOpened);
            window.removeEventListener('message', onReady);
        };
        const onWindowOpened = () => { cleanup(); resolve(true); };
        const fallback = () => {
            window.removeEventListener('groupsplit:receipt-importer-opened', onOpened);
            window.removeEventListener('message', onReady);
            if (dialog) { dialog.remove(); dialog = null; frame = null; document.documentElement.classList.remove('gs-receipt-import-open'); }
            window.addEventListener('groupsplit:receipt-importer-window-opened', onWindowOpened, { once: true });
            timer = setTimeout(() => { cleanup(); resolve(false); }, 4000);
            window.dispatchEvent(new Event('groupsplit:open-receipt-importer-window'));
        };
        const onReady = event => {
            if (!frame) return;
            const url = new URL(frame.src);
            if (event.source !== frame.contentWindow || event.origin !== `${url.protocol}//${url.host}` || event.data?.type !== 'groupsplit-importer-ready') return;
            cleanup();
            resolve(true);
        };
        const onOpened = event => {
            let url;
            try { url = new URL(event.detail); } catch { return; }
            if (url.protocol !== 'chrome-extension:' || !/^[a-p]{32}$/.test(url.hostname) || url.pathname !== '/popup.html' || url.searchParams.get('embedded') !== '1') return;
            dialog = document.createElement('dialog');
            dialog.className = 'gs-receipt-import-dialog';
            dialog.setAttribute('aria-labelledby', 'gs-receipt-import-title');
            const header = document.createElement('div');
            header.className = 'gs-receipt-import-dialog-header';
            const heading = document.createElement('h2');
            heading.id = 'gs-receipt-import-title';
            heading.textContent = 'Import from a store';
            const close = document.createElement('button');
            close.type = 'button';
            close.className = 'gs-receipt-import-close';
            close.setAttribute('aria-label', 'Close receipt importer');
            close.textContent = '×';
            close.addEventListener('click', () => dialog.close());
            frame = document.createElement('iframe');
            frame.title = 'Import receipts from a store';
            frame.src = url.href;
            header.append(heading, close);
            dialog.append(header, frame);
            dialog.addEventListener('close', () => { cleanup(); dialog.remove(); document.documentElement.classList.remove('gs-receipt-import-open'); resolve(false); }, { once: true });
            document.body.append(dialog);
            window.addEventListener('message', onReady);
            document.documentElement.classList.add('gs-receipt-import-open');
            dialog.showModal();
        };
        window.addEventListener('groupsplit:receipt-importer-opened', onOpened, { once: true });
        timer = setTimeout(fallback, 4000);
        window.dispatchEvent(new Event('groupsplit:open-receipt-importer'));
    });
}

let listener;
export function watchReceipts(dotnet) {
    if (listener) window.removeEventListener('groupsplit:receipts-imported', listener);
    listener = () => { dotnet.invokeMethodAsync('RefreshImportedReceipts').catch(() => {}); };
    window.addEventListener('groupsplit:receipts-imported', listener);
}

export function stopWatching() {
    if (listener) window.removeEventListener('groupsplit:receipts-imported', listener);
    listener = null;
}
