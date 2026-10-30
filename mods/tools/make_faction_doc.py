"""按阵营整理卡牌，输出 Markdown。

数据：dump/cards_runtime.json（BepInEx 导出）
名称与卡牌类型：本地化表 card.<type>.<image>.title
"""
import os
import json
from collections import defaultdict, Counter

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
SRC = os.path.join(ROOT, "dump", "cards_runtime.json")
LOC = os.path.join(ROOT, "game", "DuneImperium_Data", "StreamingAssets", "Localization", "win", "localization")
OUT = os.path.join(ROOT, "帝国牌总表-按阵营.md")

FACTION_ORDER = ["Fremen", "BeneGesserit", "SpacingGuild", "Emperor", "None"]
FACTION_LABEL = {
    "Fremen": "弗雷曼人",
    "BeneGesserit": "贝尼·杰瑟里特（姐妹会）",
    "SpacingGuild": "宇航工会",
    "Emperor": "皇帝",
    "None": "无阵营 / 中立",
}
TYPE_LABEL = {
    "imperium": "帝国牌",
    "intrigue": "阴谋牌",
    "contract": "合约牌",
    "conflict": "冲突牌",
    "tleilaxu": "特莱拉苏牌",
    "techtile": "科技牌",
    "leader": "领袖",
    "leaderupgrade": "领袖升级",
    "tech": "科技",
    "space": "争夺位",
}
TAG_LABEL = {
    "Faction": "阵营",
    "Landsraad": "兰兹拉德",
    "CHOAM": "宇联商会",
    "Harvest": "采收",
    "City": "城市",
    "Victory": "胜利点",
    "Persuasion": "说服力",
    "Control": "控制",
    "NamedCharacter": "具名角色",
    "FremenBond": "弗雷曼羁绊",
    "FremenInfluence": "弗雷曼影响力",
    "FremenAlliance": "弗雷曼同盟",
    "SpacingGuildInfluence": "宇航工会影响力",
    "SpacingGuildAlliance": "宇航工会同盟",
    "EmperorInfluence": "皇帝影响力",
    "EmperorAlliance": "皇帝同盟",
    "BeneGesseritInfluence": "姐妹会影响力",
    "BeneGesseritAlliance": "姐妹会同盟",
    "DiscardEnabler": "弃牌触发",
    "IncentiveDiscard": "弃牌激励",
    "HagalTech": "哈加尔科技",
    "Infiltrator": "渗透者",
    "BuyTech": "购买科技",
    "Freighter": "货运船",
    "Shadow": "暗影",
    "WantsGraft": "渴望接枝",
    "Graft": "接枝",
    "Helix": "螺旋",
    "DoubleHelix": "双螺旋",
    "Combat": "战斗",
    "ConditionalStrength": "条件战力",
    "ConditionalEndgameVP": "条件终局分",
    "WantE": "需求：皇帝",
    "WantSG": "需求：宇航工会",
    "WantBG": "需求：姐妹会",
    "WantF": "需求：弗雷曼",
    "WantHC": "需求：高议会",
    "WantTSMF": "需求：香料必须流",
    "WantSpy": "需求：间谍",
    "WantHooks": "需求：造物主钩爪",
    "WantContract2": "需求：完成 2 合约",
    "WantContract4": "需求：完成 4 合约",
    "WantContractX": "需求：合约",
    "WantRecall": "需求：召回",
    "Research": "研究",
    "BattleIcon": "战斗图标",
    "Spy": "间谍",
    "Contract": "合约",
    "Unload": "卸货",
    "RecallSpy": "召回间谍",
    "RecallAgent": "召回特工",
    "Sandworm": "沙虫",
    "MakerHooks": "造物主钩爪",
    "BlowWall": "炸开山壁",
    "ShieldWall": "盾墙",
    "Invalid": "无效",
}

SET_LABEL = {
    "BaseSet": "基础",
    "RiseOfIx": "伊克斯",
    "Immortality": "不朽",
    "Uprising": "崛起",
    "CHOAMModule": "商会",
}


def load_titles():
    """{image小写: (type, 中文名)}，只取简体表"""
    import UnityPy
    out = {}
    env = UnityPy.load(LOC)
    for o in env.objects:
        if o.type.name != "TextAsset":
            continue
        body = o.read().m_Script
        if isinstance(body, bytes):
            body = body.decode("utf-8", "replace")
        if "该帐号已存在" not in body:
            continue
        for line in body.splitlines():
            if not line.startswith("card.") or "=" not in line:
                continue
            key, _, val = line.partition("=")
            parts = key.split(".")
            if len(parts) >= 4 and parts[-1] == "title":
                # card.<type>.<image>.title
                ctype = parts[1]
                image = ".".join(parts[2:-1]).lower()
            elif len(parts) == 3:
                # card.<type>.<image>  （冲突牌 / 合约牌）
                ctype = parts[1]
                image = parts[2].lower()
            else:
                continue
            out.setdefault(image, (ctype, val.strip()))
        break
    return out


def main():
    if not os.path.exists(SRC):
        raise SystemExit(f"缺少 {SRC}，请先跑一次导出插件")

    cards = json.load(open(SRC, encoding="utf-8-sig"))
    titles = load_titles()

    rows = []
    for c in cards:
        hit = titles.get(c["image"].lower())
        if not hit:
            continue  # 没有本地化标题的，不是可展示的卡牌
        ctype, name = hit
        if ctype != "imperium":
            continue  # 只统计帝国牌
        rows.append({
            "name": name,
            "image": c["image"],
            "type": ctype,
            "faction": (c["factions"][0] if c["factions"] else "None"),
            "sets": c["sets"],
            "tags": c["tags"],
        })

    # 同名不同版本的卡（如基础版 / 崛起版），在卡名后标注内容集以便区分
    name_count = Counter(r["name"] for r in rows)
    for r in rows:
        if name_count[r["name"]] > 1:
            ver = "/".join(SET_LABEL.get(x, x) for x in r["sets"]) or "?"
            r["display"] = f"{r['name']}（{ver}版）"
        else:
            r["display"] = r["name"]

    groups = defaultdict(list)
    for r in rows:
        groups[r["faction"]].append(r)

    for g in groups.values():
        g.sort(key=lambda r: (TYPE_LABEL.get(r["type"], r["type"]), r["name"]))

    lines = ["# 帝国牌总表 · 按阵营", ""]
    lines.append(f"统计范围：游戏内全部**帝国牌**（含所有内容包），共 **{len(rows)}** 张。"
                 f"阵营取自卡牌自身的阵营归属，四大家族之外统一归入「无阵营 / 中立」。"
                 f"阴谋牌、合约牌、冲突牌、科技牌已排除。")
    lines.append("")

    lines.append("## 总览")
    lines.append("")
    lines.append("| 阵营 | 张数 |")
    lines.append("|---|---|")
    for f in FACTION_ORDER:
        g = groups.get(f, [])
        if not g:
            continue
        lines.append(f"| {FACTION_LABEL[f]} | {len(g)} |")
    lines.append("")

    for f in FACTION_ORDER:
        g = groups.get(f, [])
        if not g:
            continue
        lines.append(f"## {FACTION_LABEL[f]}（{len(g)} 张）")
        lines.append("")
        lines.append("| 卡名 | 内容集 | Tag |")
        lines.append("|---|---|---|")
        for r in g:
            sets = "/".join(SET_LABEL.get(s, s) for s in r["sets"]) or "—"
            tags = "、".join(TAG_LABEL.get(t, t) for t in r["tags"]) or "—"
            lines.append(f"| {r['display']} | {sets} | {tags} |")
        lines.append("")

    with open(OUT, "w", encoding="utf-8") as fh:
        fh.write("\n".join(lines))

    print(f"[ok] {OUT}  共 {len(rows)} 张")
    for f in FACTION_ORDER:
        if groups.get(f):
            print(f"     {FACTION_LABEL[f]}: {len(groups[f])}")


if __name__ == "__main__":
    main()
