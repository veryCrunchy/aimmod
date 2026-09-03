import { spawnSync } from "node:child_process";
import { createHash } from "node:crypto";
import {
  existsSync,
  mkdirSync,
  readFileSync,
  readdirSync,
  statSync,
  writeFileSync,
} from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(scriptDir, "..");
const distDir = path.join(repoRoot, "dist");
const cacheFile = path.join(repoRoot, ".cache", "frontend-build.json");

const inputEntries = [
  "src",
  "public",
  "index.html",
  "logs.html",
  "stats.html",
  "package.json",
  "pnpm-lock.yaml",
  "tsconfig.json",
  "tsconfig.node.json",
  "vite.config.ts",
  "tailwind.config.js",
  "postcss.config.js",
];

const requiredOutputs = ["index.html", "logs.html", "stats.html"].map((file) =>
  path.join(distDir, file),
);

function walkFiles(entryPath) {
  if (!existsSync(entryPath)) {
    return [];
  }

  const stat = statSync(entryPath);
  if (stat.isFile()) {
    return [entryPath];
  }

  if (!stat.isDirectory()) {
    return [];
  }

  return readdirSync(entryPath, { withFileTypes: true })
    .sort((left, right) => left.name.localeCompare(right.name))
    .flatMap((entry) => walkFiles(path.join(entryPath, entry.name)));
}

function toRepoRelative(filePath) {
  return path.relative(repoRoot, filePath).split(path.sep).join("/");
}

function loadCachedHash() {
  if (!existsSync(cacheFile)) {
    return null;
  }

  try {
    const cached = JSON.parse(readFileSync(cacheFile, "utf8"));
    return typeof cached.hash === "string" ? cached.hash : null;
  } catch {
    return null;
  }
}

function distOutputsExist() {
  return requiredOutputs.every((filePath) => {
    if (!existsSync(filePath)) {
      return false;
    }

    // Vite rewrites source entrypoints to fingerprinted /assets/ files. A
    // partially interrupted build can leave copied development HTML behind;
    // embedding that in Tauri produces a blank window because /src is not
    // shipped with the release.
    const html = readFileSync(filePath, "utf8");
    return html.includes("/assets/") && !html.includes('src="/src/');
  });
}

function buildVia(command, args, label) {
  console.log(`Running frontend build via ${label}...`);
  const result = spawnSync(command, args, {
    cwd: repoRoot,
    env: process.env,
    stdio: "inherit",
  });

  if (result.error) {
    const code = result.error?.code ?? "unknown";
    console.warn(
      `Failed to launch frontend build via ${label} (${command}): ${result.error.message} [${code}]`,
    );
  }

  return result;
}

const inputFiles = inputEntries
  .flatMap((entry) => walkFiles(path.join(repoRoot, entry)))
  .sort((left, right) => toRepoRelative(left).localeCompare(toRepoRelative(right)));

const hash = createHash("sha256");
hash.update("frontend-build-v1\n");
for (const filePath of inputFiles) {
  hash.update(toRepoRelative(filePath));
  hash.update("\n");
  hash.update(readFileSync(filePath));
  hash.update("\n");
}
const currentHash = hash.digest("hex");

if (
  process.env.FORCE_FRONTEND_BUILD !== "1" &&
  distOutputsExist() &&
  loadCachedHash() === currentHash
) {
  console.log("Skipping frontend build; dist is up to date.");
  process.exit(0);
}

const packageManagerCandidates = [];
const npmExecPath = process.env.npm_execpath;
if (typeof npmExecPath === "string" && npmExecPath.length > 0 && existsSync(npmExecPath)) {
  packageManagerCandidates.push({
    command: process.execPath,
    args: [npmExecPath, "run", "build:frontend"],
    label: "npm_execpath",
  });
}

packageManagerCandidates.push({
  command: process.platform === "win32" ? "pnpm.cmd" : "pnpm",
  args: ["run", "build:frontend"],
  label: "pnpm",
});

packageManagerCandidates.push({
  command: process.platform === "win32" ? "npm.cmd" : "npm",
  args: ["run", "build:frontend"],
  label: "npm",
});

const seen = new Set();
const uniqueCandidates = packageManagerCandidates.filter((candidate) => {
  const key = `${candidate.command} ${candidate.args.join(" ")}`;
  if (seen.has(key)) {
    return false;
  }
  seen.add(key);
  return true;
});

let buildSucceeded = false;
let sawLaunchError = false;

for (const candidate of uniqueCandidates) {
  const build = buildVia(candidate.command, candidate.args, candidate.label);

  if (build.error) {
    sawLaunchError = true;
    const code = build.error?.code;
    if (code === "ENOENT" || code === "EACCES") {
      continue;
    }
    process.exit(1);
  }

  if (build.status !== 0) {
    process.exit(build.status ?? 1);
  }

  buildSucceeded = true;
  break;
}

if (!buildSucceeded) {
  if (sawLaunchError) {
    console.error(
      "Unable to launch frontend build command (tried npm_execpath/pnpm/npm). Check Node/pnpm/npm availability in CI PATH.",
    );
    console.error(`Working directory: ${repoRoot}`);
  }
  process.exit(1);
}

if (!distOutputsExist()) {
  console.error("Frontend build completed but expected dist outputs are missing.");
  process.exit(1);
}

mkdirSync(path.dirname(cacheFile), { recursive: true });
writeFileSync(
  cacheFile,
  `${JSON.stringify({ hash: currentHash, builtAt: new Date().toISOString() }, null, 2)}\n`,
);
