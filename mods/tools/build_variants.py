"""生成 v2a / v2b 两个候选变体，并分别打包成可安装 bundle。

v2a 对称强化：三名玩家统一加成（官方 Campaign108 已验证该写法对 AI 生效）
v2b 仅全局参数：不碰 per-player，只改胜利点阈值作为对照
"""
import os
import json
import copy
from patch_scenario import patch, verify

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
BASE = os.path.join(ROOT, "mods", "_baseline", "Campaign01.original.json")
OUT_DIR = os.path.join(ROOT, "mods", "out")
BUNDLE = "b56aa8ad35f9c2e1694994f384e51bbe.bundle"
TA = "Campaign01"

SHARED_DECK = [
    "Dagger", "Dagger", "SardaukarInfantry",
    "DunetheDesertPlanet", "DunetheDesertPlanet",
    "Diplomacy", "Reconnaissance", "SeekAllies", "SignetRing",
    "Crysknife",
]
BONUS = {
    "StartingSwordmaster": True,
    "StartingSolari": 3,
    "StartingSpice": 2,
    "StartingWater": 2,
}


def load_base():
    with open(BASE, encoding="utf-8") as f:
        return json.load(f)


def build_v2a(doc):
    """对称强化：所有玩家同样的牌组、特工、资源、剑术大师。"""
    doc["EndgameTriggerScore"] = 15
    for p in doc["wormPlayers"]:
        p["StartingDeck"] = list(SHARED_DECK)
        p["StartingAgents"] = 3
        opts = p.setdefault("PlayerMatchSoloOptions", {})
        opts.update(BONUS)
    return doc


def build_v2b(doc):
    """仅全局：只改胜利点阈值，per-player 全部还原为原版。"""
    doc["EndgameTriggerScore"] = 15
    return doc


def dump(doc, folder, label, note):
    os.makedirs(folder, exist_ok=True)
    jp = os.path.join(folder, "scenario.json")
    with open(jp, "w", encoding="utf-8") as f:
        json.dump(doc, f, ensure_ascii=False, indent=1)
    meta = {
        "label": label,
        "note": note,
        "target_bundle": BUNDLE,
        "textasset": TA,
    }
    with open(os.path.join(folder, "meta.json"), "w", encoding="utf-8") as f:
        json.dump(meta, f, ensure_ascii=False, indent=1)
    return jp


def main():
    os.makedirs(OUT_DIR, exist_ok=True)

    a = build_v2a(copy.deepcopy(load_base()))
    ja = dump(a, os.path.join(ROOT, "mods", "v2a"),
              "v2a 对称强化",
              "三名玩家统一：10 张同款牌组、特工 3、开局剑术大师、3 币/2 香料/2 水；终点 15")
    outa = patch(ja, BUNDLE, TA, os.path.join(OUT_DIR, "v2a_" + BUNDLE))
    verify(outa, TA, [])

    b = build_v2b(copy.deepcopy(load_base()))
    jb = dump(b, os.path.join(ROOT, "mods", "v2b"),
              "v2b 仅全局参数",
              "只把胜利点阈值改为 15，其余与原版完全一致（对照组的绝对公平基线）")
    outb = patch(jb, BUNDLE, TA, os.path.join(OUT_DIR, "v2b_" + BUNDLE))
    verify(outb, TA, [])

    for tag, doc in (("v2a", a), ("v2b", b)):
        print(f"\n[{tag}] 玩家数={len(doc['wormPlayers'])} 终点={doc['EndgameTriggerScore']}")
        for i, p in enumerate(doc["wormPlayers"]):
            who = "人" if p.get("isHuman") else "AI"
            print(f"   [{i}]{who}: 牌组{len(p['StartingDeck'])}张 特工{p['StartingAgents']} "
                  f"剑术大师={p['PlayerMatchSoloOptions'].get('StartingSwordmaster')} "
                  f"资源={p['PlayerMatchSoloOptions'].get('StartingSolari')}/"
                  f"{p['PlayerMatchSoloOptions'].get('StartingSpice')}/"
                  f"{p['PlayerMatchSoloOptions'].get('StartingWater')}")


if __name__ == "__main__":
    main()
