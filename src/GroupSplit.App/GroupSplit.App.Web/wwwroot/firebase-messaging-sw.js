/* Register click handling before Firebase installs its own handler. */
self.addEventListener("notificationclick", event => {
    const route = event.notification.data?.route;
    if (typeof route !== "string" || !route.startsWith("/") || route.startsWith("//") || route.includes("\\")) return;
    event.stopImmediatePropagation();
    event.notification.close();
    event.waitUntil(clients.openWindow(new URL(route, self.location.origin).href));
});
importScripts("https://www.gstatic.com/firebasejs/12.4.0/firebase-app-compat.js");
importScripts("https://www.gstatic.com/firebasejs/12.4.0/firebase-messaging-compat.js");
const params = new URL(self.location.href).searchParams;
firebase.initializeApp(Object.fromEntries(["apiKey", "projectId", "messagingSenderId", "appId"].map(key => [key, params.get(key)])));
firebase.messaging();
