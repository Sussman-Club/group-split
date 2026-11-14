import { test } from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";

test("browser push registers independent preferences, refreshes and unregisters", async () => {
    const requests = [];
    const values = new Map();
    let click, onMessage;
    globalThis.window = globalThis;
    globalThis.isSecureContext = true;
    globalThis.PushManager = class {};
    globalThis.Notification = { permission: "default", requestPermission: async () => (Notification.permission = "granted") };
    globalThis.document = { addEventListener: (_, handler) => click = handler };
    Object.defineProperty(globalThis, "navigator", { configurable: true, value: {
        userAgent: "test", platform: "test", maxTouchPoints: 0,
        serviceWorker: { register: async () => ({ showNotification: (...args) => requests.push(args) }) }
    } });
    globalThis.localStorage = { getItem: key => values.get(key), setItem: (key, value) => values.set(key, value), removeItem: key => values.delete(key) };
    globalThis.fetch = async (url, options = {}) => {
        if (url === "/push-config") return { json: async () => ({ apiKey: "key", projectId: "project", messagingSenderId: "sender", appId: "app", vapidKey: "vapid" }) };
        requests.push({ url, ...options });
        return { ok: true };
    };
    globalThis.__appSdk = { getApps: () => [], initializeApp: config => config };
    globalThis.__messagingSdk = { isSupported: async () => true, getMessaging: () => ({}),
        onMessage: (_, handler) => onMessage = handler, getToken: async () => "token", deleteToken: async () => true };
    let source = await readFile(new URL("../src/GroupSplit.App/GroupSplit.App.Shared/wwwroot/js/browser-push.js", import.meta.url), "utf8");
    source = source.replace('await import("https://www.gstatic.com/firebasejs/12.4.0/firebase-app.js")', "globalThis.__appSdk")
        .replace('await import("https://www.gstatic.com/firebasejs/12.4.0/firebase-messaging.js")', "globalThis.__messagingSdk");
    const push = await import(`data:text/javascript;base64,${Buffer.from(source).toString("base64")}`);
    await push.refresh("owner");
    click({ target: { closest: () => ({ checked: true }) } });
    let state = await push.setEnabled("push.expenses", true);
    assert.deepEqual(state.preferences, ["push.expenses"]);
    state = await push.setEnabled("push.settlements", true);
    assert.deepEqual(state.preferences, ["push.expenses", "push.settlements"]);
    assert.equal(JSON.parse(requests.at(-1).body).settlementsEnabled, true);
    await push.setEnabled("push.expenses", false);
    assert.equal(JSON.parse(requests.at(-1).body).expensesEnabled, false);
    onMessage({ data: { route: "//evil.example", deliveryId: "one" }, notification: { title: "Alert", body: "Body" } });
    assert.equal(requests.at(-1)[1].data.route, "/account");
    await push.unregister(false);
    assert.equal(requests.at(-1).method, "DELETE");
    assert.equal(values.size, 0);
    Notification.permission = "denied";
    const count = requests.length;
    await assert.rejects(push.setEnabled("push.expenses", true), /permission/);
    assert.equal(requests.length, count);
});
