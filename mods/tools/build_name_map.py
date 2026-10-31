"""
生成「机器标识 -> 中文可读文本」的映射，供 AI 探针拼 prompt 时替换。

两类条目：
  1. 原型 GUID -> 卡名/领袖名（来自 dump/cards_runtime.json + 本地化）
  2. 本地化键 -> 中文文本（worm.space.* / worm.canis.abilities.* / card.* / button.* 等）
     游戏在待选列表里大量输出这类键，直接翻掉，模型才看得懂。

输出：mods/ai/name_map.tsv  每行 "键<TAB>值"，值里的换行写成 \\n
用法：python mods/tools/build_name_map.py
"""
import json
import os
import re

import UnityPy

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
CARDS = os.path.join(ROOT, "dump", "cards_runtime.json")
LOC = os.path.join(ROOT, "game", "DuneImperium_Data", "StreamingAssets", "Localization", "win", "localization")
OUT = os.path.join(ROOT, "mods", "ai", "name_map.tsv")
LOCALE = "zh_CN"

# 值得翻译的键前缀：游戏在协议里直接输出的机器标识
KEY_PREFIXES = ("worm.", "card.", "button.", "label.", "ability.")


def load_locale():
    env = UnityPy.load(LOC)
    for o in env.objects:
        if o.type.name != "TextAsset":
            continue
        ta = o.read()
        if getattr(ta, "m_Name", "") != LOCALE:
            continue
        t = ta.m_Script
        if isinstance(t, bytes):
            t = t.decode("utf-8", "replace")
        kv = {}
        for line in t.splitlines():
            if "=" not in line or line.startswith("#"):
                continue
            k, v = line.split("=", 1)
            kv[k.strip()] = v.strip()
        return kv
    raise SystemExit(f"本地化包里找不到语言段 {LOCALE}")


def build():
    cards = json.load(open(CARDS, encoding="utf-8-sig"))
    kv = load_locale()

    # 图片键 -> 名字（兼容 card.<类>.<img>.title 与 worm.leader.<img>）
    by_img = {}
    for k, v in kv.items():
        m = re.match(r"^(?:card|worm)\.[a-z]+\.([a-z0-9_]+)(?:\.title|\.toplevel)?$", k)
        if m and m.group(1) not in by_img:
            by_img[m.group(1)] = v

    pairs = {}
    for c in cards:
        gid, img = c.get("id"), c.get("image")
        if gid and img and img in by_img:
            pairs[gid] = by_img[img]

    for k, v in kv.items():
        if k.startswith(KEY_PREFIXES) and len(v) <= 80 and "\n" not in v:
            pairs.setdefault(k, v)

    with open(OUT, "w", encoding="utf-8", newline="\n") as f:
        for k in sorted(pairs):
            v = pairs[k].replace("\\", "\\\\").replace("\t", " ").replace("\n", "\\n")
            f.write(f"{k}\t{v}\n")
    print(f"写出 {len(pairs)} 条映射（GUID {len(cards)} 张卡命中 {sum(1 for c in cards if c.get('id') in pairs)}）-> {OUT}")


if __name__ == "__main__":
    build()

