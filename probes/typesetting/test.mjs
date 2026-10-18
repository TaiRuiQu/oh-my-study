import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { checkLatex, validateSvg, projectPaper } from './pipeline.mjs';
import { sample } from './sample.mjs';

test('TeX rejects external links, HTML and dynamic package loading', () => {
  for (const latex of ['\\href{https://example.com}{x}', '\\require{html}', '\\htmlClass{x}{y}', '\\includegraphics{a}', 'x'.repeat(8001)]) assert.throws(() => checkLatex(latex));
  checkLatex('\\ce{SO4^2- + Ba^2+ -> BaSO4 v}');
});
test('SVG refuses executable and external/font-dependent content', () => {
  for (const svg of ['<svg><script/></svg>', '<svg><text>x</text></svg>', '<svg><image href="https://example.com/x"/></svg>', '<svg><path onload="x"/></svg>', '<svg><use href="file:///x"/></svg>', '<svg><style>@import "https://example.com";</style></svg>', '<svg><animate/></svg>']) assert.throws(() => validateSvg(svg));
  validateSvg('<svg><path d="M0 0L1 1"/></svg>');
});
const formulas = new Map(sample.questions.flatMap(q => [...q.body, ...(q.parts || []).flatMap(p => p.body)]).filter(x => x.type === 'math').map(x => [x.latex, {path: 'output/f.svg', widthPt: 10, heightPt: 12, depthPt: 2}]));
test('whitelist projection strips answers and nested student data from question JSON', () => {
  const dirty = structuredClone(sample);
  dirty.questions[0].secret = 'STUDENT_PRIVATE_EXTRA';
  dirty.questions[7].parts[0].studentAttempt = 'STUDENT_PRIVATE_NESTED';
  const data = JSON.stringify(projectPaper(dirty, formulas, 'questions'));
  assert.doesNotMatch(data, /STUDENT_PRIVATE|ANSWER_ONLY|SUBANSWER|explanation|studentAttempt|secret/);
  assert.match(data, /UNTRUSTED_TYPST/);
});
test('answer projection preserves all question and sub-question references', () => {
  const data = projectPaper(sample, formulas, 'answers');
  assert.deepEqual(data.questions.map(q => q.id), sample.questions.map(q => q.id));
  assert.equal(data.questions[7].parts.length, 12);
  assert.doesNotMatch(JSON.stringify(data), /STUDENT_PRIVATE|studentAttempt/);
});
test('trusted template has no dynamic Typst evaluation or interpolated source', async () => {
  const source = await readFile(new URL('template.typ', import.meta.url), 'utf8');
  assert.doesNotMatch(source, /\beval\s*\(|#include|#import/);
  assert.match(source, /text\(item.text\)/);
});
