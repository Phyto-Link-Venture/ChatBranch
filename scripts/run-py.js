#!/usr/bin/env node
// Cross-platform stand-in for "cd <dir> && venv\Scripts\python <args>" /
// "cd <dir> && venv/bin/python <args>". Resolves the repo's backend/venv
// interpreter for whichever OS this runs on (Windows/macOS/Linux) and spawns
// it in the requested directory, forwarding stdio and the child's exit code.
//
// Usage: node scripts/run-py.js <cwd-relative-to-repo-root> <python-args...>
'use strict';
const path = require('path');
const fs = require('fs');
const { spawnSync } = require('child_process');

const repoRoot = path.resolve(__dirname, '..');
const venvDir = path.join(repoRoot, 'backend', 'venv');

function venvPython() {
  const bin = process.platform === 'win32'
    ? path.join(venvDir, 'Scripts', 'python.exe')
    : path.join(venvDir, 'bin', 'python');
  if (!fs.existsSync(bin)) {
    console.error(
      `Python virtualenv not found at ${bin}\n` +
      'Run "npm run setup" (or "npm run setup:api") first.'
    );
    process.exit(1);
  }
  return bin;
}

const [, , cwdArg, ...args] = process.argv;
if (!cwdArg || args.length === 0) {
  console.error('Usage: node scripts/run-py.js <cwd-relative-to-repo-root> <python-args...>');
  process.exit(1);
}

const cwd = path.resolve(repoRoot, cwdArg);
const result = spawnSync(venvPython(), args, { cwd, stdio: 'inherit' });
if (result.error) {
  console.error(result.error.message);
  process.exit(1);
}
process.exit(result.status === null ? 1 : result.status);
