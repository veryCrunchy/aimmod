// node --test in-game/cosmetics/catalog.test.cjs
// catalog.json is the shipped catalog; CosmeticsCatalog.lua is the team
// testbed's copy. They must list the same items, and every item stays a
// draft (never pickable) until the probe confirms its parameter names.
const test = require('node:test');
const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');

const catalog = JSON.parse(fs.readFileSync(path.join(__dirname, 'catalog.json'), 'utf8'));
const lua = fs.readFileSync(path.join(__dirname, '..', 'ue4ss', 'AimModCosmetics', 'Scripts', 'CosmeticsCatalog.lua'), 'utf8');
// Each item runs from its "{id='" to the next item (or the end of M.items).
const itemsBlock = lua.slice(lua.indexOf('M.items = {'), lua.indexOf('\n}\n', lua.indexOf('M.items = {')));
const starts = [...itemsBlock.matchAll(/\{id='/g)].map(m => m.index);
const luaItems = starts.map((start, i) => {
  const text = itemsBlock.slice(start, starts[i + 1] ?? itemsBlock.length);
  const m = /^\{id='([a-z0-9-]+)', version=(\d+), kind='([a-z_]+)'/.exec(text);
  return { id: m[1], version: Number(m[2]), kind: m[3], draft: /draft=true/.test(text) };
});

test('catalog.json and the Lua testbed catalog list the same items', () => {
  const json = catalog.items.map(i => ({ id: i.id, version: i.version, kind: i.kind, draft: i.draft === true }));
  assert.deepStrictEqual(json, luaItems);
});

test('catalog is versioned and ids are unique', () => {
  assert.ok(Number.isInteger(catalog.version) && catalog.version >= 1);
  const ids = catalog.items.map(i => i.id);
  assert.strictEqual(new Set(ids).size, ids.length);
});

test('no item names a player file, and paks are flat non-patch names', () => {
  for (const item of catalog.items) {
    for (const field of ['file', 'path', 'url', 'texture']) assert.ok(!(field in item), `${item.id} has ${field}`);
    if (item.pak) {
      assert.match(item.pak.file, /^[A-Za-z0-9][A-Za-z0-9._-]{0,63}\.pak$/);
      assert.ok(!/_P\.pak$/i.test(item.pak.file) && !item.pak.file.includes('..'), item.pak.file);
    }
  }
});

test('every item stays a draft until the probe confirms its parameter names', () => {
  // Clear "draft" (here and in CosmeticsCatalog.lua) only after replacing the
  // placeholder parameter names with names from the probe output.
  for (const item of catalog.items) assert.strictEqual(item.draft, true, `${item.id} must stay a draft`);
});
