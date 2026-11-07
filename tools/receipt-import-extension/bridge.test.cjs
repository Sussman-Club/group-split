const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

test('disconnects the page bridge when a reload invalidates the extension context', () => {
  const listeners = new Map();
  let themeChanged;
  let disconnected = false;
  vm.runInNewContext(fs.readFileSync(path.join(__dirname, 'app-bridge.js'), 'utf8'), {
    chrome: { runtime: { id: 'our-extension', onMessage: { addListener() {} },
      sendMessage() { throw new Error('Extension context invalidated.'); } } },
    document: { documentElement: { dataset: {} } },
    window: {
      addEventListener: (type, listener) => listeners.set(type, listener),
      removeEventListener: type => listeners.delete(type)
    },
    MutationObserver: class {
      constructor(callback) { themeChanged = callback; }
      observe() {}
      disconnect() { disconnected = true; }
    }
  });
  assert.doesNotThrow(themeChanged);
  assert.equal(disconnected, true);
  assert.equal(listeners.has('groupsplit:open-receipt-importer'), false);
});

function background() {
  let handler;
  const opened = [];
  const chrome = {
    runtime: { id: 'our-extension', getURL: value => `chrome-extension://our-extension/${value}`,
      onMessage: { addListener: value => { handler = value; } } },
    action: { onClicked: { addListener() {} } },
    windows: { create: async options => { opened.push(options); } }
  };
  vm.runInNewContext(fs.readFileSync(path.join(__dirname, 'background.js'), 'utf8'), { chrome, URL });
  return { handler: (...args) => handler(...args), opened };
}

test('returns an embedded importer only for the trusted receipts inbox', async () => {
  const { handler, opened } = background();
  let response;
  const result = handler({ type: 'open-receipt-importer' },
    { id: 'our-extension', frameId: 0, tab: { id: 42, windowId: 9, url: 'https://localhost:7287/inbox/receipts' }, url: 'https://localhost:7287/inbox/receipts' },
    value => { response = value; });
  assert.equal(result, false);
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(opened.length, 0);
  assert.equal(response.opened, true);
  assert.equal(response.url, 'chrome-extension://our-extension/popup.html?embedded=1&theme=light');
});

test('opens after navigating to the receipts inbox within the app', () => {
  const { handler } = background();
  let response;
  handler({ type: 'open-receipt-importer' },
    { id: 'our-extension', frameId: 0, tab: { id: 42, url: 'https://localhost:7287/inbox/receipts' }, url: 'https://localhost:7287/' },
    value => { response = value; });
  assert.equal(response.opened, true);
});

test('refuses unknown origins, subframes, and other pages', () => {
  const { handler, opened } = background();
  for (const sender of [
    { id: 'our-extension', frameId: 0, tab: { id: 42, url: 'https://untrusted.example/inbox/receipts' }, url: 'https://untrusted.example/inbox/receipts' },
    { id: 'our-extension', frameId: 1, tab: { id: 42, url: 'https://localhost:7287/inbox/receipts' }, url: 'https://localhost:7287/inbox/receipts' },
    { id: 'our-extension', frameId: 0, tab: { id: 42, url: 'https://localhost:7287/settings' }, url: 'https://localhost:7287/inbox/receipts' },
    { id: 'our-extension', frameId: 0, tab: { id: 42, url: 'https://localhost:7287/inbox/receipts' }, url: 'https://untrusted.example/' },
    { id: 'other-extension', frameId: 0, tab: { id: 42, url: 'https://localhost:7287/inbox/receipts' }, url: 'https://localhost:7287/inbox/receipts' }
  ]) {
    assert.equal(handler({ type: 'open-receipt-importer' }, sender, () => {}), false);
    assert.equal(handler({ type: 'open-receipt-importer-window' }, sender, () => {}), false);
  }
  assert.equal(opened.length, 0);
});

test('binds an embedded importer to its own trusted inbox tab', () => {
  const { handler } = background();
  const sender = { id: 'our-extension', frameId: 3, url: 'chrome-extension://our-extension/popup.html?embedded=1&theme=light',
    tab: { id: 42, windowId: 9, url: 'https://groupsplit.sussman.win/inbox/receipts' } };
  let context;
  handler({ type: 'embedded-context' }, sender, value => { context = value; });
  assert.equal(context.tabId, 42);
  assert.equal(context.windowId, 9);
  context = null;
  handler({ type: 'embedded-context' }, { ...sender, tab: { ...sender.tab, url: 'https://evil.example/inbox/receipts' } }, value => { context = value; });
  assert.equal(context, null);
});

test('carries the app theme without carrying receipt or account information', async () => {
  const { handler, opened } = background();
  let response;
  handler({ type: 'open-receipt-importer', theme: 'dark' },
    { id: 'our-extension', frameId: 0, tab: { id: 42, url: 'https://localhost:7287/inbox/receipts' }, url: 'https://localhost:7287/inbox/receipts' }, value => { response = value; });
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(opened.length, 0);
  assert.equal(response.url, 'chrome-extension://our-extension/popup.html?embedded=1&theme=dark');
});

test('opens a separate window when embedding is unavailable and rate limits repeats', async () => {
  const { handler, opened } = background();
  const sender = { id: 'our-extension', frameId: 0, tab: { id: 42, windowId: 9, url: 'https://localhost:7287/inbox/receipts' }, url: 'https://localhost:7287/inbox/receipts' };
  let first;
  assert.equal(handler({ type: 'open-receipt-importer-window', theme: 'dark' }, sender, value => { first = value; }), true);
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(first.opened, true);
  assert.equal(opened.length, 1);
  assert.equal(opened[0].url, 'chrome-extension://our-extension/popup.html?appTab=42&theme=dark&windowId=9');
  let response;
  assert.equal(handler({ type: 'open-receipt-importer-window' }, sender, value => { response = value; }), false);
  assert.equal(response.opened, false);
});
