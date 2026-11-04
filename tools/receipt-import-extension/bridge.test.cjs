const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

function background() {
  let handler;
  const opened = [];
  const chrome = {
    runtime: { id: 'our-extension', getURL: value => `chrome-extension://our-extension/${value}`,
      onMessage: { addListener: value => { handler = value; } } },
    action: { onClicked: { addListener() {} } },
    tabs: { create: async options => { opened.push(options.url); } }
  };
  vm.runInNewContext(fs.readFileSync(path.join(__dirname, 'background.js'), 'utf8'), { chrome, URL });
  return { handler: (...args) => handler(...args), opened };
}

test('opens the importer only for the trusted receipts inbox', async () => {
  const { handler, opened } = background();
  let response;
  const result = handler({ type: 'open-receipt-importer' },
    { id: 'our-extension', frameId: 0, tab: { id: 42 }, url: 'https://localhost:7287/inbox/receipts' },
    value => { response = value; });
  assert.equal(result, true);
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(opened[0], 'chrome-extension://our-extension/popup.html?appTab=42&theme=light');
  assert.equal(response.opened, true);
});

test('refuses unknown origins, subframes, and other pages', () => {
  const { handler, opened } = background();
  for (const sender of [
    { id: 'our-extension', frameId: 0, tab: { id: 42 }, url: 'https://untrusted.example/inbox/receipts' },
    { id: 'our-extension', frameId: 1, tab: { id: 42 }, url: 'https://localhost:7287/inbox/receipts' },
    { id: 'our-extension', frameId: 0, tab: { id: 42 }, url: 'https://localhost:7287/settings' },
    { id: 'other-extension', frameId: 0, tab: { id: 42 }, url: 'https://localhost:7287/inbox/receipts' }
  ]) assert.equal(handler({ type: 'open-receipt-importer' }, sender, () => {}), false);
  assert.equal(opened.length, 0);
});

test('carries the app theme without carrying receipt or account information', async () => {
  const { handler, opened } = background();
  handler({ type: 'open-receipt-importer', theme: 'dark' },
    { id: 'our-extension', frameId: 0, tab: { id: 42 }, url: 'https://localhost:7287/inbox/receipts' }, () => {});
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(opened[0], 'chrome-extension://our-extension/popup.html?appTab=42&theme=dark');
});

test('rate limits repeated page requests to open importer tabs', async () => {
  const { handler, opened } = background();
  const sender = { id: 'our-extension', frameId: 0, tab: { id: 42 }, url: 'https://localhost:7287/inbox/receipts' };
  handler({ type: 'open-receipt-importer' }, sender, () => {});
  let response;
  assert.equal(handler({ type: 'open-receipt-importer' }, sender, value => { response = value; }), false);
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(opened.length, 1);
  assert.equal(response.opened, false);
});
