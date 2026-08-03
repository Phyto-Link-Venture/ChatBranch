#!/usr/bin/env node
// Cross-platform stand-in for the old Windows-only "setup:api" one-liner:
// create backend/venv, pip install, copy .env.example -> .env, migrate,
// seed_demo. Works on Windows (py launcher, venv\Scripts\python.exe),
// macOS and Linux (python3, venv/bin/python) without touching a shell.
'use strict';
const path = require('path');
const fs = require('fs');
const { spawnSync } = require('child_process');

const repoRoot = path.resolve(__dirname, '..');
const backendDir = path.join(repoRoot, 'backend');
const venvDir = path.join(backendDir, 'venv');

function run(cmd, args, opts = {}) {
  const result = spawnSync(cmd, args, { stdio: 'inherit', ...opts });
  if (result.error) {
    console.error(`Failed to run "${cmd} ${args.join(' ')}": ${result.error.message}`);
    process.exit(1);
  }
  if (result.status !== 0) {
    process.exit(result.status ?? 1);
  }
}

function probe(cmd, args) {
  const result = spawnSync(cmd, args, { stdio: 'ignore' });
  return !result.error && result.status === 0;
}

// Prefer Python 3.13 (what the venv is tested against), then fall back to
// whatever python3/python is on PATH.
function findBasePython() {
  const candidates = process.platform === 'win32'
    ? [['py', ['-3.13']], ['py', []], ['python', []]]
    : [['python3.13', []], ['python3', []], ['python', []]];
  for (const [cmd, versionArgs] of candidates) {
    if (probe(cmd, [...versionArgs, '--version'])) {
      return [cmd, versionArgs];
    }
  }
  console.error('No usable Python interpreter found (tried python3.13/python3/python/py).');
  process.exit(1);
}

function venvPython() {
  return process.platform === 'win32'
    ? path.join(venvDir, 'Scripts', 'python.exe')
    : path.join(venvDir, 'bin', 'python');
}

if (!fs.existsSync(venvDir)) {
  const [cmd, versionArgs] = findBasePython();
  console.log(`Creating backend/venv with "${cmd} ${versionArgs.join(' ')} -m venv venv"...`);
  run(cmd, [...versionArgs, '-m', 'venv', 'venv'], { cwd: backendDir });
} else {
  console.log('backend/venv already exists, skipping creation.');
}

console.log('Installing backend requirements...');
run(venvPython(), ['-m', 'pip', 'install', '-r', 'requirements.txt'], { cwd: backendDir });

const envPath = path.join(backendDir, '.env');
const envExamplePath = path.join(backendDir, '.env.example');
if (!fs.existsSync(envPath) && fs.existsSync(envExamplePath)) {
  fs.copyFileSync(envExamplePath, envPath);
  console.log('Created backend/.env from .env.example');
} else if (fs.existsSync(envPath)) {
  console.log('backend/.env already exists, leaving it alone.');
}

console.log('Running migrations...');
run(venvPython(), ['manage.py', 'migrate'], { cwd: backendDir });

console.log('Seeding demo data...');
run(venvPython(), ['manage.py', 'seed_demo'], { cwd: backendDir });

console.log('Backend setup complete.');
