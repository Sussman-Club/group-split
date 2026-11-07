'use strict';

const lastOpenByTab = new Map();

// A popup window stays open while the user visits the retailer and prints receipts.
function openImporter(query, windowId) {
  const parent = Number.isInteger(windowId) ? `&windowId=${windowId}` : '';
  return chrome.windows.create({ url: chrome.runtime.getURL(`popup.html?${query}${parent}`), type: 'popup', width: 560, height: 800 });
}

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
  await openImporter(`${key}=${tab.id}${theme}`, tab.windowId);
});

chrome.runtime.onMessage.addListener((message, sender, respond) => {
  if (message?.type === 'embedded-context' && sender.id === chrome.runtime.id && sender.frameId > 0 && sender.tab?.id &&
      sender.url?.startsWith(chrome.runtime.getURL('popup.html?embedded=1'))) {
    let page;
    try { page = new URL(sender.tab.url); } catch { return false; }
    const local = ['localhost', '127.0.0.1'].includes(page.hostname) && ['http:', 'https:'].includes(page.protocol);
    if ((!local && page.origin !== 'https://groupsplit.sussman.win') || page.pathname !== '/inbox/receipts') return false;
    respond({ tabId: sender.tab.id, windowId: sender.tab.windowId });
    return false;
  }
  if (!['open-receipt-importer', 'open-receipt-importer-window'].includes(message?.type) || sender.id !== chrome.runtime.id || sender.frameId !== 0 || !sender.tab?.id)
    return false;
  let url, bridgeUrl;
  try { url = new URL(sender.tab.url); bridgeUrl = new URL(sender.url); } catch { return false; }
  const local = ['localhost', '127.0.0.1'].includes(url.hostname) && ['http:', 'https:'].includes(url.protocol);
  const official = url.origin === 'https://groupsplit.sussman.win';
  if ((!local && !official) || bridgeUrl.origin !== url.origin || url.pathname !== '/inbox/receipts') return false;
  const theme = message.theme === 'dark' ? 'dark' : 'light';
  if (message.type === 'open-receipt-importer') {
    respond({ opened: true, url: chrome.runtime.getURL(`popup.html?embedded=1&theme=${theme}`) });
    return false;
  }
  const now = Date.now();
  if (now - (lastOpenByTab.get(sender.tab.id) || 0) < 2000) {
    respond({ opened: false });
    return false;
  }
  lastOpenByTab.set(sender.tab.id, now);
  if (lastOpenByTab.size > 100)
    for (const [id, time] of lastOpenByTab) if (now - time >= 2000) lastOpenByTab.delete(id);
  openImporter(`appTab=${sender.tab.id}&theme=${theme}`, sender.tab.windowId)
    .then(() => respond({ opened: true }), () => respond({ opened: false }));
  return true;
});
