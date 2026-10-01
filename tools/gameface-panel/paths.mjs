import { isAbsolute, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

export const repositoryRoot = fileURLToPath(new URL('../../', import.meta.url));

// Captures can contain private scores and names; never write them into the repository.
export function outsideRepository(file, root = repositoryRoot) {
    if (typeof file !== 'string' || !file) return false;
    const path = relative(resolve(root), resolve(file));
    return path === '..' || path.startsWith('..\\') || path.startsWith('../') || isAbsolute(path);
}
