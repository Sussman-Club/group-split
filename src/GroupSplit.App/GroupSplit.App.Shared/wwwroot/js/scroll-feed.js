export function observe(element, reference) {
    const observer = new IntersectionObserver(entries => {
        if (entries.some(entry => entry.isIntersecting)) {
            observer.disconnect();
            reference.invokeMethodAsync('LoadMoreAsync').catch(() => {});
        }
    }, { rootMargin: '300px' });
    observer.observe(element);
    return { dispose() { observer.disconnect(); } };
}
