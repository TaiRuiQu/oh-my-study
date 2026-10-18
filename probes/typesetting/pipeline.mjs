import { DOMParser, XMLSerializer } from '@xmldom/xmldom';
import assert from 'node:assert/strict';

export const FONT_SHA256 = '78aa7a328fd974df2d688c8a9fd74a33d8334dfa84ab24d9d11efb2ffc464117';
export function checkLatex(latex) {
  if (typeof latex !== 'string' || latex.length > 8000) throw new Error('Invalid formula size');
  if (/\\(?:href|url|html\w*|includegraphics|require|style|class|cssId)\b/i.test(latex)) throw new Error('External/HTML TeX commands prohibited');
}
export function outlineSvg(raw, font, fontSizePt = 11) {
  const doc = new DOMParser().parseFromString(raw, 'image/svg+xml');
  const svg = doc.documentElement;
  const ex = value => {
    assert.match(value, /^-?[\d.]+ex$/);
    return Number.parseFloat(value) * fontSizePt / 2;
  };
  const widthPt = ex(svg.getAttribute('width'));
  const heightPt = ex(svg.getAttribute('height'));
  const depthEx = /vertical-align:\s*(-?[\d.]+)ex/.exec(svg.getAttribute('style'));
  const depthPt = depthEx ? -Number(depthEx[1]) * fontSizePt / 2 : 0;
  assert.ok(widthPt > 0 && widthPt < 450 && heightPt > 0 && heightPt < 200 && depthPt >= 0 && depthPt < heightPt);
  // MathJax emits unknown CJK glyphs as <text>; outline with the same pinned print font.
  for (const node of Array.from(doc.getElementsByTagName('text'))) {
    const size = Number.parseFloat(node.getAttribute('font-size') || '884');
    const value = node.textContent;
    for (const ch of value) assert.notEqual(font.charToGlyph(ch).index, 0, `Missing glyph: ${ch}`);
    const group = doc.createElementNS('http://www.w3.org/2000/svg', 'g');
    if (node.hasAttribute('transform')) group.setAttribute('transform', node.getAttribute('transform'));
    const path = doc.createElementNS('http://www.w3.org/2000/svg', 'path');
    path.setAttribute('d', font.getPath(value, 0, 0, size).toPathData(3));
    group.appendChild(path); node.parentNode.replaceChild(group, node);
  }
  svg.setAttribute('width', `${widthPt}pt`); svg.setAttribute('height', `${heightPt}pt`);
  svg.removeAttribute('style'); svg.setAttribute('color', '#000000');
  const output = new XMLSerializer().serializeToString(doc).replaceAll('currentColor', '#000000');
  validateSvg(output);
  return {svg: output, widthPt, heightPt, depthPt};
}
export function validateSvg(svg) {
  assert.ok(!/<(?:script|foreignObject|text|image)\b|\bon\w+\s*=|<!DOCTYPE|<!ENTITY/i.test(svg), 'Non-self-contained SVG');
  assert.ok(!/(?:href|src)\s*=\s*["'](?!#)/i.test(svg), 'External SVG reference');
  assert.ok(!/url\(\s*(?!#)/i.test(svg), 'External SVG style');
  const doc = new DOMParser().parseFromString(svg, 'image/svg+xml');
  const elements = new Set(['svg', 'g', 'path', 'rect', 'line', 'circle', 'ellipse', 'polygon', 'polyline', 'defs', 'use']);
  assert.equal(doc.documentElement.tagName, 'svg');
  for (const element of Array.from(doc.getElementsByTagName('*'))) {
    assert.ok(elements.has(element.tagName), `Unsupported SVG element: ${element.tagName}`);
    for (const attr of Array.from(element.attributes)) {
      assert.ok(!/^on|^style$/i.test(attr.name), 'SVG event/style prohibited');
      if (/(?:^|:)href$|^src$/i.test(attr.name)) assert.match(attr.value, /^#[\w-]+$/);
    }
  }
}
export function projectPaper(source, formulas, kind) {
  assert.ok(['questions', 'answers'].includes(kind));
  const body = items => items.map(item => {
    if (item.type === 'text') return {type: 'text', text: String(item.text)};
    assert.equal(item.type, 'math');
    assert.ok(formulas.has(item.latex));
    const f = formulas.get(item.latex);
    return {type: 'math', path: f.path, widthPt: f.widthPt, heightPt: f.heightPt, depthPt: f.depthPt};
  });
  return {title: source.title, kind, questions: source.questions.map(q => {
    const result = {id: q.id, number: q.number, body: body(q.body)};
    if (q.section) result.section = q.section;
    if (kind === 'answers') {
      result.answer = q.answer; result.explanation = q.explanation;
      if (q.parts) result.parts = q.parts.map(p => ({label: p.label, answer: p.answer}));
    }
    else {
      if (q.options) result.options = q.options.map(String);
      if (q.table) result.table = q.table.map(row => row.map(String));
      if (q.figure) result.figurePath = 'output/structure.svg';
      if (q.blankMm) result.blankMm = q.blankMm;
      if (q.parts) result.parts = q.parts.map(p => ({label: p.label, body: body(p.body), blankMm: p.blankMm}));
    }
    return result;
  })};
}
