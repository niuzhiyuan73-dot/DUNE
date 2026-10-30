"""生成《冲突牌与科技牌总表.md》（数据取自 dump/cards_runtime.json + 游戏本地化表）。

冲突牌等级来源：存档里的 920007（ConflictLevel）实测值；
未出场过的 11 张按约定归类（金融霸权/香料运输=三级，遭遇战=一级，其余=二级）。
"""
import io
import json
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
sys.path.insert(0, HERE)

import UnityPy
from make_faction_doc import TAG_LABEL, SET_LABEL  # noqa: E402

CARDS_JSON = os.path.join(ROOT, "dump", "cards_runtime.json")
SAVEDIR = os.path.join(ROOT, "profile", "DireWolf", "Dune_ Imperium", "savedgames")
LOC = os.path.join(ROOT, "game", "DuneImperium_Data", "StreamingAssets",
                   "Localization", "win", "localization")
OUT = os.path.join(ROOT, "冲突牌与科技牌总表.md")

# 冲突等级表：level, 来源（实测=存档 920007 读到过；约定=没出场过，按规则归类）
LEVEL = {
    # ---- 三级（10）----
    "battleforarrakeen":       (3, "实测"),
    "battleforarrakeenup":     (3, "实测"),
    "battleforcarthag":        (3, "实测"),
    "battleforimperialbasin":  (3, "实测"),
    "battleforimperialbasinup": (3, "实测"),
    "battleforspicerefineryup": (3, "实测"),
    "grandvision":             (3, "实测"),
    "propagandaup":            (3, "实测"),
    "economicsupremacy":       (3, "约定"),
    "spicefreightersup":       (3, "约定"),
    # ---- 二级（20）----
    "cloakanddagger":          (2, "实测"),
    "siegeofcarthag":          (2, "实测"),
    "machinations":            (2, "实测"),
    "secureimperialbasin":     (2, "实测"),
    "guildbankraid":           (2, "实测"),
    "siegeofarrakeen":         (2, "实测"),
    "sortthroughthechaos":     (2, "实测"),
    "raidstockpiles":          (2, "实测"),
    "testofloyaltyup":         (2, "实测"),
    "seizespicerefineryup":    (2, "实测"),
    "siegeofarrakeenup":       (2, "实测"),
    "protectthesietchesup":    (2, "实测"),
    "secureimperialbasinup":   (2, "实测"),
    "terriblepurpose":         (2, "约定"),
    "trademonopoly":           (2, "约定"),
    "shadowcontestup":         (2, "约定"),
    "tradedisputeup":          (2, "约定"),
    "tradedisputechoam":       (2, "约定"),
    "choamsecuritychoam":      (2, "约定"),
    "choamsecurityup":         (2, "约定"),
    # ---- 一级（3）----
    "skirmishg":               (1, "约定"),
    "skirmishh":               (1, "约定"),
    "skirmishi":               (1, "约定"),
}

PRO_DECK = {  # Pro 版实际用到的 10 张
    "cloakanddagger", "machinations", "guildbankraid", "testofloyaltyup",
    "seizespicerefineryup", "battleforarrakeen", "battleforcarthag",
    "battleforimperialbasin", "grandvision", "spicefreightersup",
}


def load_loc():
    """返回 {key: value}，只取简中表。"""
    out = {}
    env = UnityPy.load(LOC)
    tables = []
    for o in env.objects:
        if o.type.name != "TextAsset":
            continue
        raw = o.read().m_Script
        if isinstance(raw, bytes):
            raw = raw.decode("utf-8", "replace")
        if "card.imperium.dagger.title" in raw:
            tables.append(raw)
    tables.sort(key=lambda t: 0 if "该帐号已存在" in t else 1)
    if tables:
        for line in tables[0].splitlines():
            if "=" not in line or not line.startswith("card."):
                continue
            k, _, v = line.partition("=")
            out[k] = v
    return out


def clean(text):
    """把 Unity 富文本标签换成可读文本。"""
    if not text:
        return ""
    text = re.sub(r"<sprite name=\"?([^\">]+)\"?>", r"【\1】", text)
    text = text.replace("<br>", " ").replace("<br/>", " ")
    text = re.sub(r"<[^>]+>", "", text)
    text = text.replace("【", "[").replace("】", "]")
    return re.sub(r"\s+", " ", text).strip()


def main():
    loc = load_loc()
    cards = json.load(io.open(CARDS_JSON, encoding="utf-8-sig"))

    # 类别：以本地化键为准（运行时的 type 字段在 interop 下读不出来）
    def ctype(c):
        img = c["image"].lower()
        for k in loc:
            parts = k.split(".")
            if len(parts) >= 3 and parts[0] == "card" and parts[2].lower() == img:
                return parts[1]
        return ""

    title = {}
    ability = {}
    for k, v in loc.items():
        parts = k.split(".")
        if len(parts) < 3 or parts[0] != "card":
            continue
        img = parts[2].lower()
        if len(parts) == 3:                 # card.conflict.cloakanddagger
            title.setdefault(img, v.strip())
            continue
        field = parts[3]
        if field == "title":
            title[img] = v.strip()
        elif field == "ability":
            ability[img] = clean(v)

    conflicts = [c for c in cards if ctype(c) == "conflict"]
    techs = [c for c in cards if ctype(c) in ("techtile", "tech")]

    def level_of(c):
        return LEVEL.get(c["image"].lower(), (2, "约定"))

    by_level = {1: [], 2: [], 3: []}
    for c in conflicts:
        l, src = level_of(c)
        by_level[l].append((c, src))

    def tags_text(c):
        ts = [TAG_LABEL.get(t, t) for t in c.get("tags", [])]
        return "、".join(ts) if ts else "—"

    def sets_text(c):
        return " / ".join(SET_LABEL.get(s, s) for s in c.get("sets", [])) or "—"

    lines = []
    lines.append("# 冲突牌与科技牌总表")
    lines.append("")
    lines.append(
        f"数据来源：游戏运行时导出（`dump/cards_runtime.json`）+ 本地化表。"
        f"冲突牌 **{len(conflicts)}** 张，科技牌 **{len(techs)}** 张。")
    lines.append("")
    n_measured = sum(1 for c in conflicts if LEVEL.get(c["image"].lower(), ("", ""))[1] == "实测")
    lines.append("> 冲突等级取自存档里的 `920007`（ConflictLevel）："
                 f"其中 **{n_measured} 张**是对局里实际出场过的（标「实测」），"
                 "另外 11 张在采样对局中从未出现，按约定归类（标「约定」）："
                 "金融霸权 / 香料运输记三级，遭遇战记一级，其余记二级。")
    lines.append("")
    lines.append("---")
    lines.append("")

    # ---------------- 冲突牌 ----------------
    lines.append("## 一、冲突牌")
    lines.append("")
    lines.append("| 等级 | 张数 |")
    lines.append("|---|---|")
    for l in (3, 2, 1):
        lines.append(f"| {['','一级','二级','三级'][l]} | {len(by_level[l])} |")
    lines.append("")

    idx = 0
    for l in (3, 2, 1):
        rows = sorted(by_level[l], key=lambda x: (x[0]["sets"][0], x[0]["image"]))
        lines.append(f"### {['','一级','二级','三级'][l]}冲突（{len(rows)} 张）")
        lines.append("")
        lines.append("| # | 卡名 | ID | 内容集 | Tag | Pro 选用 | 等级来源 |")
        lines.append("|---|---|---|---|---|---|---|")
        for c, src in rows:
            img = c["image"].lower()
            mark = "✅" if img in PRO_DECK else "—"
            lines.append(f"| {idx + 1} | {title.get(img, c['image'])} | `{c['id']}` | {sets_text(c)} | "
                         f"{tags_text(c)} | {mark} | {src} |")
            idx += 1
        lines.append("")

    lines.append("> Pro 版牌堆：5 张二级（斗篷与匕首、阴谋、突袭公会银行、忠诚考验、占领香料加工厂）"
                 "→ 5 张三级（厄拉奇恩之战、迦太格之战、帝国盆地之战、远大目标、香料运输）。")
    lines.append("> Extra 版牌堆：上表全部 10 张三级，顺序交给游戏洗。")
    lines.append("")
    lines.append("---")
    lines.append("")

    # ---------------- 科技牌 ----------------
    lines.append("## 二、科技牌")
    lines.append("")
    lines.append("科技牌（Tech Tile）通过「研究」获得，放在玩家面前提供持续能力或一次性效果。")
    lines.append("")

    grouped = {}
    for c in techs:
        s = c["sets"][0] if c.get("sets") else "Unknown"
        grouped.setdefault(s, []).append(c)

    for s in sorted(grouped):
        rows = sorted(grouped[s], key=lambda c: c["image"])
        lines.append(f"### {SET_LABEL.get(s, s)}（{len(rows)} 张）")
        lines.append("")
        lines.append("| # | 科技牌 | ID | Tag | 效果 |")
        lines.append("|---|---|---|---|---|")
        for i, c in enumerate(rows, 1):
            img = c["image"].lower()
            eff = ability.get(img, "")
            lines.append(f"| {i} | {title.get(img, c['image'])} | `{c['id']}` | {tags_text(c)} | "
                         f"{eff if eff else '（文本烘焙在预制体，未导出）'} |")
        lines.append("")

    lines.append("---")
    lines.append("")
    lines.append("## 说明")
    lines.append("")
    lines.append("- 冲突牌的「Pro 选用」只标 Pro 版；Extra 版用的是全部三级冲突。")
    lines.append("- 科技牌效果文本来自本地化表的 `card.techtile.<名称>.ability`，已去掉富文本标签，")
    lines.append("  图标原样保留为 `[Strength]`、`[Spice_1]` 这类符号名。")
    lines.append("- 科技牌的费用与购买条件存在运行时属性里（`Nullable<int>` 在 interop 下读不出），")
    lines.append("  所以这张表不含价格。")

    io.open(OUT, "w", encoding="utf-8").write("\n".join(lines) + "\n")
    print(f"已生成 {OUT}")
    print(f"  冲突牌 {len(conflicts)} 张（三级 {len(by_level[3])} / 二级 {len(by_level[2])} / 一级 {len(by_level[1])}）")
    print(f"  科技牌 {len(techs)} 张")


if __name__ == "__main__":
    main()
