'use strict';

const lastOpenByTab = new Map();

// Keep the importer open while Costco dialogs are being opened and printed.
chrome.action.onClicked.addListener(async tab => {
  if (!tab.id) return;
  const url = new URL(tab.url || 'about:blank');
  const key = ['www.costco.com', 'www.amazon.com'].includes(url.hostname) ? 'tab' : 'appTab';
  let theme = '';
  if (key === 'appTab') {
    try {
      const [{ result }] = await chrome.scripting.executeScript({ target: { tabId: tab.id }, func: () => document.documentElement.dataset.theme });
      if (result === 'light' || result === 'dark') theme = `&theme=${result}`;
    } catch { /* Fall back to the browser's colour preference. */ }
  }
  await chrome.tabs.create({ url: chrome.runtime.getURL(`popup.html?${key}=${tab.id}${theme}`) });
});

chrome.runtime.onMessage.addListener((message, sender, respond) => {
  if (message?.type !== 'open-receipt-importer' || sender.id !== chrome.runtime.id || sender.frameId !== 0 || !sender.tab?.id)
    return false;
  let url;
  try { url = new URL(sender.url); } catch { return false; }
  const local = ['localhost', '127.0.0.1'].includes(url.hostname) && ['http:', 'https:'].includes(url.protocol);
  const official = url.origin === 'https://groupsplit.sussman.win';
  if ((!local && !official) || url.pathname !== '/inbox/receipts') return false;
  const now = Date.now();
  if (now - (lastOpenByTab.get(sender.tab.id) || 0) < 2000) {
    respond({ opened: false });
    return false;
  }
  lastOpenByTab.set(sender.tab.id, now);
  if (lastOpenByTab.size > 100)
    for (const [id, time] of lastOpenByTab) if (now - time >= 2000) lastOpenByTab.delete(id);
  const theme = message.theme === 'dark' ? 'dark' : 'light';
  chrome.tabs.create({ url: chrome.runtime.getURL(`popup.html?appTab=${sender.tab.id}&theme=${theme}`) })
    .then(() => respond({ opened: true }), () => respond({ opened: false }));
  return true;
});
