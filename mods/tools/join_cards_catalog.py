"""从只读目录导出（dump/current_mode/）还原卡牌效果，按触发时机分组。

数据源（catalog_probe 只读导出的快照，不碰运行时状态）：
  dump/current_mode/archetypes.json
      每个原型一条：卡牌原型带 920003 = image 键，900013 = [AbilityID...]
  dump/current_mode/ability_observations.json
      AbilityID -> {Name(类名), AbilityTiming(Agent/Reveal/Combat/Endgame), GameText(文本键)}

为什么这是正路（前几版都踩过）：
  - 存档的 Abilities 子树只含已实例化能力，条件/未触发分支都没有；
  - AbilityFactory.CreateEntity 是写操作，会把实体注册进 Match，导致开局崩；
  - 真正的文本键在 GameText 里，而且常常不等于类名，例：
        ForHumanityAgentAbility 的 GameText = button.GainInfluenceAbility
    这解释了「为了全人类」特使效果为何一直抓不到。
"""
import io
import json
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
sys.path.insert(0, os.path.join(ROOT, "mods", "tools"))
import build_name_map as B

MODE = os.path.join(ROOT, "dump", "current_mode")
KB = os.path.join(ROOT, "mods", "ai", "kb_extra.tsv")
EFFECT = os.path.join(ROOT, "mods", "ai", "effect_map.tsv")
SPRITES = os.path.join(ROOT, "mods", "ai", "sprite_map.tsv")

MAX_LEN = 130
SPRITE_RE = re.compile(r"<sprite\s+name=\"?([^\">]+)\"?\s*/?>")
TIMING_LABEL = {"Agent": "特使", "Reveal": "展示", "Combat": "战斗",
                "CombatResolution": "战斗结算", "Endgame": "终局"}
GENERIC = {"AgentAbility", "RevealAbility", "AcquireAbility", "ActiveAbility"}

sprites_global = {}


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


def merge_counts(parts):
    out = []
    for p in parts:
        m = re.match(r"^(.*?)(\d+)(\D*)$", p)
        if m and m.group(1):
            nm, n, suf = m.group(1), int(m.group(2)), m.group(3)
            if out and out[-1][0] == nm and out[-1][2] == suf:
                out[-1] = (nm, out[-1][1] + n, suf)
                continue
            out.append((nm, n, suf))
        else:
            out.append((p, 0, ""))
    txt = ""
    for nm, n, suf in out:
        piece = nm if n == 0 else ("%s1%s" % (nm, suf) if n == 1 else "%s%d%s" % (nm, n, suf))
        txt += ("、" if txt else "") + piece
    return txt


def icons_to_text(raw):
    tokens = [m.strip() for m in SPRITE_RE.findall(raw or "")]
    if not tokens:
        return None
    groups = [[]]
    for tok in tokens:
        t = sprites_global.get(tok)
        if not t:
            return None
        if t == "→":
            groups.append([])
        else:
            groups[-1].append(t)
    segs = [merge_counts(g) for g in groups if g]
    return "→".join(segs) or None


def clean_text(t):
    if not t:
        return ""
    t = SPRITE_RE.sub(lambda m: sprites_global.get(m.group(1).strip(), ""), t)
    t = re.sub(r"</?[a-z][^>]*>", "", t, flags=re.I)
    t = re.sub(r"\[[a-z]+\]", "N", t, flags=re.I)
    return re.sub(r"\s+", "", t).replace("{0}", "N").strip(" 　;；。")


def ability_text(o, zh):
    gt = o.get("GameText")
    nm = o.get("Name")
    cls = nm.get("id") if isinstance(nm, dict) else nm
    cands = []
    if isinstance(gt, dict) and gt.get("id"):
        cands.append(gt["id"])
    if cls:
        cands.append("button." + cls)
        cands.append("worm.canis.abilities." + cls)
    for key in cands:
        raw = zh.get(key)
        if not raw:
            continue
        t = icons_to_text(raw)
        if t:
            return t
        t = clean_text(raw)
        if t and len(t) >= 6 and not t.endswith("：") and not t.startswith("选择一项能力"):
            return t
    return None


def main():
    write = "--write" in sys.argv
    global sprites_global
    sprites_global = load_sprites()
    zh = B.load_locale()

    ar = json.load(io.open(os.path.join(MODE, "archetypes.json"), encoding="utf-8-sig"))
    ab = json.load(io.open(os.path.join(MODE, "ability_observations.json"), encoding="utf-8-sig"))
    print("原型 %d 个、能力观测 %d 条、图标语义 %d 条" % (len(ar), len(ab), len(sprites_global)))

    img2zh = {}
    for k, v in zh.items():
        m = re.match(r"^card\.[a-z]+\.([a-z0-9_]+)\.title$", k)
        if m:
            img2zh.setdefault(m.group(1), v.strip())

    eff_map = {}
    for line in io.open(EFFECT, encoding="utf-8"):
        k, _, v = line.rstrip("\n").partition("\t")
        eff_map[k] = v
    kb_keys = {l.split("\t")[0].strip() for l in io.open(KB, encoding="utf-8")
               if l.strip() and not l.startswith("#")}

    rows, stats = [], {"cards": 0, "have": 0, "gap": 0, "built": 0, "none": 0}
    for a in ar:
        at = {x["name"]: x["value"] for x in (a.get("attributes") or [])}
        img = at.get(920003)
        ids = at.get(900013) or []
        if not img or not ids:
            continue
        zh_name = img2zh.get(img)
        if not zh_name:
            continue
        stats["cards"] += 1
        if ("name:" + zh_name) in eff_map or zh_name in kb_keys:
            stats["have"] += 1
            continue
        stats["gap"] += 1

        groups = {}
        for aid in ids:
            obs = ab.get(aid)
            if not obs:
                continue
            o = obs[0].get("attributes") or {}
            nm = o.get("Name")
            cls = nm.get("id") if isinstance(nm, dict) else nm
            # 通用容器能力一律跳过（AcquireAbility 有 GameText 但那是「放牌到哪」的流程提示）
            if cls in GENERIC:
                continue
            timing = o.get("AbilityTiming") or "Other"
            txt = ability_text(o, zh)
            if not txt:
                continue
            lst = groups.setdefault(timing, [])
            if txt not in lst and len(lst) < 4:
                lst.append(txt)

        if not groups:
            stats["none"] += 1
            continue

        segs = []
        for timing in ("Agent", "Reveal", "Combat", "CombatResolution", "Endgame", "Other"):
            lst = groups.get(timing)
            if not lst:
                continue
            label = TIMING_LABEL.get(timing)
            body = "；".join(lst)
            segs.append((label + "：" + body) if label else body)

        text = "；".join(segs)
        if len(text) > MAX_LEN:
            text = text[:MAX_LEN] + "…"
        rows.append((zh_name, text))
        stats["built"] += 1

    print("卡牌原型 %d：已有 %d、待补 %d、可还原 %d、仍无 %d"
          % (stats["cards"], stats["have"], stats["gap"], stats["built"], stats["none"]))
    for r in rows:
        print("  %-12s %s" % (r[0], r[1][:84]))

    if write and rows:
        s = io.open(KB, encoding="utf-8").read()
        if not s.endswith("\n"):
            s += "\n"
        s += "\n# 卡牌效果：catalog_probe 只读导出（archetypes 900013 -> ability_observations 的\n"
        s += "# GameText/AbilityTiming）。GameText 键常与能力类名不同，例 ForHumanityAgentAbility\n"
        s += "# 指向 button.GainInfluenceAbility。按 Agent/Reveal/Combat 分组。\n"
        s += "\n".join("%s\t%s\t只读目录+GameText" % (n, t) for n, t in rows) + "\n"
        io.open(KB, "w", encoding="utf-8", newline="\n").write(s)
        print("\n写入 %d 条；接着跑 python mods/tools/build_effect_map.py" % len(rows))


if __name__ == "__main__":
    main()
