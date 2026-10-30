"""把游戏内导出的卡牌数据整理成两份文档：
  1) 卡牌总表-按阵营.md
  2) 卡牌总表-按花费.md

数据来源：dump/cards_runtime.json（由 BepInEx 导出插件写出）
名称来源：游戏本地化表 card.<type>.<image>.title
"""
import os
import json
from collections import defaultdict, Counter

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
SRC = os.path.join(ROOT, "dump", "cards_runtime.json")
LOC = os.path.join(ROOT, "game", "DuneImperium_Data", "StreamingAssets", "Localization", "win", "localization")
OUT_DIR = os.path.join(ROOT, "dump")

TYPE_KEY = {
    "Imperium": "imperium",
    "Intrigue": "intrigue",
    "Contract": "contract",
    "Conflict": "conflict",
    "LeaderUpgrade": "leaderupgrade",
    "ConflictReward": "conflictreward",
}

TYPE_LABEL = {
    "Imperium": "帝国牌",
    "Intrigue": "阴谋牌",
    "Contract": "合约牌",
    "Conflict": "冲突牌",
    "LeaderUpgrade": "领袖升级",
    "ConflictReward": "冲突奖励",
}

FACTION_LABEL = {
    "Fremen": "弗雷曼人",
    "BeneGesserit": "贝尼·杰瑟里特（姐妹会）",
    "SpacingGuild": "宇航工会",
    "Emperor": "皇帝",
    "None": "无阵营",
}

SET_LABEL = {
    "BaseSet": "基础",
    "RiseOfIx": "伊克斯",
    "Immortality": "不朽",
    "Uprising": "崛起",
    "CHOAMModule": "商会",
    "None": "无",
}


def load_titles():
    """返回 {(type, image): 中文标题}"""
    try:
        import UnityPy
    except ImportError:
        return {}
    titles = {}
    env = UnityPy.load(LOC)
    for o in env.objects:
        if o.type.name != "TextAsset":
            continue
        body = o.read().m_Script
        if isinstance(body, bytes):
            body = body.decode("utf-8", "replace")
        if "该帐号已存在" not in body:
            continue  # 只要简体表
        for line in body.splitlines():
            if not line.startswith("card.") or "=" not in line:
                continue
            key, _, val = line.partition("=")
            parts = key.split(".")
            if len(parts) < 4:
                continue
            # card.<type>.<image>.title  (image 可能含点，取中间整段)
            if parts[-1] != "title":
                continue
            ctype = parts[1]
            image = ".".join(parts[2:-1])
            titles[(ctype, image)] = val.strip()
    return titles


def label_of(card, titles):
    ctype = TYPE_KEY.get(card["type"], card["type"].lower())
    return titles.get((ctype, card["image"])) or card["image"]


def fmt_cost(c):
    return "—" if c is None else str(c)


def main():
    if not os.path.exists(SRC):
        raise SystemExit(f"缺少数据文件：{SRC}\n请先运行游戏导出（插件会把 JSON 写到这个路径）")

    with open(SRC, encoding="utf-8") as f:
        cards = json.load(f)

    titles = load_titles()
    for c in cards:
        c["name"] = label_of(c, titles)
        c["faction"] = c["factions"][0] if c["factions"] else "None"

    cards.sort(key=lambda c: (TYPE_LABEL.get(c["type"], c["type"]),
                              c["faction"], c["cost"] if c["cost"] is not None else 99, c["name"]))

    # ---------- 文档一：按阵营 ----------
    groups = defaultdict(list)
    for c in cards:
        groups[c["faction"]].append(c)

    order = ["Fremen", "BeneGesserit", "SpacingGuild", "Emperor", "None"]
    extra = [k for k in groups if k not in order]

    lines = ["# 卡牌总表 · 按阵营", ""]
    lines.append(f"共 **{len(cards)}** 张卡。阵营以卡牌自身的 `FactionList` 为准，"
                 f"四大家族之外归入「无阵营」。")
    lines.append("")
    lines.append("| 阵营 | 张数 |")
    lines.append("|---|---|")
    for k in order + extra:
        if k not in groups:
            continue
        lines.append(f"| {FACTION_LABEL.get(k, k)} | {len(groups[k])} |")
    lines.append("")

    for k in order + extra:
        if k not in groups:
            continue
        g = groups[k]
        lines.append(f"## {FACTION_LABEL.get(k, k)}（{len(g)} 张）")
        lines.append("")
        lines.append("| 卡名 | 类型 | 花费 | 内容集 | 相关 Tag |")
        lines.append("|---|---|---|---|---|")
        for c in g:
            sets = "/".join(SET_LABEL.get(s, s) for s in c["sets"]) or "—"
            tags = ", ".join(c["tags"]) or "—"
            lines.append(f"| {c['name']} | {TYPE_LABEL.get(c['type'], c['type'])} | "
                         f"{fmt_cost(c['cost'])} | {sets} | {tags} |")
        lines.append("")

    p1 = os.path.join(OUT_DIR, "卡牌总表-按阵营.md")
    with open(p1, "w", encoding="utf-8") as f:
        f.write("\n".join(lines))

    # ---------- 文档二：按花费 ----------
    buckets = defaultdict(list)
    for c in cards:
        cost = c["cost"]
        buckets["无花费" if cost is None else cost].append(c)

    def bucket_key(k):
        return (0, 99) if k == "无花费" else (1, k)

    lines = ["# 卡牌总表 · 按花费", ""]
    lines.append(f"共 **{len(cards)}** 张卡。花费取自卡牌的说服力费用（PersuasionCost），"
                 f"阴谋牌等无费用卡单列。")
    lines.append("")
    lines.append("| 花费 | 张数 |")
    lines.append("|---|---|")
    for k in sorted(buckets, key=bucket_key):
        lines.append(f"| {k} | {len(buckets[k])} |")
    lines.append("")

    for k in sorted(buckets, key=bucket_key):
        g = buckets[k]
        title = "无花费" if k == "无花费" else f"{k} 费"
        lines.append(f"## {title}（{len(g)} 张）")
        lines.append("")
        lines.append("| 卡名 | 类型 | 阵营 | 内容集 | 相关 Tag |")
        lines.append("|---|---|---|---|---|")
        for c in g:
            sets = "/".join(SET_LABEL.get(s, s) for s in c["sets"]) or "—"
            tags = ", ".join(c["tags"]) or "—"
            lines.append(f"| {c['name']} | {TYPE_LABEL.get(c['type'], c['type'])} | "
                         f"{FACTION_LABEL.get(c['faction'], c['faction'])} | {sets} | {tags} |")
        lines.append("")

    p2 = os.path.join(OUT_DIR, "卡牌总表-按花费.md")
    with open(p2, "w", encoding="utf-8") as f:
        f.write("\n".join(lines))

    print(f"[ok] {p1}  ({len(cards)} 张)")
    print(f"[ok] {p2}")
    print(f"[stat] 阵营分布: {dict(Counter(c['faction'] for c in cards))}")
    print(f"[stat] 类型分布: {dict(Counter(c['type'] for c in cards))}")
    missing = [c['image'] for c in cards if c['name'] == c['image']]
    if missing:
        print(f"[warn] {len(missing)} 张卡缺少中文名（前 10）: {missing[:10]}")


if __name__ == "__main__":
    main()
