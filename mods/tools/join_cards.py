"""从存档还原卡牌效果，按「特使 / 展示」分开写。

链路（全离线）：
  存档 ImperiumPlayable / IntriguePlayable / TechTilePlayable
    └ children: Abilities 容器
        └ children: 能力实体（entityName 即类名，如 ForHumanityAgentAbility / ForHumanityRevealAbility）
  两类文案来源，**图标序列优先**（它才是完整效果）：
    button.<能力>                 = <Discard> <IfThenArrow> <DrawCard>  → 弃1张牌→抽1张牌
    worm.canis.abilities.<能力>   = 「选择并弃一张牌。」（只有前半句，图标缺的时候兜底）

为什么要区分 Agent / Reveal：同一个能力的两个后缀对应**不同时机**——
Agent 是打出特使时结算，Reveal 是展示回合结算，两者效果不同，合并会误导。

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
SPRITES = os.path.join(ROOT, "mods", "ai", "sprite_map.tsv")
PFX = "worm.canis.abilities."

# 容器/通用能力：不是这张牌的独有效果（抽牌、部署这类会被所有牌复用）
GENERIC = {"Abilities", "AgentAbility", "RevealAbility", "ActiveAbility", "AcquireAbility"}
SUFFIXES = ("Agent", "Reveal", "Acquire", "Triggered", "Deferred", "Custom", "Preview")
MAX_LEN = 110
MAX_PARTS = 4

SPRITE_RE = re.compile(r"<sprite\s+name=\"?([^\">]+)\"?\s*/?>")


def clean(text):
    if not text:
        return ""
    t = SPRITE_RE.sub(r"\1", text)
    t = re.sub(r"</?[a-z][^>]*>", "", t, flags=re.I)
    t = re.sub(r"\[[a-z]+\]", "N", t, flags=re.I)
    t = t.replace("{0}", "N").replace("[name]", "")
    return re.sub(r"\s+", "", t).strip(" 　;；。")


def load_sprites():
    out = {}
    if os.path.exists(SPRITES):
        for line in io.open(SPRITES, encoding="utf-8"):
            if not line.strip() or line.startswith("#"):
                continue
            p = line.rstrip("\n").split("\t")
            if len(p) >= 2 and p[0].strip():
                out[p[0].strip()] = p[1].strip()
    return out


def from_icons(cls, btn_map, sprites):
    """button.<能力> 的图标序列 -> 中文。图标缺任何一个就整条放弃，避免半句。"""
    raw = btn_map.get(cls)
    if not raw:
        return None
    tokens = [m.strip() for m in SPRITE_RE.findall(raw)]
    if not tokens:
        return None
    out = ""
    for tok in tokens:
        t = sprites.get(tok)
        if not t:
            return None                     # 有未翻译的图标，宁可不写
        if t == "→":
            out += "→"
        else:
            if out and not out.endswith("→"):
                out += "、"
            out += t
    return out or None


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
    """card.*.title 键 -> [能力类名...]（保持子树顺序，Agent 通常在 Reveal 前）"""
    out = {}
    for f in sorted(glob.glob(os.path.join(SAVES, "*.json"))):
        try:
            d = json.load(io.open(f, encoding="utf-8-sig"))
        except Exception:
            continue
        for e in walk_dicts(d):
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


def ability_text(cls, abil_text, btn_map, sprites):
    """单个能力的完整效果：图标序列优先，退回文字（同样按后缀回退）。"""
    cands = [cls]
    for suf in SUFFIXES:
        if cls.endswith(suf + "Ability"):
            cands.append(cls[: -len(suf) - len("Ability")] + "Ability")

    for c in cands:
        t = from_icons(c, btn_map, sprites)
        if t:
            return t, c

    for c in cands:
        raw = abil_text.get(c)
        if not raw:
            continue
        t = clean(raw)
        if not t or len(t) < 6 or t.endswith("：") or t in ("选择一项能力来使用", "选择两项", "选择："):
            continue
        return t, c
    return None, None


def main():
    write = "--write" in sys.argv
    zh = B.load_locale()
    abil_text = {k[len(PFX):]: v for k, v in zh.items() if k.startswith(PFX)}
    btn_map = {k[len("button."):]: v for k, v in zh.items() if k.startswith("button.")}
    sprites = load_sprites()
    print(f"能力文案 {len(abil_text)} 条、按钮图标 {len(btn_map)} 条、图标语义 {len(sprites)} 条")

    cards = load_cards_from_saves()
    print(f"存档里带具体能力的卡牌 {len(cards)} 张")

    eff_map = {}
    for line in io.open(EFFECT, encoding="utf-8"):
        k, _, v = line.rstrip("\n").partition("\t")
        eff_map[k] = v
    kb_keys = {l.split("\t")[0].strip() for l in io.open(KB, encoding="utf-8")
               if l.strip() and not l.startswith("#")}

    rows, stats = [], {"gap": 0, "have": 0, "built": 0, "none": 0}
    for key, names in cards.items():
        zh_name = zh.get(key, "").strip()
        if not zh_name:
            continue
        if ("name:" + zh_name) in eff_map or zh_name in kb_keys:
            stats["have"] += 1
            continue
        stats["gap"] += 1

        agent_parts, reveal_parts = [], []
        for cls in names:
            txt, used = ability_text(cls, abil_text, btn_map, sprites)
            if not txt:
                continue
            base = cls[:-len("Ability")] if cls.endswith("Ability") else cls
            if base.endswith("Agent"):
                target = agent_parts
            elif base.endswith("Reveal"):
                target = reveal_parts
            else:
                target = reveal_parts if agent_parts else agent_parts   # 无后缀：放到先出现的那一组
            if txt not in target and len(target) < MAX_PARTS:
                target.append(txt)

        segs = []
        if agent_parts:
            segs.append("特使：" + "；".join(agent_parts))
        if reveal_parts:
            segs.append("展示：" + "；".join(reveal_parts))
        if not segs:
            stats["none"] += 1
            continue

        text = "；".join(segs)
        if len(text) > MAX_LEN:
            text = text[:MAX_LEN] + "…"
        rows.append((zh_name, text))
        stats["built"] += 1

    print(f"缺文本卡 {stats['gap']} 张：可还原 {stats['built']}、仍无 {stats['none']}"
          f"（已有文本 {stats['have']}）")
    for r in rows:
        print(f"  {r[0]:12s} {r[1][:80]}")

    if write and rows:
        s = io.open(KB, encoding="utf-8").read()
        if not s.endswith("\n"):
            s += "\n"
        s += "\n# 卡牌效果：存档 Abilities 子树里的能力实体 -> button.<能力> 图标序列（完整）\n"
        s += "# 或 worm.canis.abilities.<能力> 文案（兜底）。特使/展示按时机分开，不再合并。\n"
        s += "\n".join(f"{n}\t{t}\t能力实体+图标" for n, t in rows) + "\n"
        io.open(KB, "w", encoding="utf-8", newline="\n").write(s)
        print(f"\n写入 {len(rows)} 条；接着跑 python mods/tools/build_effect_map.py")


if __name__ == "__main__":
    main()
