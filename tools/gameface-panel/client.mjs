import WebSocket from 'ws';

export async function connect(viewId = '0') {
    const pages = await (await fetch('http://127.0.0.1:9444/json', { signal: AbortSignal.timeout(4000) })).json();
    const page = pages.find(entry => entry.id === viewId);
    if (!page || !page.url?.toLowerCase().startsWith('coui://uiresources/')) throw new Error('Kovaak\'s UI view not found.');
    const endpoint = new URL(page.webSocketDebuggerUrl);
    if (!['127.0.0.1', 'localhost'].includes(endpoint.hostname) || endpoint.port !== '9444' || endpoint.protocol !== 'ws:') {
        throw new Error('Unexpected debugger endpoint.');
    }
    endpoint.hostname = '127.0.0.1';
    const ws = new WebSocket(endpoint, { handshakeTimeout: 4000, maxPayload: 32 * 1024 * 1024 });
    await new Promise((resolve, reject) => { ws.once('open', resolve); ws.once('error', reject); });
    let sequence = 0;
    const pending = new Map();
    ws.on('message', data => {
        const message = JSON.parse(data);
        const request = pending.get(message.id);
        if (!request) return;
        pending.delete(message.id);
        clearTimeout(request.timer);
        if (message.error) request.reject(new Error(message.error.message));
        else request.resolve(message.result);
    });
    function rejectPending(error) {
        for (const request of pending.values()) { clearTimeout(request.timer); request.reject(error); }
        pending.clear();
    }
    ws.on('close', () => rejectPending(new Error('Gameface disconnected.')));
    ws.on('error', rejectPending);
    return {
        async request(method, params = {}) {
            const id = ++sequence;
            return new Promise((resolve, reject) => {
                const timer = setTimeout(() => { pending.delete(id); reject(new Error(`${method} timed out.`)); }, 5000);
                pending.set(id, { resolve, reject, timer });
                ws.send(JSON.stringify({ id, method, params }));
            });
        },
        close() { ws.close(); }
    };
}
