export function afterWebStarted(blazor) {
    blazor.addEventListener('enhancedload', () => window.meshAll.restoreTheme());
}
