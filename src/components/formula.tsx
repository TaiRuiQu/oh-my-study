import { useMemo } from 'react';
import { mathjax } from 'mathjax-full/js/mathjax.js';
import { TeX } from 'mathjax-full/js/input/tex.js';
import { SVG } from 'mathjax-full/js/output/svg.js';
import { liteAdaptor } from 'mathjax-full/js/adaptors/liteAdaptor.js';
import { RegisterHTMLHandler } from 'mathjax-full/js/handlers/html.js';
import 'mathjax-full/js/input/tex/ams/AmsConfiguration.js';
import 'mathjax-full/js/input/tex/mhchem/MhchemConfiguration.js';

const adaptor = liteAdaptor();
RegisterHTMLHandler(adaptor);
const document = mathjax.document('', {
  InputJax: new TeX({ packages: ['base', 'ams', 'mhchem'], maxBuffer: 20000 }),
  OutputJax: new SVG({ fontCache: 'none' }),
});
export function Formula({ latex }: { latex: string }) {
  const markup = useMemo(() => {
    try {
      if (latex.length > 20000) return null;
      const node = document.convert(latex, { display: true });
      const svg = adaptor.outerHTML(node);
      // No HTML/require extension or external resources. Keep a strict SVG vocabulary.
      const parsed = new DOMParser().parseFromString(svg, 'text/html');
      const root = parsed.querySelector('svg');
      if (!root) return null;
      for (const element of [root, ...root.querySelectorAll('*')]) {
        if (
          !['svg', 'g', 'path', 'rect', 'text', 'defs', 'use', 'title'].includes(
            element.tagName.toLowerCase(),
          )
        ) {
          element.remove();
          continue;
        }
        for (const attribute of [...element.attributes]) {
          if (
            attribute.name.startsWith('on') ||
            (/(?:href|src)$/i.test(attribute.name) && !attribute.value.startsWith('#')) ||
            /url\s*\(/i.test(attribute.value)
          )
            element.removeAttribute(attribute.name);
        }
      }
      return root.outerHTML;
    } catch {
      return null;
    }
  }, [latex]);
  return markup ? (
    <span
      className="math-formula"
      role="math"
      aria-label={latex}
      dangerouslySetInnerHTML={{ __html: markup }}
    />
  ) : (
    <code className="formula-fallback">{latex}</code>
  );
}
