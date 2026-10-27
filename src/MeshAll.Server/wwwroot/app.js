(() => {
    function restoreTheme() {
        let theme = 'light';
        try {
            theme = localStorage.getItem('meshall-theme') === 'dark' ? 'dark' : 'light';
        } catch {}
        document.documentElement.dataset.theme = theme;
    }

    function toggleTheme() {
        const next = document.documentElement.dataset.theme === 'dark' ? 'light' : 'dark';
        try {
            localStorage.setItem('meshall-theme', next);
        } catch {}
        document.documentElement.dataset.theme = next;
    }

    function toggleFullscreen(button) {
        const root = button.closest('[data-remote-workspace]');
        if (!root) return;
        const error = root.querySelector('[data-remote-fullscreen-error]');
        if (error) error.hidden = true;
        const failed = () => {
            if (error) error.hidden = false;
        };
        try {
            // Call synchronously in the click handler to preserve browser user activation.
            const task =
                document.fullscreenElement === root ? document.exitFullscreen() : root.requestFullscreen();
            task.catch(failed);
        } catch {
            failed();
        }
    }

    // Keep the synchronous UI bootstrap small. Blazor calls await the shared module,
    // including the first mount, before enabling any remote connection controls.
    let viewer;
    const invokeRemote = async (method, ...args) => {
        viewer ??= import('/remote/viewer.mjs').catch((error) => {
            viewer = null;
            throw error;
        });
        return (await viewer).remote[method](...args);
    };
    const remote = {};
    for (const method of ['mount', 'dispose', 'connect', 'disconnect', 'control', 'credentials', 'cad'])
        remote[method] = (...args) => invokeRemote(method, ...args);

    window.meshAll = { restoreTheme, toggleTheme, remote };
    restoreTheme();
    document.addEventListener('change', (event) => {
        if (event.target instanceof HTMLSelectElement && event.target.matches('[data-language-picker]'))
            event.target.form?.requestSubmit();
    });
    document.addEventListener('click', (event) => {
        if (!(event.target instanceof Element)) return;
        if (event.target.closest('[data-theme-toggle]')) toggleTheme();
        const button = event.target.closest('[data-remote-fullscreen]');
        if (button) toggleFullscreen(button);
    });
    window.addEventListener('storage', (event) => {
        if (event.key === 'meshall-theme') restoreTheme();
    });
})();
