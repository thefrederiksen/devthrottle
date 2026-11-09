#!/usr/bin/env node

/**
 * cc-browser deploy script
 *
 * Copies source files to the deployed cc-browser directory and removes any
 * stale .exe that would shadow the .cmd entry point.
 *
 * Usage: npm run deploy  (from src/cc-browser/)
 */

import { copyFileSync, readdirSync, unlinkSync, existsSync, mkdirSync } from 'fs';
import { join, dirname } from 'path';
import { fileURLToPath } from 'url';

const __dirname = dirname(fileURLToPath(import.meta.url));

const LOCAL_APP_DATA = process.env.LOCALAPPDATA;
if (!LOCAL_APP_DATA) {
  console.error('ERROR: LOCALAPPDATA environment variable not set');
  process.exit(1);
}

const DEPLOY_BASE = join(LOCAL_APP_DATA, 'cc-tools', 'bin');
const DEPLOY_DIR = join(DEPLOY_BASE, 'cc-browser', 'src');
const SOURCE_DIR = join(__dirname, 'src');
const STALE_EXE = join(DEPLOY_BASE, 'cc-browser.exe');

// Ensure target directory exists
if (!existsSync(DEPLOY_DIR)) {
  mkdirSync(DEPLOY_DIR, { recursive: true });
}

// Copy all .mjs source files
const files = readdirSync(SOURCE_DIR).filter(f => f.endsWith('.mjs'));
let copied = 0;

for (const file of files) {
  const src = join(SOURCE_DIR, file);
  const dst = join(DEPLOY_DIR, file);
  copyFileSync(src, dst);
  console.log(`  [+] ${file}`);
  copied++;
}

console.log(`Deployed ${copied} files to ${DEPLOY_DIR}`);

// Remove stale .exe if present
if (existsSync(STALE_EXE)) {
  unlinkSync(STALE_EXE);
  console.log(`Removed stale exe: ${STALE_EXE}`);
}

// Copy package.json
const pkgSrc = join(__dirname, 'package.json');
const pkgDst = join(DEPLOY_BASE, 'cc-browser', 'package.json');
copyFileSync(pkgSrc, pkgDst);
console.log('  [+] package.json');

console.log('');
console.log('Done. Remember to restart the daemon:');
console.log('  cc-browser stop');
console.log('  cc-browser daemon --workspace edge-work');
