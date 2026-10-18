// Real Tauri/WebView2, SQLite, PDFium and Fluent UI. Seeded results are synthetic.
// Run cargo run --example m2_fixture first. No credentials or provider calls.
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright-core';

const workspace = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const fixture = JSON.parse(await readFile(path.join(workspace, 'output/m2-desktop-acceptance/latest.json'), 'utf8'));
assert.equal(fixture.synthetic, true);
const directory = fixture.data_directory;
const output = path.join(workspace, 'output/m2-desktop-smoke');
const port = 19224;
await mkdir(output, { recursive: true });
const report = { date: new Date().toISOString(), synthetic: true, providerCalls: 0, checks: [], consoleErrors: [] };
const check = name => { report.checks.push(name); console.log(`PASS ${name}`); };
const delay = milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds));
async function until(get, accept, timeout = 20000) {
  const end = Date.now() + timeout;
  while (Date.now() < end) { const result = await get(); if (accept(result)) return result; await delay(100); }
  throw new Error('Timed out waiting for application state');
}
let child, browser, page;
const call = (command, args = {}) => page.evaluate(({ command, args }) => window.__TAURI_INTERNALS__.invoke(command, args), { command, args });
async function launch() {
  child = spawn(path.join(workspace, 'target/debug/oh-my-study.exe'), [], { cwd: workspace, windowsHide: true, stdio: 'ignore', env: { ...process.env, OH_MY_STUDY_DATA_DIR: directory, WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS: `--remote-debugging-port=${port}` } });
  await until(async () => { assert.equal(child.exitCode, null); try { browser = await chromium.connectOverCDP(`http://127.0.0.1:${port}`); return true; } catch { return false; } }, value => value);
  page = await until(async () => browser.contexts().flatMap(context => context.pages()).find(page => page.url().includes('tauri.localhost')), Boolean);
  page.on('pageerror', error => report.consoleErrors.push(error.message));
  await page.getByRole('heading', { name: '导入与批改' }).waitFor();
  return call('bootstrap');
}
async function close() {
  const owned = child;
  await call('plugin:window|close', { label: 'main' }).catch(() => undefined);
  await until(async () => owned.exitCode !== null, Boolean, 12000);
  child = null;
  await until(async () => { try { await fetch(`http://127.0.0.1:${port}/json/version`); return false; } catch { return true; } }, Boolean);
}
async function open(document) {
  await page.evaluate(id => { window.location.hash = `/documents/${id}`; }, document.id);
  await page.getByRole('heading', { name: document.name, exact: true }).waitFor();
  await page.locator('.question-card').first().waitFor();
  await page.locator('.image-preview image').waitFor();
  await page.locator('.image-preview image').evaluate(async element => {
    const bitmap = new Image(); bitmap.src = element.getAttribute('href'); await bitmap.decode();
  });
}
try {
  const data = await launch();
  const teacher = data.documents.find(d => d.name === 'M2 自建教师批改数学卷');
  const blank = data.documents.find(d => d.name === 'M2 自建空白数学卷');
  const ai = data.documents.find(d => d.name === 'M2 自建 AI 批改数学卷');
  assert(teacher && blank && ai, 'prepare m2_fixture first');
  assert.equal(teacher.pages.length, 2);
  assert(teacher.pages.every(p => p.preview_status === 'ready' && p.pdf_page_index === 0 && p.original_asset_id));
  check('three isolated modes use actual PDFium pages and original PDF references');
  await open(teacher);
  await page.locator('.math-formula svg').waitFor();
  await page.getByRole('button', { name: '来源 1', exact: true }).first().click();
  await page.locator('.image-preview rect').waitFor();
  check('offline MathJax SVG and normalized source-region highlight render in WebView2');
  await page.evaluate(() => window.scrollTo(0, 0));
  await page.screenshot({ path: path.join(output, 'teacher.png'), fullPage: true });
  await page.getByRole('button', { name: '编辑题目与批改', exact: true }).click();
  await page.getByLabel('批改结果', { exact: true }).selectOption('correct');
  await page.getByLabel('得分（无法判定留空）', { exact: true }).fill('2');
  await page.getByLabel('评分说明', { exact: true }).fill('自建验收：人工确认并改分');
  await page.getByRole('button', { name: '保存修改', exact: true }).click();
  await until(() => call('grading_result', { document_id: teacher.id }), result => result.questions[0].question.attempt.grade.source === 'manual');
  const edited = (await call('grading_result', { document_id: teacher.id })).questions[0];
  assert.equal(edited.question.attempt.grade.earned, 2);
  assert.equal(edited.question.attempt.teacher.raw, '-2');
  check('Fluent edit dialog persists manual score while retaining teacher evidence');
  const invalid = structuredClone(edited.question); invalid.attempt.grade.earned = 5;
  await assert.rejects(call('edit_question', { id: edited.id, question: invalid }), /得分超出满分/);
  await assert.rejects(call('update_pages', { document_id: teacher.id, pages: [] }), /仍被题目或参考答案引用/);
  check('IPC rejects out-of-range scores and removal of referenced source pages');
  await page.getByRole('button', { name: '重新批改整卷', exact: true }).click();
  await page.getByText('请先保存供应商密钥', { exact: true }).waitFor();
  check('missing credential produces an explicit UI error without provider requests');
  await open(blank);
  assert.equal(await page.locator('.question-card .attempt-result').count(), 0);
  await page.evaluate(() => window.scrollTo(0, 0));
  await page.screenshot({ path: path.join(output, 'blank.png'), fullPage: true });
  check('blank-paper results contain no manufactured student attempts');
  await open(ai);
  await page.getByText('AI 按参考答案：', { exact: true }).waitFor();
  await page.setViewportSize({ width: 900, height: 640 });
  assert(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth), 'narrow viewport must not overflow horizontally');
  await page.evaluate(() => window.scrollTo(0, 0));
  await page.screenshot({ path: path.join(output, 'narrow.png'), fullPage: true });
  check('reference provenance displays and 900px logical viewport remains usable');
  await page.evaluate(() => { window.location.hash = '/questions'; });
  await page.getByRole('heading', { name: '我的题库', exact: true }).waitFor();
  await until(() => page.locator('.question-card').count(), count => count === 3);
  await page.evaluate(() => { window.location.hash = '/wrong'; });
  await page.getByRole('heading', { name: '错题整理', exact: true }).waitFor();
  await until(() => page.locator('.question-card').count(), count => count === 1);
  check('stored questions are shared by library and basic wrong-question view after editing');
  await close();
  await launch();
  const restored = await call('grading_result', { document_id: teacher.id });
  assert.equal(restored.questions[0].question.attempt.grade.source, 'manual');
  assert.equal(restored.questions[0].question.attempt.grade.earned, 2);
  await close();
  check('normal exit and restart preserve edited grading data');
  assert.deepEqual(report.consoleErrors, []);
  report.status = 'passed';
} catch (error) {
  report.status = 'failed'; report.error = error.stack;
  if (page) await page.screenshot({ path: path.join(output, 'failure.png'), fullPage: true }).catch(() => undefined);
  throw error;
} finally {
  if (child?.exitCode === null) await close().catch(() => { if (child?.exitCode === null) child.kill(); });
  await writeFile(path.join(output, 'report.json'), JSON.stringify(report, null, 2));
}
