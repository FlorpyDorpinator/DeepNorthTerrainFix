"""Tiny dependency-free PNG icon generator for Thunderstore (256x256).
usage: make_icon.py <out.png> <style> where style is 'storm' or 'net'."""
import sys, zlib, struct, math

W = H = 256

def png(path, pixels):
    raw = b''.join(b'\x00' + bytes(row) for row in pixels)
    def chunk(t, d):
        return struct.pack('>I', len(d)) + t + d + struct.pack('>I', zlib.crc32(t + d) & 0xffffffff)
    data = b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', W, H, 8, 2, 0, 0, 0))
    data += chunk(b'IDAT', zlib.compress(raw, 9)) + chunk(b'IEND', b'')
    open(path, 'wb').write(data)

def lerp(a, b, t): return tuple(int(a[i] + (b[i] - a[i]) * t) for i in range(3))

def storm():
    top, bottom = (18, 30, 52), (72, 96, 128)
    rows = []
    for y in range(H):
        base = lerp(top, bottom, y / H)
        row = []
        for x in range(W):
            c = base
            # house silhouette: walls x 70..186, roof peak at (128, 70)
            inside = 88 <= x <= 168 and 120 <= y <= 210
            roof = 70 <= y <= 120 and abs(x - 128) <= (y - 70) * 1.16 + 6
            if roof: c = (150, 110, 70)
            elif inside: c = (120, 85, 55)
            # window (storm visible through it): warm frame, snow inside the window
            if 112 <= x <= 144 and 140 <= y <= 172:
                c = (40, 60, 90)
                if (x * 7 + y * 13) % 29 < 3: c = (235, 240, 250)
                if x in (112, 144) or y in (140, 172): c = (200, 170, 110)
            # snow sheets outside the house only
            if not inside and not roof:
                s = math.sin((x + y * 0.6) * 0.09 + y * 0.03) * math.sin(y * 0.05)
                if s > 0.55: c = lerp(c, (230, 236, 246), 0.55)
                if (x * 31 + y * 17) % 97 < 2: c = (245, 248, 255)
            row.extend(c)
        rows.append(row)
    return rows

def net():
    top, bottom = (14, 28, 40), (20, 70, 80)
    rows = []
    nodes = [(60, 70), (196, 60), (128, 128), (56, 196), (200, 190)]
    def seg(px, py, ax, ay, bx, by):
        vx, vy = bx - ax, by - ay
        l2 = vx * vx + vy * vy
        t = max(0, min(1, ((px - ax) * vx + (py - ay) * vy) / l2))
        return math.hypot(px - (ax + vx * t), py - (ay + vy * t))
    edges = [(0, 2), (1, 2), (3, 2), (4, 2), (0, 1), (3, 4)]
    for y in range(H):
        base = lerp(top, bottom, y / H)
        row = []
        for x in range(W):
            c = base
            for a, b in edges:
                d = seg(x, y, *nodes[a], *nodes[b])
                if d < 3: c = (80, 220, 200)
                elif d < 6: c = lerp(c, (80, 220, 200), 0.35)
            for i, (nx, ny) in enumerate(nodes):
                d = math.hypot(x - nx, y - ny)
                r = 22 if i == 2 else 14
                if d < r: c = (250, 250, 250) if i == 2 else (120, 235, 215)
                elif d < r + 3: c = lerp(c, (255, 255, 255), 0.5)
            # speed arrow in the centre node
            if math.hypot(x - 128, y - 128) < 20:
                if abs(y - 128) < 3 and 116 <= x <= 140: c = (20, 40, 60)
                if 132 <= x <= 140 and abs(y - 128) <= (140 - x): c = (20, 40, 60)
            row.extend(c)
        rows.append(row)
    return rows

def tag():
    """A price tag with a check mark: the item is clean."""
    top, bottom = (40, 28, 58), (88, 52, 96)
    rows = []
    for y in range(H):
        base = lerp(top, bottom, y / H)
        row = []
        for x in range(W):
            c = base
            # tag body: rounded rectangle with a pointed left end
            inside = 96 <= x <= 216 and 78 <= y <= 178
            point = 48 <= x < 96 and abs(y - 128) <= (x - 48) * 1.04
            if inside or point:
                c = (236, 226, 200)
                if (x in range(96, 217) and y in (78, 178)) or x == 216: c = (150, 130, 96)
            # string hole
            if math.hypot(x - 84, y - 128) < 9: c = base
            elif math.hypot(x - 84, y - 128) < 12: c = (150, 130, 96)
            # check mark on the tag
            def seg(ax, ay, bx, by):
                vx, vy = bx - ax, by - ay
                t = max(0, min(1, ((x - ax) * vx + (y - ay) * vy) / (vx * vx + vy * vy)))
                return math.hypot(x - (ax + vx * t), y - (ay + vy * t))
            if inside and (seg(122, 130, 148, 156) < 7 or seg(148, 156, 196, 100) < 7): c = (46, 160, 90)
            row.extend(c)
        rows.append(row)
    return rows

if __name__ == '__main__':
    out, style = sys.argv[1], sys.argv[2]
    png(out, {'storm': storm, 'net': net, 'tag': tag}[style]())
    print('wrote', out)
