import zlib
import struct
import math
from pathlib import Path

SIZES = [16, 32, 48, 64, 128, 256]


def make_png(size):
    pixels = bytearray()
    for y in range(size):
        for x in range(size):
            dx = x - size / 2 + 0.5
            dy = y - size / 2 + 0.5
            dist = math.hypot(dx, dy)
            if dist < size * 0.35:
                r, g, b, a = 0x00, 0xAD, 0xFF, 0xFF
            elif dist < size * 0.43:
                r, g, b = 0x00, 0xAD, 0xFF
                a = int(255 * max(0.0, 1.0 - (dist - size * 0.35) / (size * 0.08)))
            else:
                r, g, b, a = 0x12, 0x12, 0x12, 0xFF
            pixels.extend((r, g, b, a))

    # Add simple P letter for the center
    scale = size / 256.0
    bar_width = max(2, int(20 * scale))
    left = int(size * 0.28)
    top = int(size * 0.26)
    for y in range(top, int(size * 0.74)):
        for x in range(left, left + bar_width):
            idx = (y * size + x) * 4
            pixels[idx:idx + 4] = (255, 255, 255, 255)

    circle_radius = int(size * 0.15)
    circle_center_x = int(size * 0.55)
    circle_center_y = int(size * 0.37)
    for y in range(circle_center_y - circle_radius, circle_center_y + circle_radius):
        for x in range(circle_center_x - circle_radius, circle_center_x + circle_radius):
            dx = x - circle_center_x
            dy = y - circle_center_y
            if dx * dx + dy * dy <= circle_radius * circle_radius:
                if 0 <= x < size and 0 <= y < size:
                    idx = (y * size + x) * 4
                    pixels[idx:idx + 4] = (255, 255, 255, 255)

    return make_png_bytes(size, pixels)


def make_png_bytes(size, pixels):
    def chunk(chunk_type, data):
        chunk = chunk_type + data
        return struct.pack('>I', len(data)) + chunk + struct.pack('>I', zlib.crc32(chunk) & 0xFFFFFFFF)

    png = b'\x89PNG\r\n\x1a\n'
    png += chunk(b'IHDR', struct.pack('>IIBBBBB', size, size, 8, 6, 0, 0, 0))
    scanlines = b''.join(b'\x00' + pixels[y * size * 4:(y + 1) * size * 4] for y in range(size))
    png += chunk(b'IDAT', zlib.compress(scanlines, level=9))
    png += chunk(b'IEND', b'')
    return png


def make_ico(path):
    pngs = [make_png(size) for size in SIZES]
    entries = []
    offset = 6 + 16 * len(pngs)
    ico = bytearray()
    ico += struct.pack('<HHH', 0, 1, len(pngs))
    for size, png in zip(SIZES, pngs):
        width = 0 if size == 256 else size
        height = 0 if size == 256 else size
        entries.append(struct.pack('<BBBBHHII', width, height, 0, 0, 1, 32, len(png), offset))
        offset += len(png)

    for entry in entries:
        ico += entry
    for png in pngs:
        ico += png
    Path(path).write_bytes(ico)
    print(f'Wrote {path} ({Path(path).stat().st_size} bytes)')


if __name__ == '__main__':
    make_ico('src/PCStatsMonitor.App/Assets/tray-icon.ico')
