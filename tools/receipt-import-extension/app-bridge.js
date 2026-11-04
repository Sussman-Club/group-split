'use strict';

// This bridge carries only an instruction to open the importer. Receipt data
// and credentials never travel through page events.
const currentTheme = () => document.documentElement.dataset.theme === 'dark' ? 'dark' : 'light';
window.addEventListener('groupsplit:open-receipt-importer', () => {
  chrome.runtime.sendMessage({ type: 'open-receipt-importer', theme: currentTheme() }, response => {
    if (chrome.runtime.lastError || !response?.opened) return;
    window.dispatchEvent(new Event('groupsplit:receipt-importer-opened'));
  });
});

new MutationObserver(() => {
  chrome.runtime.sendMessage({ type: 'groupsplit-theme-changed', theme: currentTheme() }, () => { void chrome.runtime.lastError; });
}).observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme'] });

chrome.runtime.onMessage.addListener((message, sender) => {
  if (sender.id === chrome.runtime.id && message?.type === 'receipts-imported')
    window.dispatchEvent(new Event('groupsplit:receipts-imported'));
});
