import { readFile, writeFile } from 'node:fs/promises';
import { connect } from './client.mjs';

const [action = 'inspect', viewId = '0', output] = process.argv.slice(2);
if (!['inspect', 'mount', 'open', 'close', 'remove', 'capture'].includes(action)) throw new Error('Use inspect, mount, open, close, remove, or capture.');
if (action === 'capture' && !output) throw new Error('Capture requires an output filename outside the repository.');
const client = await connect(viewId);
try {
    if (action === 'capture') {
        const result = await client.request('Page.captureScreenshot', { format: 'png' });
        if (!result.data) throw new Error('Gameface did not return a screenshot.');
        await writeFile(output, Buffer.from(result.data, 'base64'));
        console.log('Captured.');
    } else {
        const status = `JSON.stringify({route:location.pathname,panel:window.__aimmodPanel ? window.__aimmodPanel.status():null,gameRootPresent:!!document.getElementById('root'),engineReady:typeof (window.engine||{}).on==='function'})`;
        let expression = status;
        if (action === 'mount') expression = (await readFile(new URL('../../in-game/aimmod-panel.js', import.meta.url), 'utf8')) + '\n;' + status;
        if (['open', 'close', 'remove'].includes(action)) expression = `if(window.__aimmodPanel)window.__aimmodPanel.${action}();` + status;
        const result = await client.request('Runtime.evaluate', { expression, returnByValue: true });
        if (result.exceptionDetails) throw new Error(result.exceptionDetails.exception?.description || result.exceptionDetails.text);
        console.log(result.result.value);
    }
} finally {
    client.close();
}
