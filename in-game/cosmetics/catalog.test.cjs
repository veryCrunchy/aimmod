// node --test in-game/cosmetics/catalog.test.cjs
// catalog.json is the shipped catalog; native-mod/cosmetics/catalog.json is
// the copy AimModCore's tests read, and CosmeticsCatalog.lua is the team
// testbed's copy. They must list the same items. Parameter items use only the
// material parameters the probe found; pak items stay drafts (never pickable)
// until their pak ships.
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

// From the probe (cosmetics-probe.txt): MM_BaseDummy on the Meso and Endo body
// and head slots, M_SingleAssetMaster on the viewmodel weapons.
const PROBED = {
  body: { vector: ['MetalPaint', 'TriangularPaint', 'RawMetal', 'Silicone'], scalar: ['Roughness', 'Metallic', 'FullBright'] },
  weapon: { vector: ['AccentColor', 'Emissive'], scalar: [] },
};
// The game's own weapon accent (1, 0.396, 0) as relative luminance.
const luminance = c => 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B;
const GAME_ACCENT = luminance({ R: 1, G: 0.396, B: 0 });

test('catalog.json and the Lua testbed catalog list the same items', () => {
  const json = catalog.items.map(i => ({ id: i.id, version: i.version, kind: i.kind, draft: i.draft === true }));
  assert.deepStrictEqual(json, luaItems);
  const luaVersion = Number(/M\.version = (\d+)/.exec(lua)[1]);
  assert.strictEqual(luaVersion, catalog.version);
});

test('the copy AimModCore tests read is the shipped catalog', () => {
  const copy = JSON.parse(fs.readFileSync(path.join(__dirname, '..', 'native-mod', 'cosmetics', 'catalog.json'), 'utf8'));
  assert.deepStrictEqual(copy, catalog);
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

test('a first curated set is pickable today: tints and weapon finishes from probed parameters only', () => {
  const ready = catalog.items.filter(i => !i.draft);
  assert.ok(ready.filter(i => i.kind === 'avatar_tint').length >= 6, 'at least six tints');
  assert.ok(ready.filter(i => i.kind === 'weapon_finish').length >= 4, 'weapon finishes');
  for (const item of ready) {
    assert.ok(!item.pak, `${item.id}: needs no pak`);
    assert.ok(item.name && item.name.length <= 40, `${item.id}: has a short name`);
    for (const part of item.parts) {
      const probed = PROBED[part];
      assert.ok(probed, `${item.id}: part ${part} has probed parameters`);
      for (const name of Object.keys(item.vector || {})) assert.ok(probed.vector.includes(name), `${item.id}: ${name} is not a probed ${part} parameter`);
      for (const name of Object.keys(item.scalar || {})) assert.ok(probed.scalar.includes(name), `${item.id}: ${name} is not a probed ${part} parameter`);
    }
    for (const c of Object.values(item.vector || {})) for (const k of 'RGBA') assert.ok(c[k] >= 0 && c[k] <= 1, `${item.id}: colour in range`);
    for (const v of Object.values(item.scalar || {})) assert.ok(v >= 0 && v <= 1, `${item.id}: scalar in range`);
    if (item.kind === 'avatar_tint') assert.deepStrictEqual(item.models, ['Meso', 'Endo'], `${item.id}: fits both free models`);
  }
});

test('weapon finishes never glow brighter than the game\'s own accent (no beacons)', () => {
  for (const item of catalog.items.filter(i => i.kind === 'weapon_finish' && !i.draft)) {
    const emissive = item.vector.Emissive;
    assert.ok(emissive && luminance(emissive) <= GAME_ACCENT * 1.2, `${item.id}: emissive ${luminance(emissive).toFixed(3)}`);
  }
});

test('accessories are built from curated game meshes, fitted to a slot within the size budget', () => {
  const accessories = catalog.items.filter(i => i.kind === 'accessory' && !i.draft);
  assert.ok(accessories.length >= 3, 'a first set of accessories (few, but each one looks intentional)');
  const roles = new Set();
  for (const item of accessories) {
    // A curated game mesh, or an AimMod runtime mesh shipped next to the catalog.
    if (item.shape) {
      assert.match(item.shape, /^[a-z0-9][a-z0-9-]*\.amsh$/, `${item.id}: mesh file name`);
      assert.ok(fs.existsSync(path.join(__dirname, 'meshes', item.shape)), `${item.id}: ships ${item.shape}`);
      assert.ok(!item.mesh, `${item.id}: a shape or a game mesh, not both`);
    } else assert.match(item.mesh, /^(\/Engine\/BasicShapes\/|\/Game\/Art\/StaticMeshes\/KMC\/Brushes\/)[A-Za-z0-9_.-]+$/, `${item.id}: curated mesh`);
    // A free character material, or (for runtime meshes) a flat material without per-primitive data.
    const flat = ['/MapCreator/Materials/MM_G_Basic.MM_G_Basic', '/Engine/BasicShapes/BasicShapeMaterial.BasicShapeMaterial',
      '/Game/Materials/Masters/Environment/MM_Glow.MM_Glow', '/MapCreator/Materials/DefaultManipulationMaterial.DefaultManipulationMaterial'];
    assert.ok(/^\/Game\/Materials\/Instances\/Characters\/S_(Meso|Endo)\/Base\/MI_PaintedMetal_[A-Za-z0-9_.-]+$/.test(item.material) || flat.includes(item.material), `${item.id}: curated material`);
    if (item.shape) assert.ok(flat.includes(item.material), `${item.id}: runtime meshes use a flat material (the character masters dither away without per-primitive data)`);
    const { role, fit } = item.attach;
    assert.ok(['head', 'neck', 'spine'].includes(role), `${item.id}: slot`);
    roles.add(role);
    assert.ok(['bone', 'top', 'crown', 'chin'].includes(fit.anchor ?? 'bone') && /^[A-Za-z]+$/.test(fit.bone), `${item.id}: anchor`);
    // Head items inside 35 x 35 x 30 cm, others inside 45 x 30 x 50 cm (docs, Accessories).
    const [f, r, u] = fit.size, limit = role === 'head' ? [35, 35, 30] : [45, 45, 50];
    assert.ok(f <= limit[0] && r <= limit[1] && u <= limit[2] && Math.min(f, r, u) >= 1, `${item.id}: size budget`);
    for (const v of fit.offset) assert.ok(Math.abs(v) <= 30, `${item.id}: stays on the character`);
    assert.deepStrictEqual(item.models, ['Meso', 'Endo']);
  }
  assert.ok(roles.has('head') && roles.has('neck'), 'head and neck pieces');
});

test('every shipped runtime mesh is a valid .amsh the catalog uses', () => {
  const files = fs.readdirSync(path.join(__dirname, 'meshes'));
  const used = new Set(catalog.items.map(i => i.shape).filter(Boolean));
  assert.deepStrictEqual(files.sort(), [...used].sort(), 'no unused or missing mesh files');
  for (const name of files) {
    const b = fs.readFileSync(path.join(__dirname, 'meshes', name));
    assert.strictEqual(b.toString('latin1', 0, 4), 'AMSH', name);
    const vertices = b.readUInt32LE(8), indices = b.readUInt32LE(12);
    assert.strictEqual(b.length, 16 + vertices * 36 + indices * 4, `${name}: size`);
    assert.ok(indices % 3 === 0 && vertices <= 65536, name);
  }
});

test('pak items stay drafts until their pak ships', () => {
  for (const item of catalog.items.filter(i => i.pak)) assert.strictEqual(item.draft, true, `${item.id} must stay a draft`);
});
