"""把「空间诊断」与「存档」关联起来，得到 空间中文名 -> 效果能力 的对照。

输入：
  dump/ai/spaces.txt              插件诊断输出：Space <原型GUID> <子实体名[子原型GUID] | ...>
  profile/.../savedgames/*.json   存档：空间实体的 10000=原型GUID、10140=worm.space.X、920003=image

输出：
  dump/tmp/spaces_joined.tsv      空间中文名 <TAB> 原型GUID <TAB> 能力名列表
  并打印无法关联的条目（能力名缺失/子实体为空）

用法：python mods/tools/join_spaces.py
"""
import glob
import io
import json
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
sys.path.insert(0, os.path.join(ROOT, "mods", "tools"))
import build_name_map as B

SPACES = os.path.join(ROOT, "dump", "ai", "spaces.txt")
SAVES = os.path.join(ROOT, "profile", "DireWolf", "Dune_ Imperium", "savedgames")
OUT = os.path.join(ROOT, "dump", "tmp", "spaces_joined.tsv")


def save_space_map():
    """原型 GUID -> (worm.space.X, image)"""
    out = {}
    for f in glob.glob(os.path.join(SAVES, "*.json")):
        try:
            d = json.load(io.open(f, encoding="utf-8-sig"))
        except Exception:
            continue
        stack = [d]
        while stack:
            o = stack.pop()
            if isinstance(o, dict):
                at = o.get("attributes") or []
                gid = key = img = None
                for a in at:
                    if not isinstance(a, dict):
                        continue
                    n, v = str(a.get("name")), a.get("value")
                    if n == "10000":
                        gid = str(v)
                    elif n == "10140" and isinstance(v, dict):
                        key = str(v.get("id", ""))
                    elif n == "920003":
                        img = str(v)
                if gid and key and key.startswith("worm.space"):
                    out.setdefault(gid, (key, img or ""))
                stack.extend(o.values())
            elif isinstance(o, list):
                stack.extend(o)
    return out


def main():
    if not os.path.exists(SPACES):
        sys.exit("缺少 dump/ai/spaces.txt —— 先跑一局让插件产出")
    zh = B.load_locale()
    smap = save_space_map()
    print(f"存档里可识别的空间 {len(smap)} 个")

    rows, missing = [], []
    for line in open(SPACES, encoding="utf-8"):
        line = line.rstrip("\n")
        if not line or line.startswith("#"):
            continue
        parts = line.split("\t")
        if len(parts) < 3 or parts[0] != "Space":
            continue
        gid, kids = parts[1], parts[2]
        info = smap.get(gid)
        if not info:
            missing.append((gid, "存档里没有该原型", kids))
            continue
        key, img = info
        name = zh.get(key, "")
        abilities = [k.split("[")[0] for k in kids.split(" | ") if k.strip()]
        if not abilities:
            missing.append((gid, key, "子实体为空"))
            continue
        # 能力名 -> 本地化文案（若有）
        detail = []
        for a in abilities:
            t = zh.get("worm.canis.abilities." + a, "")
            detail.append(f"{a}{('=' + t) if t else ''}")
        rows.append((name or img or key, gid, " ; ".join(detail)))

    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    with open(OUT, "w", encoding="utf-8", newline="\n") as f:
        for r in rows:
            f.write("\t".join(r) + "\n")
    print(f"关联成功 {len(rows)} 条 -> {OUT}")
    for m in missing[:10]:
        print("  未关联:", m)


if __name__ == "__main__":
    main()
