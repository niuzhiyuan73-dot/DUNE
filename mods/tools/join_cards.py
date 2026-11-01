"""从存档还原卡牌效果：卡牌实体 -> Abilities 容器 -> 能力类名 -> 本地化文案。

链路（全部离线，不需要跑游戏）：
  存档 savedgames/*.json
    └ ImperiumPlayable（10140 = card.<set>.<image>.title）
        └ children: Abilities 容器
            └ children: 具体能力实体（entityName 即类名，如 ForHumanityAgentAbility）
  本地化
    └ worm.canis.abilities.<类名> -> 官方中文文案

命名差异：卡的 Agent/Reveal 能力常带后缀（ForHumanityAgentAbility），
而文案键是不带后缀的（ForHumanityAbility），所以按 Agent/Reveal/Acquire/Triggered/Deferred/Custom
逐一剥掉再试。

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
KB = os.path.join(ROOT, "mods", "ai", "kb_extra.tsv")
EFFECT = os.path.join(ROOT, "mods", "ai", "effect_map.tsv")
PFX = "worm.canis.abilities."

# 容器/通用能力：不是这张牌的独有效果
GENERIC = {"Abilities", "AgentAbility", "RevealAbility", "ActiveAbility", "AcquireAbility",
           "DrawAbility", "DeployUnitsAbility", "TrashAbility"}
SUFFIXES = ("Agent", "Reveal", "Acquire", "Triggered", "Deferred", "Custom", "Preview")
MAX_ABIL = 4
MAX_LEN = 90


def clean(text):
    if not text:
        return ""
    t = re.sub(r"<sprite\s+name=\"?([^\">]+)\"?\s*/?>", r"\1", text, flags=re.I)
    t = re.sub(r"</?[a-z][^>]*>", "", t, flags=re.I)
    t = re.sub(r"\[[a-z]+\]", "N", t, flags=re.I)     # [amount] / [SourceName]
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


def key_of(e):
    for a in (e.get("attributes") or []):
        if isinstance(a, dict) and str(a.get("name")) == "10140" and isinstance(a.get("value"), dict):
            return a["value"].get("id")
    return None


def load_cards_from_saves():
    """card.*.title 键 -> [能力类名...]"""
    out = {}
    for f in sorted(glob.glob(os.path.join(SAVES, "*.json"))):
        try:
            d = json.load(io.open(f, encoding="utf-8-sig"))
        except Exception:
            continue
        for e in walk_dicts(d):
            # 帝国牌、阴谋牌、科技牌都是「可打出的卡」，子树结构一致
            if e.get("entityName") not in ("ImperiumPlayable", "IntriguePlayable", "TechTilePlayable"):
                continue
            k = key_of(e)
            if not k or not k.endswith(".title"):
                continue
            names = out.setdefault(k, [])
            for c in (e.get("children") or []):
                for cc in (c.get("children") or []):
                    n = cc.get("entityName")
                    if n and n not in GENERIC and n not in names:
                        names.append(n)
    return {k: v for k, v in out.items() if v}


def lookup_ability(name, abil_text):
    """能力类名 -> 文案。带 Agent/Reveal 等后缀的先按原名，再逐个剥掉后缀。"""
    cands = [name]
    for suf in SUFFIXES:
        if name.endswith(suf + "Ability"):
            cands.append(name[: -len(suf) - len("Ability")] + "Ability")
    for c in cands:
        t = abil_text.get(c)
        if t:
            return c, t
    return None, None


def main():
    write = "--write" in sys.argv
    zh = B.load_locale()
    abil_text = {k[len(PFX):]: v for k, v in zh.items() if k.startswith(PFX)}
    print(f"本地化能力文案 {len(abil_text)} 条")

    cards = load_cards_from_saves()
    print(f"存档里带具体能力的卡牌 {len(cards)} 张")

    eff_map = {}
    for line in io.open(EFFECT, encoding="utf-8"):
        k, _, v = line.rstrip("\n").partition("\t")
        eff_map[k] = v
    kb_keys = {l.split("\t")[0].strip() for l in io.open(KB, encoding="utf-8")
               if l.strip() and not l.startswith("#")}

    rows, stats = [], {"gap": 0, "have": 0, "built": 0, "nofallback": 0}
    for key, names in cards.items():
        zh_name = zh.get(key, "").strip()
        if not zh_name:
            continue
        if ("name:" + zh_name) in eff_map or zh_name in kb_keys:
            stats["have"] += 1
            continue
        stats["gap"] += 1

        texts, used = [], []
        for a in names:
            cls, raw = lookup_ability(a, abil_text)
            if not raw:
                continue
            t = clean(raw)
            # 「选择：」「选择两项：」「选择一项能力来使用」是交互提示，不是效果
            if not t or len(t) < 6 or t in texts:
                continue
            if t in ("选择一项能力来使用", "选择两项", "选择："):
                continue
            # 以「：」结尾说明后面还有内容没抓到，半句不如不写
            if t.endswith("："):
                continue
            texts.append(t)
            used.append(cls)
            if len(texts) >= MAX_ABIL:
                break

        if not texts:
            stats["nofallback"] += 1
            print(f"   [无文案] {zh_name:10s} {names[:3]}")
            continue

        text = "；".join(texts)
        if len(text) > MAX_LEN:
            text = text[:MAX_LEN] + "…"
        rows.append((zh_name, text, "能力实体 " + ",".join(used[:2])))
        stats["built"] += 1

    print(f"缺文本卡 {stats['gap']} 张：可还原 {stats['built']}、能力无文案 {stats['nofallback']}"
          f"（已有文本 {stats['have']}）")
    for r in rows:
        print(f"  {r[0]:10s} {r[1][:66]}")

    if write and rows:
        s = io.open(KB, encoding="utf-8").read()
        if not s.endswith("\n"):
            s += "\n"
        s += "\n# 卡牌效果：存档 Abilities 容器里的能力实体 + 本地化 worm.canis.abilities.*（官方中文）。\n"
        s += "# 参数（[amount]/[SourceName]/N）无法取值；通用流程能力已排除。\n"
        s += "\n".join(f"{n}\t{t}\t{src}" for n, t, src in rows) + "\n"
        io.open(KB, "w", encoding="utf-8", newline="\n").write(s)
        print(f"\n写入 {len(rows)} 条；接着跑 python mods/tools/build_effect_map.py")


if __name__ == "__main__":
    main()
