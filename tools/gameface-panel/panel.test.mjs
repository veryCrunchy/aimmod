import test from 'node:test';
import assert from 'node:assert/strict';
import vm from 'node:vm';
import { readFile } from 'node:fs/promises';

const source = await readFile(new URL('../../in-game/aimmod-panel.js', import.meta.url), 'utf8');
function setup(route = '/benchmarks') {
    class Element {
        constructor(tag) { this.tagName = tag; this.children = []; this.style = { display: 'none' }; this.attributes = {}; }
        appendChild(node) { this.children.push(node); node.parentNode = this; }
        removeChild(node) { this.children.splice(this.children.indexOf(node), 1); node.parentNode = null; }
        setAttribute(key, value) { this.attributes[key] = value; }
    }
    const body = new Element('body');
    const gameRoot = new Element('div');
    body.appendChild(gameRoot);
    const listeners = new Map();
    let originalCompletions = 0;
    listeners.set('ScenarioComplete', [() => originalCompletions++]);
    const forbidden = () => { throw new Error('Game mutation attempted'); };
    const engine = {
        call: forbidden, trigger: forbidden,
        on(name, callback) {
            if (!listeners.has(name)) listeners.set(name, []);
            const list = listeners.get(name);
            list.push(callback);
            return { clear() { const index = list.indexOf(callback); if (index >= 0) list.splice(index, 1); } };
        }
    };
    const scope = { document: { body, createElement: tag => new Element(tag) }, engine, location: { pathname: route } };
    const context = vm.createContext({ window: scope });
    return { scope, body, gameRoot, listeners, engine, forbidden,
        run: () => vm.runInContext(source, context, { timeout: 1000 }),
        emit: (name, ...args) => listeners.get(name)?.slice().forEach(fn => fn(...args)),
        completions: () => originalCompletions };
}

test('mount adds only an owned subtree and never replaces game methods', () => {
    const app = setup(); const originalOn = app.engine.on; app.run();
    assert.equal(app.body.children[0], app.gameRoot);
    assert.equal(app.body.children.length, 2);
    assert.equal(app.engine.on, originalOn);
    assert.equal(app.engine.call, app.forbidden);
    assert.equal(app.engine.trigger, app.forbidden);
    assert.equal(app.scope.__aimmodPanel.status().open, false);
});
test('unmount preserves original score listener and DOM', () => {
    const app = setup(); app.run(); app.scope.__aimmodPanel.remove();
    assert.deepEqual(app.body.children, [app.gameRoot]);
    assert.equal(app.listeners.get('ScenarioComplete').length, 1);
    assert.equal(app.listeners.get('CountdownTimeSet').length, 0);
    assert.equal(app.listeners.get('DPSCountSet').length, 0);
    assert.equal(app.scope.__aimmodPanel, undefined);
});
test('score payload is never read or mutated and original event handling continues', () => {
    const app = setup(); app.run();
    const payload = new Proxy(Object.freeze({ score: 1234 }), { get() { throw new Error('Score read'); }, set() { throw new Error('Score write'); } });
    app.emit('ScenarioComplete', 'Synthetic scenario', payload);
    assert.equal(app.completions(), 1);
    app.scope.__aimmodPanel.remove();
    app.emit('ScenarioComplete', 'Synthetic scenario', payload);
    assert.equal(app.completions(), 2);
});
test('mount is idempotent', () => {
    const app = setup(); app.run(); app.run();
    assert.equal(app.body.children.length, 2);
    assert.equal(app.listeners.get('ScenarioComplete').length, 2);
});
test('gameplay routes are rejected without changing DOM or events', () => {
    const app = setup('/hud'); assert.throws(app.run, /restricted/);
    assert.equal(app.body.children.length, 1);
    assert.equal(app.listeners.get('ScenarioComplete').length, 1);
});
test('opening and closing changes only the owned panel', () => {
    const app = setup(); app.run();
    app.scope.__aimmodPanel.open(); assert.equal(app.scope.__aimmodPanel.status().open, true);
    app.scope.__aimmodPanel.close(); assert.equal(app.scope.__aimmodPanel.status().open, false);
});
test('unexpected telemetry objects are never coerced', () => {
    const app = setup(); app.run();
    const value = { toString() { throw new Error('Untrusted coercion'); } };
    app.emit('CountdownTimeSet', value); app.emit('DPSCountSet', value);
});
test('numeric telemetry is shown without float noise', () => {
    const app = setup(); app.run();
    app.emit('DPSCountSet', 123.456789); app.emit('CountdownTimeSet', 0.1 + 0.2);
    const walk = node => [node, ...(node.children || []).flatMap(walk)];
    const texts = walk(app.body).map(node => node.textContent);
    assert.ok(texts.includes('123.5')); assert.ok(texts.includes('0.3'));
});
test('captures are refused inside the repository', async () => {
    const { outsideRepository, repositoryRoot } = await import('./paths.mjs');
    const { join, dirname } = await import('node:path');
    assert.equal(outsideRepository(join(repositoryRoot, 'capture.png')), false);
    assert.equal(outsideRepository(join(repositoryRoot, 'in-game', 'ui', 'capture.png')), false);
    assert.equal(outsideRepository(''), false);
    assert.equal(outsideRepository(join(dirname(repositoryRoot.replace(/[\\/]$/, '')), 'outside-capture.png')), true);
});
test('partial subscription failure clears earlier listeners', () => {
    const app = setup(); const on = app.engine.on;
    app.engine.on = (name, cb) => { if (name === 'DPSCountSet') throw new Error('Subscription failed'); return on(name, cb); };
    assert.throws(app.run, /Subscription failed/);
    assert.equal(app.listeners.get('ScenarioComplete').length, 1);
    assert.equal(app.listeners.get('CountdownTimeSet').length, 0);
    assert.equal(app.body.children.length, 1);
});
