// Run with: node tests/browser/android-device.check.cjs
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const source = fs.readFileSync(path.join(__dirname,
    '../../src/GroupSplit.App/GroupSplit.App.Shared/wwwroot/gs.js'), 'utf8');

for (const [device, userAgent, android] of [
    ['Android phone', 'Mozilla/5.0 (Linux; Android 15; Pixel 9) AppleWebKit/537.36 Chrome/130.0 Mobile Safari/537.36', true],
    ['Android tablet', 'Mozilla/5.0 (Linux; Android 14; SM-X710) AppleWebKit/537.36 Chrome/130.0 Safari/537.36', true],
    ['iPhone', 'Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 Mobile/15E148 Safari/604.1', false],
    ['iPad', 'Mozilla/5.0 (iPad; CPU OS 18_0 like Mac OS X) AppleWebKit/605.1.15 Mobile/15E148 Safari/604.1', false],
    ['iPad desktop mode', 'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15) AppleWebKit/605.1.15 Version/18.0 Safari/605.1.15', false],
    ['Windows desktop', 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/130.0 Safari/537.36', false]
]) {
    const attributes = new Set();
    const root = {
        dataset: {},
        toggleAttribute(name, enabled) { enabled ? attributes.add(name) : attributes.delete(name); },
        setAttribute(name) { attributes.add(name); },
        hasAttribute(name) { return attributes.has(name); }
    };
    const window = {matchMedia: () => ({matches:false})};
    vm.runInNewContext(source, {
        document: {documentElement: root, body: null, addEventListener() {}},
        window, navigator: {userAgent}, localStorage: {getItem: () => null},
        MutationObserver: class {observe() {}}
    });
    assert.equal(attributes.has('data-gs-android'), android, device + ' on first load');
    // Enhanced navigation replaces root attributes with the server copy.
    attributes.clear();
    window.gs.reapply();
    assert.equal(attributes.has('data-gs-android'), android, device + ' after navigation');
}
console.log('6 device profiles passed on first load and after enhanced navigation.');
