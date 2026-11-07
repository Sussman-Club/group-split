const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const source = fs.readFileSync(require('node:path').join(__dirname, 'popup.js'), 'utf8');

function page(rowText, href = 'https://example.test/?tab=1') {
  const body = {};
  const action = { textContent: 'View Receipt', offsetWidth: 1 };
  const row = {
    innerText: rowText,
    parentElement: body,
    querySelectorAll: () => [action]
  };
  action.parentElement = row;
  const controls = new Map();
  const document = {
    body,
    documentElement: { dataset: {} },
    addEventListener() {},
    querySelectorAll: () => [action],
    getElementById: id => {
      if (!controls.has(id)) controls.set(id, { addEventListener(event, handler) { this[event] = handler; } });
      return controls.get(id);
    }
  };
  const context = vm.createContext({ document, URL, location: { href }, setTimeout });
  vm.runInContext(source, context);
  return context;
}

test('connects a store in the background without focusing its window', async () => {
  const context = page('');
  context.document.getElementById('retailer').value = 'amazon';
  let created;
  context.chrome = {
    permissions: { request: async () => true },
    tabs: {
      query: async () => [],
      create: async options => { created = options; return { id: 7 }; },
      update: async () => assert.fail('store tab should not be activated')
    },
    scripting: { executeScript: async () => [{ result: 'signin' }] },
    windows: { update: async () => assert.fail('store window should not be focused') }
  };
  await context.document.getElementById('open-costco').click();
  assert.equal(created.active, false);
  assert.equal(created.url, 'https://www.amazon.com/your-orders/orders');
  assert.match(context.document.getElementById('store-state-title').textContent, /sign in/i);
  assert.equal(context.document.getElementById('show-store').hidden, false);
});

test('recognizes sign-in and confirmed orders pages without assuming an unknown page is signed in', () => {
  const context = page('');
  context.document.readyState = 'complete';
  context.location.pathname = '/ap/signin';
  context.document.querySelector = () => null;
  assert.equal(context.retailerSignInState('amazon'), 'signin');
  context.location.pathname = '/your-orders/orders';
  context.document.querySelector = selector => selector === 'input[type="password"]' ? null : ({});
  assert.equal(context.retailerSignInState('amazon'), 'ready');
  context.document.querySelector = () => null;
  context.document.body.innerText = 'Loading';
  assert.equal(context.retailerSignInState('amazon'), 'unknown');
});

test('finds a visible receipt and normalizes its date', () => {
  const rows = page('Warehouse 9/5/2026 $266.39 View Receipt').scanPage();
  assert.equal(rows.length, 1);
  assert.equal(rows[0].date, '2026-09-05');
});

test('refuses a row containing multiple dates', () => {
  const rows = page('9/5/2026 returned 9/6/2026 View Receipt').scanPage();
  assert.equal(rows[0], null);
});

test('reads localized Costco warehouse receipt dates', () => {
  const context = page('Compras en almacén 05/09/2026 US$12.99 Ver recibo');
  context.document.documentElement.lang = 'es';
  context.document.querySelectorAll()[0].textContent = 'Ver recibo';
  assert.equal(context.scanPage()[0].date, '2026-09-05');
});

test('refuses to open a Costco receipt when its row has changed', () => {
  const context = page('Warehouse 9/5/2026 $266.39 View Receipt');
  context.location.origin = 'https://www.costco.com';
  assert.match(context.openReceipt(0, 'a different receipt').error, /changed/);
});

test('only clicks a specifically labelled pagination control', () => {
  const context = page('Warehouse 9/5/2026 $266.39 View Receipt');
  let clicked = false;
  const button = { getClientRects: () => [1], getAttribute: name => name === 'aria-label' ? 'Next page' : null, click: () => { clicked = true; } };
  context.document.querySelectorAll = () => [button];
  assert.equal(context.clickPagination('next'), true);
  assert.equal(clicked, true);
});

test('does not click a disabled next-page control', () => {
  const context = page('Warehouse 9/5/2026 $266.39 View Receipt');
  const button = { disabled: true, getClientRects: () => [1], getAttribute: name => name === 'aria-label' ? 'Next page' : null, click: () => assert.fail('disabled control clicked') };
  context.document.querySelectorAll = () => [button];
  assert.equal(context.clickPagination('next'), false);
});

test('selects overlapping Costco years and identifiable quarters conservatively', () => {
  const context = page('');
  assert.equal(context.periodOverlaps({ label: 'Q1 2026', value: '1' }, '2026-04-01', '2026-06-30'), false);
  assert.equal(context.periodOverlaps({ label: 'Q2 2026', value: '2' }, '2026-04-01', '2026-06-30'), true);
  assert.equal(context.periodOverlaps({ label: '2025', value: '3' }, '2026-04-01', '2026-06-30'), false);
  assert.equal(context.periodOverlaps({ label: 'Recent', value: '4' }, '2026-04-01', '2026-06-30'), true);
});

test('uses Costco’s next-page icon and honors a disabled button', () => {
  const context = page('');
  let clicked = false;
  const button = { getClientRects: () => [1], getAttribute: () => null, click: () => { clicked = true; } };
  context.document.querySelectorAll = selector => selector.includes('NavigateNextIcon') ? [{ closest: () => button }] : [];
  assert.equal(context.clickPagination('next'), true);
  assert.equal(clicked, true);
  button.disabled = true;
  assert.equal(context.clickPagination('next'), false);
});

test('waits through Costco loading before accepting changed receipt rows', async () => {
  const context = page('');
  let checks = 0;
  context.setTimeout = callback => { callback(); return 0; };
  context.testExecute = async (_id, fn) => {
    if (fn.name === 'chooseCostcoPeriod') return { changed: true };
    if (fn.name === 'costcoPageState') return { loading: ++checks === 1, empty: false };
    if (fn.name === 'scanPage') return [{ date: '2026-04-10' }];
  };
  vm.runInContext('execute = testExecute', context);
  const rows = await context.switchCostcoPeriod(1, 'q2', [{ date: '2026-01-10' }]);
  assert.equal(checks, 2);
  assert.equal(rows[0].date, '2026-04-10');
});

test('recognizes Costco Online’s empty default period so discovery can switch periods', async () => {
  const context = page('');
  context.document.querySelectorAll = selector => selector.includes('[role="heading"]') ? [{
    textContent: 'No orders are available for the selected date range.',
    getClientRects: () => [{}]
  }] : [];
  context.testExecute = async (_id, fn) => fn();
  vm.runInContext('execute = testExecute', context);
  const rows = await context.waitForCostcoPage(1, async () => []);
  assert.equal(rows.length, 0);
});

test('accepts a confirmed empty Costco period after switching', async () => {
  const context = page('');
  context.setTimeout = callback => { callback(); return 0; };
  context.testExecute = async (_id, fn) => fn.name === 'chooseCostcoPeriod' ? { changed: true } :
    fn.name === 'costcoPageState' ? { loading: false, empty: true } : [];
  vm.runInContext('execute = testExecute', context);
  const rows = await context.switchCostcoPeriod(1, 'q2', [{ date: '2026-01-10' }]);
  assert.equal(rows.length, 0);
});

test('prepares Costco gas and warehouse panels with an embedded body and no dialog role', async () => {
  const context = page('');
  context.location.origin = 'https://www.costco.com';
  context.getComputedStyle = () => ({ visibility: 'visible' });
  let marked = false;
  let closed = 0;
  let style;
  const close = {
    innerText: 'Close', textContent: 'Close', getClientRects: () => [{}],
    getAttribute: () => null,
    click: () => { closed++; panel.isConnected = false; }
  };
  const panel = {
    parentElement: context.document.body,
    innerText: 'Gas Station Receipt\nPrint Receipt\nClose',
    getClientRects: () => [{}],
    closest: () => null,
    querySelectorAll: selector => selector.startsWith('iframe') ? [{ getClientRects: () => [{}] }] : [close],
    setAttribute: name => { marked = name === 'data-groupsplit-print'; },
    removeAttribute: () => { marked = false; }
  };
  const print = { innerText: 'Print Receipt', parentElement: panel, getClientRects: () => [{}] };
  context.document.querySelectorAll = selector => selector === 'button, a, [role="button"]' ? [print]
    : selector === '[data-groupsplit-print]' && marked ? [panel] : [];
  context.document.querySelector = selector => selector === '[data-groupsplit-print]' && marked ? panel : null;
  context.document.createElement = () => ({ remove: () => { style = null; } });
  context.document.head = { append: element => { style = element; } };
  context.document.getElementById = id => style?.id === id ? style : null;
  for (const title of ['Gas Station Receipt', 'In-Warehouse Receipt']) {
    panel.innerText = title + '\nPrint Receipt\nClose';
    panel.isConnected = true;
    assert.equal((await context.prepareReceipt()).prepared, true);
    assert.equal(context.verifyPreparedCostcoReceipt(), true);
    assert.equal((await context.cleanupReceipt()).closed, true);
    assert.equal(context.verifyPreparedCostcoReceipt(), false);
  }
  assert.equal(closed, 2);
});

test('refuses to upload after the GroupSplit tab changes origin', async () => {
  const context = page('Warehouse 9/5/2026 $266.39 View Receipt');
  context.location.origin = 'https://other.example';
  await assert.rejects(context.uploadToInbox('', 'receipt.pdf', 'https://groupsplit.example'), /navigated away/);
});

test('uploads a PDF through the existing inbox endpoint and detects a retry', async () => {
  const context = page('Warehouse 9/5/2026 $266.39 View Receipt');
  context.location.origin = 'https://groupsplit.example';
  context.location.pathname = '/inbox/receipts';
  context.atob = atob;
  context.Uint8Array = Uint8Array;
  context.FormData = FormData;
  context.Blob = Blob;
  context.crypto = { randomUUID: () => '11111111-1111-4111-8111-111111111111' };
  context.fetch = async (url, options) => {
    assert.equal(url, '/api/receipts/inbox/11111111-1111-4111-8111-111111111111/files');
    assert.equal(options.credentials, 'same-origin');
    assert.equal(options.body.get('file').name, 'receipt.pdf');
    return { ok: true, json: async () => ({ receiptId: '22222222-2222-4222-8222-222222222222' }) };
  };
  const result = await context.uploadToInbox('JVBERg==', 'receipt.pdf', context.location.origin);
  assert.equal(result.ok, true);
  assert.equal(result.alreadyImported, true);
});

test('automatically reads a newly uploaded Costco PDF and saves the returned details', async () => {
  const context = page('');
  context.location.origin = 'https://groupsplit.example';
  context.location.pathname = '/inbox/receipts';
  Object.assign(context, { atob, Uint8Array, FormData, Blob });
  const receiptId = '11111111-1111-4111-8111-111111111111';
  const attachmentId = '22222222-2222-4222-8222-222222222222';
  const receipt = { merchantName: 'Costco', total: 38.35, items: [] };
  context.crypto = { randomUUID: () => receiptId };
  const requests = [];
  context.fetch = async (url, options) => {
    requests.push({ url, options });
    if (url.endsWith('/files')) return { ok: true, json: async () => ({ id: attachmentId, receiptId }) };
    if (url.endsWith('/transcribe')) return { ok: true, json: async () => ({ receipt }) };
    return { ok: true };
  };
  const result = await context.uploadToInbox('JVBERg==', 'receipt.pdf', context.location.origin);
  assert.equal(result.detailsSaved, true);
  assert.equal(requests[1].url, '/api/receipts/inbox/' + receiptId + '/files/' + attachmentId + '/transcribe');
  assert.equal(requests[1].options.method, 'POST');
  assert.match(requests[2].url, /\/extracted$/);
  assert.deepEqual(JSON.parse(requests[2].options.body), receipt);
});

test('saves extracted details only for a newly uploaded receipt', async () => {
  const context = page('');
  context.location.origin = 'https://groupsplit.example';
  context.location.pathname = '/inbox/receipts';
  Object.assign(context, { atob, Uint8Array, FormData, Blob });
  context.crypto = { randomUUID: () => '11111111-1111-4111-8111-111111111111' };
  const requests = [];
  context.fetch = async (url, options) => {
    requests.push({ url, options });
    return url.endsWith('/files')
      ? { ok: true, json: async () => ({ receiptId: '11111111-1111-4111-8111-111111111111' }) }
      : { ok: true };
  };
  const receipt = { merchantName: 'Amazon', total: 19.34 };
  const result = await context.uploadToInbox('JVBERg==', 'receipt.pdf', context.location.origin, receipt);
  assert.equal(result.detailsSaved, true);
  assert.match(requests[1].url, /\/extracted$/);
  assert.deepEqual(JSON.parse(requests[1].options.body), receipt);
});

test('keeps a successfully uploaded file when saving extracted details fails', async () => {
  const context = page('');
  context.location.origin = 'https://groupsplit.example';
  context.location.pathname = '/inbox/receipts';
  Object.assign(context, { atob, Uint8Array, FormData, Blob });
  context.crypto = { randomUUID: () => '11111111-1111-4111-8111-111111111111' };
  context.fetch = async url => url.endsWith('/files')
    ? { ok: true, json: async () => ({ receiptId: '11111111-1111-4111-8111-111111111111' }) }
    : { ok: false, status: 409, json: async () => ({ detail: 'Receipt was edited.' }) };
  const result = await context.uploadToInbox('JVBERg==', 'receipt.pdf', context.location.origin, { merchantName: 'Amazon' });
  assert.equal(result.ok, true);
  assert.equal(result.detailsSaved, false);
  assert.equal(result.detail, 'Receipt was edited.');
});

test('finds Amazon invoice dates and verifies the invoice host', () => {
  const context = page('');
  context.location.href = 'https://www.amazon.com/your-orders/orders';
  const card = {
    innerText: 'Order placed September 5, 2026 View order details Invoice',
    querySelector: () => ({ href: 'https://www.amazon.com/gp/css/summary/print.html?orderID=example' })
  };
  context.document.querySelectorAll = () => [card];
  const rows = context.scanAmazonPage();
  assert.equal(rows[0].date, '2026-09-05');
  assert.equal(rows[0].label, 'Amazon order placed 2026-09-05');
  assert.match(rows[0].url, /^https:\/\/www\.amazon\.com\//);
  card.querySelector = () => ({ href: 'https://other.example/summary/print.html' });
  assert.equal(context.scanAmazonPage()[0].url, null);
});

test('reads the ship-to recipient without using the order buyer', () => {
  const context = page('');
  context.location.href = 'https://www.amazon.com/your-orders/orders';
  const card = {
    innerText: 'Order placed September 5, 2026\nShip to\nAnother Person\nOrdered by\nFamily Member\nView invoice',
    querySelector: selector => selector === '.yohtmlc-recipient' ? { innerText: 'Ship to\nAnother Person' }
      : { href: 'https://www.amazon.com/gp/css/summary/print.html?orderID=example' }
  };
  context.document.querySelectorAll = () => [card];
  assert.equal(context.scanAmazonPage()[0].buyer, 'Another Person');
  card.innerText = 'Pedido realizado el 5 de septiembre de 2026\nEnvío a\nMaría José\nPedido realizado por\nOtra Persona';
  card.querySelector = selector => selector === '.yohtmlc-recipient' ? { innerText: 'Envío a\nMaría José' } : null;
  assert.equal(context.scanAmazonPage()[0].buyer, 'María José');
  card.innerText = 'Order placed September 5, 2026\nOrdered by\nFamily Member\nView invoice';
  card.querySelector = selector => selector === '.yohtmlc-recipient' ? null
    : { href: 'https://www.amazon.com/gp/css/summary/print.html?orderID=example' };
  assert.equal(context.scanAmazonPage()[0].buyer, null);
});

test('filters the selected date range by recipient without assigning unknown orders', () => {
  const context = page('');
  const orders = [
    { date: '2026-10-01', buyer: 'Person One' },
    { date: '2026-10-02', buyer: 'Person Two' },
    { date: '2026-10-03', buyer: null },
    { date: '2026-09-30', buyer: 'Person One' }
  ];
  const selected = context.filterAmazonOrders(orders, '2026-10-01', '2026-10-31', 'buyer:Person One');
  assert.equal(selected.length, 1);
  assert.equal(selected[0].date, '2026-10-01');
  const unknown = context.filterAmazonOrders(orders, '2026-10-01', '2026-10-31', 'unknown');
  assert.equal(unknown.length, 1);
  assert.equal(unknown[0].buyer, null);
});

test('follows only a forward Amazon orders page within the selected period', () => {
  const context = page('');
  context.location.href = 'https://www.amazon.com/your-orders/orders?timeFilter=year-2026&startIndex=0';
  context.document.querySelectorAll = () => [{ href: 'https://www.amazon.com/your-orders/orders?timeFilter=year-2026&startIndex=10' }];
  assert.match(context.readAmazonNavigation().next, /startIndex=10/);
  context.document.querySelectorAll = () => [{ href: 'https://www.amazon.com/your-orders/orders?timeFilter=year-2025&startIndex=10' }];
  assert.match(context.readAmazonNavigation().error, /unexpected next page/i);
  context.document.querySelectorAll = () => [{ href: 'https://untrusted.example/your-orders/orders?startIndex=10' }];
  assert.match(context.readAmazonNavigation().error, /unexpected next-page address/i);
});

test('accepts Amazon page-number pagination and preserves the selected period', () => {
  const context = page('');
  context.location.href = 'https://www.amazon.com/your-orders/orders?timeFilter=year-2026';
  context.document.querySelectorAll = () => [{ href: 'https://www.amazon.com/-/es/your-orders/orders?page=1' }];
  const next = new URL(context.readAmazonNavigation().next);
  assert.equal(next.pathname, '/-/es/your-orders/orders');
  assert.equal(next.searchParams.get('page'), '1');
  assert.equal(next.searchParams.get('timeFilter'), 'year-2026');
  context.location.href = next.href;
  context.document.querySelectorAll = () => [{ href: 'https://www.amazon.com/your-orders/orders?page=0&timeFilter=year-2026' }];
  assert.match(context.readAmazonNavigation().error, /unexpected next page/i);
});

test('follows Portuguese Amazon US order-history routes', () => {
  const context = page('');
  for (const locale of ['pt', 'pt-BR']) {
    const route = 'https://www.amazon.com/-/' + locale + '/your-orders/orders';
    context.location.href = route + '?page=1&timeFilter=year-2026';
    context.document.querySelectorAll = () => [{ href: route + '?page=2' }];
    assert.equal(context.readAmazonNavigation().next, route + '?page=2&timeFilter=year-2026');
  }
});

test('waits for the requested page-number result', async () => {
  const context = page('');
  let attempts = 0;
  context.setTimeout = callback => { callback(); return 0; };
  context.testExecute = async () => ({ current: `https://www.amazon.com/your-orders/orders?page=${attempts++ ? 1 : 0}`, next: null });
  vm.runInContext('execute = testExecute', context);
  const result = await context.waitForAmazonNavigation(7, 'https://www.amazon.com/your-orders/orders', { key: 'page', value: '1' });
  assert.match(result.current, /page=1/);
  assert.equal(attempts, 2);
});

test('waits for Amazon navigation when Chrome returns a transient null result', async () => {
  const context = page('');
  let attempts = 0;
  context.setTimeout = callback => { callback(); return 0; };
  context.testExecute = async () => ++attempts === 1 ? null : { current: 'https://www.amazon.com/your-orders/orders', next: null };
  vm.runInContext('execute = testExecute', context);
  const navigation = await context.waitForAmazonNavigation(7);
  assert.equal(attempts, 2);
  assert.equal(navigation.next, null);
});

test('reports navigation errors returned from the Amazon page', async () => {
  const context = page('');
  context.testExecute = async () => ({ error: 'Open Amazon Your Orders before finding receipts.' });
  vm.runInContext('execute = testExecute', context);
  await assert.rejects(context.waitForAmazonNavigation(7), /open amazon your orders/i);
});

test('continues past unreadable and older cards, deduplicates orders, and restores the original page', async () => {
  const context = page('');
  context.status = context.document.getElementById('status');
  let current = 'https://www.amazon.com/your-orders/orders?timeFilter=year-2026&startIndex=0';
  const next = 'https://www.amazon.com/your-orders/orders?timeFilter=year-2026&startIndex=10';
  const navigated = [];
  context.chrome = { tabs: { update: async (_id, args) => { current = args.url; navigated.push(current); } } };
  context.setTimeout = callback => { callback(); return 0; };
  context.testExecute = async (_id, fn) => fn.name === 'readAmazonNavigation'
    ? { current, next: current === next ? null : next }
    : current === next
      ? [{ date: '2026-10-02', buyer: 'Person Two', url: 'https://www.amazon.com/gp/css/summary/print.html?orderID=2' },
         { date: '2026-10-01', buyer: 'Person One', url: 'https://www.amazon.com/gp/css/summary/print.html?orderID=1' }]
      : [null, { date: '2026-10-01', buyer: 'Person One', url: 'https://www.amazon.com/gp/css/summary/print.html?orderID=1' }];
  vm.runInContext('execute = testExecute', context);
  const report = { pages: 0, unreadable: 0 };
  const results = await context.collectAmazonOrders(7, '2026-10-02', '2026-10-31', report);
  assert.equal(results.length, 2);
  assert.deepEqual(report, { pages: 2, unreadable: 1 });
  assert.equal(navigated.length, 2);
  assert.match(navigated[1], /startIndex=0/);
});

test('scans every selected Amazon year and restores the original page', async () => {
  const context = page('');
  let current = 'https://www.amazon.com/your-orders/orders?timeFilter=months-3';
  const original = current;
  const visited = [];
  context.chrome = { tabs: { update: async (_id, args) => { current = args.url; visited.push(current); } } };
  context.setTimeout = callback => { callback(); return 0; };
  context.testExecute = async (_id, fn) => fn.name === 'readAmazonNavigation'
    ? { current, next: null }
    : [{ date: new URL(current).searchParams.get('timeFilter') === 'year-2025' ? '2025-12-30' : '2026-01-02',
         url: null, buyer: null }];
  vm.runInContext('execute = testExecute', context);
  const orders = await context.collectAmazonOrders(1, '2025-12-01', '2026-01-31');
  assert.deepEqual(Array.from(orders, order => order.date), ['2026-01-02', '2025-12-30']);
  assert.equal(visited.length, 3);
  assert.equal(visited[2], original);
});

test('uses the order-placed date even when a delivery date is also present', () => {
  const context = page('');
  context.location.href = 'https://www.amazon.com/your-orders/orders';
  const card = { innerText: 'Delivered October 10, 2026 Order placed September 5, 2026', querySelector: () => null };
  context.document.querySelectorAll = () => [card];
  assert.equal(context.scanAmazonPage()[0].date, '2026-09-05');
});

test('reuses one background tab for multiple Amazon invoices', async () => {
  const context = page('');
  const created = [], updated = [];
  const styles = [], detached = [], media = [];
  context.chrome = { tabs: {
    create: async args => { created.push(args); return { id: 42 }; },
    update: async (id, args) => { updated.push({ id, ...args }); },
    get: async () => ({ status: 'complete' })
  }, debugger: {
    attach: async () => {},
    sendCommand: async (_target, command, options) => {
      if (command === 'Emulation.setEmulatedMedia') { media.push(options.media); return; }
      assert.equal(command, 'Page.printToPDF');
      assert.equal(options.marginLeft, options.marginRight);
      assert.equal(options.scale, (8.5 - 2 * 0.375) * 96 / 1200);
      return { data: updated.length ? 'invalid' : 'JVBERi0xLjQ=' };
    },
    detach: async target => { detached.push(target.tabId); }
  } };
  context.testExecute = async (_id, fn, args) => {
    if (fn.name === 'verifyAmazonInvoice') return true;
    if (fn.name === 'setAmazonPrintStyle') { styles.push(args[0]); return; }
    if (fn.name === 'amazonPrintWidth') return 1200;
    throw new Error('Unexpected invoice script: ' + fn.name);
  };
  vm.runInContext('execute = testExecute', context);
  const workspace = { tabId: null };
  const row = id => ({ date: '2026-10-01', url: `https://www.amazon.com/gp/css/summary/print.html?orderID=${id}` });
  const summary = await context.prepareAmazonInvoice(row('123-1234567-1234567'), workspace);
  assert.equal(summary.pdf, 'JVBERi0xLjQ=');
  await assert.rejects(context.prepareAmazonInvoice(row('123-1234567-7654321'), workspace), /did not produce a PDF/);
  assert.deepEqual(styles, [true, false, true, false]);
  assert.deepEqual(media, ['print', '', 'print', '']);
  assert.deepEqual(detached, [42, 42]);
  assert.equal(created.length, 1);
  assert.equal(created[0].active, false);
  assert.equal(updated.length, 1);
  assert.equal(updated[0].id, 42);
});

test('only accepts the official GroupSplit origin or a local app', () => {
  const context = page('');
  assert.equal(context.validAppOrigin('https://groupsplit.sussman.win/inbox/receipts'), 'https://groupsplit.sussman.win');
  assert.equal(context.validAppOrigin('https://localhost:7287/inbox/receipts'), 'https://localhost:7287');
  assert.throws(() => context.validAppOrigin('https://unrelated.example/inbox/receipts'));
  assert.throws(() => context.validAppOrigin('http://groupsplit.sussman.win/inbox/receipts'));
  assert.throws(() => context.validAppOrigin('https://groupsplit.sussman.win.attacker.example/inbox/receipts'));
  assert.throws(() => context.validAppOrigin(undefined), /receipts inbox/);
});

test('connects to a permitted retailer tab when Chrome does not expose its URL', async () => {
  const context = page('');
  vm.runInContext('sourceTabId = 7', context);
  context.document.getElementById('retailer').value = 'amazon';
  context.chrome = {
    tabs: { get: async () => ({ id: 7 }) },
    scripting: { executeScript: async () => [{ result: { origin: 'https://www.amazon.com', pathname: '/your-orders/orders' } }] }
  };
  const tab = await context.activeCostcoTab();
  assert.equal(tab.id, 7);
});

test('connects to the inbox when its tab metadata does not expose a URL', async () => {
  const context = page('', 'https://example.test/?appTab=7');
  context.chrome = {
    tabs: { get: async () => ({ id: 7 }) },
    scripting: { executeScript: async () => [{ result: { origin: 'https://localhost:7287', pathname: '/inbox/receipts' } }] }
  };
  await context.connectInbox();
  assert.match(context.document.getElementById('connection').textContent, /Inbox connected/);
});

test('opens with the theme supplied by the inbox', () => {
  const context = page('', 'https://example.test/?theme=dark');
  assert.equal(context.document.documentElement.dataset.theme, 'dark');
});

test('retailer access is limited to Amazon and Costco', () => {
  const manifest = JSON.parse(fs.readFileSync(require('node:path').join(__dirname, 'manifest.json'), 'utf8'));
  assert.deepEqual(manifest.optional_host_permissions, ['https://www.amazon.com/*', 'https://www.costco.com/*']);
  assert.match(manifest.content_security_policy.extension_pages, /connect-src 'none'/);
  assert.ok(manifest.permissions.includes('debugger'));
});

test('changing the date clears the prepared receipt selection', () => {
  const context = page('');
  context.document.getElementById('receipts').replaceChildren = () => {};
  vm.runInContext('selection = { retailer: "amazon", rows: [{}] }', context);
  context.document.getElementById('from').change();
  assert.equal(vm.runInContext('selection', context), null);
  assert.equal(context.document.getElementById('download').hidden, true);
});

test('Costco saving requires a connected inbox', async () => {
  const context = page('');
  vm.runInContext('selection = { retailer: "costco", rows: [{}] }', context);
  await context.document.getElementById('download').click();
  assert.match(context.document.getElementById('status').textContent, /signed-in GroupSplit receipts inbox/i);
});

test('does not upload a receipt when the connected inbox has changed', async () => {
  const context = page('');
  vm.runInContext('inbox = { tabId: 7, origin: "https://groupsplit.sussman.win" }', context);
  context.chrome = { tabs: { get: async () => ({ id: 7 }) }, scripting: { executeScript: () => assert.fail('must not upload') } };
  context.testExecute = async () => ({ origin: 'https://other.example', pathname: '/inbox/receipts' });
  vm.runInContext('execute = testExecute', context);
  await assert.rejects(context.uploadReceiptFile('data', 'receipt.pdf'), /inbox/);
});

test('uploads to the connected inbox using an isolated script world', async () => {
  const context = page('');
  vm.runInContext('inbox = { tabId: 7, origin: "https://groupsplit.sussman.win" }', context);
  let upload;
  context.chrome = { tabs: { get: async () => ({ id: 7 }) },
    scripting: { executeScript: async options => { upload = options; return [{ result: { ok: true } }]; } } };
  context.testExecute = async () => ({ origin: 'https://groupsplit.sussman.win', pathname: '/inbox/receipts' });
  vm.runInContext('execute = testExecute', context);
  await context.uploadReceiptFile('data', 'receipt.pdf');
  assert.equal(upload.world, 'ISOLATED');
  assert.equal(upload.target.tabId, 7);
});

test('does not read an invoice after the tab changes to another order', () => {
  const context = page('');
  context.location = { href: 'https://www.amazon.com/gp/css/summary/print.html?orderID=other', protocol: 'https:',
    hostname: 'www.amazon.com', pathname: '/gp/css/summary/print.html' };
  assert.equal(context.verifyAmazonInvoice('expected'), false);
});

test('refuses invoice URLs with credentials or a nonstandard port', async () => {
  const context = page('');
  for (const url of ['https://name@www.amazon.com/gp/css/summary/print.html?orderID=123-1234567-1234567',
    'https://www.amazon.com:8443/gp/css/summary/print.html?orderID=123-1234567-1234567'])
    await assert.rejects(context.prepareAmazonInvoice({ url }, {}), /not a supported Amazon address/);
});

test('rejects malformed receipt identifiers from the upload response', async () => {
  const context = page('');
  context.location.origin = 'https://groupsplit.example';
  context.location.pathname = '/inbox/receipts';
  Object.assign(context, { atob, Uint8Array, FormData, Blob });
  context.crypto = { randomUUID: () => '11111111-1111-4111-8111-111111111111' };
  context.fetch = async () => ({ ok: true, json: async () => ({ receiptId: 'not-an-id' }) });
  const result = await context.uploadToInbox('JVBERg==', 'receipt.pdf', context.location.origin);
  assert.equal(result.ok, false);
  assert.match(result.detail, /unexpected response/i);
});

test('creates a private summary using only allowlisted values', () => {
  const context = page('');
  context.btoa = btoa;
  const output = context.privateCostcoOnlineReceipt('2026-11-02',
    { items: 1799, shipping: 0, tax: 135, total: 1934, products: [{ name: 'Storage bins', quantity: 1, unitPrice: 1799 }], customer: 'PRIVATE NAME', address: 'PRIVATE ADDRESS', buyer: 'PRIVATE BUYER' },
    'a'.repeat(32));
  const pdf = atob(output.pdf);
  assert.match(pdf, /^%PDF-1\.4/);
  assert.doesNotMatch(pdf, /PRIVATE NAME|PRIVATE ADDRESS|PRIVATE BUYER/);
  assert.doesNotMatch(JSON.stringify(output.receipt), /PRIVATE NAME|PRIVATE ADDRESS|PRIVATE BUYER/);
  assert.equal(output.receipt.total, 19.34);
  assert.equal(output.receipt.items[0].name, 'Storage bins');
  assert.match(pdf, /Storage bins/);
});

test('renders multiple item names, quantities and prices in a valid PDF', t => {
  const context = page('');
  context.btoa = btoa;
  const result = context.privateCostcoOnlineReceipt('2026-10-28', {
    items: 4400, shipping: 500, tax: 392, total: 5292,
    products: [{ name: 'Storage (small)', quantity: 2, unitPrice: 1200 },
      { name: 'Free item', quantity: 1, unitPrice: 0 },
      { name: 'Storage large', quantity: 1, unitPrice: 2000 }]
  }, 'b'.repeat(32));
  assert.equal(result.receipt.items.length, 4);
  assert.equal(result.receipt.items[0].quantity, 2);
  assert.equal(result.receipt.items[1].totalPrice, 0);
  assert.equal(result.receipt.items[3].name, 'Shipping');
  const pdf = Buffer.from(result.pdf, 'base64');
  assert.ok(pdf.toString('ascii').includes('Storage \\(small\\)'));
  const { spawnSync } = require('node:child_process');
  const check = spawnSync('pdfinfo', ['-'], { input: pdf, encoding: 'utf8' });
  if (check.error?.code === 'ENOENT') return t.diagnostic('pdfinfo unavailable; skipped external PDF validation');
  assert.equal(check.status, 0, check.stderr);
  assert.match(check.stdout, /Pages:\s+1/);
});

test('rejects an invoice when totals cannot be reconciled', () => {
  const context = page('');
  assert.throws(() => context.privateCostcoOnlineReceipt('2026-11-02',
    { items: 1799, shipping: 0, tax: 135, total: 2000, products: [{ name: 'Bins', quantity: 1, unitPrice: 1799 }] }, 'a'.repeat(32)), /reconciled/);
});

test('refuses item totals that do not match the subtotal', () => {
  const context = page('');
  assert.throws(() => context.privateCostcoOnlineReceipt('2026-11-02',
    { items: 1799, shipping: 0, tax: 135, total: 1934, products: [{ name: 'Bins', quantity: 2, unitPrice: 1799 }] }, 'a'.repeat(32)), /reconciled/);
});

test('verifies the printed order number and final total on English, Spanish, and Portuguese invoices', () => {
  const orderId = '123-1234567-1234567';
  const context = page('', 'https://www.amazon.com/gp/css/summary/print.html?orderID=' + orderId);
  context.document.readyState = 'complete';
  context.document.querySelector = () => null;
  for (const [heading, total] of [
    ['Order Summary', 'Grand Total: $68.92'],
    ['Resumen del pedido', 'Total (I.V.A. Incluido): US$68.92'],
    ['Resumo do pedido', 'Total do pedido: US$ 68,92']
  ]) {
    context.document.body.innerText = [heading, orderId, total].join('\n');
    assert.equal(context.verifyAmazonInvoice(orderId), true);
    context.document.body.innerText = [heading, '123-1234567-7654321', total].join('\n');
    assert.equal(context.verifyAmazonInvoice(orderId), false);
  }
  context.document.body.innerText = 'Pedido ' + orderId + '\nTotal antes de impuestos: US$68.92';
  assert.equal(context.verifyAmazonInvoice(orderId), false);
});


test('closes a separate importer after successful completion', () => {
  const context = page('');
  let closed = false;
  context.window = { close: () => { closed = true; } };
  context.closeCompletedImporter();
  assert.equal(closed, true);
});
