// Touch the server only in response to activity on a visible page. A timer
// would turn an unattended browser tab into an indefinitely active session.
(() => {
    const interval = 5 * 60 * 1000;
    let lastSuccess = 0;
    let lastAttempt = 0;
    let pending = false;

    async function touch() {
        if (document.visibilityState !== "visible" || pending) return;
        const form = document.getElementById("gs-session-activity");
        if (!form) return;
        const now = Date.now();
        if (now - lastSuccess < interval || now - lastAttempt < 60 * 1000) return;
        pending = true;
        lastAttempt = now;
        try {
            const response = await fetch(form.action, {
                method: "POST",
                credentials: "same-origin",
                redirect: "error",
                body: new FormData(form)
            });
            if (response.ok) lastSuccess = Date.now();
            // Authentication recovery belongs to normal app navigation/API
            // handling. Never redirect from a fetch that could race sign-out.
        } catch { /* Retry on later activity; a network failure is not sign-out. */ }
        finally { pending = false; }
    }

    for (const name of ["pointerdown", "keydown", "wheel", "touchstart"]) {
        document.addEventListener(name, event => { if (event.isTrusted) void touch(); }, { passive: true });
    }
    window.addEventListener("focus", () => { void touch(); });
    document.addEventListener("visibilitychange", () => { void touch(); });
})();
