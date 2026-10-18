"""Independent developer QA for generated PDFs; never a product runtime dependency."""
import argparse
import json
import subprocess
from pathlib import Path

import pdfplumber
from PIL import Image, ImageDraw
from pypdf import PdfReader


def inspect_pdf(path, poppler):
    reader = PdfReader(path)
    a3 = path.name.startswith("a3-")
    expected = (420, 297) if a3 else (210, 297)
    fonts = set()
    images = 0
    for page in reader.pages:
        actual = (float(page.mediabox.width) * 25.4 / 72, float(page.mediabox.height) * 25.4 / 72)
        assert all(abs(a - b) < 0.05 for a, b in zip(actual, expected)), (path, actual)
        assert not page.get("/Annots"), (path, "unexpected annotations")
        resources = page.get("/Resources").get_object()
        for reference in resources.get("/Font", {}).get_object().values():
            font = reference.get_object()
            descendants = font.get("/DescendantFonts")
            if descendants:
                font = descendants[0].get_object()
            descriptor = font.get("/FontDescriptor")
            assert descriptor, (path, "missing font descriptor")
            descriptor = descriptor.get_object()
            assert any(descriptor.get(key) for key in ("/FontFile", "/FontFile2", "/FontFile3")), (path, "font not embedded")
            fonts.add(str(font.get("/BaseFont")))
        xobjects = resources.get("/XObject")
        if xobjects:
            images += sum(ref.get_object().get("/Subtype") == "/Image" for ref in xobjects.get_object().values())
    characters = 0
    with pdfplumber.open(path) as document:
        for index, page in enumerate(document.pages):
            for char in page.chars:
                characters += 1
                assert char["text"] not in ("\ufffd", "\0"), (path, index, "invalid text")
                assert char["x0"] >= -0.5 and char["x1"] <= page.width + 0.5, (path, index, "horizontal overflow", char)
                assert char["top"] >= -0.5 and char["bottom"] <= page.height + 0.5, (path, index, "vertical overflow", char)
    if path.parent.name == "complex":
        assert images > 0, (path, "missing image")
    render_directory = path.parent / (path.stem + "-pages")
    render_directory.mkdir(exist_ok=True)
    subprocess.run([str(poppler), "-r", "90", "-png", str(path), str(render_directory / "page")], check=True, capture_output=True)
    pages = sorted(render_directory.glob("page-*.png"))
    assert len(pages) == len(reader.pages), (path, "render count")
    columns = 3
    thumb_width, thumb_height = (500, 390) if a3 else (300, 450)
    sheet = Image.new("RGB", (columns * thumb_width, ((len(pages) + columns - 1) // columns) * (thumb_height + 24)), "#d8d8d8")
    draw = ImageDraw.Draw(sheet)
    for index, page in enumerate(pages):
        with Image.open(page) as image:
            image.thumbnail((thumb_width - 12, thumb_height - 12))
            x, y = index % columns * thumb_width, index // columns * (thumb_height + 24)
            sheet.paste(image, (x + 6, y + 24))
            draw.text((x + 6, y + 4), f"{path.stem} page {index + 1}", fill="black")
    sheet.save(path.parent / (path.stem + "-contact.png"))
    return {"path": str(path), "pages": len(reader.pages), "fonts": sorted(fonts), "image_resources": images, "characters": characters, "geometry": "within page"}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("directory", type=Path)
    parser.add_argument("--pdftoppm", type=Path, required=True)
    args = parser.parse_args()
    paths = sorted(args.directory.glob("a[34]-*.pdf")) + sorted((args.directory / "complex").glob("a[34]-*.pdf"))
    assert len(paths) == 8, f"Expected 8 PDF editions, got {len(paths)}"
    results = [inspect_pdf(path, args.pdftoppm) for path in paths]
    report = args.directory / "verification-pdf.json"
    report.write_text(json.dumps(results, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"PASS {len(results)} PDFs; embedded fonts, image presence, text geometry, Poppler render; {report}")


if __name__ == "__main__":
    main()
