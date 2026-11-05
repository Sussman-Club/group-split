const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

test('mouse dragging pans both axes while touch retains native scrolling', () => {
  const handlers = new Map();
  const classes = new Set();
  let captured = null;
  const viewport = { scrollWidth: 1000, clientWidth: 400, scrollHeight: 1200, clientHeight: 500,
    scrollLeft: 120, scrollTop: 200,
    classList: { add: value => classes.add(value), remove: value => classes.delete(value) },
    addEventListener: (type, handler) => handlers.set(type, handler),
    removeEventListener: type => handlers.delete(type),
    setPointerCapture: id => { captured = id; }, hasPointerCapture: id => captured === id,
    releasePointerCapture: () => { captured = null; } };
  const source = fs.readFileSync(path.join(__dirname, '../../src/GroupSplit.App/GroupSplit.App.Shared/wwwroot/js/pdf-pan.js'), 'utf8')
    .replace('export function', 'function');
  const context = {};
  vm.runInNewContext(source, context);
  const dispose = context.enablePdfPan(viewport);
  let prevented = false;
  handlers.get('pointerdown')({ pointerType: 'touch', button: 0, pointerId: 2, preventDefault() { prevented = true; } });
  assert.equal(prevented, false);
  handlers.get('pointerdown')({ pointerType: 'mouse', button: 0, pointerId: 1, clientX: 100, clientY: 100, preventDefault() { prevented = true; } });
  handlers.get('pointermove')({ pointerId: 1, clientX: 70, clientY: 60 });
  assert.equal(viewport.scrollLeft, 150);
  assert.equal(viewport.scrollTop, 240);
  assert.equal(classes.has('is-panning'), true);
  handlers.get('pointerup')({ pointerId: 1 });
  assert.equal(classes.has('is-panning'), false);
  dispose();
  assert.equal(handlers.size, 0);
});
