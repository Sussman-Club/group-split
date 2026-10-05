// Blazor JS initializer for this library: found by its file name and loaded by
// the framework, so App.razor carries no <script> tag for it.
//
// gs.js stamps the theme and the ready flag on <html> at runtime, and every
// enhanced page update removes both, because the server's copy of <html> has
// neither. Blazor raises "enhancedload" in the same task as that update, so
// putting them back here happens before the browser paints the page without
// them.
export function afterWebStarted(blazor) {
    blazor.addEventListener("enhancedload", () => window.gs?.reapply());
}
