import json
import sys
from PIL import Image


def hex_to_rgb(h):
    h = h.lstrip("#")
    return tuple(int(h[i : i + 2], 16) for i in (0, 2, 4))


def main():
    if len(sys.argv) != 4:
        print("usage: python recolor.py <source.png> <out.png> <mapping.json>")
        sys.exit(1)
    src_path, out_path, mapping_path = sys.argv[1], sys.argv[2], sys.argv[3]

    with open(mapping_path, "r", encoding="utf-8") as f:
        cfg = json.load(f)
    tolerance = int(cfg.get("tolerance", 65))
    mapping = [(hex_to_rgb(k), hex_to_rgb(v)) for k, v in cfg["map"].items()]

    im = Image.open(src_path).convert("RGBA")
    px = im.load()
    tol2 = tolerance * tolerance
    changed = 0
    for y in range(im.height):
        for x in range(im.width):
            r, g, b, a = px[x, y]
            if a < 8:
                continue
            for src, dst in mapping:
                dr = r - src[0]
                dg = g - src[1]
                db = b - src[2]
                if dr * dr + dg * dg + db * db <= tol2:
                    px[x, y] = (dst[0], dst[1], dst[2], a)
                    changed += 1
                    break
    im.save(out_path)
    print("saved", out_path, "changed_pixels", changed)


if __name__ == "__main__":
    main()
