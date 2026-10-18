import { readFile, writeFile, mkdir } from 'node:fs/promises';
import { createServer } from 'node:http';
import { resolve, dirname, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createHash } from 'node:crypto';
import assert from 'node:assert/strict';
import opentype from 'opentype.js';
import { chromium } from 'playwright-core';
import { NodeCompiler } from '@myriaddreamin/typst-ts-node-compiler';
import { checkLatex, outlineSvg, projectPaper, FONT_SHA256 } from './pipeline.mjs';
import { sample } from './sample.mjs';

const root = dirname(fileURLToPath(import.meta.url));
const output = resolve(root, 'output');
await mkdir(output, {recursive: true});
const holdIndex = process.argv.indexOf('--hold-seconds');
const holdSeconds = holdIndex < 0 ? 0 : Number(process.argv[holdIndex + 1]);
assert.ok(Number.isFinite(holdSeconds) && holdSeconds >= 0 && holdSeconds <= 30, 'hold-seconds must be 0..30');
const hold = async phase => {
  await writeFile(resolve(output, 'phase.json'), JSON.stringify({phase, pid: process.pid, date: new Date().toISOString(), memory: process.memoryUsage()}));
  if (holdSeconds) await new Promise(done => setTimeout(done, holdSeconds * 1000));
};
const fontBytes = await readFile(resolve(root, 'assets/SourceHanSerifSC-Regular.otf'));
assert.equal(createHash('sha256').update(fontBytes).digest('hex'), FONT_SHA256);
const font = opentype.parse(fontBytes.buffer.slice(fontBytes.byteOffset, fontBytes.byteOffset + fontBytes.byteLength));
const formulas = new Map();
const latex = [...new Set(sample.questions.flatMap(q => [ ...q.body, ...(q.parts || []).flatMap(p => p.body) ]).filter(b => b.type === 'math').map(b => b.latex))];
latex.forEach(checkLatex);
// Browser uses entirely local MathJax and mhchem resources. No browser download/system install.
const server = createServer(async (req, res) => {
  try {
    if (req.url === '/') {
      res.setHeader('Content-Type', 'text/html; charset=utf-8');
      res.end(`<!doctype html><html><head><script>window.MathJax={loader:{load:['[tex]/mhchem']},tex:{packages:{'[+]':['mhchem']}},svg:{fontCache:'none'},startup:{typeset:false}};</script><script src="/node_modules/mathjax-full/es5/tex-svg.js"></script></head><body>Local MathJax probe</body></html>`);
      return;
    }
    const file = resolve(root, '.' + decodeURIComponent(req.url.split('?')[0]));
    const allowed = resolve(root, 'node_modules/mathjax-full') + sep;
    if (!file.startsWith(allowed)) { res.writeHead(403); res.end(); return; }
    res.setHeader('Content-Type', 'application/javascript'); res.end(await readFile(file));
  } catch { res.writeHead(404); res.end(); }
});
await new Promise(done => server.listen(0, '127.0.0.1', done));
const origin = `http://127.0.0.1:${server.address().port}`;
let browser;
const started = performance.now();
let browserVersion;
try {
  browser = await chromium.launch({channel: 'msedge', headless: true});
  browserVersion = browser.version();
  const page = await browser.newPage();
  await page.route('**/*', route => route.request().url().startsWith(origin + '/') ? route.continue() : route.abort());
  await page.goto(origin);
  await page.evaluate(() => window.MathJax.startup.promise);
  for (const [i, formula] of latex.entries()) {
    const raw = await page.evaluate(async formula => {
      const node = await window.MathJax.tex2svgPromise(formula, {display: false, em: 16, ex: 8});
      if (node.querySelector('[data-mjx-error], mjx-merror, [data-mml-node="merror"]')) throw new Error('MathJax formula error: ' + formula);
      return node.querySelector('svg').outerHTML;
    }, formula);
    const result = outlineSvg(raw, font);
    result.path = `output/formula-${i+1}.svg`;
    await writeFile(resolve(root, result.path), result.svg);
    formulas.set(formula, result);
  }
  await hold('mathjax-browser-alive');
} finally { if (browser) await browser.close(); await new Promise(done => server.close(done)); }

// Original path-only benzene-like structural diagram, no imported scripts or remote references.
await writeFile(resolve(output, 'structure.svg'), `<svg xmlns="http://www.w3.org/2000/svg" width="240" height="160" viewBox="0 0 240 160"><g fill="none" stroke="black" stroke-width="2"><path d="M75 25 L135 25 L165 77 L135 129 L75 129 L45 77 Z M81 35 L129 35 M151 77 L128 118 M58 77 L81 118 M165 77 L210 77"/></g></svg>`);
const metrics = {date: new Date().toISOString(), engine: 'typst-ts-node-compiler 0.7.0 (native Typst)', formulaEngine: 'MathJax 3.2.2 + mhchem, Edge headless; not WebView2 integration', browserVersion, fontSha256: FONT_SHA256, formulaCount: formulas.size, formulas: [...formulas].map(([latex, f]) => ({latex, path: f.path, widthPt: f.widthPt, heightPt: f.heightPt, depthPt: f.depthPt})), pdfs: []};
for (const paper of ['a3', 'a4']) {
  for (const kind of ['questions', 'answers']) {
    const data = projectPaper(sample, formulas, kind);
    const jsonPath = resolve(output, `${paper}-${kind}.json`);
    await writeFile(jsonPath, JSON.stringify(data, null, 2));
    const compiler = NodeCompiler.create({workspace: root, fontArgs: [{fontBlobs: [fontBytes]}]});
    const before = performance.now();
    const result = compiler.compile({mainFilePath: resolve(root, 'template.typ'), inputs: {data: `output/${paper}-${kind}.json`, paper}});
    const diagnostics = result.takeDiagnostics();
    if (diagnostics) {
      const items = compiler.fetchDiagnostics(diagnostics);
      if (items.length) { console.error(JSON.stringify(items, null, 2)); throw new Error('Typst diagnostics; inspect output'); }
    }
    assert.ok(result.result, 'Missing compiled document');
    const pdf = compiler.pdf(result.result);
    await writeFile(resolve(output, `${paper}-${kind}.pdf`), pdf);
    metrics.pdfs.push({paper, kind, bytes: pdf.length, elapsedMs: Math.round(performance.now() - before)});
  }
}
metrics.totalElapsedMs = Math.round(performance.now() - started);
await hold('pdfs-compiled-node-cache-retained');
await writeFile(resolve(output, 'metrics.json'), JSON.stringify(metrics, null, 2));
console.log(JSON.stringify(metrics, null, 2));
