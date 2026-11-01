"""从存档 + 能力 ID 表还原卡牌效果。

链路：
  存档（savedgames/*.json）
    └ ImperiumPlayable 实体：10140 = card.<set>.<image>.title，900013 = [AbilityID...]
  dump/ai/ability_ids.tsv（插件导出）
    └ AbilityID -> 能力类名（如 DrawCardAbility）
  本地化
    └ worm.canis.abilities.<类名> -> 官方中文文案

为什么绕这一圈：本地化里 314 组卡牌有 106 组只有标题（图标化表达），
但效果本身是能力实体，能力名对应的中文文案是齐全的（380 条 / 338 条有文案）。

用法：
  python mods/tools/join_cards.py            # 只报告
  python mods/tools/join_cards.py --write    # 写入 kb_extra.tsv
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

SAVES = os.path.join(ROOT, "profile", "DireWolf", "Dune_ Imperium", "savedgames")
IDS = os.path.join(ROOT, "dump", "ai", "ability_ids.tsv")
KB = os.path.join(ROOT, "mods", "ai", "kb_extra.tsv")
EFFECT = os.path.join(ROOT, "mods", "ai", "effect_map.tsv")
PFX = "worm.canis.abilities."

MAX_ABIL = 4
MAX_LEN = 90
# 这些能力是「获取/放置」的通用流程提示，不是卡牌效果，必须排除
NOISE = ("AcquireAbility", "PlaceAcquiredCardAbility", "SelectAbilityToAcquireAbility")


def clean(text):
    if not text:
        return ""
    t = re.sub(r"<sprite\s+name=\"?([^\">]+)\"?\s*/?>", r"\1", text, flags=re.I)
    t = re.sub(r"</?[a-z][^>]*>", "", t, flags=re.I)
    t = t.replace("{0}", "N").replace("[name]", "")
    return re.sub(r"\s+", "", t).strip(" 　;；。")


def walk_dicts(o):
    if isinstance(o, dict):
        yield o
        for v in o.values():
            yield from walk_dicts(v)
    elif isinstance(o, list):
        for x in o:
            yield from walk_dicts(x)


def load_cards_from_saves():
    """10140 键 -> [AbilityID...]"""
    out = {}
    files = sorted(glob.glob(os.path.join(SAVES, "*.json")))
    for f in files:
        try:
            d = json.load(io.open(f, encoding="utf-8-sig"))
        except Exception:
            continue
        for e in walk_dicts(d):
            if e.get("entityName") not in ("ImperiumPlayable", "Leader", "Space"):
                continue
            key, abil = None, []
            for a in (e.get("attributes") or []):
                if not isinstance(a, dict):
                    continue
                n, v = str(a.get("name")), a.get("value")
                if n == "10140" and isinstance(v, dict):
                    key = str(v.get("id", ""))
                elif n == "900013" and isinstance(v, list):
                    abil = [x for x in v if isinstance(x, str)]
            if key and abil:
                out.setdefault(key, [])
                for x in abil:
                    if x not in out[key]:
                        out[key].append(x)
    return out


def main():
    write = "--write" in sys.argv
    if not os.path.exists(IDS):
        sys.exit("缺少 dump/ai/ability_ids.tsv —— 先跑一局（日志搜 ABILITYMAP）")

    zh = B.load_locale()
    abil_text = {k[len(PFX):]: v for k, v in zh.items() if k.startswith(PFX)}
    id2name = {}
    for line in io.open(IDS, encoding="utf-8"):
        line = line.rstrip("\r\n")
        if not line or line.startswith("#"):
            continue
        p = line.split("\t")
        if len(p) >= 2 and p[0]:
            id2name[p[0].strip()] = p[1].strip()
    print(f"能力 ID 表 {len(id2name)} 条")

    cards = load_cards_from_saves()
    print(f"存档里带能力列表的实体 {len(cards)} 个")

    eff_map = {}
    for line in io.open(EFFECT, encoding="utf-8"):
        k, _, v = line.rstrip("\n").partition("\t")
        eff_map[k] = v
    kb_keys = {l.split("\t")[0].strip() for l in io.open(KB, encoding="utf-8")
               if l.strip() and not l.startswith("#")}

    stats = {"total": 0, "have": 0, "built": 0, "noMap": 0, "noText": 0}
    rows = []
    for key, ids in cards.items():
        m = re.match(r"^card\.[a-z]+\.([a-z0-9_]+)\.title$", key)
        if not m:
            continue
        stats["total"] += 1
        # 中文名：优先用本地化里该 title 键的值
        zh_name = zh.get(key, "").strip()
        if not zh_name:
            continue
        if ("name:" + zh_name) in eff_map or zh_name in kb_keys:
            stats["have"] += 1
            continue

        names, texts = [], []
        for aid in ids:
            cls = id2name.get(aid)
            if not cls:
                stats["noMap"] += 1
                continue
            if cls in NOISE or cls.endswith("AcquireAbility"):
                continue
            t = clean(abil_text.get(cls, ""))
            if not t or t in texts:
                continue
            names.append(cls)
            texts.append(t)
            if len(texts) >= MAX_ABIL:
                break

        if not texts:
            stats["noText"] += 1
            continue

        text = "；".join(texts)
        if len(text) > MAX_LEN:
            text = text[:MAX_LEN] + "…"
        rows.append((zh_name, text, "游戏内能力 " + ",".join(names[:2])))
        stats["built"] += 1

    print(f"卡牌 {stats['total']}：已有文本 {stats['have']}、可还原 {stats['built']}、"
          f"ID 无映射 {stats['noMap']}、能力无文案 {stats['noText']}")
    for r in rows[:15]:
        print(f"  {r[0]:10s} {r[1][:66]}")

    if write and rows:
        s = io.open(KB, encoding="utf-8").read()
        if not s.endswith("\n"):
            s += "\n"
        s += "\n# 卡牌效果：由存档 900013 能力列表 + AbilityID->类名表 + 本地化文案还原（官方中文）。\n"
        s += "# 参数（如“最多N个”）无法取值，以 N 占位；通用获取/放置流程能力已排除。\n"
        s += "\n".join(f"{n}\t{t}\t{src}" for n, t, src in rows) + "\n"
        io.open(KB, "w", encoding="utf-8", newline="\n").write(s)
        print(f"\n写入 {len(rows)} 条；接着跑 python mods/tools/build_effect_map.py")


if __name__ == "__main__":
    main()
