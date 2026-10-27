const bindings = new WeakMap();

export function open(dialog, dotnet) {
    const opener = document.activeElement;
    const closed = () => {
        if (opener?.isConnected) opener.focus();
        dotnet.invokeMethodAsync('ClosedAsync').catch(() => {});
    };
    const backdrop = (event) => {
        if (event.target !== dialog) return;
        const bounds = dialog.getBoundingClientRect();
        if (
            event.clientX < bounds.left ||
            event.clientX > bounds.right ||
            event.clientY < bounds.top ||
            event.clientY > bounds.bottom
        )
            dialog.close();
    };
    dialog.addEventListener('close', closed);
    dialog.addEventListener('click', backdrop);
    bindings.set(dialog, { closed, backdrop, opener });
    if (!dialog.open) dialog.showModal();
}

export function close(dialog) {
    if (dialog.open) dialog.close();
}

export function dispose(dialog) {
    const binding = bindings.get(dialog);
    if (binding) {
        dialog.removeEventListener('close', binding.closed);
        dialog.removeEventListener('click', binding.backdrop);
        bindings.delete(dialog);
    }
    if (dialog.open) dialog.close();
    if (binding?.opener?.isConnected) binding.opener.focus();
}
