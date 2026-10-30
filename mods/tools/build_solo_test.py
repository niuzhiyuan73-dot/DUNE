"""路线 A 实验：给 StandardSolo 补上 wormPlayers，用 StartingDeck 给人和 AI 各加卡。

只改数据，不动代码。失败可立即回滚（switch_variant.ps1 选 original）。
"""
import os
import json
import copy
import UnityPy
from patch_scenario import patch, verify

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
OUT_DIR = os.path.join(ROOT, "mods", "out")
VAR_DIR = os.path.join(ROOT, "mods", "v3solo")
BUNDLE = "b56aa8ad35f9c2e1694994f384e51bbe.bundle"

# 与 Campaign01 一致的 9 张基础牌组，额外加一张晶刃用于识别
DECK = ["Dagger", "Dagger", "SardaukarInfantry", "DunetheDesertPlanet",
        "DunetheDesertPlanet", "Diplomacy", "Reconnaissance", "SeekAllies",
        "SignetRing", "Crysknife"]

BONUS = {"StartingSwordmaster": False, "StartingSolari": 2, "StartingSpice": 1,
         "StartingWater": 1}


def make_player(idx, human, color):
    return {
        "StartingDeck": list(DECK),
        "StartingIntrigue": [],
        "LeaderOptions": [],
        "AllianceTokenVP": 1,
        "StartingAgents": 2,
        "StartingOrder": idx + 1,
        "PlayerColor": color,
        "HagalPlayer": False,
        "StartingTechTiles": [],
        "PreferredLeaders": [],
        "PlayerMatchSoloOptions": {
            "StartingDiscard": [], "StartingDeckOrder": [], "Reshuffle1DeckOrder": [],
            "Reshuffle2DeckOrder": [], "Reshuffle3DeckOrder": [],
            "BonusStartingGarrison": 0, "BonusStartingWater": 0, "ControlLocations": [],
            "BonusStartingVP": 0, "StartingSwordmaster": BONUS["StartingSwordmaster"],
            "StartingHighCouncil": False, "StartingSolari": BONUS["StartingSolari"],
            "StartingSpice": BONUS["StartingSpice"], "StartingWater": BONUS["StartingWater"],
            "StartingGarrison": 0, "StartingSpecimens": 0,
        },
        "name": "",
        "isHuman": human,
        "aiLevel": 0 if human else 1,
        "metadata": {},
        "accountID": f"00000000-0000-4000-8000-0000000000{idx:02d}",
    }


def build():
    src = os.path.join(ROOT, "mods", "out", "v2a_" + BUNDLE)
    base_bundle = os.path.join(ROOT, "backup", "game", "scenario_bundle.orig")

    env = UnityPy.load(base_bundle)
    doc = None
    for o in env.objects:
        if o.type.name != "TextAsset":
            continue
        ta = o.read()
        if getattr(ta, "m_Name", "") == "StandardSolo":
            t = ta.m_Script
            if isinstance(t, bytes):
                t = t.decode("utf-8", "replace")
            doc = json.loads(t)
            break
    if doc is None:
        raise SystemExit("未能在原始包中找到 StandardSolo")

    colors = ["Green", "Red", "Yellow", "Blue"]
    doc["wormPlayers"] = [make_player(i, i == 0, colors[i]) for i in range(4)]

    os.makedirs(VAR_DIR, exist_ok=True)
    jp = os.path.join(VAR_DIR, "scenario.json")
    with open(jp, "w", encoding="utf-8") as f:
        json.dump(doc, f, ensure_ascii=False, indent=1)
    with open(os.path.join(VAR_DIR, "meta.json"), "w", encoding="utf-8") as f:
        json.dump({
            "label": "v3solo 路线A：StandardSolo 补 wormPlayers",
            "note": "四个玩家统一 10 张牌组（含晶刃），验证 wormPlayers 在单人局是否被读取",
            "target_bundle": BUNDLE,
            "textasset": "StandardSolo",
            "players": 4,
        }, f, ensure_ascii=False, indent=1)

    out = patch(jp, BUNDLE, "StandardSolo", os.path.join(OUT_DIR, "v3solo_" + BUNDLE))
    verify(out, "StandardSolo", [])
    print(f"玩家数: {len(doc['wormPlayers'])}  每个玩家牌组: {len(DECK)} 张")
    for i, p in enumerate(doc["wormPlayers"]):
        print(f"  [{i}] {'人' if p['isHuman'] else 'AI'} 牌组={len(p['StartingDeck'])} 颜色={p['PlayerColor']}")


if __name__ == "__main__":
    build()
