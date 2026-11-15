import { spawn, spawnSync } from 'node:child_process';
import { mkdirSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('../../', import.meta.url));
const certDirectory = fileURLToPath(new URL('../../.dev/certs/', import.meta.url));
mkdirSync(certDirectory, { recursive: true, mode: 0o700 });
const exported = spawnSync(
  'dotnet',
  [
    'dev-certs',
    'https',
    '--export-path',
    `${certDirectory}/localhost.pem`,
    '--format',
    'Pem',
    '--no-password',
  ],
  { cwd: root, stdio: 'inherit' },
);
if (exported.status !== 0) process.exit(exported.status ?? 1);

const children = [];
let stopping = false;
function stop(code) {
  if (stopping) return;
  stopping = true;
  for (const child of children) {
    if (child.exitCode === null) {
      if (process.platform === 'win32')
        spawnSync('taskkill', ['/pid', String(child.pid), '/T', '/F']);
      else {
        try {
          process.kill(-child.pid, 'SIGTERM');
        } catch (error) {
          if (error.code !== 'ESRCH') console.error(error);
        }
      }
    }
  }
  // dotnet watch can keep its supervisor alive after SIGTERM; also reap descendants.
  setTimeout(() => {
    for (const child of children) {
      if (process.platform !== 'win32' && child.pid) {
        try {
          process.kill(-child.pid, 'SIGKILL');
        } catch (error) {
          if (error.code !== 'ESRCH') console.error(error);
        }
      }
    }
  }, 3000);
  process.exitCode = code;
}
function start(command, args, env = {}) {
  const child = spawn(command, args, {
    cwd: root,
    stdio: 'inherit',
    detached: process.platform !== 'win32',
    env: { ...process.env, ...env },
  });
  children.push(child);
  child.on('error', (error) => {
    console.error(error.message);
    stop(1);
  });
  child.on('exit', (code) => {
    if (!stopping) stop(code ?? 1);
  });
}
process.on('SIGINT', () => stop(0));
process.on('SIGTERM', () => stop(0));
start('dotnet', ['watch', '--project', 'AliceIdentityService', '--no-launch-profile'], {
  ASPNETCORE_ENVIRONMENT: 'Development',
  ASPNETCORE_URLS: 'http://127.0.0.1:5442',
  DOTNET_WATCH_SUPPRESS_LAUNCH_BROWSER: 'true',
});
// Show the public URL only after the backend can answer proxied requests.
const deadline = Date.now() + 60_000;
let ready = false;
while (!stopping && Date.now() < deadline) {
  try {
    const response = await fetch('http://127.0.0.1:5442/health', {
      signal: AbortSignal.timeout(1000),
    });
    ready = response.ok;
  } catch {
    /* The host is still building or starting. */
  }
  if (ready) break;
  await new Promise((resolve) => setTimeout(resolve, 200));
}
if (!stopping && ready) {
  start(process.platform === 'win32' ? 'npm.cmd' : 'npm', [
    'run',
    'dev:vite',
    '--prefix',
    'AliceIdentityService.Admin',
  ]);
} else if (!stopping) {
  console.error('The AIS3 backend did not become ready within 60 seconds.');
  stop(1);
}
