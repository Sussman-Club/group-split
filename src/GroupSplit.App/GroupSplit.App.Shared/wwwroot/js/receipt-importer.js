export function openReceiptImporter() {
    return new Promise(resolve => {
        let timer;
        const finish = opened => {
            clearTimeout(timer);
            window.removeEventListener('groupsplit:receipt-importer-opened', onOpened);
            resolve(opened);
        };
        const onOpened = () => finish(true);
        window.addEventListener('groupsplit:receipt-importer-opened', onOpened, { once: true });
        timer = setTimeout(() => finish(false), 1000);
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
