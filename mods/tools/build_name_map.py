"""
生成「机器标识 -> 中文可读文本」的映射，供 AI 探针拼 prompt 时替换。

两类条目：
  1. 原型 GUID -> 卡名/领袖名（来自 dump/cards_runtime.json + 本地化）
  2. 本地化键 -> 中文文本（worm.space.* / worm.canis.abilities.* / card.* / button.* 等）
     游戏在待选列表里大量输出这类键，直接翻掉，模型才看得懂。

输出：mods/ai/name_map.tsv  每行 "键<TAB>值"，值里的换行写成 \\n
       mods/ai/long_text.tsv 每行 "键<TAB>完整长文本"，短通道放不下的条目在此保留
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
OUT_LONG = os.path.join(ROOT, "mods", "ai", "long_text.tsv")
LOCALE = "zh_CN"

# 值得翻译的键前缀：游戏在协议里直接输出的机器标识
KEY_PREFIXES = ("worm.", "card.", "button.", "label.", "ability.", "lookup.")


def load_locale(locale=None):
    want = locale or LOCALE
    env = UnityPy.load(LOC)
    for o in env.objects:
        if o.type.name != "TextAsset":
            continue
        ta = o.read()
        if getattr(ta, "m_Name", "") != want:
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
    raise SystemExit(f"本地化包里找不到语言段 {want}")


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

    # 本地化分两个通道，不再靠"长度过滤"丢弃信息。
    #
    # 短通道（name_map.tsv）：短名称/短描述，用于把机器标识翻成人话。
    #   旧实现用 `len(v) <= 80 and "\n" not in v` 一刀切，把 190 条长条目整个丢掉
    #   （card 95 / button 68 / worm 27，最长的 leader signetability.desc 有 336 字）。
    #   这些长文本是领袖戒指能力、阴谋牌多段规则等语义的唯一来源，丢掉即永久失联。
    #
    # 长通道（long_text.tsv）：完整文本，不截断、不改写，供需要全文的场合按需取用。
    #   name_map.tsv 保持"这是什么"的短名语义，避免模型把整段规则当名字读。
    longs = {}
    for k, v in kv.items():
        if not k.startswith(KEY_PREFIXES):
            continue
        if len(v) <= 80 and "\n" not in v:
            pairs.setdefault(k, v)
        else:
            longs.setdefault(k, v)

    with open(OUT, "w", encoding="utf-8", newline="\n") as f:
        for k in sorted(pairs):
            v = pairs[k].replace("\\", "\\\\").replace("\t", " ").replace("\n", "\\n")
            f.write(f"{k}\t{v}\n")

    with open(OUT_LONG, "w", encoding="utf-8", newline="\n") as f:
        for k in sorted(longs):
            v = longs[k].replace("\\", "\\\\").replace("\t", " ").replace("\n", "\\n")
            f.write(f"{k}\t{v}\n")

    print(f"写出 {len(pairs)} 条映射（GUID {len(cards)} 张卡命中 {sum(1 for c in cards if c.get('id') in pairs)}）-> {OUT}")
    longest = max((len(v) for v in longs.values()), default=0)
    print(f"长文本通道 {len(longs)} 条（最长 {longest} 字）-> {OUT_LONG}")


if __name__ == "__main__":
    build()

