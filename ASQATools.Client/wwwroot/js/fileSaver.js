window.saveFileLocally = (fileName, content) => {
    const blob = new Blob([content], { type: 'text/plain;charset=utf-8' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = fileName;
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
    URL.revokeObjectURL(url);
};

// Cross-browser clipboard copy with fallback for non-secure HTTP contexts
window.copyTextToClipboard = async (text) => {
    if (!text) return false;

    // 1. Try modern Async Clipboard API if in Secure Context (HTTPS or localhost)
    if (navigator.clipboard && window.isSecureContext) {
        try {
            await navigator.clipboard.writeText(text);
            return true;
        } catch (err) {
            console.warn('navigator.clipboard failed, attempting fallback', err);
        }
    }

    // 2. Fallback for unencrypted HTTP contexts (e.g. http://pilot.malath.com.sa:9001/)
    try {
        const textArea = document.createElement("textarea");
        textArea.value = text;
        textArea.style.position = "fixed";
        textArea.style.left = "-999999px";
        textArea.style.top = "-999999px";
        document.body.appendChild(textArea);
        textArea.focus();
        textArea.select();
        const successful = document.execCommand('copy');
        document.body.removeChild(textArea);
        return successful;
    } catch (err) {
        console.error('Fallback copy failed', err);
        return false;
    }
};

window.readTextFromClipboard = async () => {
    if (navigator.clipboard && window.isSecureContext) {
        try {
            return await navigator.clipboard.readText();
        } catch (e) { }
    }
    return "";
};
