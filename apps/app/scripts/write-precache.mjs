#!/usr/bin/env node
/**
 * Runs after `expo export -p web`. Writes the list of exported files, and a hash of them, into
 * dist/sw.js so the service worker caches the whole app on its first install. Without this an
 * offline reload after the first visit gets HTML but none of the JavaScript. The hash changes
 * dist/sw.js on every deploy that changes a file, which makes browsers install the new worker.
 */
import { createHash } from 'node:crypto';
import { readdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { join, relative, sep } from 'node:path';

const dist = new URL('../dist/', import.meta.url).pathname;
// Not needed at runtime: source maps, export metadata, the worker itself, and the encrypted-SQLite
// builds (the app doesn't use SQLCipher yet).
const SKIP = [/\.map$/, /^metadata\.json$/, /^sw\.js$/, /(^|\/)mc-wa-sqlite/];

function walk(dir) {
  return readdirSync(dir).flatMap((name) => {
    const path = join(dir, name);
    return statSync(path).isDirectory() ? walk(path) : [path];
  });
}

const hash = createHash('sha256');
const urls = [];
for (const file of walk(dist).sort()) {
  const rel = relative(dist, file).split(sep).join('/');
  if (SKIP.some((re) => re.test(rel))) continue;
  hash.update(rel).update(readFileSync(file));
  urls.push(rel === 'index.html' ? '/' : `/${rel}`);
}

const swPath = join(dist, 'sw.js');
const sw = readFileSync(swPath, 'utf8');
const PRECACHE = 'const PRECACHE = [];';
const VERSION = "const VERSION = 'dev';";
if (!sw.includes(PRECACHE) || !sw.includes(VERSION)) throw new Error('sw.js placeholders not found');
writeFileSync(
  swPath,
  sw
    .replace(PRECACHE, `const PRECACHE = ${JSON.stringify(urls)};`)
    .replace(VERSION, `const VERSION = '${hash.digest('hex').slice(0, 16)}';`),
);
console.log(`sw.js: precaching ${urls.length} files`);
