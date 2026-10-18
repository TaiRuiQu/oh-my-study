// Runs against the real Tauri/WebView2 window and real isolated SQLite profile.
// Prepare with cargo run --example m1_fixture; no provider calls or user material.
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright-core';

const workspace = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const directory = path.join(workspace, 'output/m1-desktop-acceptance');
const output = path.join(workspace, 'output/m1-desktop-smoke');
const port = 19223;
await mkdir(output, { recursive: true });
const report = { date: new Date().toISOString(), fixture: directory, checks: [], consoleErrors: [] };
const check = (name) => { report.checks.push(name); console.log(`PASS ${name}`); };
const delay = (milliseconds) => new Promise((resolve) => setTimeout(resolve, milliseconds));
async function until(get, accept, timeout = 15000) {
  const deadline = Date.now() + timeout;
  let last;
  while (Date.now() < deadline) {
    last = await get();
    if (accept(last)) return last;
    await delay(100);
  }
  throw new Error(`Timed out; last state: ${JSON.stringify(last)}`);
}
let child;
let page;
let browser;
function call(command, args = {}) {
  return page.evaluate(({ command, args }) => window.__TAURI_INTERNALS__.invoke(command, args), { command, args });
}
async function launch() {
  child = spawn(path.join(workspace, 'target/debug/oh-my-study.exe'), [], {
    cwd: workspace, windowsHide: true, stdio: 'ignore',
    env: { ...process.env, OH_MY_STUDY_DATA_DIR: directory, WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS: `--remote-debugging-port=${port}` },
  });
  const deadline = Date.now() + 20000;
  while (true) {
    assert.equal(child.exitCode, null, 'desktop process exited during startup');
    try { browser = await chromium.connectOverCDP(`http://127.0.0.1:${port}`); break; }
    catch (error) { if (Date.now() > deadline) throw error; await delay(200); }
  }
  page = await until(async () => browser.contexts().flatMap((context) => context.pages()),
    (pages) => pages.some((item) => item.url().includes('tauri.localhost'))).then((pages) => pages.find((item) => item.url().includes('tauri.localhost')));
  page.on('pageerror', (error) => report.consoleErrors.push(error.message));
  await page.getByRole('heading', { name: /导入与批改/ }).waitFor();
  return call('bootstrap');
}
async function close() {
  const owned = child;
  const exited = new Promise((resolve) => owned.once('exit', resolve));
  await call('plugin:window|close', { label: 'main' }).catch(() => undefined);
  let timeout;
  try {
    await Promise.race([exited, new Promise((_, reject) => {
      timeout = setTimeout(() => reject(new Error('desktop failed to drain and exit')), 10000);
    })]);
  } finally { clearTimeout(timeout); }
  child = null;
  browser = null;
  await until(async () => {
    try { await fetch(`http://127.0.0.1:${port}/json/version`); return true; }
    catch { return false; }
  }, (listening) => !listening, 5000);
}

try {
  const initial = await launch();
  assert(initial.documents.length > 0, 'prepare m1_fixture before this test');
  assert.equal(initial.environment.policy.executor_kind, 'host');
  assert(initial.environment.policy.warning.includes('未隔离'));
  check('static desktop startup, SQLite bootstrap and host execution warning');
  await page.screenshot({ path: path.join(output, 'import.png'), fullPage: true });

  const document = initial.documents[0];
  await page.getByRole('link', { name: new RegExp(document.name.slice(0, 7)) }).first().click();
  await page.locator('.image-preview [role="img"]').waitFor();
  const original = document.pages[0];
  await page.getByTitle('顺时针旋转').click();
  await until(() => call('list_documents'), (documents) => documents.find((item) => item.id === document.id)?.pages.find((item) => item.id === original.id)?.rotation === (original.rotation + 90) % 360);
  check('opaque asset image preview and persisted rotation through real IPC');
  await page.evaluate(() => window.scrollTo(0, 0));
  await page.screenshot({ path: path.join(output, 'material.png'), fullPage: true });

  await page.getByRole('link', { name: '设置', exact: true }).click();
  await page.getByRole('button', { name: '添加供应商', exact: true }).first().click();
  const providerName = `M1 验收供应商 ${Date.now()}`;
  await page.getByLabel('供应商名称', { exact: true }).fill(providerName);
  await page.getByRole('button', { name: '手动添加模型', exact: true }).click();
  await page.getByLabel('模型 ID', { exact: true }).fill('m1-manual-unknown');
  await page.getByRole('button', { name: '保存配置', exact: true }).click();
  const provider = await until(() => call('bootstrap'), (data) => data.providers.some((item) => item.name === providerName))
    .then((data) => data.providers.find((item) => item.name === providerName));
  assert.equal(provider.has_key, false);
  assert.equal(provider.models[0].capabilities.vision, null);
  assert.equal(provider.models[0].price_input, null);
  check('provider/manual unknown model persisted, missing key and prices remain unknown');

  await page.getByRole('tab', { name: '执行与权限' }).click();
  await page.getByRole('radio', { name: /请求权限/ }).click();
  await page.getByLabel('默认模型', { exact: true }).selectOption(`${provider.id}:m1-manual-unknown`);
  await page.getByRole('button', { name: '保存设置', exact: true }).click();
  await until(() => call('bootstrap'), (data) => data.settings.execution_mode === 'request_permission');
  check('permission preference and provider-qualified default model persisted');
  await page.screenshot({ path: path.join(output, 'settings.png'), fullPage: true });

  await page.getByRole('button', { name: /^任务/ }).first().click();
  const known = new Set((await call('list_jobs')).map((job) => job.id));
  await page.getByRole('button', { name: '检查存储', exact: true }).click();
  const storageJob = await until(() => call('list_jobs'), (jobs) => jobs.some((job) => !known.has(job.id) && job.kind === 'storage' && job.state === 'COMPLETED'))
    .then((jobs) => jobs.find((job) => !known.has(job.id) && job.kind === 'storage'));
  check('real SQLite diagnostic queue completes with persisted task events');

  await page.getByRole('button', { name: '执行诊断', exact: true }).click();
  await page.getByRole('dialog', { name: '任务请求执行权限', exact: true }).waitFor();
  await page.locator('.permission-code').first().getByText(/sys.flags.isolated/).waitFor();
  await page.screenshot({ path: path.join(output, 'permission.png'), fullPage: true });
  await page.getByRole('button', { name: '允许本任务', exact: true }).click();
  const pythonJob = await until(() => call('list_jobs'), (jobs) => jobs.some((job) => !known.has(job.id) && job.kind === 'permission' && job.state === 'COMPLETED'))
    .then((jobs) => jobs.find((job) => !known.has(job.id) && job.kind === 'permission' && job.state === 'COMPLETED'));
  check('real permission dialog binds reviewed code and runs both private Python checks');
  await page.screenshot({ path: path.join(output, 'tasks.png'), fullPage: true });
  await close();
  const restored = await launch();
  assert(restored.providers.some((item) => item.id === provider.id));
  assert.equal(restored.settings.default_model, `${provider.id}:m1-manual-unknown`);
  assert(restored.jobs.some((job) => job.id === storageJob.id && job.state === 'COMPLETED'));
  assert(restored.jobs.some((job) => job.id === pythonJob.id && job.state === 'COMPLETED'));
  check('normal exit drains work and restart preserves settings, material and terminal jobs');

  const waitingJob = await call('queue_diagnostic_job', { kind: 'permission' });
  await until(() => call('list_jobs'), (jobs) => jobs.some((job) => job.state === 'WAITING_PERMISSION'));
  await close();
  const afterWaiting = await launch();
  assert(!afterWaiting.jobs.some((job) => ['QUEUED', 'RUNNING', 'WAITING_PERMISSION'].includes(job.state)));
  assert.equal(afterWaiting.jobs.find((job) => job.id === waitingJob.id).state, 'CANCELLED');
  check('exit while waiting revokes pending execution and does not replay on restart');
  await close();
  assert.deepEqual(report.consoleErrors, []);
  report.status = 'passed';
} catch (error) {
  report.status = 'failed';
  report.error = error.stack;
  if (page) await page.screenshot({ path: path.join(output, 'failure.png'), fullPage: true }).catch(() => undefined);
  throw error;
} finally {
  if (child && child.exitCode === null) {
    await close().catch(() => { if (child?.exitCode === null) child.kill(); });
  }
  await writeFile(path.join(output, 'report.json'), JSON.stringify(report, null, 2));
}
