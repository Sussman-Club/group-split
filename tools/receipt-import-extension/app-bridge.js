'use strict';

// This bridge carries only an instruction to open the importer. Receipt data
// and credentials never travel through page events.
const currentTheme = () => document.documentElement.dataset.theme === 'dark' ? 'dark' : 'light';
const runtime = chrome.runtime;
const openEvent = 'groupsplit:open-receipt-importer';
const windowEvent = 'groupsplit:open-receipt-importer-window';
const themeObserver = new MutationObserver(() => send('groupsplit-theme-changed'));

function stopBridge() {
  themeObserver.disconnect();
  window.removeEventListener(openEvent, openImporter);
  window.removeEventListener(windowEvent, openImporterWindow);
}

function send(type, onResponse) {
  try {
    if (!runtime.id) { stopBridge(); return; }
    runtime.sendMessage({ type, theme: currentTheme() }, response => {
      if (runtime.lastError) return;
      onResponse?.(response);
    });
  } catch (error) {
    if (!/extension context invalidated/i.test(error.message)) throw error;
    stopBridge();
  }
}

function openImporter() {
  send('open-receipt-importer', response => {
    if (!response?.opened) return;
    window.dispatchEvent(new CustomEvent('groupsplit:receipt-importer-opened', { detail: response.url }));
  });
}

function openImporterWindow() {
  send('open-receipt-importer-window', response => {
    if (response?.opened) window.dispatchEvent(new Event('groupsplit:receipt-importer-window-opened'));
  });
}

window.addEventListener(openEvent, openImporter);
window.addEventListener(windowEvent, openImporterWindow);
themeObserver.observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme'] });

runtime.onMessage.addListener((message, sender) => {
  if (sender.id === runtime.id && message?.type === 'receipts-imported')
    window.dispatchEvent(new Event('groupsplit:receipts-imported'));
});
