"""Validate real generated PDFs, then render the exact same bytes for visual review.
Run with the bundled Python (pypdf + pdfplumber) and Poppler on PATH.
"""
import json
import re
import shutil
import subprocess
from pathlib import Path
import pdfplumber
from pypdf import PdfReader

ROOT = Path(__file__).resolve().parent
OUTPUT = ROOT / 'output'
summary = []
for paper in ('a3', 'a4'):
    for kind in ('questions', 'answers'):
        path = OUTPUT / f'{paper}-{kind}.pdf'
        reader = PdfReader(path)
        expected = (420, 297) if paper == 'a3' else (210, 297)
        text = '\n'.join(page.extract_text() for page in reader.pages)
        assert 'STUDENT_PRIVATE' not in text, path
        if kind == 'questions':
            assert 'ANSWER_ONLY' not in text and 'SUBANSWER' not in text, path
            assert 'UNTRUSTED_TYPST' in text, 'Untrusted source was not printed as data'
            for i in range(1, 13):
                assert re.search(rf'第\s*{i}\s*小问', text), f'Missing sub-question {i}'
        else:
            for i in range(1, 10):
                assert re.search(rf'ANSWER_ONLY_{i}\s*[：:]', text), f'Missing answer {i}'
            for i in range(1, 13):
                assert re.search(rf'SUBANSWER_{i}\s*[：:]', text), f'Missing sub-answer {i}'
        fonts = set()
        for page in reader.pages:
            width, height = float(page.mediabox.width), float(page.mediabox.height)
            assert abs(width * 25.4 / 72 - expected[0]) < .05
            assert abs(height * 25.4 / 72 - expected[1]) < .05
            assert not page.get('/Annots'), 'Unexpected interactive annotations'
            for ref in page['/Resources'].get('/Font', {}).values():
                font = ref.get_object()
                fonts.add(str(font.get('/BaseFont')))
                descendants = font.get('/DescendantFonts')
                if descendants:
                    font = descendants[0].get_object()
                descriptor = font.get('/FontDescriptor')
                assert descriptor, 'Missing font descriptor'
                descriptor = descriptor.get_object()
                assert any(k in descriptor for k in ('/FontFile', '/FontFile2', '/FontFile3')), 'Unembedded font'
        char_count = 0
        with pdfplumber.open(path) as pdf:
            for page in pdf.pages:
                for char in page.chars:
                    char_count += 1
                    assert 0 <= char['x0'] < char['x1'] <= page.width + .1, (path, char)
                    assert 0 <= char['top'] < char['bottom'] <= page.height + .1, (path, char)
                    assert '\ufffd' not in char['text'] and '\x00' not in char['text'], 'Missing text glyph'
        assert char_count > 100
        assert all('SourceHanSerifSC' in f for f in fonts), fonts
        poppler = shutil.which('pdftoppm')
        if not poppler:
            raise RuntimeError('Poppler pdftoppm missing; PDF assertions passed but render is not verified')
        subprocess.run([poppler, '-scale-to', '1800', '-png', str(path), str(OUTPUT / path.stem)], check=True)
        summary.append({'file': path.name, 'pages': len(reader.pages), 'sizeMm': expected, 'embeddedFonts': sorted(fonts), 'charactersChecked': char_count})
report = {'status': 'automatic checks passed; inspect all PNG pages separately; physical printing and WebView2 remain pending', 'pdfs': summary}
(OUTPUT / 'verification.json').write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
print(json.dumps(report, ensure_ascii=False, indent=2))
