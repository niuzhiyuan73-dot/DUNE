"""一次性诊断：四家起始资源全部设成不可能看错的数值。

人类  water 3 / spice 3 / solari 3
AI1-3 water 4 / spice 4 / solari 4

跑一局后读存档即可判断：
  - 数值 3 / 4 出现 -> 该字段对这类玩家生效
  - 全是 1 / 0     -> 该字段被忽略
"""
import os
import sys
import json
import shutil
import UnityPy

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
ORIG = os.path.join(ROOT, "backup", "game", "scenario_bundle.orig")
OUT_DIR = os.path.join(ROOT, "mods", "out")
BUNDLE = "b56aa8ad35f9c2e1694994f384e51bbe.bundle"
TARGET = os.path.join(ROOT, "game", "DuneImperium_Data", "StreamingAssets", "aa",
                      "StandaloneWindows64", BUNDLE)

sys.path.insert(0, HERE)
import build_presets as bp

DIAG_HUMAN = {"water": 3, "spice": 3, "solari": 3}
DIAG_AI = {"water": 4, "spice": 4, "solari": 4}


def main():
    env = UnityPy.load(ORIG)
    doc = None
    for o in env.objects:
        if o.type.name != "TextAsset":
            continue
        ta = o.read()
        if getattr(ta, "m_Name", "") != bp.TA_NAME:
            continue
        raw = ta.m_Script
        doc = json.loads(raw if isinstance(raw, str) else raw.decode("utf-8", "replace"))
        break
    if doc is None:
        raise SystemExit("找不到 StandardSolo")

    assign = ["fremen", "bene", "guild", "emperor"]   # 人类弗雷曼，AI 各一家（固定，便于对号）
    players = []
    for i, fk in enumerate(assign):
        deck, _res, intrigue, _removed = bp.faction_loadout("faction", fk, bp.SETS_ALL)
        res = DIAG_HUMAN if i == 0 else DIAG_AI
        players.append(bp.make_player(i, i == 0, deck, intrigue, res))
        print(f"  玩家{i} = {bp.FACTIONS[fk]['label']:5s} 牌组 {len(deck)} 张 "
              f"| 水 {res['water']} 香料 {res['spice']} 太阳币 {res['solari']}")

    doc["sets"] = list(bp.SETS_ALL)
    doc["EndgameTriggerScore"] = 10
    doc["LeaderAssignmentMode"] = "Random"
    doc["wormPlayers"] = players

    for o in env.objects:
        if o.type.name != "TextAsset":
            continue
        ta = o.read()
        if getattr(ta, "m_Name", "") != bp.TA_NAME:
            continue
        ta.m_Script = json.dumps(doc, ensure_ascii=False, indent=1)
        ta.save()
        break

    os.makedirs(OUT_DIR, exist_ok=True)
    out = os.path.join(OUT_DIR, f"diag_bonus_{BUNDLE}")
    with open(out, "wb") as f:
        f.write(env.file.save(packer="lz4"))
    print(f"[diag] 已生成 {out}")
    shutil.copy2(out, TARGET)
    print(f"[diag] 已安装到游戏")


if __name__ == "__main__":
    main()
