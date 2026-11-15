# -*- coding: utf-8 -*-
"""极简 PNG 读/写（8bit RGB/RGBA，非隔行），用于裁图放大，避免依赖 Pillow。"""
import struct
import zlib


def read_png(path):
    raw = open(path, "rb").read()
    assert raw[:8] == b"\x89PNG\r\n\x1a\n"
    i = 8
    idat = b""
    w = h = bd = ct = None
    while i < len(raw):
        ln = struct.unpack(">I", raw[i:i + 4])[0]
        typ = raw[i + 4:i + 8]
        data = raw[i + 8:i + 8 + ln]
        if typ == b"IHDR":
            w, h, bd, ct = struct.unpack(">IIBB", data[:10])
        elif typ == b"IDAT":
            idat += data
        elif typ == b"IEND":
            break
        i += 12 + ln
    assert bd == 8 and ct in (2, 6), (bd, ct)
    nch = 3 if ct == 2 else 4
    dec = zlib.decompress(idat)
    stride = w * nch
    out = bytearray(w * h * nch)
    prev = bytearray(stride)
    pos = 0
    for y in range(h):
        f = dec[pos]
        pos += 1
        line = bytearray(dec[pos:pos + stride])
        pos += stride
        if f == 1:
            for x in range(nch, stride):
                line[x] = (line[x] + line[x - nch]) & 0xFF
        elif f == 2:
            for x in range(stride):
                line[x] = (line[x] + prev[x]) & 0xFF
        elif f == 3:
            for x in range(stride):
                a = line[x - nch] if x >= nch else 0
                line[x] = (line[x] + ((a + prev[x]) >> 1)) & 0xFF
        elif f == 4:
            for x in range(stride):
                a = line[x - nch] if x >= nch else 0
                b = prev[x]
                c = prev[x - nch] if x >= nch else 0
                p = a + b - c
                pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
                pr = a if (pa <= pb and pa <= pc) else (b if pb <= pc else c)
                line[x] = (line[x] + pr) & 0xFF
        out[y * stride:(y + 1) * stride] = line
        prev = line
    return w, h, nch, bytes(out)


def write_png(path, w, h, nch, pixels):
    ct = 2 if nch == 3 else 6
    stride = w * nch
    raw = bytearray()
    for y in range(h):
        raw.append(0)
        raw += pixels[y * stride:(y + 1) * stride]

    def chunk(typ, data):
        return (struct.pack(">I", len(data)) + typ + data
                + struct.pack(">I", zlib.crc32(typ + data) & 0xFFFFFFFF))

    png = b"\x89PNG\r\n\x1a\n"
    png += chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, ct, 0, 0, 0))
    png += chunk(b"IDAT", zlib.compress(bytes(raw), 6))
    png += chunk(b"IEND", b"")
    open(path, "wb").write(png)


def crop_scale(src, dst, x0, y0, cw, ch, k):
    w, h, nch, px = read_png(src)
    x1, y1 = min(x0 + cw, w), min(y0 + ch, h)
    cw2, ch2 = x1 - x0, y1 - y0
    ow, oh = cw2 * k, ch2 * k
    out = bytearray(ow * oh * nch)
    for y in range(oh):
        sy = y0 + y // k
        for x in range(ow):
            sx = x0 + x // k
            s = (sy * w + sx) * nch
            d = (y * ow + x) * nch
            out[d:d + nch] = px[s:s + nch]
    write_png(dst, ow, oh, nch, bytes(out))
    print("写出", dst, ow, oh, "源尺寸", w, h, "通道", nch)


if __name__ == "__main__":
    import sys
    a = sys.argv
    crop_scale(a[1], a[2], int(a[3]), int(a[4]), int(a[5]), int(a[6]), int(a[7]) if len(a) > 7 else 3)
