'use strict';

const $ = id => document.getElementById(id);
const status = $('status');
let selection = null;
let inbox = null;
let sourceTabId = Number(new URL(location.href).searchParams.get('tab'));
let inboxTabId = Number(new URL(location.href).searchParams.get('appTab'));
const parentWindowId = Number(new URL(location.href).searchParams.get('windowId'));
let parentWindow = Number.isInteger(parentWindowId) && parentWindowId > 0 ? { windowId: parentWindowId } : {};
const embedded = new URL(location.href).searchParams.get('embedded') === '1';
if (embedded) document.documentElement.dataset.embedded = 'true';
const requestedTheme = new URL(location.href).searchParams.get('theme');
const costcoPrivacyMessage = 'Costco receipt files may include customer, membership and masked payment details. Review them before sharing.';
if (requestedTheme === 'dark' || requestedTheme === 'light') document.documentElement.dataset.theme = requestedTheme;
if (typeof chrome !== 'undefined' && chrome.runtime?.onMessage) {
  chrome.runtime.onMessage.addListener((message, sender) => {
    if (message?.type === 'groupsplit-theme-changed' && sender.id === chrome.runtime.id &&
        sender.tab?.id === inboxTabId && (message.theme === 'dark' || message.theme === 'light'))
      document.documentElement.dataset.theme = message.theme;
  });
}

function localDate(date) {
  return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}-${String(date.getDate()).padStart(2, '0')}`;
}

const today = new Date();
const threeMonthsAgo = new Date(today.getFullYear(), today.getMonth() - 3, today.getDate());
$('from').value = localDate(threeMonthsAgo);
$('to').value = localDate(today);

function scanPage() {
  const visible = element => !!(element.offsetWidth || element.offsetHeight || element.getClientRects().length);
  const receiptLabel = /^(?:view receipt|ver recibo|ver comprobante|visualizar recibo|ver comprovante)$/i;
  const dayFirst = /^(?:es|pt)(?:-|$)/i.test(document.documentElement.lang || '') &&
    !/-US$/i.test(document.documentElement.lang || '');
  const actions = [...document.querySelectorAll('button, a, [role="button"]')]
    .filter(element => visible(element) && receiptLabel.test(element.textContent.trim()));
  return actions.map((action, index) => {
    let row = action.parentElement;
    while (row && row !== document.body) {
      const count = [...row.querySelectorAll('button, a, [role="button"]')]
        .filter(element => visible(element) && receiptLabel.test(element.textContent.trim())).length;
      const text = row.innerText.replace(/\s+/g, ' ').trim();
      const dates = [...text.matchAll(/\b(\d{1,2})\/(\d{1,2})\/(\d{4})\b/g)];
      if (count === 1 && dates.length === 1 && text.length < 1200) {
        const [, first, second, year] = dates[0];
        const useDayFirst = Number(first) > 12 || (Number(second) <= 12 && dayFirst);
        const [month, day] = useDayFirst ? [second, first] : [first, second];
        const date = new Date(Date.UTC(Number(year), Number(month) - 1, Number(day)));
        if (date.getUTCMonth() + 1 !== Number(month) || date.getUTCDate() !== Number(day)) return null;
        return { index, date: date.toISOString().slice(0, 10), label: text.slice(0, 240) };
      }
      row = row.parentElement;
    }
    return null;
  });
}

function scanAmazonPage() {
  const cards = [...document.querySelectorAll('.js-order-card, [id="orderCard"], .order-card, .a-box-group.order')];
  return cards.map(card => {
    const rawText = card.innerText;
    const text = rawText.replace(/\s+/g, ' ').trim();
    const months = [
      'january enero janeiro', 'february febrero fevereiro', 'march marzo março',
      'april abril', 'may mayo maio', 'june junio junho', 'july julio julho',
      'august agosto', 'september septiembre setiembre setembro', 'october octubre outubro',
      'november noviembre novembro', 'december diciembre dezembro'
    ];
    const monthPattern = months.join(' ').replaceAll(' ', '|');
    const datePattern = '(?:(?:' + monthPattern + ')\\s+\\d{1,2},?\\s+\\d{4}|\\d{1,2}\\s+(?:de\\s+)?(?:' +
      monthPattern + ')\\s+(?:de\\s+)?\\d{4})';
    const placed = text.match(new RegExp('\\b(?:order placed|pedido realizado(?: el| em)?|pedido efetuado(?: em)?|fecha del pedido|data do pedido)\\s*:?\\s*(' + datePattern + ')', 'i'));
    const dates = [...text.matchAll(new RegExp(`\\b${datePattern}\\b`, 'gi'))];
    const dateText = placed?.[1] || (dates.length === 1 ? dates[0][0] : null);
    const link = card.querySelector('a[href*="summary/print.html"]');
    if (!dateText) return null;
    const monthName = dateText.match(new RegExp('\\b(?:' + monthPattern + ')\\b', 'i'))[0].toLowerCase();
    const month = months.findIndex(names => names.split(' ').includes(monthName));
    const [dayNumber, year] = dateText.match(/\d+/g).map(Number);
    const date = new Date(Date.UTC(year, month, dayNumber));
    if (date.getUTCDate() !== dayNumber || date.getUTCMonth() !== month) return null;
    let invoiceUrl = null;
    if (link?.href) {
      try {
        const url = new URL(link.href, location.href);
        if (url.origin === 'https://www.amazon.com' && !url.username && !url.password && url.pathname.endsWith('/summary/print.html')) invoiceUrl = url.href;
      } catch { /* An unusable invoice should not hide this order's recipient. */ }
    }
    const day = date.toISOString().slice(0, 10);
    const recipientArea = card.querySelector('.yohtmlc-recipient');
    const recipientText = typeof recipientArea?.innerText === 'string' ? recipientArea.innerText : rawText;
    const recipientMatch = recipientText.match(/(?:^|\n)[^\S\n]*(?:ship to|enviar a|enviado a|enviar para|enviado para|env[ií]o a|destinatario|entregar a|entrega a)(?=\s|:|$)[^\S\n]*:?[^\S\n]*([^\n]*)/i);
    let recipient = recipientMatch?.[1]?.trim() || '';
    if (!recipient && recipientMatch) {
      const rest = recipientText.slice(recipientMatch.index + recipientMatch[0].length).split('\n').map(part => part.trim()).find(Boolean);
      recipient = rest || '';
    }
    recipient = recipient.replace(/[\s\u2304\u25be\u25bc]+$/u, '');
    if (!/^[\p{L}\p{M}][\p{L}\p{M} .’'\-]{0,79}$/u.test(recipient) ||
        /^(?:view|order|ship|deliver|total|buy|return|track|invoice|pedido|ver pedido|n[uú]mero de pedido|entregado|factura)\b/i.test(recipient)) recipient = '';
    return { date: day, label: `Amazon order placed ${day}`, url: invoiceUrl, buyer: recipient || null };
  });
}

function updateBuyerOptions(cards) {
  const select = $('buyer');
  const previous = select.value || 'all';
  const names = [...new Set(cards.filter(Boolean).map(card => card.buyer).filter(Boolean))].sort((a, b) => a.localeCompare(b));
  select.replaceChildren();
  const add = (value, label) => { const option = document.createElement('option'); option.value = value; option.textContent = label; select.append(option); };
  add('all', 'All recipients');
  names.forEach(name => add(`buyer:${name}`, name));
  add('unknown', 'Recipient not shown');
  select.value = [...select.options].some(option => option.value === previous) ? previous : 'all';
  $('buyer-note').textContent = names.length
    ? 'Amazon PDFs may include recipient names and addresses.'
    : 'No recipient names were found for the selected dates.';
}

function filterAmazonOrders(cards, from, to, recipient) {
  return cards.filter(row => row.date >= from && row.date <= to &&
    (recipient === 'all' || recipient === 'unknown' && !row.buyer ||
      recipient.startsWith('buyer:') && row.buyer === recipient.slice(6)));
}

function readAmazonNavigation() {
  try {
  if (document.readyState && document.readyState !== 'complete') return { pending: true };
  const isOrdersPath = path => /^(?:\/-\/[a-z]{2}(?:-[a-z]{2})?)?\/your-orders\/orders$/i.test(path) ||
    path === '/gp/css/order-history' || path === '/gp/your-account/order-history';
  const current = new URL(location.href);
  if (current.origin !== 'https://www.amazon.com' || !isOrdersPath(current.pathname))
    throw new Error('Open Amazon Your Orders before finding receipts.');
  const nextLinks = [...document.querySelectorAll('.a-pagination .a-last:not(.a-disabled) a[href]')];
  if (nextLinks.length > 1) throw new Error('Amazon pagination could not be identified safely.');
  let next = null;
  if (nextLinks.length) {
    const candidate = new URL(nextLinks[0].href, current.href);
    if (candidate.origin !== current.origin || !isOrdersPath(candidate.pathname))
      throw new Error('Amazon returned an unexpected next-page address.');
    for (const filter of ['timeFilter', 'orderFilter', 'householdMembers']) {
      const currentValue = current.searchParams.get(filter);
      const nextValue = candidate.searchParams.get(filter);
      if (currentValue && nextValue && currentValue !== nextValue)
        throw new Error('Amazon returned an unexpected next page.');
      if (currentValue && !nextValue) candidate.searchParams.set(filter, currentValue);
    }
    // Amazon uses both an offset (startIndex) and a zero-based page number.
    const key = candidate.searchParams.has('page') ? 'page' : 'startIndex';
    const currentIndex = Number(current.searchParams.get(key) || 0);
    const nextIndex = Number(candidate.searchParams.get(key));
    if (!candidate.searchParams.has(key) || !Number.isSafeInteger(currentIndex) ||
        !Number.isSafeInteger(nextIndex) || nextIndex <= currentIndex)
      throw new Error('Amazon returned an unexpected next page.');
    next = candidate.href;
  }
  return { current: current.href, next };
  } catch (error) {
    // Exceptions inside an injected script can arrive as a null result in Chrome.
    return { error: error.message || 'Could not read Amazon orders.' };
  }
}

async function waitForAmazonNavigation(tabId, previous = null, expected = null) {
  const deadline = Date.now() + 20000;
  do {
    let candidate;
    try { candidate = await execute(tabId, readAmazonNavigation); }
    catch { /* The tab may be navigating. */ }
    if (candidate?.error) throw new Error(candidate.error);
    if (candidate?.current && (!previous || candidate.current !== previous)) {
      const actual = new URL(candidate.current);
      if (!expected || actual.searchParams.get(expected.key) === expected.value) return candidate;
    }
    await new Promise(resolve => setTimeout(resolve, 250));
  } while (Date.now() < deadline);
  throw new Error('Amazon orders did not finish loading. Open Your Orders, wait for it to load, and try again.');
}

async function collectAmazonOrders(tabId, from = null, to = null, report = { pages: 0, unreadable: 0 }) {
  let navigation = await waitForAmazonNavigation(tabId);
  const original = navigation.current;
  let lastVisited = original;
  const seenPages = new Set();
  const seenOrders = new Set();
  const orders = [];
  const years = from && to
    ? Array.from({ length: Number(to.slice(0, 4)) - Number(from.slice(0, 4)) + 1 }, (_, index) => Number(to.slice(0, 4)) - index)
    : [null];
  if (years.length > 50) throw new Error('Choose a date range of 50 years or less.');
  try {
    for (const year of years) {
    if (year !== null) {
      const yearUrl = new URL(original);
      yearUrl.searchParams.set('timeFilter', `year-${year}`);
      yearUrl.searchParams.set('startIndex', '0');
      yearUrl.searchParams.delete('page');
      if (yearUrl.href !== navigation.current) {
        await chrome.tabs.update(tabId, { url: yearUrl.href });
        navigation = await waitForAmazonNavigation(tabId, navigation.current, { key: 'timeFilter', value: `year-${year}` });
        lastVisited = navigation.current;
      }
    }
    let yearPages = 0;
    let previousDate = null;
    let newestFirst = true;
    while (true) {
      if (seenPages.has(navigation.current)) throw new Error('Amazon repeated an orders page. Try again.');
      seenPages.add(navigation.current);
      const page = await execute(tabId, scanAmazonPage);
      if (!Array.isArray(page)) throw new Error('Amazon orders could not be read. Check your sign-in and try again.');
      if (yearPages > 0 && page.length === 0) throw new Error('Amazon did not load the next orders page.');
      yearPages++;
      report.pages++;
      for (const row of page) {
        if (!row) {
          report.unreadable++;
          newestFirst = false;
          continue;
        }
        if (previousDate && row.date > previousDate) newestFirst = false;
        previousDate = row.date;
        const id = row.url ? new URL(row.url).searchParams.get('orderID') : null;
        if (id && seenOrders.has(id)) continue;
        if (id) seenOrders.add(id);
        orders.push(row);
      }
      status.textContent = `Checking Amazon${year ? ` ${year}` : ''}, page ${yearPages}…`;
      // Your Orders lists newest purchases first. Keep orders on the boundary
      // page, then stop once it contains a purchase before the start date.
      if (!navigation.next || (from && newestFirst && previousDate && previousDate < from)) break;
      if (seenPages.size >= 100) throw new Error('Stopped after 100 pages. Narrow the period on Amazon.');
      const target = navigation.next;
      const targetUrl = new URL(target);
      const key = targetUrl.searchParams.has('page') ? 'page' : 'startIndex';
      const expected = { key, value: targetUrl.searchParams.get(key) };
      await chrome.tabs.update(tabId, { url: target });
      navigation = await waitForAmazonNavigation(tabId, navigation.current, expected);
      lastVisited = navigation.current;
    }
    }
    return orders;
  } finally {
    if (lastVisited !== original) {
      try {
        const current = await execute(tabId, readAmazonNavigation);
        if (current?.current === lastVisited) await chrome.tabs.update(tabId, { url: original });
      } catch { /* Leave the tab alone if it no longer shows the page we opened. */ }
    }
  }
}

async function refreshBuyers() {
  if (!sourceTabId || $('retailer').value !== 'amazon') {
    $('buyer-note').textContent = 'Connect Amazon and open Your Orders first.';
    return;
  }
  const from = $('from').value, to = $('to').value;
  if (!from || !to || from > to) {
    $('buyer-note').textContent = 'Choose a valid start and end date.';
    return;
  }
  setImportBusy(true);
  try {
    const tab = await activeCostcoTab();
    const report = { pages: 0, unreadable: 0 };
    const cards = await collectAmazonOrders(tab.id, from, to, report);
    const inRange = filterAmazonOrders(cards, from, to, 'all');
    updateBuyerOptions(inRange);
    status.textContent = amazonDiscoveryText(report, inRange.length) + ' Select a recipient, then choose Find receipts.';
  } catch (error) {
    $('buyer-note').textContent = error.message;
    status.textContent = '';
  } finally {
    setImportBusy(false);
  }
}

function amazonDiscoveryText(report, inRange) {
  return 'Checked ' + report.pages + ' Amazon page(s). Found ' + inRange + ' order(s) in range.' +
    (report.unreadable ? ' Skipped ' + report.unreadable + ' unreadable order card(s).' : '');
}

function tabLocation() { return { origin: location.origin, pathname: location.pathname }; }

async function activeCostcoTab() {
  if (!Number.isSafeInteger(sourceTabId) || sourceTabId <= 0)
    throw new Error('Open this importer using its button on the Costco page.');
  const tab = await chrome.tabs.get(sourceTabId);
  let page;
  try { page = await execute(tab.id, tabLocation); }
  catch { throw new Error('Connect the store and open its order history first.'); }
  const expected = $('retailer').value === 'amazon' ? 'www.amazon.com' : 'www.costco.com';
  if (page?.origin !== `https://${expected}`)
    throw new Error('Connect the selected retailer and open your order history first.');
  return tab;
}

async function execute(tabId, func, args = []) {
  const [{ result }] = await chrome.scripting.executeScript({ target: { tabId }, func, args });
  return result;
}

function validAppOrigin(value) {
  let url;
  try { url = new URL(value); } catch { throw new Error('Open the importer from the Group Split receipts inbox.'); }
  const local = url.hostname === 'localhost' || url.hostname === '127.0.0.1';
  const production = url.hostname === 'groupsplit.sussman.win' && url.protocol === 'https:' && !url.port;
  if ((!production && !(local && (url.protocol === 'https:' || url.protocol === 'http:'))) || url.username || url.password)
    throw new Error('Open the extension from the official GroupSplit site or your local app.');
  return url.origin;
}

// Executes only in the explicitly connected GroupSplit tab. The browser sends the
// existing session cookie; no credentials or access tokens enter the extension.
async function uploadToInbox(base64, fileName, expectedOrigin, receipt = null) {
  if (location.origin !== expectedOrigin || location.pathname !== '/inbox/receipts')
    throw new Error('The GroupSplit tab navigated away.');
  const bytes = Uint8Array.from(atob(base64), char => char.charCodeAt(0));
  const body = new FormData();
  body.append('file', new Blob([bytes], { type: 'application/pdf' }), fileName);
  const receiptId = crypto.randomUUID();
  const response = await fetch(`/api/receipts/inbox/${receiptId}/files`, {
    method: 'POST', body, credentials: 'same-origin', redirect: 'error'
  });
  if (!response.ok) {
    let detail = '';
    try { const problem = await response.json(); detail = problem.detail || problem.title || ''; } catch { }
    return { ok: false, status: response.status, detail };
  }
  const attachment = await response.json();
  if (typeof attachment.receiptId !== 'string' ||
      !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(attachment.receiptId))
    return { ok: false, status: response.status, detail: 'Unexpected response from the receipt inbox.' };
  const alreadyImported = attachment.receiptId.toLowerCase() !== receiptId.toLowerCase();
  if (!alreadyImported) {
    if (!receipt) {
      try {
        if (typeof attachment.id !== 'string' ||
            !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(attachment.id))
          throw new Error('The inbox did not return an attachment ID.');
        const transcription = await fetch(`/api/receipts/inbox/${receiptId}/files/${attachment.id}/transcribe`, {
          method: 'POST', credentials: 'same-origin', redirect: 'error'
        });
        if (!transcription.ok) {
          const problem = await transcription.json().catch(() => ({}));
          throw new Error(problem.detail || problem.title || `Reading receipt failed (${transcription.status}).`);
        }
        const draft = await transcription.json();
        if (!draft.receipt || !Array.isArray(draft.receipt.items))
          throw new Error('GroupSplit did not return readable receipt details.');
        receipt = draft.receipt;
      } catch (error) {
        return { ok: true, alreadyImported: false, detailsSaved: false,
          detail: 'Uploaded, but automatic reading failed. ' + error.message };
      }
    }
    try {
      const saved = await fetch(`/api/receipts/inbox/${receiptId}/extracted`, {
        method: 'PUT', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(receipt), credentials: 'same-origin', redirect: 'error'
      });
      if (!saved.ok) {
        let detail = '';
        try { const problem = await saved.json(); detail = problem.detail || problem.title || ''; } catch { }
        return { ok: true, alreadyImported: false, detailsSaved: false, detail: detail || `Saving details failed (${saved.status}).` };
      }
      return { ok: true, alreadyImported: false, detailsSaved: true };
    } catch {
      return { ok: true, alreadyImported: false, detailsSaved: false, detail: 'Could not save receipt details. The source file is in your inbox.' };
    }
  }
  return { ok: true, alreadyImported, detailsSaved: false };
}

async function connectInbox() {
  try {
    if (!Number.isSafeInteger(inboxTabId) || inboxTabId <= 0) return;
    await chrome.tabs.get(inboxTabId);
    let page;
    try { page = await execute(inboxTabId, tabLocation); }
    catch { throw new Error('Refresh the Group Split inbox and reopen the importer from that page.'); }
    const origin = validAppOrigin(page?.origin);
    if (page?.pathname !== '/inbox/receipts') throw new Error('Open the extension from the Group Split receipts inbox.');
    inbox = { origin, tabId: inboxTabId };
    $('connection').textContent = 'Inbox connected. Keep the GroupSplit tab open and signed in.';
    $('back-to-inbox').hidden = embedded;
  } catch (error) { $('connection').textContent = error.message; }
}
if (embedded) {
  chrome.runtime.sendMessage({ type: 'embedded-context' }, context => {
    if (chrome.runtime.lastError || !context?.tabId) {
      $('connection').textContent = 'Open the importer from the GroupSplit receipts inbox.';
      return;
    }
    inboxTabId = context.tabId;
    if (Number.isInteger(context.windowId)) parentWindow = { windowId: context.windowId };
    window.parent.postMessage({ type: 'groupsplit-importer-ready' }, '*');
    void connectInbox();
  });
} else void connectInbox();

$('back-to-inbox').addEventListener('click', async () => {
  if (inbox) {
    const tab = await chrome.tabs.update(inbox.tabId, { active: true });
    if (Number.isInteger(tab.windowId)) await chrome.windows.update(tab.windowId, { focused: true });
  }
});

$('retailer').addEventListener('change', () => {
  sourceTabId = 0;
  $('store-state').hidden = true;
  selection = null;
  $('receipts').replaceChildren();
  $('download').hidden = true;
  $('buyer-field').hidden = $('retailer').value !== 'amazon';
  $('buyer-guidance').hidden = $('retailer').value !== 'amazon';
  $('buyer').value = 'all';
  status.textContent = '';
  $('privacy-note').textContent = $('retailer').value === 'amazon'
    ? 'Amazon PDFs contain the full printable receipt and may include addresses, payment details and order numbers.'
    : costcoPrivacyMessage;
});

$('refresh-buyers').addEventListener('click', refreshBuyers);

$('buyer').addEventListener('change', () => {
  selection = null;
  $('receipts').replaceChildren();
  $('download').hidden = true;
  status.textContent = 'Select Find receipts to apply the recipient filter.';
});

for (const id of ['from', 'to']) for (const event of ['input', 'change']) $(id).addEventListener(event, () => {
  selection = null;
  $('receipts').replaceChildren();
  $('download').hidden = true;
  if ($('retailer').value === 'amazon') {
    updateBuyerOptions([]);
    $('buyer-note').textContent = 'Check recipients to scan the selected dates.';
  }
  status.textContent = 'Select Find receipts to apply the date range.';
});

function setImportBusy(busy) {
  for (const id of ['retailer', 'from', 'to', 'buyer', 'open-costco', 'refresh-buyers', 'find', 'download'])
    $(id).disabled = busy;
}

// Only report a signed-in state when the retailer exposes an orders view.
function retailerSignInState(retailer) {
  if (document.readyState !== 'complete') return 'loading';
  const path = location.pathname.toLowerCase();
  const password = document.querySelector('input[type="password"]');
  if (password || /\/(?:ap\/signin|signin|sign-in|login)(?:\/|$)/i.test(path)) return 'signin';
  if (retailer === 'amazon') {
    const ordersPath = /^(?:\/-\/[a-z]{2}(?:-[a-z]{2})?)?\/your-orders\/orders\/?$/i.test(path) ||
      /^\/gp\/(?:css\/order-history|your-account\/order-history)/i.test(path);
    if (ordersPath && (document.querySelector('.js-order-card, [id="orderCard"], .order-card, .a-box-group.order, select[name="timeFilter"]') ||
      /\b(?:no orders|no order history)\b/i.test(document.body?.innerText || ''))) return 'ready';
  } else if (location.hash.includes('ordersandpurchases') &&
      /\b(?:orders\s*(?:&|and)\s*purchases|warehouse|online orders)\b/i.test(document.body?.innerText || '')) return 'ready';
  return 'unknown';
}

function showStoreState(title, detail, showButton = false) {
  $('store-state-title').textContent = title;
  $('store-state-detail').textContent = detail;
  $('show-store').hidden = !showButton;
  $('store-state').hidden = false;
}

$('show-store').addEventListener('click', async () => {
  try {
    const tab = await chrome.tabs.update(sourceTabId, { active: true });
    if (Number.isInteger(tab.windowId)) await chrome.windows.update(tab.windowId, { focused: true });
  } catch { showStoreState('Store tab unavailable', 'Connect the store again to open a new tab.'); }
});

$('open-costco').addEventListener('click', async () => {
  try {
    const amazon = $('retailer').value === 'amazon';
    const origin = amazon ? 'https://www.amazon.com' : 'https://www.costco.com';
    if (!await chrome.permissions.request({ origins: [`${origin}/*`] })) throw new Error('Retailer access was not granted.');
    const tabs = await chrome.tabs.query({ url: `${origin}/*` });
    const ordersUrl = amazon ? `${origin}/your-orders/orders`
      : `${origin}/myaccount/#/app/4900eb1f-0c10-4bd9-99c3-c59e6c1ecebf/ordersandpurchases`;
    const existing = tabs.find(tab => amazon
      ? tab.url?.startsWith(`${origin}/your-orders/orders`)
      : tab.url === ordersUrl);
    const tab = existing || await chrome.tabs.create({ ...parentWindow, url: ordersUrl, active: false });
    sourceTabId = tab.id;
    const retailer = $('retailer').value;
    showStoreState('Checking your store connection…', 'The store is open in a background tab.');
    let state = 'loading';
    for (let attempt = 0; attempt < 20 && (state === 'loading' || state === 'unknown'); attempt++) {
      if (sourceTabId !== tab.id || $('retailer').value !== retailer) return;
      state = await execute(tab.id, retailerSignInState, [retailer]).catch(() => 'loading');
      if (state === 'loading' || state === 'unknown') await new Promise(resolve => setTimeout(resolve, 500));
    }
    if (sourceTabId !== tab.id || $('retailer').value !== retailer) return;
    if (state === 'signin') showStoreState('Sign in to continue', 'Open the store tab, sign in, then come back and select Connect store again.', true);
    else if (state === 'ready') showStoreState('Store ready', 'You are signed in. Choose a date range and find receipts.');
    else showStoreState('Check your store sign-in', 'We could not confirm your sign-in. Open the store tab, sign in if needed, then come back and select Connect store again.', true);
  } catch (error) { showStoreState('Could not connect store', error.message); }
});

// Only activate explicit pagination controls; never infer actions from position.
function clickPagination(direction) {
  const expression = direction === 'next'
    ? /^(?:next page|go to next page|página siguiente|ir a la página siguiente|próxima página|ir para a próxima página)$/i
    : /^(?:previous page|go to previous page|página anterior|ir a la página anterior|ir para a página anterior)$/i;
  const labelled = [...document.querySelectorAll('button, a, [role="button"]')]
    .filter(element => element.getClientRects().length && expression.test((element.getAttribute('aria-label') || element.textContent).trim()));
  const icon = direction === 'next' ? 'NavigateNextIcon' : 'NavigateBeforeIcon';
  const iconButtons = [...document.querySelectorAll(`[data-testid="${icon}"]`)]
    .map(element => element.closest?.('button')).filter(button => button?.getClientRects().length);
  const candidates = [...new Set([...labelled, ...iconButtons])];
  if (candidates.length > 1) throw new Error(`Multiple ${direction} page controls found. Narrow the list and try again.`);
  const button = candidates[0];
  if (!button || button.disabled || button.getAttribute('aria-disabled') === 'true' || button.classList?.contains('Mui-disabled')) return false;
  button.click();
  return true;
}

function costcoPeriods() {
  const select = document.querySelector('select#Showing');
  if (!select) return { current: null, options: [] };
  return { current: select.value, options: [...select.options].filter(option => option.value && !option.disabled)
    .map(option => ({ value: option.value, label: option.textContent.trim() })) };
}

function chooseCostcoPeriod(value) {
  const select = document.querySelector('select#Showing');
  if (!select || ![...select.options].some(option => option.value === value && !option.disabled))
    return { error: 'Costco’s date periods changed. Find receipts again.' };
  if (select.value === value) return { changed: false };
  const setter = Object.getOwnPropertyDescriptor(HTMLSelectElement.prototype, 'value')?.set;
  if (setter) setter.call(select, value); else select.value = value;
  select.dispatchEvent(new Event('input', { bubbles: true }));
  select.dispatchEvent(new Event('change', { bubbles: true }));
  return { changed: true };
}

function costcoPageState() {
  const visible = element => !!element.getClientRects().length;
  const loading = [...document.querySelectorAll('[role="progressbar"], [aria-busy="true"], .MuiCircularProgress-root')].some(visible);
  const empty = [...document.querySelectorAll('[role="status"], [role="heading"], h1, h2, h3, h4, h5, h6, p, td, div, span')]
    .some(element => visible(element) &&
      /^(?:no (?:(?:online )?orders|receipts|purchases|results)(?: found| are available for the selected date range)?|no (?:hay|se encontraron) (?:pedidos|compras|recibos|resultados)(?: disponibles)?(?: para el (?:rango|intervalo) de fechas seleccionado)?|n[aã]o (?:h[aá]|foram encontrados) (?:pedidos|compras|recibos|resultados)(?: dispon[ií]veis)?(?: para o intervalo de datas selecionado)?)[.!]?$/i
        .test(element.textContent.trim()));
  return { loading, empty };
}

function periodOverlaps(period, from, to) {
  const text = `${period.label} ${period.value}`;
  const years = [...text.matchAll(/\b(20\d{2})\b/g)].map(match => Number(match[1]));
  if (!years.length) return true;
  const quarter = text.match(/\b(?:Q([1-4])|([1-4])(?:st|nd|rd|th)\s+quarter)\b/i);
  if (quarter && years.every(year => year === years[0])) {
    const month = (Number(quarter[1] || quarter[2]) - 1) * 3;
    const start = new Date(Date.UTC(years[0], month, 1)).toISOString().slice(0, 10);
    const end = new Date(Date.UTC(years[0], month + 3, 0)).toISOString().slice(0, 10);
    return start <= to && end >= from;
  }
  return Math.max(...years) >= Number(from.slice(0, 4)) && Math.min(...years) <= Number(to.slice(0, 4));
}

async function switchCostcoPeriod(tabId, value, before, readRows = pageRows) {
  const result = await execute(tabId, chooseCostcoPeriod, [value]);
  if (result?.error) throw new Error(result.error);
  if (!result) throw new Error('Could not select the Costco date period.');
  if (!result.changed) return before;
  const previous = JSON.stringify(before);
  const deadline = Date.now() + 20000;
  while (Date.now() < deadline) {
    await new Promise(resolve => setTimeout(resolve, 300));
    const state = await execute(tabId, costcoPageState);
    if (!state || state.loading) continue;
    const rows = await readRows(tabId);
    if (rows && ((rows.length && JSON.stringify(rows) !== previous) || (state.empty && !rows.length))) return rows;
  }
  throw new Error('Costco did not finish loading the selected period. Try again.');
}

async function pageRows(tabId) {
  const rows = await execute(tabId, scanPage);
  if (!Array.isArray(rows)) throw new Error('Could not read Costco receipts. Open the warehouse purchases view and try again.');
  if (rows.some(row => !row)) throw new Error('Some visible receipts could not be identified safely.');
  return rows;
}

function selectCostcoView(online) {
  if (location.origin !== 'https://www.costco.com' ||
      !location.hash.endsWith('/ordersandpurchases')) return false;
  const label = online ? /^(?:online(?: orders)?|en l[ií]nea|pedidos (?:en l[ií]nea|online))$/i
    : /^(?:(?:in[\s-]+)?warehouse(?: purchases)?|(?:compras )?(?:en (?:la )?|em |no )?(?:almac[eé]n|bodega|tienda|armaz[eé]m|loja))$/i;
  const tabs = [...document.querySelectorAll('[role="tab"]')].filter(tab =>
    tab.getClientRects().length && label.test(tab.textContent.trim()));
  if (tabs.length !== 1) return false;
  const [tab] = tabs;
  if (tab.getAttribute('aria-selected') === 'true') return true;
  tab.click();
  return false;
}

async function openCostcoView(tabId, online = false) {
  const deadline = Date.now() + 10000;
  while (Date.now() < deadline) {
    if (await execute(tabId, selectCostcoView, [online]).catch(() => false)) return;
    await new Promise(resolve => setTimeout(resolve, 300));
  }
  throw new Error('Could not open the selected Costco view. Finish signing in and try Find receipts again.');
}

async function waitForCostcoPage(tabId, readRows = pageRows) {
  const deadline = Date.now() + 20000;
  while (Date.now() < deadline) {
    const state = await execute(tabId, costcoPageState);
    if (state && !state.loading) {
      const rows = await readRows(tabId);
      if (rows && (rows.length || state.empty)) return rows;
    }
    await new Promise(resolve => setTimeout(resolve, 300));
  }
  throw new Error('Costco receipts did not finish loading. Check the selected Costco view and try again.');
}

async function changePage(tabId, direction, before) {
  if (!await execute(tabId, clickPagination, [direction])) return null;
  const signature = JSON.stringify(before);
  const deadline = Date.now() + 10000;
  while (Date.now() < deadline) {
    await new Promise(resolve => setTimeout(resolve, 250));
    const state = await execute(tabId, costcoPageState);
    if (!state || state.loading) continue;
    const rows = await pageRows(tabId);
    if ((rows.length || state.empty) && JSON.stringify(rows) !== signature) return rows;
  }
  throw new Error('The receipt page did not change. Check Costco and try again.');
}

function openReceipt(index, expectedLabel) {
  if (location.origin !== 'https://www.costco.com') return { error: 'Return to Costco and find receipts again.' };
  const visible = element => !!(element.offsetWidth || element.offsetHeight || element.getClientRects().length);
  const actions = [...document.querySelectorAll('button, a, [role="button"]')]
    .filter(element => visible(element) && /^(?:view receipt|ver recibo|ver comprobante|visualizar recibo|ver comprovante)$/i.test(element.textContent.trim()));
  const button = actions[index];
  let row = button?.parentElement;
  let found = false;
  while (row && row !== document.body) {
    const actionsInRow = [...row.querySelectorAll('button, a, [role="button"]')]
      .filter(element => visible(element) && /^(?:view receipt|ver recibo|ver comprobante|visualizar recibo|ver comprovante)$/i.test(element.textContent.trim()));
    const text = row.innerText.replace(/\s+/g, ' ').trim();
    if (actionsInRow.length === 1 && [...text.matchAll(/\b\d{1,2}\/\d{1,2}\/\d{4}\b/g)].length === 1 && text.length < 1200) {
      found = text.slice(0, 240) === expectedLabel;
      break;
    }
    row = row.parentElement;
  }
  if (!found) return { error: 'The receipt list changed. Find receipts again.' };
  button.click();
  return { opened: true };
}

async function prepareReceipt() {
  const until = Date.now() + 10000;
  while (Date.now() < until) {
    if (location.origin !== 'https://www.costco.com') return { error: 'Return to Costco and find receipts again.' };
    const controls = 'button, a, [role="button"]';
    const label = element => (element.innerText || element.textContent || element.getAttribute('aria-label') || '')
      .replace(/\s+/g, ' ').trim();
    const visible = element => element.getClientRects().length && getComputedStyle(element).visibility === 'visible';
    const printLabel = /^(?:print receipt|imprimir (?:recibo|comprobante|comprovante))$/i;
    const closeLabel = /^(?:close|cerrar|fechar)$/i;
    const receiptTitle = /^(?:gas station|in[\s-]+warehouse) receipt$/i;
    const hasReceiptBody = panel => /\b(?:total sale|gallons|invoice|subtotal|items sold|member|membre|s[oó]cio)\b/i.test(panel.innerText) ||
      [...panel.querySelectorAll('iframe, object, embed, canvas, img')].some(visible);
    const populated = panel => {
      if (!visible(panel) || panel.closest('[aria-hidden="true"]') || !hasReceiptBody(panel)) return false;
      const actions = [...panel.querySelectorAll(controls)].filter(visible).map(label);
      return actions.some(action => printLabel.test(action)) && actions.some(action => closeLabel.test(action));
    };
    const dialogs = [...document.querySelectorAll('.modal.show .modal-content, [role="dialog"], dialog[open]')]
      .filter(populated);
    if (!dialogs.length) {
      const printButtons = [...document.querySelectorAll(controls)].filter(element =>
        visible(element) && printLabel.test(label(element)));
      const headings = [...document.querySelectorAll('h1, h2, h3, h4, h5, h6, [role="heading"], div, span')]
        .filter(element => visible(element) && receiptTitle.test(label(element)));
      const panels = new Set();
      for (const anchor of [...headings, ...printButtons]) {
        for (let panel = anchor.parentElement; panel && panel !== document.body; panel = panel.parentElement) {
          const hasClose = [...panel.querySelectorAll(controls)].some(element =>
            visible(element) && closeLabel.test(label(element)));
          const containsOrderList = [...panel.querySelectorAll(controls)].some(element =>
            visible(element) && /^(?:view receipt|ver recibo|ver comprobante|visualizar recibo|ver comprovante)$/i.test(label(element)));
          if (containsOrderList) break;
          const hasReceiptTitle = panel.innerText.split('\n').some(line => receiptTitle.test(line.trim()));
          if (hasClose && hasReceiptBody(panel) && hasReceiptTitle) {
            panels.add(panel);
            break;
          }
        }
      }
      dialogs.push(...[...panels].filter(panel =>
        ![...panels].some(other => other !== panel && panel.contains(other))));
    }
    const innermost = dialogs.filter(panel => !dialogs.some(other => other !== panel && panel.contains(other)));
    if (innermost.length === 1) {
      const dialog = innermost[0];
      dialog.setAttribute('data-groupsplit-print', '');
      const style = document.createElement('style');
      style.id = 'groupsplit-receipt-print-style';
      style.textContent = '@media print {' +
        'html, body, body :has([data-groupsplit-print]), [data-groupsplit-print] {' +
        'position: static !important; transform: none !important; overflow: visible !important;' +
        'height: auto !important; max-height: none !important; width: auto !important; max-width: none !important; margin: 0 !important; }' +
        'body *:not([data-groupsplit-print], [data-groupsplit-print] *, :has([data-groupsplit-print])) { display: none !important; }' +
        '[data-groupsplit-print], [data-groupsplit-print] * { visibility: visible !important; }' +
        '[data-groupsplit-print] :is(button, [role="button"]) { display: none !important; } }';
      document.head.append(style);
      return { prepared: true };
    }
    await new Promise(resolve => setTimeout(resolve, 200));
  }
  return { error: 'Could not identify the opened Costco receipt for printing. Close any open receipt and try again.' };
}

function verifyPreparedCostcoReceipt() {
  return location.origin === 'https://www.costco.com' &&
    document.querySelectorAll('[data-groupsplit-print]').length === 1 &&
    !!document.getElementById('groupsplit-receipt-print-style');
}

async function cleanupReceipt() {
  if (location.origin !== 'https://www.costco.com') return;
  document.getElementById('groupsplit-receipt-print-style')?.remove();
  const dialog = document.querySelector('[data-groupsplit-print]');
  dialog?.removeAttribute('data-groupsplit-print');
  if (dialog) {
    const close = [...dialog.querySelectorAll('button, [role="button"]')]
      .find(button => /^(?:close|cerrar|fechar)$/i.test((button.getAttribute('aria-label') || button.textContent).trim()));
    if (close) close.click();
    else document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', code: 'Escape', bubbles: true }));
    const deadline = Date.now() + 5000;
    while (dialog.isConnected && dialog.getClientRects().length && !dialog.closest('[aria-hidden="true"]')) {
      if (Date.now() >= deadline) return { closed: false };
      await new Promise(resolve => setTimeout(resolve, 100));
    }
  }
  return { closed: true };
}

async function saveReceiptFile(data, fileName, receipt = null) {
  await chrome.tabs.get(inbox.tabId);
  const page = await execute(inbox.tabId, tabLocation);
  if (page?.origin !== inbox.origin || page.pathname !== '/inbox/receipts')
    throw new Error('Return to your GroupSplit receipts inbox and start the import again.');
  await chrome.downloads.download({ url: `data:application/pdf;base64,${data}`, filename: `GroupSplit/${fileName}`, conflictAction: 'uniquify' });
  const [{ result }] = await chrome.scripting.executeScript({
    target: { tabId: inbox.tabId }, world: 'ISOLATED', func: uploadToInbox, args: [data, fileName, inbox.origin, receipt]
  });
  if (!result?.ok) throw new Error(result?.detail || `Upload failed (${result?.status || 'no response'}).`);
  return result;
}

function verifyAmazonInvoice(expectedOrderId) {
  const url = new URL(location.href);
  if (url.origin !== 'https://www.amazon.com' || url.username || url.password ||
      !url.pathname.endsWith('/summary/print.html') || url.searchParams.get('orderID') !== expectedOrderId ||
      document.readyState !== 'complete' || document.querySelector('input[type="password"]')) return false;
  const body = document.body?.innerText || '';
  const orderIds = body.match(/\b\d{3}-\d{7}-\d{7}\b/g) || [];
  const hasTotal = /(?:^|\n)\s*(?:grand\s+total|order\s+total|total(?!\s+(?:antes|before|sem)\b))\b/i.test(body);
  return hasTotal && orderIds.length > 0 && orderIds.every(id => id === expectedOrderId);
}

function setAmazonPrintStyle(enabled) {
  const id = 'groupsplit-amazon-print-style';
  document.getElementById(id)?.remove();
  if (!enabled) return;
  if (location.origin !== 'https://www.amazon.com' || !location.pathname.endsWith('/summary/print.html')) return;
  const style = document.createElement('style');
  style.id = id;
  style.textContent = '@media print {' +
    'body { margin: 0 auto !important; color: #171717; background: #fff; }' +
    'tr { break-inside: avoid; }' +
    '}';
  document.head.append(style);
}

function amazonPrintWidth() {
  return Math.max(document.documentElement.scrollWidth, document.body.scrollWidth);
}

function privateCostcoOnlineReceipt(date, amounts, reference, merchant = 'Costco') {
  const rewards = amounts?.rewards ?? 0;
  if (!/^\d{4}-\d{2}-\d{2}$/.test(date) || !amounts ||
      !['items', 'shipping', 'tax', 'total'].every(key => Number.isSafeInteger(amounts[key]) && amounts[key] >= 0 && amounts[key] <= 100000000) ||
      !Number.isSafeInteger(rewards) || rewards < 0 || rewards > amounts.items + amounts.shipping ||
      !/^[a-f0-9]{32}$/.test(reference) ||
      !Array.isArray(amounts.products) || amounts.products.length < 1 || amounts.products.length > 100 ||
      !amounts.products.every(product => typeof product.name === 'string' && product.name.length > 0 && product.name.length <= 128 &&
        Number.isSafeInteger(product.quantity) && product.quantity > 0 && product.quantity <= 1000 &&
        Number.isSafeInteger(product.unitPrice) && product.unitPrice >= 0 && product.unitPrice <= 100000000))
    throw new Error('The invoice contains invalid receipt data.');
  const money = cents => (cents / 100).toFixed(2);
  const lineTotal = amounts.products.reduce((sum, product) => sum + product.unitPrice * product.quantity, 0);
  if (lineTotal !== amounts.items)
    throw new Error('Invoice could not be reconciled. Item prices × quantities add up to US$' + money(lineTotal) +
      ', but the item subtotal is US$' + money(amounts.items) + '.');
  const subtotal = amounts.items + amounts.shipping - rewards;
  const calculatedTotal = subtotal + amounts.tax;
  if (calculatedTotal !== amounts.total)
    throw new Error('Invoice could not be reconciled. Items (US$' + money(amounts.items) +
      '), shipping (US$' + money(amounts.shipping) + '), tax (US$' + money(amounts.tax) +
      '), and rewards credit (−US$' + money(rewards) +
      ') add up to US$' + money(calculatedTotal) + ', but the order total is US$' + money(amounts.total) + '.');
  const lines = [`${merchant} - private order summary`, `Date: ${date}`, 'Items:'];
  for (const product of amounts.products) {
    const printable = product.name.normalize('NFKD').replace(/[^\x20-\x7e]/g, '?');
    const pieces = printable.match(/.{1,72}(?:\s|$)|.{1,72}/g) || [printable];
    lines.push(...pieces.map(piece => piece.trim()));
    lines.push(`  ${product.quantity} x $${money(product.unitPrice)} = $${money(product.unitPrice * product.quantity)}`);
  }
  lines.push(
    `Items subtotal: $${money(amounts.items)}`, `Shipping: $${money(amounts.shipping)}`,
    `Tax: $${money(amounts.tax)}`, ...(rewards ? [`Rewards Points: -$${money(rewards)}`] : []),
    `Total: $${money(amounts.total)}`,
    'Customer and payment details omitted.', `Reference: ${reference}`
  );
  const items = amounts.products.map(product => ({ name: product.name, quantity: product.quantity,
    unitPrice: product.unitPrice / 100, totalPrice: product.unitPrice * product.quantity / 100 }));
  if (amounts.shipping) items.push({ name: 'Shipping', quantity: 1, unitPrice: amounts.shipping / 100, totalPrice: amounts.shipping / 100 });
  if (rewards) items.push({ name: 'Rewards Points', quantity: 1, unitPrice: -rewards / 100, totalPrice: -rewards / 100 });
  return {
    pdf: renderReceiptPdf(lines),
    receipt: { merchantName: merchant, purchaseDate: date, subtotal: subtotal / 100,
      tax: amounts.tax / 100, tip: 0, total: amounts.total / 100, items }
  };
}

function renderReceiptPdf(lines, leftMargin = 36) {
  const pageHeight = Math.max(792, 72 + lines.length * 18);
  if (pageHeight > 14000) throw new Error('This receipt has too many item lines to save safely.');
  const escape = text => text.replaceAll('−', '-').replaceAll('’', "'").replace(/[^\x20-\x7e\xa0-\xff]/g, '?').replace(/[\\()]/g, '\\$&');
  const stream = lines.map((line, index) => `BT /F1 11 Tf ${leftMargin} ${pageHeight - 36 - index * 18} Td (${escape(line)}) Tj ET`).join('\n');
  const objects = [
    '<< /Type /Catalog /Pages 2 0 R >>',
    '<< /Type /Pages /Kids [3 0 R] /Count 1 >>',
    `<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 ${pageHeight}] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>`,
    '<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>',
    `<< /Length ${stream.length} >>\nstream\n${stream}\nendstream`
  ];
  let pdf = '%PDF-1.4\n';
  const offsets = [0];
  objects.forEach((object, index) => { offsets.push(pdf.length); pdf += `${index + 1} 0 obj\n${object}\nendobj\n`; });
  const xref = pdf.length;
  pdf += `xref\n0 ${offsets.length}\n0000000000 65535 f \n`;
  pdf += offsets.slice(1).map(offset => `${String(offset).padStart(10, '0')} 00000 n \n`).join('');
  pdf += `trailer\n<< /Size ${offsets.length} /Root 1 0 R >>\nstartxref\n${xref}\n%%EOF\n`;
  return btoa(pdf);
}

function scanCostcoOnline() {
  const panel = document.querySelector('#simple-tabpanel-0');
  if (!panel?.getClientRects().length) return null;
  const language = document.documentElement.lang || '';
  const dayFirst = /^(?:es|pt)(?:-|$)/i.test(language) && !/-US$/i.test(language);
  return [...panel.querySelectorAll('a[id^="orderNumber_"]')].map(link => {
    const id = link.id.slice('orderNumber_'.length);
    const url = new URL(link.href);
    if (!/^\d+$/.test(id) || url.origin !== location.origin || url.username || url.password ||
        url.pathname !== location.pathname ||
        url.hash !== location.hash.replace(/ordersandpurchases$/, 'orderdetails/' + id)) return null;
    for (let card = link.parentElement; card && card !== panel; card = card.parentElement) {
      const date = card.innerText.match(/\b(\d{1,2})\/(\d{1,2})\/(\d{4})\b/);
      const total = card.innerText.match(/\bTotal\s*:?\s*(?:US\s*)?\$\s*([\d.,]+[.,]\d{2})(?![\d.,])/i);
      if (date && total) {
        const [, first, second, year] = date;
        const useDayFirst = Number(first) > 12 || (Number(second) <= 12 && dayFirst);
        const [month, dayNumber] = useDayFirst ? [second, first] : [first, second];
        const purchaseDate = new Date(Date.UTC(Number(year), Number(month) - 1, Number(dayNumber)));
        if (purchaseDate.getUTCMonth() + 1 !== Number(month) || purchaseDate.getUTCDate() !== Number(dayNumber)) return null;
        const day = purchaseDate.toISOString().slice(0, 10);
        return { id, url: url.href, date: day, label: 'Costco online order ' + day,
          total: Number(total[1].replace(/[,.]/g, '')) };
      }
    }
    return null;
  });
}

function readCostcoOnlineSummary(expectedUrl) {
  if (location.href !== expectedUrl) return null;
  const toggle = [...document.querySelectorAll('button')].find(button =>
    button.getClientRects().length && /^(?:show details|mostrar detalles|ver detalles|mostrar detalhes|ver detalhes)$/i.test(button.innerText.trim()));
  if (toggle) {
    toggle.click();
    return null;
  }
  const cents = value => Number(value.replace(/[,.]/g, ''));
  const money = '(?:US\\s*)?\\$\\s*([\\d.,]+[.,]\\d{2})(?![\\d.,])';
  const summary = document.body.innerText.match(new RegExp(
    'Subtotal\\s*:?\\s*' + money + '\\s+(?:Shipping|Env[ií]o|Frete)\\s*:?\\s*' + money +
    '\\s+(?:Tax|Impuestos?|Impostos?)\\s*:?\\s*' + money + '\\s+Total\\s*:?\\s*' + money, 'i'));
  if (!summary) return null;
  const products = [];
  for (const link of document.querySelectorAll('a[href*=".product."]')) {
    if (!link.getClientRects().length || !link.innerText.trim()) continue;
    for (let item = link.parentElement; item && item !== document.body; item = item.parentElement) {
      const details = item.innerText.match(new RegExp(
        '(?:Item|Art[ií]culo|Produto)\\s+\\d+\\s+' + money + '\\s+(?:Quantity|Cantidad|Quantidade)\\s*:?\\s*(\\d+)\\b', 'i'));
      if (!details) continue;
      if (item.querySelectorAll('a[href*=".product."]').length > 1) break;
      products.push({ name: link.innerText.trim(), unitPrice: cents(details[1]), quantity: Number(details[2]) });
      break;
    }
  }
  return { items: cents(summary[1]), shipping: cents(summary[2]), tax: cents(summary[3]),
    total: cents(summary[4]), products };
}

async function collectCostcoOnlineOrders(tabId, from, to) {
  await openCostcoView(tabId, true);
  const readRows = id => execute(id, scanCostcoOnline);
  let rows = await waitForCostcoPage(tabId, readRows);
  const available = await execute(tabId, costcoPeriods);
  if (!available?.options.length) throw new Error('Could not read Costco Online date periods.');
  const periods = available.options.filter(period => periodOverlaps(period, from, to));
  const matches = new Map();
  try {
    for (const period of periods) {
      status.textContent = 'Checking Costco Online: ' + period.label;
      rows = await switchCostcoPeriod(tabId, period.value, rows, readRows);
      if (rows.some(order => !order)) throw new Error('Could not read Costco online orders for ' + period.label + '.');
      for (const order of rows) {
        if (order.date >= from && order.date <= to) matches.set(order.id, order);
      }
    }
  } finally {
    if (available.current) await execute(tabId, chooseCostcoPeriod, [available.current]).catch(() => {});
  }
  return [...matches.values()];
}

async function prepareCostcoOnlineOrder(row, workspace) {
  if (workspace.tabId) await chrome.tabs.update(workspace.tabId, { url: row.url });
  else workspace.tabId = (await chrome.tabs.create({ url: row.url, active: false })).id;
  const deadline = Date.now() + 20000;
  while (Date.now() < deadline) {
    const amounts = await execute(workspace.tabId, readCostcoOnlineSummary, [row.url]).catch(() => null);
    if (amounts?.products.length) {
      if (amounts.total !== row.total) throw new Error('The Costco list and detail totals differ.');
      const digest = await crypto.subtle.digest('SHA-256', new TextEncoder().encode('costco:' + row.id));
      const reference = [...new Uint8Array(digest).slice(0, 16)].map(byte => byte.toString(16).padStart(2, '0')).join('');
      return privateCostcoOnlineReceipt(row.date, amounts, reference, 'Costco');
    }
    await new Promise(resolve => setTimeout(resolve, 300));
  }
  throw new Error('Costco did not provide readable online order details.');
}

async function prepareAmazonInvoice(row, workspace) {
  if (!row.url) throw new Error('Amazon has no usable printable invoice for this order.');
  const target = new URL(row.url);
  if (target.origin !== 'https://www.amazon.com' || target.username || target.password || !target.pathname.endsWith('/summary/print.html'))
    throw new Error('The invoice address is not a supported Amazon address.');
  const orderId = target.searchParams.get('orderID');
  if (!/^\d{3}-\d{7}-\d{7}$/.test(orderId || '')) throw new Error('The Amazon order could not be identified safely.');
  if (!workspace.tabId) workspace.tabId = (await chrome.tabs.create({ ...parentWindow, url: target.href, active: false })).id;
  else await chrome.tabs.update(workspace.tabId, { url: target.href });
  const deadline = Date.now() + 20000;
  let ready = false;
  while (Date.now() < deadline) {
    try {
      if ((await chrome.tabs.get(workspace.tabId)).status === 'complete' &&
          await execute(workspace.tabId, verifyAmazonInvoice, [orderId])) { ready = true; break; }
    } catch { /* Wait until Amazon finishes navigating. */ }
    await new Promise(resolve => setTimeout(resolve, 250));
  }
  if (!ready) throw new Error('Could not verify the printed Amazon order number and total. Check your sign-in and order.');
  const targetTab = { tabId: workspace.tabId };
  await chrome.debugger.attach(targetTab, '1.3');
  try {
    if (!await execute(workspace.tabId, verifyAmazonInvoice, [orderId]))
      throw new Error('The Amazon printable receipt changed before saving.');
    await execute(workspace.tabId, setAmazonPrintStyle, [true]);
    await chrome.debugger.sendCommand(targetTab, 'Emulation.setEmulatedMedia', { media: 'print' });
    const width = await execute(workspace.tabId, amazonPrintWidth);
    const margin = 0.375;
    const paperWidth = 8.5;
    const scale = Math.min(1, (paperWidth - 2 * margin) * 96 / width);
    if (!Number.isFinite(width) || width <= 0 || scale < 0.1)
      throw new Error('The Amazon invoice is too wide to fit safely on a PDF page.');
    const result = await chrome.debugger.sendCommand(targetTab, 'Page.printToPDF', {
      printBackground: true, displayHeaderFooter: false,
      paperWidth, paperHeight: 11, marginTop: 0.65, marginBottom: 0.65, marginLeft: margin, marginRight: margin, scale
    });
    if (!result?.data?.startsWith('JVBERi0') || !await execute(workspace.tabId, verifyAmazonInvoice, [orderId]))
      throw new Error('The browser did not produce a PDF for the expected Amazon order.');
    return { pdf: result.data, sourceUrl: target.href };
  } finally {
    await chrome.debugger.sendCommand(targetTab, 'Emulation.setEmulatedMedia', { media: '' }).catch(() => {});
    await execute(workspace.tabId, setAmazonPrintStyle, [false]).catch(() => {});
    await chrome.debugger.detach(targetTab).catch(() => {});
  }
}

$('find').addEventListener('click', async () => {
  setImportBusy(true);
  selection = null;
  $('download').hidden = true;
  $('receipts').replaceChildren();
  try {
    const from = $('from').value, to = $('to').value;
    if (!from || !to || from > to) throw new Error('Choose a valid start and end date.');
    const tab = await activeCostcoTab();
    const retailer = $('retailer').value;
    if (retailer === 'amazon' || retailer === 'costco-online') {
      const amazon = retailer === 'amazon';
      let matches;
      let discovery;
      if (amazon) {
        const report = { pages: 0, unreadable: 0 };
        const cards = await collectAmazonOrders(tab.id, from, to, report);
        const inRange = filterAmazonOrders(cards, from, to, 'all');
        const recipient = $('buyer').value;
        updateBuyerOptions(inRange);
        matches = filterAmazonOrders(inRange, from, to, recipient);
        discovery = amazonDiscoveryText(report, inRange.length) + ' ' + matches.length + ' matched the recipient filter.';
      } else {
        matches = await collectCostcoOnlineOrders(tab.id, from, to);
        discovery = matches.length + ' Costco Online order(s) found in range.';
      }
      const prepared = [];
      let failed = 0;
      const workspace = { tabId: null };
      const prepare = amazon ? prepareAmazonInvoice : prepareCostcoOnlineOrder;
      const showResult = () => {
        status.textContent = discovery + ' ' + prepared.length + ' ready for review. ' + failed + ' could not be prepared.';
        $('download').hidden = prepared.length === 0;
      };
      const prepareRow = async (row, item, currentWorkspace) => {
        const summary = await prepare(row, currentWorkspace);
        prepared.push({ ...row, summary });
        if (summary.sourceUrl) {
          item.textContent = row.label + ': PDF ready. ';
          const source = document.createElement('a');
          source.href = summary.sourceUrl;
          source.target = '_blank';
          source.rel = 'noopener noreferrer';
          source.textContent = 'Review printable receipt';
          item.append(source);
        } else {
          item.textContent = row.label + ': ' + summary.receipt.items.map(product => product.name).join(', ');
        }
      };
      try {
      for (let i = 0; i < matches.length; i++) {
        const row = matches[i];
        const item = document.createElement('li');
        $('receipts').append(item);
        status.textContent = discovery + ' Preparing order ' + (i + 1) + ' of ' + matches.length + '…';
        try { await prepareRow(row, item, workspace); }
        catch (error) {
          failed++;
          const detail = document.createElement('span');
          detail.textContent = row.label + ': ' + error.message + ' ';
          const retry = document.createElement('button');
          retry.type = 'button';
          retry.className = 'text-button';
          retry.textContent = 'Retry';
          retry.addEventListener('click', async () => {
            if ($('find').disabled || selection?.rows !== prepared) return;
            setImportBusy(true);
            const retryWorkspace = { tabId: null };
            status.textContent = 'Retrying ' + row.label + '…';
            try {
              await prepareRow(row, item, retryWorkspace);
              failed--;
            } catch (error) {
              detail.textContent = row.label + ': ' + error.message + ' ';
            } finally {
              if (retryWorkspace.tabId) await chrome.tabs.remove(retryWorkspace.tabId).catch(() => {});
              setImportBusy(false);
              showResult();
            }
          });
          item.append(detail, retry);
        }
      }
      } finally {
        if (workspace.tabId) await chrome.tabs.remove(workspace.tabId).catch(() => {});
      }
      selection = { retailer, tabId: tab.id, rows: prepared };
      showResult();
      return;
    }
    await openCostcoView(tab.id);
    let rows = await waitForCostcoPage(tab.id);
    const available = await execute(tab.id, costcoPeriods);
    if (!available) throw new Error('Could not read Costco’s date periods.');
    const periods = available.options.length ? available.options.filter(period => periodOverlaps(period, from, to)) : [{ value: null, label: 'Current period' }];
    const scanned = [];
    for (const period of periods) {
      if (period.value !== null) rows = await switchCostcoPeriod(tab.id, period.value, rows);
      for (let i = 0; i < 100; i++) {
        const previous = await changePage(tab.id, 'previous', rows);
        if (!previous) break;
        rows = previous;
        if (i === 99) throw new Error('Could not reach the first Costco page.');
      }
      const pages = [];
      const signatures = new Set();
      while (true) {
        const signature = JSON.stringify(rows);
        if (signatures.has(signature)) throw new Error('Pagination repeated a page. Check Costco and try again.');
        signatures.add(signature);
        pages.push(rows);
        status.textContent = `Checking ${period.label}, page ${pages.length}…`;
        const next = await changePage(tab.id, 'next', rows);
        if (!next) break;
        if (pages.length >= 100) throw new Error('Stopped after 100 pages. Narrow the selected period.');
        rows = next;
      }
      for (let i = pages.length - 1; i > 0; i--) {
        rows = await changePage(tab.id, 'previous', rows);
        if (!rows || JSON.stringify(rows) !== JSON.stringify(pages[i - 1]))
          throw new Error('Could not return to the first page. Find receipts again.');
      }
      scanned.push({ ...period, pages });
    }
    if (available.current && periods.length && periods.at(-1).value !== available.current)
      rows = await switchCostcoPeriod(tab.id, available.current, rows);
    const matches = scanned.flatMap((period, periodIndex) => period.pages.flatMap((page, pageIndex) =>
      page.filter(row => row.date >= from && row.date <= to).map(row => ({ ...row, periodIndex, pageIndex }))));
    selection = { retailer: 'costco', tabId: tab.id, rows: matches, periods: scanned };
    for (const row of matches) {
      const item = document.createElement('li');
      item.textContent = `Costco receipt ${row.date}`;
      $('receipts').append(item);
    }
    status.textContent = `${matches.length} warehouse receipt(s) found across ${scanned.length} period(s). ${costcoPrivacyMessage}`;
    $('download').hidden = matches.length === 0;
  } catch (error) { status.textContent = error.message; }
  finally { setImportBusy(false); }
});

$('download').addEventListener('click', async () => {
  if (!selection) return;
  if (!inbox) {
    status.textContent = 'Open the extension from your signed-in GroupSplit receipts inbox to connect it.';
    return;
  }
  setImportBusy(true);
  let attached = false, saved = 0, imported = 0, duplicates = 0;
  const failures = [];
  const tabId = selection.tabId;
  try {
    const tab = await activeCostcoTab();
    if (tab.id !== selection.tabId) throw new Error('Return to the original Costco tab and find receipts again.');
    if (selection.retailer === 'amazon' || selection.retailer === 'costco-online') {
      for (let i = 0; i < selection.rows.length; i++) {
        status.textContent = `Saving ${i + 1} of ${selection.rows.length}…`;
        try {
          const row = selection.rows[i];
          const { pdf, receipt } = row.summary;
          const fileName = `${receipt?.merchantName || 'Amazon'}-${row.date}-${i + 1}.pdf`;
          const result = await saveReceiptFile(pdf, fileName, receipt);
          saved++;
          if (result.alreadyImported) duplicates++; else imported++;
          if (!result.alreadyImported && !result.detailsSaved)
            failures.push(`Order ${i + 1}: ${result.detail || 'The file was saved, but receipt details need review.'}`);
        } catch (error) { failures.push(`Order ${i + 1}: ${error.message}`); }
      }
      status.textContent = `${saved} files saved and uploaded, ${imported} new, ${duplicates} already in your inbox, ${failures.length} need attention. ${failures.join(' ')}`;
      if (saved) await chrome.tabs.sendMessage(inbox.tabId, { type: 'receipts-imported' }).catch(() => {});
      selection = null;
      $('download').hidden = true;
      return;
    }
    if (selection.retailer !== 'costco') throw new Error('Choose a supported store and find receipts again.');
    await chrome.debugger.attach({ tabId: tab.id }, '1.3');
    attached = true;
    let currentPage = 0, currentPeriod = -1;
    let currentRows = await pageRows(tab.id);
    for (const row of selection.rows) {
      await activeCostcoTab();
      const period = selection.periods[row.periodIndex];
      if (currentPeriod !== row.periodIndex) {
        if (period.value !== null) currentRows = await switchCostcoPeriod(tab.id, period.value, currentRows);
        currentPeriod = row.periodIndex;
        currentPage = 0;
        if (JSON.stringify(currentRows) !== JSON.stringify(period.pages[0]))
          throw new Error('The receipt list changed. Find receipts again.');
      }
      while (currentPage < row.pageIndex) {
        currentRows = await changePage(tab.id, 'next', currentRows);
        currentPage++;
        if (!currentRows || JSON.stringify(currentRows) !== JSON.stringify(period.pages[currentPage]))
          throw new Error('The receipt list changed. Find receipts again.');
      }
      status.textContent = `Saving ${saved + 1} of ${selection.rows.length}…`;
      try {
        const opened = await execute(tab.id, openReceipt, [row.index, row.label]);
        if (!opened?.opened) throw new Error(opened?.error || 'Could not open the selected receipt.');
        const prepared = await execute(tab.id, prepareReceipt);
        if (!prepared?.prepared) throw new Error(prepared?.error || 'Could not prepare the selected receipt.');
        if (!await execute(tab.id, verifyPreparedCostcoReceipt)) throw new Error('The receipt page changed. Find receipts again.');
        const result = await chrome.debugger.sendCommand({ tabId: tab.id }, 'Page.printToPDF', { printBackground: true, preferCSSPageSize: true });
        if (!result?.data) throw new Error('The browser did not produce a PDF.');
        const fileName = `Costco-${row.date}-${row.periodIndex + 1}-${row.pageIndex + 1}-${row.index + 1}.pdf`;
        try {
          const upload = await saveReceiptFile(result.data, fileName);
          saved++;
          if (upload.alreadyImported) duplicates++; else imported++;
          if (!upload.alreadyImported && !upload.detailsSaved)
            failures.push(`${fileName}: ${upload.detail || 'Uploaded. Receipt details need review.'}`);
        } catch (error) { failures.push(`${fileName}: ${error.message}`); }
      } finally {
        const cleanup = await execute(tab.id, cleanupReceipt);
        if (cleanup?.closed === false) throw new Error('The Costco receipt did not close. Close it before retrying.');
      }
    }
    status.textContent = `${saved} saved, ${imported} imported, ${duplicates} already in your inbox, ${failures.length} need attention. ${failures.join(' ')}`;
    if (saved) await chrome.tabs.sendMessage(inbox.tabId, { type: 'receipts-imported' }).catch(() => {});
    selection = null;
    $('download').hidden = true;
  } catch (error) {
    status.textContent = `Stopped after ${saved} receipt(s): ${error.message}`;
    selection = null;
    $('download').hidden = true;
  }
  finally {
    if (attached) await chrome.debugger.detach({ tabId }).catch(() => {});
    setImportBusy(false);
  }
});
