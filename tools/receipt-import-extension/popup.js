'use strict';

const $ = id => document.getElementById(id);
const status = $('status');
let selection = null;
let inbox = null;
let sourceTabId = Number(new URL(location.href).searchParams.get('tab'));
const inboxTabId = Number(new URL(location.href).searchParams.get('appTab'));
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
  const actions = [...document.querySelectorAll('button, a, [role="button"]')]
    .filter(element => visible(element) && /^view receipt$/i.test(element.textContent.trim()));
  return actions.map((action, index) => {
    let row = action.parentElement;
    while (row && row !== document.body) {
      const count = [...row.querySelectorAll('button, a, [role="button"]')]
        .filter(element => visible(element) && /^view receipt$/i.test(element.textContent.trim())).length;
      const text = row.innerText.replace(/\s+/g, ' ').trim();
      const dates = [...text.matchAll(/\b(\d{1,2})\/(\d{1,2})\/(\d{4})\b/g)];
      if (count === 1 && dates.length === 1 && text.length < 1200) {
        const [, month, day, year] = dates[0];
        return { index, date: `${year}-${month.padStart(2, '0')}-${day.padStart(2, '0')}`, label: text.slice(0, 240) };
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
    const datePattern = '(?:January|February|March|April|May|June|July|August|September|October|November|December)\\s+\\d{1,2},?\\s+\\d{4}';
    const placed = text.match(new RegExp(`\\border placed\\s*:?\\s*(${datePattern})`, 'i'));
    const dates = [...text.matchAll(new RegExp(`\\b${datePattern}\\b`, 'gi'))];
    const dateText = placed?.[1] || (dates.length === 1 ? dates[0][0] : null);
    const link = card.querySelector('a[href*="summary/print.html"]');
    if (!dateText) return null;
    const date = new Date(`${dateText.replace(',', '')} 12:00:00`);
    if (Number.isNaN(date.getTime())) return null;
    let invoiceUrl = null;
    if (link?.href) {
      try {
        const url = new URL(link.href, location.href);
        if (url.origin === 'https://www.amazon.com' && !url.username && !url.password && url.pathname.endsWith('/summary/print.html')) invoiceUrl = url.href;
      } catch { /* An unusable invoice should not hide this order's recipient. */ }
    }
    const day = `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}-${String(date.getDate()).padStart(2, '0')}`;
    const recipientArea = card.querySelector('.yohtmlc-recipient');
    const recipientText = typeof recipientArea?.innerText === 'string' ? recipientArea.innerText : rawText;
    const recipientMatch = recipientText.match(/(?:^|\n)[^\S\n]*ship to[\t ]*:?[\t ]*([^\n]*)/i);
    let recipient = recipientMatch?.[1]?.trim() || '';
    if (!recipient && recipientMatch) {
      const rest = recipientText.slice(recipientMatch.index + recipientMatch[0].length).split('\n').map(part => part.trim()).find(Boolean);
      recipient = rest || '';
    }
    recipient = recipient.replace(/[\s\u2304\u25be\u25bc]+$/u, '');
    if (!/^[\p{L}\p{M}][\p{L}\p{M} .’'\-]{0,79}$/u.test(recipient) ||
        /^(?:view|order|ship|deliver|total|buy|return|track|invoice)\b/i.test(recipient)) recipient = '';
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
  if (previous.startsWith('buyer:') && !names.includes(previous.slice(6))) add(previous, previous.slice(6));
  add('unknown', 'Recipient not shown');
  select.value = [...select.options].some(option => option.value === previous) ? previous : 'all';
  $('buyer-note').textContent = names.length
    ? 'Recipient names stay on this device and are not saved to receipts.'
    : 'No recipient names were found on this page. Check recipients after opening Your Orders.';
}

function filterAmazonOrders(cards, from, to, recipient) {
  return cards.filter(row => row.date >= from && row.date <= to &&
    (recipient === 'all' || recipient === 'unknown' && !row.buyer ||
      recipient.startsWith('buyer:') && row.buyer === recipient.slice(6)));
}

function readAmazonNavigation() {
  try {
  if (document.readyState && document.readyState !== 'complete') return { pending: true };
  const isOrdersPath = path => path === '/your-orders/orders' || path === '/gp/css/order-history' || path === '/gp/your-account/order-history';
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

async function collectAmazonOrders(tabId, from = null, to = null) {
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
    while (true) {
      if (seenPages.has(navigation.current)) throw new Error('Amazon repeated an orders page. Try again.');
      seenPages.add(navigation.current);
      const page = await execute(tabId, scanAmazonPage);
      if (!Array.isArray(page)) throw new Error('Amazon orders could not be read. Check your sign-in and try again.');
      if (page.some(row => !row)) throw new Error('Some orders could not be identified safely.');
      if (yearPages > 0 && page.length === 0) throw new Error('Amazon did not load the next orders page.');
      yearPages++;
      for (const row of page) {
        const id = row.url ? new URL(row.url).searchParams.get('orderID') : null;
        if (id && seenOrders.has(id)) continue;
        if (id) seenOrders.add(id);
        orders.push(row);
      }
      status.textContent = `Checking Amazon${year ? ` ${year}` : ''}, page ${yearPages}…`;
      if (!navigation.next) break;
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

async function refreshBuyers(report = false) {
  if (!sourceTabId || $('retailer').value !== 'amazon') {
    if (report) $('buyer-note').textContent = 'Connect Amazon and open Your Orders first.';
    return;
  }
  try {
    const tab = await activeCostcoTab();
    const cards = await execute(tab.id, scanAmazonPage);
    if (!cards.length) {
      if (report) $('buyer-note').textContent = 'Open Your Orders in Amazon, then check recipients again.';
      return;
    }
    updateBuyerOptions(cards);
  } catch {
    if (report) $('buyer-note').textContent = 'Connect Amazon and open Your Orders, then check recipients again.';
  }
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
  if (receipt && !alreadyImported) {
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
    $('back-to-inbox').hidden = false;
  } catch (error) { $('connection').textContent = error.message; }
}
connectInbox();

$('back-to-inbox').addEventListener('click', async () => {
  if (inbox) await chrome.tabs.update(inbox.tabId, { active: true });
});

$('retailer').addEventListener('change', () => {
  sourceTabId = 0;
  selection = null;
  $('receipts').replaceChildren();
  $('download').hidden = true;
  $('buyer-field').hidden = $('retailer').value !== 'amazon';
  $('buyer-guidance').hidden = $('retailer').value !== 'amazon';
  $('buyer').value = 'all';
  status.textContent = '';
  $('privacy-note').textContent = $('retailer').value === 'amazon'
    ? 'Amazon summaries omit customer, address and payment sections. Check item names for personal text.'
    : costcoPrivacyMessage;
});

$('refresh-buyers').addEventListener('click', async () => { await refreshBuyers(true); });

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
  status.textContent = 'Select Find receipts to apply the date range.';
});

function setImportBusy(busy) {
  for (const id of ['retailer', 'from', 'to', 'buyer', 'open-costco', 'refresh-buyers', 'find', 'download'])
    $(id).disabled = busy;
}

document.addEventListener('visibilitychange', () => { if (!document.hidden) void refreshBuyers(); });
if (sourceTabId) void refreshBuyers();

$('open-costco').addEventListener('click', async () => {
  try {
    const amazon = $('retailer').value === 'amazon';
    const origin = amazon ? 'https://www.amazon.com' : 'https://www.costco.com';
    if (!await chrome.permissions.request({ origins: [`${origin}/*`] })) throw new Error('Retailer access was not granted.');
    const tabs = await chrome.tabs.query({ url: `${origin}/*` });
    const tab = tabs[0] || await chrome.tabs.create({ url: amazon ? `${origin}/your-orders/orders` : `${origin}/OrderStatusCmd` });
    sourceTabId = tab.id;
    await chrome.tabs.update(tab.id, { active: true });
    status.textContent = 'Sign in if needed, open your orders, then return here to find receipts.';
  } catch (error) { status.textContent = error.message; }
});

// Only activate explicit pagination controls; never infer actions from position.
function clickPagination(direction) {
  const expression = direction === 'next'
    ? /^(next page|go to next page)$/i : /^(previous page|go to previous page)$/i;
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
  const empty = [...document.querySelectorAll('[role="status"], p, td')].some(element => visible(element) &&
    /^(?:no (?:receipts|purchases|orders|results)(?: found)?|no results found)[.!]?$/i.test(element.textContent.trim()));
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

async function switchCostcoPeriod(tabId, value, before) {
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
    const rows = await pageRows(tabId);
    if ((rows.length && JSON.stringify(rows) !== previous) || (state.empty && !rows.length)) return rows;
  }
  throw new Error('Costco did not finish loading the selected period. Try again.');
}

async function pageRows(tabId) {
  const rows = await execute(tabId, scanPage);
  if (!Array.isArray(rows)) throw new Error('Could not read Costco receipts. Open the warehouse purchases view and try again.');
  if (rows.some(row => !row)) throw new Error('Some visible receipts could not be identified safely.');
  return rows;
}

function costcoSelectedView() {
  const selected = [...document.querySelectorAll('[role="tab"][aria-selected="true"]')]
    .map(tab => tab.textContent.trim()).find(Boolean);
  return selected || '';
}

async function waitForCostcoPage(tabId) {
  const deadline = Date.now() + 20000;
  while (Date.now() < deadline) {
    const state = await execute(tabId, costcoPageState);
    if (state && !state.loading) {
      const rows = await pageRows(tabId);
      if (rows.length || state.empty) return rows;
    }
    await new Promise(resolve => setTimeout(resolve, 300));
  }
  throw new Error('Costco receipts did not finish loading. Open warehouse purchases and try again.');
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
    .filter(element => visible(element) && /^view receipt$/i.test(element.textContent.trim()));
  const button = actions[index];
  let row = button?.parentElement;
  let found = false;
  while (row && row !== document.body) {
    const actionsInRow = [...row.querySelectorAll('button, a, [role="button"]')]
      .filter(element => visible(element) && /^view receipt$/i.test(element.textContent.trim()));
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
    const dialogs = [...document.querySelectorAll('[role="dialog"], dialog[open]')]
      .filter(element => element.getClientRects().length && /receipt/i.test(element.innerText));
    if (dialogs.length === 1) {
      const dialog = dialogs[0];
      dialog.setAttribute('data-groupsplit-print', '');
      const style = document.createElement('style');
      style.id = 'groupsplit-receipt-print-style';
      style.textContent = '@media print { body * { visibility: hidden !important; } [data-groupsplit-print], [data-groupsplit-print] * { visibility: visible !important; } [data-groupsplit-print] { position: absolute !important; top: 0 !important; left: 0 !important; width: 100% !important; max-width: none !important; max-height: none !important; overflow: visible !important; } }';
      document.head.append(style);
      return { prepared: true };
    }
    await new Promise(resolve => setTimeout(resolve, 200));
  }
  return { error: 'Could not open the receipt. Check Costco and try again.' };
}

function verifyPreparedCostcoReceipt() {
  return location.origin === 'https://www.costco.com' &&
    document.querySelectorAll('[role="dialog"][data-groupsplit-print], dialog[open][data-groupsplit-print]').length === 1 &&
    !!document.getElementById('groupsplit-receipt-print-style');
}

function cleanupReceipt() {
  if (location.origin !== 'https://www.costco.com') return;
  document.getElementById('groupsplit-receipt-print-style')?.remove();
  const dialog = document.querySelector('[data-groupsplit-print]');
  dialog?.removeAttribute('data-groupsplit-print');
  if (dialog) {
    const close = [...dialog.querySelectorAll('button, [role="button"]')]
      .find(button => /^close$/i.test((button.getAttribute('aria-label') || button.textContent).trim()));
    if (close) close.click();
    else document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', code: 'Escape', bubbles: true }));
  }
}

async function savePdf(data, fileName, receipt = null) {
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
  return url.origin === 'https://www.amazon.com' && !url.username && !url.password &&
    location.pathname.endsWith('/summary/print.html') &&
    url.searchParams.get('orderID') === expectedOrderId &&
    document.readyState === 'complete' && /order/i.test(document.body?.innerText || '') &&
    !document.querySelector('input[type="password"]');
}

// Read only product titles, quantity, prices and numeric totals from the
// identified item and summary sections; never return the full invoice HTML.
function readAmazonSummary(expectedOrderId) {
  try {
  const invoiceUrl = new URL(location.href);
  if (invoiceUrl.origin !== 'https://www.amazon.com' || invoiceUrl.username || invoiceUrl.password ||
      !location.pathname.endsWith('/summary/print.html') ||
      invoiceUrl.searchParams.get('orderID') !== expectedOrderId || document.querySelector('input[type="password"]'))
    throw new Error('Amazon did not open a supported invoice.');
  const lines = [...document.querySelectorAll('li')].map(element => element.textContent.replace(/\s+/g, ' ').trim());
  const amount = (label, required) => {
    const found = lines.filter(text => label.test(text));
    if (found.length !== 1) {
      if (!required && found.length === 0) return 0;
      throw new Error('The invoice totals could not be identified safely.');
    }
    const values = [...found[0].matchAll(/\$\s*([\d,]+\.\d{2})/g)];
    if (values.length !== 1) throw new Error('The invoice totals could not be identified safely.');
    return Math.round(Number(values[0][1].replace(/,/g, '')) * 100);
  };
  const productCards = [...document.querySelectorAll('[data-component="purchasedItemsRightGrid"]')];
  if (!productCards.length || productCards.length > 100) throw new Error('The invoice items could not be identified safely.');
  const products = productCards.map(card => {
    const titles = card.querySelectorAll('[data-component="itemTitle"] a[href*="/dp/"], [data-component="itemTitle"] a[href*="/gp/product/"]');
    const prices = card.querySelectorAll('[data-component="unitPrice"] .a-offscreen');
    const quantityElements = card.querySelectorAll('[data-component="quantity"]');
    const custom = card.querySelector('[data-component="customizedItemDetails"]');
    if (titles.length !== 1 || prices.length !== 1 || quantityElements.length > 1 || custom?.textContent.trim())
      throw new Error('An item could not be read safely, or contains customization details.');
    const name = titles[0].textContent.replace(/\s+/g, ' ').trim();
    // Titles are arbitrary retailer-supplied text. Reject common contact details;
    // the user should still review item names for personalized information.
    if (!name || /[\w.+-]+@[\w.-]+\.[a-z]{2,}|\b\d{3}[-. ]\d{3}[-. ]\d{4}\b/i.test(name))
      throw new Error('An item title may contain personal contact information.');
    const price = prices[0].textContent.trim().match(/^\$\s*([\d,]+\.\d{2})$/);
    const quantityText = quantityElements[0]?.textContent.replace(/\s+/g, ' ').trim() || '';
    const parsedQuantity = quantityText && quantityText.match(/^(?:quantity|qty)\s*:?\s*(\d+)$/i);
    if (!price || (quantityText && !parsedQuantity)) throw new Error('An item price or quantity could not be read safely.');
    const quantity = parsedQuantity ? Number(parsedQuantity[1]) : 1;
    const unitPrice = Math.round(Number(price[1].replace(/,/g, '')) * 100);
    if (!Number.isSafeInteger(quantity) || quantity < 1 || quantity > 1000 || !Number.isSafeInteger(unitPrice))
      throw new Error('An item price or quantity is invalid.');
    return { name: name.slice(0, 128), quantity, unitPrice };
  });
  return {
    items: amount(/^item\(?s\)?\s*subtotal\s*:/i, true),
    shipping: amount(/^shipping\s*(?:&\s*handling)?\s*:/i, false),
    tax: amount(/^(?:estimated tax to be collected|tax collected|tax)\s*:/i, false),
    total: amount(/^(?:grand total|order total)\s*:/i, true),
    products
  };
  } catch (error) {
    return { error: error.message || 'The invoice could not be read safely.' };
  }
}

function privateAmazonReceipt(date, amounts, reference) {
  if (!/^\d{4}-\d{2}-\d{2}$/.test(date) || !amounts ||
      !['items', 'shipping', 'tax', 'total'].every(key => Number.isSafeInteger(amounts[key]) && amounts[key] >= 0 && amounts[key] <= 100000000) ||
      amounts.items + amounts.shipping + amounts.tax !== amounts.total || !/^[a-f0-9]{32}$/.test(reference) ||
      !Array.isArray(amounts.products) || amounts.products.length < 1 || amounts.products.length > 100 ||
      !amounts.products.every(product => typeof product.name === 'string' && product.name.length > 0 && product.name.length <= 128 &&
        Number.isSafeInteger(product.quantity) && product.quantity > 0 && product.quantity <= 1000 &&
        Number.isSafeInteger(product.unitPrice) && product.unitPrice >= 0 && product.unitPrice <= 100000000) ||
      amounts.products.reduce((sum, product) => sum + product.unitPrice * product.quantity, 0) !== amounts.items)
    throw new Error('The invoice totals could not be safely reconciled. No receipt was saved.');
  const money = cents => (cents / 100).toFixed(2);
  const lines = ['Amazon - private order summary', `Date: ${date}`, 'Items:'];
  for (const product of amounts.products) {
    const printable = product.name.normalize('NFKD').replace(/[^\x20-\x7e]/g, '?');
    const pieces = printable.match(/.{1,72}(?:\s|$)|.{1,72}/g) || [printable];
    lines.push(...pieces.map(piece => piece.trim()));
    lines.push(`  ${product.quantity} x $${money(product.unitPrice)} = $${money(product.unitPrice * product.quantity)}`);
  }
  lines.push(
    `Items subtotal: $${money(amounts.items)}`, `Shipping: $${money(amounts.shipping)}`,
    `Tax: $${money(amounts.tax)}`, `Total: $${money(amounts.total)}`,
    'Customer and payment details omitted.', `Reference: ${reference}`
  );
  const pageHeight = Math.max(792, 72 + lines.length * 18);
  if (pageHeight > 14000) throw new Error('This receipt has too many item lines to save safely.');
  const escape = text => text.replace(/[\\()]/g, '\\$&');
  const stream = lines.map((line, index) => `BT /F1 11 Tf 36 ${pageHeight - 36 - index * 18} Td (${escape(line)}) Tj ET`).join('\n');
  const objects = [
    '<< /Type /Catalog /Pages 2 0 R >>',
    '<< /Type /Pages /Kids [3 0 R] /Count 1 >>',
    `<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 ${pageHeight}] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>`,
    '<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>',
    `<< /Length ${stream.length} >>\nstream\n${stream}\nendstream`
  ];
  let pdf = '%PDF-1.4\n';
  const offsets = [0];
  objects.forEach((object, index) => { offsets.push(pdf.length); pdf += `${index + 1} 0 obj\n${object}\nendobj\n`; });
  const xref = pdf.length;
  pdf += `xref\n0 ${offsets.length}\n0000000000 65535 f \n`;
  pdf += offsets.slice(1).map(offset => `${String(offset).padStart(10, '0')} 00000 n \n`).join('');
  pdf += `trailer\n<< /Size ${offsets.length} /Root 1 0 R >>\nstartxref\n${xref}\n%%EOF\n`;
  const items = amounts.products.map(product => ({ name: product.name, quantity: product.quantity,
    unitPrice: product.unitPrice / 100, totalPrice: product.unitPrice * product.quantity / 100 }));
  if (amounts.shipping) items.push({ name: 'Shipping', quantity: 1, unitPrice: amounts.shipping / 100, totalPrice: amounts.shipping / 100 });
  return {
    pdf: btoa(pdf),
    receipt: { merchantName: 'Amazon', purchaseDate: date, subtotal: (amounts.items + amounts.shipping) / 100,
      tax: amounts.tax / 100, tip: 0, total: amounts.total / 100, items }
  };
}

async function prepareAmazonInvoice(row, workspace) {
  if (!row.url) throw new Error('Amazon has no usable printable invoice for this order.');
  const target = new URL(row.url);
  if (target.origin !== 'https://www.amazon.com' || target.username || target.password || !target.pathname.endsWith('/summary/print.html'))
    throw new Error('The invoice address is not a supported Amazon address.');
  const orderId = target.searchParams.get('orderID');
  if (!/^\d{3}-\d{7}-\d{7}$/.test(orderId || '')) throw new Error('The Amazon order could not be identified safely.');
  const hash = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(orderId));
  const reference = [...new Uint8Array(hash)].slice(0, 16).map(value => value.toString(16).padStart(2, '0')).join('');
  if (!workspace.tabId) workspace.tabId = (await chrome.tabs.create({ url: target.href, active: false })).id;
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
  if (!ready) throw new Error('Amazon did not open an invoice. Check your sign-in and order.');
  const amounts = await execute(workspace.tabId, readAmazonSummary, [orderId]);
  if (amounts?.error) throw new Error(amounts.error);
  if (!amounts) throw new Error('Amazon did not provide readable invoice details.');
  return privateAmazonReceipt(row.date, amounts, reference);
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
    if ($('retailer').value === 'amazon') {
      const cards = await collectAmazonOrders(tab.id, from, to);
      updateBuyerOptions(cards);
      const buyer = $('buyer').value;
      const matches = filterAmazonOrders(cards, from, to, buyer);
      const prepared = [];
      const failures = [];
      const workspace = { tabId: null };
      try {
      for (let i = 0; i < matches.length; i++) {
        status.textContent = `Preparing invoice ${i + 1} of ${matches.length} (${failures.length} need attention)…`;
        try { prepared.push({ ...matches[i], summary: await prepareAmazonInvoice(matches[i], workspace) }); }
        catch (error) { failures.push(`Order from ${matches[i].date}: ${error.message}`); }
      }
      } finally {
        if (workspace.tabId) await chrome.tabs.remove(workspace.tabId).catch(() => {});
      }
      selection = { retailer: 'amazon', tabId: tab.id, rows: prepared };
      for (const row of prepared) {
        const item = document.createElement('li');
        item.textContent = `${row.label}: ${row.summary.receipt.items.map(product => product.name).join(', ')}`;
        $('receipts').append(item);
      }
      status.textContent = `${prepared.length} invoice(s) ready for review; ${failures.length} could not be prepared. ${failures.join(' ')}`;
      if (prepared.length) status.textContent += ' Review item names for personal text before importing. Customer and payment sections are omitted.';
      $('download').hidden = prepared.length === 0;
      return;
    }
    const view = await execute(tab.id, costcoSelectedView);
    if (/online/i.test(view || '')) throw new Error('Costco online orders are not supported by this preview yet. Select warehouse purchases.');
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
    if (selection.retailer === 'amazon') {
      for (let i = 0; i < selection.rows.length; i++) {
        status.textContent = `Saving ${i + 1} of ${selection.rows.length}…`;
        try {
          const row = selection.rows[i];
          const result = await savePdf(row.summary.pdf, `Amazon-${row.date}-${i + 1}.pdf`, row.summary.receipt);
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
          const upload = await savePdf(result.data, fileName);
          saved++;
          if (upload.alreadyImported) duplicates++; else imported++;
        } catch (error) { failures.push(`${fileName}: ${error.message}`); }
      } finally { await execute(tab.id, cleanupReceipt); }
    }
    status.textContent = `${saved} saved, ${imported} imported, ${duplicates} already in your inbox, ${failures.length} failed. ${failures.join(' ')}`;
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
