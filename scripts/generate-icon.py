"""Code-native book icon source; requires Pillow only at development time."""
from pathlib import Path
from PIL import Image, ImageDraw

root = Path(__file__).resolve().parents[1]
icon = Image.new("RGBA", (256, 256), (0, 0, 0, 0))
draw = ImageDraw.Draw(icon)
draw.rounded_rectangle((8, 8, 248, 248), radius=56, fill="#16756b")
draw.polygon([(55, 58), (114, 58), (128, 72), (128, 204), (99, 189), (55, 189)], fill="white")
draw.polygon([(201, 58), (142, 58), (128, 72), (128, 204), (157, 189), (201, 189)], fill="white")
for y in (88, 110):
    draw.line((77, y, 106, y), fill="#16756b", width=8)
    draw.line((150, y, 179, y), fill="#16756b", width=8)
icon.save(root / "src-tauri/icons/icon.ico", sizes=[(16, 16), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
