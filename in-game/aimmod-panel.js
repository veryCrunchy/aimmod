/* AimMod's additive Gameface experiment. This file owns only its DOM subtree
 * and three event subscriptions. It never invokes a game command or persists
 * data. The debugger is a development delivery mechanism, not a release loader. */
(function (scope) {
    'use strict';
    if (scope.__aimmodPanel) return;
    var doc = scope.document;
    var engine = scope.engine;
    if (!doc || !doc.body || !engine || typeof engine.on !== 'function') {
        throw new Error('AimMod requires a ready Gameface view.');
    }
    if (!/^\/(benchmarks|sandbox-graph|benchmark-wrap-up)\/?$/.test(scope.location.pathname)) {
        throw new Error('AimMod preview is restricted to review and benchmark menus.');
    }

    var handles = [];
    var runs = 0;
    var root = doc.createElement('div');
    root.id = 'aimmod-panel-root';
    root.style.cssText = 'position:absolute;top:112px;right:24px;width:360px;z-index:10000;pointer-events:none;font-family:Roboto,Metropolis,sans-serif;color:#ffffff;';

    function element(tag, text, css, parent) {
        var node = doc.createElement(tag);
        if (text) node.textContent = text;
        if (css) node.style.cssText = css;
        if (parent) parent.appendChild(node);
        return node;
    }
    var launch = element('button', 'AimMod', 'float:right;background:#312d35;color:#ffffff;border:1px solid #ff7800;padding:10px 20px;font-size:16px;pointer-events:auto;cursor:pointer;', root);
    launch.setAttribute('aria-expanded', 'false');
    launch.setAttribute('aria-controls', 'aimmod-session-panel');
    var panel = element('div', '', 'display:none;clear:both;background:#232027;border:1px solid #4d4757;padding:24px;pointer-events:none;', root);
    panel.id = 'aimmod-session-panel';
    panel.setAttribute('role', 'region');
    panel.setAttribute('aria-label', 'AimMod session');
    element('div', 'THIS VISIT', 'font-size:11px;color:#ff7800;margin-bottom:10px;', panel);
    element('h2', 'Your session', 'font-size:26px;margin:0 0 18px 0;color:#ffffff;', panel);
    var message = element('p', 'Finish a run to see its summary.', 'font-size:14px;color:#bbbbbb;margin:0 0 20px 0;', panel);

    function metric(label) {
        var row = element('div', '', 'border-top:1px solid #3e3a45;padding:12px 0;', panel);
        element('span', label, 'font-size:14px;color:#bbbbbb;', row);
        return element('span', '\u2014', 'float:right;font-size:16px;color:#ffffff;', row);
    }
    var completed = metric('Completed runs');
    var countdown = metric('Time remaining');
    var damage = metric('Damage per second');
    var close = element('button', 'Close', 'margin-top:16px;background:#3e3a45;border:0;color:#ffffff;padding:10px 18px;font-size:14px;cursor:pointer;pointer-events:auto;', panel);

    function setOpen(open) {
        panel.style.display = open ? 'block' : 'none';
        launch.setAttribute('aria-expanded', open ? 'true' : 'false');
    }
    launch.onclick = function () { setOpen(panel.style.display === 'none'); };
    close.onclick = function () { setOpen(false); };
    function boundedText(value) {
        // Never retain an event object or call user-defined coercion methods.
        if (typeof value === 'number' && isFinite(value)) return String(value);
        if (typeof value === 'string' && value.length <= 64) return value;
        return '\u2014';
    }
    function listen(name, callback) {
        var handle = engine.on(name, callback);
        if (!handle || typeof handle.clear !== 'function') {
            throw new Error('Gameface did not supply a removable event subscription.');
        }
        handles.push(handle);
    }
    function remove() {
        handles.forEach(function (handle) { handle.clear(); });
        handles = [];
        launch.onclick = null;
        close.onclick = null;
        if (root.parentNode) root.parentNode.removeChild(root);
        delete scope.__aimmodPanel;
    }
    try {
        listen('ScenarioComplete', function () {
            runs = Math.min(runs + 1, 1000000);
            completed.textContent = String(runs);
            countdown.textContent = '\u2014';
            damage.textContent = '\u2014';
            message.textContent = 'Run complete.';
        });
        listen('CountdownTimeSet', function (value) { countdown.textContent = boundedText(value); });
        listen('DPSCountSet', function (value) { damage.textContent = boundedText(value); });
        doc.body.appendChild(root);
        scope.__aimmodPanel = {
            open: function () { setOpen(true); },
            close: function () { setOpen(false); },
            remove: remove,
            status: function () { return { mounted: root.parentNode === doc.body, open: panel.style.display !== 'none', subscriptions: handles.length }; }
        };
    } catch (error) {
        remove();
        throw error;
    }
})(window);
