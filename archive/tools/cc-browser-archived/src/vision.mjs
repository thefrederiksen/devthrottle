// CC Browser - Vision Integration via Claude Code CLI
// Uses claude -p with Read tool for image analysis (no API key needed)

import childProcess from 'node:child_process';
import { writeFile, unlink } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

/**
 * Analyze a screenshot using Claude Code CLI vision.
 * Saves the image as a temp file and invokes `claude -p` with the Read tool,
 * which natively supports viewing images.
 *
 * @param {string} base64 - Base64-encoded PNG image
 * @param {string} prompt - Text prompt describing what to analyze
 * @param {Object} [options]
 * @param {string} [options.model='haiku'] - Model alias (haiku, sonnet, opus)
 * @returns {Promise<string>} The model's text response
 */
export async function analyzeScreenshot(base64, prompt, { model = 'haiku' } = {}) {
  const tempPath = join(tmpdir(), `cc-browser-captcha-${Date.now()}.png`);
  await writeFile(tempPath, Buffer.from(base64, 'base64'));

  try {
    const fullPrompt = `Read the screenshot image at ${tempPath} using the Read tool, then answer:\n\n${prompt}`;

    // Remove CLAUDECODE env var to avoid nested-session detection
    const env = { ...process.env };
    delete env.CLAUDECODE;

    const stdout = await new Promise((resolve, reject) => {
      childProcess.execFile('claude', [
        '-p', fullPrompt,
        '--tools', 'Read',
        '--dangerously-skip-permissions',
        '--max-turns', '2',
        '--model', model,
        '--output-format', 'text',
        '--no-session-persistence',
      ], { env, timeout: 60000 }, (err, stdout, stderr) => {
        if (err) reject(new Error(`Claude CLI error: ${err.message}${stderr ? '\n' + stderr : ''}`));
        else resolve(stdout.trim());
      });
    });

    return stdout;
  } finally {
    await unlink(tempPath).catch(() => {});
  }
}
