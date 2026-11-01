"""扫描所有 addressable 包，找出包含卡面贴图的包。

思路：卡面贴图/精灵的名字通常等于卡牌的 image 键（wormriders、desertsurvival…）。
包是压缩的，grep 字符串不可靠，所以直接逐包加载、按对象名匹配 image 键集合。

输出 dump/tmp/cardart_hits.tsv：包<TAB>对象类型<TAB>对象名
用法：python mods/tools/scan_cardart.py
"""
import json
import os
import sys

import UnityPy

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
AA = os.path.join(ROOT, "game", "DuneImperium_Data", "StreamingAssets", "aa", "StandaloneWindows64")
OUT = os.path.join(ROOT, "dump", "tmp", "cardart_hits.tsv")


def main():
    cards = json.load(open(os.path.join(ROOT, "dump", "cards_runtime.json"), encoding="utf-8-sig"))
    keys = {str(c.get("image")).lower() for c in cards if c.get("image")}
    print(f"卡图键 {len(keys)} 个")

    bundles = sorted(f for f in os.listdir(AA) if f.endswith(".bundle"))
    total = len(bundles)
    hits = []
    for i, b in enumerate(bundles, 1):
        path = os.path.join(AA, b)
        try:
            env = UnityPy.load(path)
        except Exception:
            continue
        for o in env.objects:
            if o.type.name not in ("Sprite", "Texture2D"):
                continue
            try:
                nm = o.peek_name()
            except Exception:
                continue
            if not nm:
                continue
            k = nm.lower()
            k = k.split("_")[-1] if k.startswith("t_") else k
            if k in keys:
                hits.append((b, o.type.name, nm))
                print(f"  HIT {b[:12]} {o.type.name} {nm}")
        if i % 50 == 0:
            print(f"...{i}/{total} 命中共 {len(hits)}")

    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    with open(OUT, "w", encoding="utf-8", newline="\n") as f:
        for h in hits:
            f.write("\t".join(h) + "\n")
    print(f"扫描完成：{total} 个包，命中 {len(hits)} 个对象 -> {OUT}")


if __name__ == "__main__":
    main()
