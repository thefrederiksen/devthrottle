// CC Browser - Recording storage in vault
// Recordings are stored at: %LOCALAPPDATA%/cc-myvault/recordings/<date_time_name>/recording.json

import { join } from 'path';
import { homedir } from 'os';
import { existsSync, mkdirSync, writeFileSync, readFileSync, readdirSync } from 'fs';

// ---------------------------------------------------------------------------
// Paths
// ---------------------------------------------------------------------------

function getVaultRecordingsDir() {
  const localAppData = process.env.LOCALAPPDATA || join(homedir(), 'AppData', 'Local');
  return join(localAppData, 'cc-myvault', 'recordings');
}

function slugify(name) {
  return name
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-|-$/g, '');
}

function formatTimestamp(date) {
  const d = date || new Date();
  const yyyy = d.getFullYear();
  const mm = String(d.getMonth() + 1).padStart(2, '0');
  const dd = String(d.getDate()).padStart(2, '0');
  const hh = String(d.getHours()).padStart(2, '0');
  const min = String(d.getMinutes()).padStart(2, '0');
  const ss = String(d.getSeconds()).padStart(2, '0');
  return `${yyyy}-${mm}-${dd}_${hh}-${min}-${ss}`;
}

// ---------------------------------------------------------------------------
// Save
// ---------------------------------------------------------------------------

/**
 * Save a recording to the vault.
 * @param {string} name - Human-readable name (e.g. "acmeflow login flow")
 * @param {object} recording - { name, recordedAt, steps }
 * @returns {string} Full path to the saved recording.json
 */
export function saveRecording(name, recording) {
  const baseDir = getVaultRecordingsDir();
  mkdirSync(baseDir, { recursive: true });

  const timestamp = formatTimestamp(new Date(recording.recordedAt || Date.now()));
  const slug = slugify(name);
  const folderName = `${timestamp}_${slug}`;
  const folderPath = join(baseDir, folderName);
  mkdirSync(folderPath, { recursive: true });

  recording.name = name;
  const filePath = join(folderPath, 'recording.json');
  writeFileSync(filePath, JSON.stringify(recording, null, 2));
  return filePath;
}

// ---------------------------------------------------------------------------
// Find / Load
// ---------------------------------------------------------------------------

/**
 * Find a recording by name. Returns the most recent match.
 * @param {string} name - Name or partial name to search for
 * @returns {{ path: string, recording: object } | null}
 */
export function findRecording(name) {
  const baseDir = getVaultRecordingsDir();
  if (!existsSync(baseDir)) return null;

  const slug = slugify(name);
  const entries = readdirSync(baseDir).sort().reverse(); // newest first

  for (const entry of entries) {
    if (entry.includes(slug)) {
      const filePath = join(baseDir, entry, 'recording.json');
      if (existsSync(filePath)) {
        const recording = JSON.parse(readFileSync(filePath, 'utf8'));
        return { path: filePath, recording };
      }
    }
  }

  return null;
}

/**
 * List all recordings in the vault.
 * @returns {Array<{ folder: string, name: string, date: string, steps: number, path: string }>}
 */
export function listRecordings() {
  const baseDir = getVaultRecordingsDir();
  if (!existsSync(baseDir)) return [];

  const entries = readdirSync(baseDir).sort().reverse();
  const results = [];

  for (const entry of entries) {
    const filePath = join(baseDir, entry, 'recording.json');
    if (existsSync(filePath)) {
      try {
        const recording = JSON.parse(readFileSync(filePath, 'utf8'));
        results.push({
          folder: entry,
          name: recording.name || '',
          date: recording.recordedAt || '',
          steps: recording.steps ? recording.steps.length : 0,
          path: filePath,
        });
      } catch (err) {
        console.warn(`[cc-browser] Skipping corrupt recording: ${entry} (${err.message})`);
      }
    }
  }

  return results;
}
