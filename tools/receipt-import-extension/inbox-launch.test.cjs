const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

test('falls back to a separate window when the embedded importer cannot start', async () => {
  const listeners = new Map();
  const timers = [];
  const window = {
    addEventListener: (type, fn) => listeners.set(type, fn),
    removeEventListener: type => listeners.delete(type),
    dispatchEvent: event => {
      if (event.type === 'groupsplit:open-receipt-importer-window')
        listeners.get('groupsplit:receipt-importer-window-opened')?.({ type: 'opened' });
    }
  };
  const source = fs.readFileSync(path.join(__dirname, '../../src/GroupSplit.App/GroupSplit.App.Shared/wwwroot/js/receipt-importer.js'), 'utf8')
    .replaceAll('export function ', 'function ');
  const context = { window, Event: class { constructor(type) { this.type = type; } },
    setTimeout: fn => { timers.push(fn); return timers.length; }, clearTimeout() {} };
  vm.runInNewContext(source, context);
  const result = context.openReceiptImporter();
  timers[0]();
  assert.equal(await result, true);
  assert.equal(listeners.size, 0);
});
