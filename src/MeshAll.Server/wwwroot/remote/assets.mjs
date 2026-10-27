const scripts = new Map();

export function loadScript(source) {
    if (scripts.has(source)) return scripts.get(source);
    const task = new Promise((resolve, reject) => {
        const script = document.createElement('script');
        script.src = source;
        script.onload = resolve;
        script.onerror = () => {
            script.remove();
            reject(Error('The remote viewer could not be loaded.'));
        };
        document.head.append(script);
    });
    scripts.set(source, task);
    task.catch(() => scripts.delete(source));
    return task;
}

export function loadStylesheet(href) {
    if (document.querySelector(`link[href="${href}"]`)) return;
    const link = document.createElement('link');
    link.rel = 'stylesheet';
    link.href = href;
    document.head.append(link);
}

export function socketUrl(path) {
    return location.protocol.replace('http', 'ws') + '//' + location.host + path;
}

export function fitCanvas(canvas) {
    const stage = canvas.parentElement;
    if (!stage || !canvas.width || !canvas.height) return;
    const scale = Math.min(stage.clientWidth / canvas.width, stage.clientHeight / canvas.height);
    if (!(scale > 0)) return;
    canvas.style.width = Math.floor(canvas.width * scale) + 'px';
    canvas.style.height = Math.floor(canvas.height * scale) + 'px';
}
