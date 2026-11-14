const keys = ["push.expenses", "push.settlements", "push.invitations", "push.bank_sync", "push.staff_support"];
const storeKey = "groupsplit.webpush";
let config, messaging, sdk, registration, reason;
let pendingPermission, initialization;

function read() {
    try { return JSON.parse(localStorage.getItem(storeKey)) || { preferences: [] }; }
    catch { return { preferences: [] }; }
}
function save(value) { localStorage.setItem(storeKey, JSON.stringify(value)); }
function state() { return { supported: !!messaging, reason, preferences: read().preferences || [] }; }

// Preserve the browser's user activation, including in interactive server rendering.
document.addEventListener("click", event => {
    const input = event.target.closest?.(".gs-notifications input[type=checkbox]");
    if (input && input.checked && window.Notification?.permission === "default") {
        pendingPermission = Notification.requestPermission();
    }
}, true);

export function initialize() {
    return initialization ||= initializeOnce().catch(error => { initialization = undefined; messaging = undefined; throw error; });
}
async function initializeOnce() {
    if (messaging || reason) return state();
    if (!window.isSecureContext || !("serviceWorker" in navigator) || !("PushManager" in window) || !("Notification" in window)) {
        reason = /iPad|iPhone|iPod/.test(navigator.userAgent) || (navigator.platform === "MacIntel" && navigator.maxTouchPoints > 1)
            ? "On iPhone or iPad, add Group Split to your Home Screen and open it there to enable notifications."
            : "Notifications are not supported in this browser or are unavailable on this connection.";
        return state();
    }
    config = await (await fetch("/push-config", { cache: "no-store" })).json();
    if (![config.apiKey, config.projectId, config.messagingSenderId, config.appId, config.vapidKey].every(Boolean)) {
        reason = "Browser notifications are not configured.";
        return state();
    }
    const appSdk = await import("https://www.gstatic.com/firebasejs/12.4.0/firebase-app.js");
    sdk = await import("https://www.gstatic.com/firebasejs/12.4.0/firebase-messaging.js");
    if (!await sdk.isSupported()) { reason = "Notifications are not supported in this browser."; return state(); }
    const app = appSdk.getApps().length ? appSdk.getApp() : appSdk.initializeApp(config);
    messaging = sdk.getMessaging(app);
    const params = new URLSearchParams({ apiKey: config.apiKey, projectId: config.projectId, messagingSenderId: config.messagingSenderId, appId: config.appId });
    registration = await navigator.serviceWorker.register(`/firebase-messaging-sw.js?${params}`);
    sdk.onMessage(messaging, payload => {
        const route = safeRoute(payload.data?.route);
        registration.showNotification(payload.notification?.title || "Group Split", {
            body: payload.notification?.body || "You have a new alert.",
            data: { route }, tag: payload.data?.deliveryId
        });
    });
    return state();
}
function safeRoute(route) {
    if (typeof route !== "string" || !route.startsWith("/") || route.startsWith("//") || route.includes("\\")) return "/account";
    return route;
}
async function api(method, id, body) {
    const response = await fetch(`/api/users/me/push-devices/${id}`, {
        method, credentials: "same-origin", headers: { "Content-Type": "application/json" },
        body: body && JSON.stringify(body), redirect: "error"
    });
    if (!response.ok) throw new Error("Push registration failed");
}
async function register(value) {
    const token = await sdk.getToken(messaging, { vapidKey: config.vapidKey, serviceWorkerRegistration: registration });
    if (!token) throw new Error("No push token");
    value.id ||= crypto.randomUUID();
    // Persist the id before the request so a retry can remove the same registration.
    save({ ...read(), id: value.id });
    await api("PUT", value.id, { token, expensesEnabled: value.preferences.includes(keys[0]),
        settlementsEnabled: value.preferences.includes(keys[1]), invitationsEnabled: value.preferences.includes(keys[2]),
        bankSyncIssuesEnabled: value.preferences.includes(keys[3]), staffSupportEnabled: value.preferences.includes(keys[4]) });
}
export async function setEnabled(key, enabled) {
    await initialize();
    if (!messaging || !keys.includes(key)) throw new Error("Notifications unavailable");
    if (enabled) {
        const permission = pendingPermission ? await pendingPermission : Notification.permission;
        pendingPermission = undefined;
        if (permission !== "granted") throw new Error("Notification permission was not granted");
    }
    const previous = read();
    const value = { ...previous, preferences: (previous.preferences || []).filter(k => k !== key) };
    if (enabled) value.preferences.push(key);
    if (value.preferences.length) await register(value);
    else {
        if (value.id) await api("DELETE", value.id);
        await sdk.deleteToken(messaging);
    }
    save(value);
    return state();
}
export async function refresh(owner) {
    await initialize();
    if (!messaging || !owner) return state();
    const value = read();
    if (value.owner && value.owner !== owner) {
        // The previous owner cannot be deleted using the new owner's credentials.
        await sdk.deleteToken(messaging);
        localStorage.removeItem(storeKey);
        return state();
    }
    value.owner = owner;
    save(value);
    if (value.preferences?.length && Notification.permission === "granted") await register(value);
    else if (value.preferences?.length && Notification.permission === "denied") {
        if (value.id) await api("DELETE", value.id);
        await sdk.deleteToken(messaging);
        value.preferences = [];
        delete value.id;
        save(value);
    }
    return state();
}
export async function unregister(accountDeleted) {
    const value = read();
    if (!accountDeleted && value.id) await api("DELETE", value.id);
    if (value.id && !messaging) await initialize();
    if (messaging && value.id) await sdk.deleteToken(messaging);
    localStorage.removeItem(storeKey);
    return state();
}
