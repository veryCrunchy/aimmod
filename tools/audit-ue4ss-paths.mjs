// Compare literal reflection paths with a fresh loaded-object dump. Absence of
// a cooked asset is not proof of removal: it may not be loaded in this scene.
import { readFile, readdir, writeFile } from 'node:fs/promises';
import { resolve, relative, extname } from 'node:path';

const [dumpFile, outputFile] = process.argv.slice(2);
if (!dumpFile || !outputFile) throw new Error('Usage: node tools/audit-ue4ss-paths.mjs <object-dump> <private-output.json>');
const repository = resolve(import.meta.dirname, '..');
const outputRelative = relative(repository, resolve(outputFile));
if (!outputRelative.startsWith('..') && !outputRelative.includes(':')) throw new Error('Audit output must be outside the repository.');
const dump = await readFile(dumpFile, 'utf8');
const objects = new Map();
for (const line of dump.split('\n')) {
    const match = line.match(/^\[[0-9A-Fa-f]+\] (\S+) (.*?) \[/);
    if (match) objects.set(match[2], match[1]);
}
const paths = new Map();
async function walk(directory) {
    for (const entry of await readdir(directory, { withFileTypes: true })) {
        const file = resolve(directory, entry.name);
        if (entry.isDirectory()) await walk(file);
        else if (['.cpp', '.hpp', '.inl'].includes(extname(file))) {
            const content = await readFile(file, 'utf8');
            for (const match of content.matchAll(/(?:STR\(|L)?"(\/(?:Script|Game|Xsolla)[^"\r\n]*\.[^"\r\n]+)"/g)) {
                const path = match[1];
                if (!paths.has(path)) paths.set(path, []);
                paths.get(path).push({ file: relative(repository, file).replaceAll('\\', '/'), line: content.slice(0, match.index).split('\n').length });
            }
        }
    }
}
await walk(resolve(repository, 'ue4ss-mod/src'));
const entries = [...paths].map(([path, references]) => ({ path, found: objects.has(path), kind: objects.get(path) ?? null,
    scope: path.startsWith('/Script/') ? 'native-reflection' : 'loaded-asset', references }));
const report = { schemaVersion: 1, scope: 'Loaded objects only; verify missing assets in their owning scene.',
    objectCount: objects.size, literalPathCount: entries.length, found: entries.filter(e => e.found).length, entries };
await writeFile(outputFile, JSON.stringify(report, null, 2) + '\n');
console.log(JSON.stringify({ objects: report.objectCount, paths: report.literalPathCount, found: report.found,
    absentNative: entries.filter(e => !e.found && e.scope === 'native-reflection').map(e => e.path),
    absentAssets: entries.filter(e => !e.found && e.scope === 'loaded-asset').map(e => e.path) }, null, 2));
