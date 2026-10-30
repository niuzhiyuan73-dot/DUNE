"""构建 v2c：在 v2a 的对称配置基础上，把条目改成自定义名称（独立显示）。

做法：
1) 剧本 JSON 的 Category / Description / FlavorText 指向自定义本地化键
2) 往 win/localization 的每个语言表追加这三个键
产出两个文件：剧本 bundle + 本地化 bundle
"""
import os
import json
import copy
import UnityPy
from patch_scenario import patch, verify

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
BASE = os.path.join(ROOT, "mods", "_baseline", "Campaign01.original.json")
OUT_DIR = os.path.join(ROOT, "mods", "out")
VAR_DIR = os.path.join(ROOT, "mods", "v2c")

BUNDLE = "b56aa8ad35f9c2e1694994f384e51bbe.bundle"
TA = "Campaign01"
LOC_SRC = r"C:\Users\17789\Desktop\DUNE\game\DuneImperium_Data\StreamingAssets\Localization\win\localization"
LOC_OUT = os.path.join(OUT_DIR, "v2c_localization_win")

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
NAME_KEY = "scenariomod.spicetide.name"
DESC_KEY = "scenariomod.spicetide.desc"
FLAVOR_KEY = "scenariomod.spicetide.flavor"
TEXTS = {
    NAME_KEY: "香料洪流",
    DESC_KEY: "三方的开局条件完全相同：10 张同款起手牌、3 名特工、开局即有剑术大师，并携带 3 太阳币 / 2 香料 / 2 水。胜利点阈值提高到 15。",
    FLAVOR_KEY: "谁掌控香料，谁就掌控沙丘。",
}


def build_doc():
    with open(BASE, encoding="utf-8") as f:
        doc = json.load(f)
    doc["EndgameTriggerScore"] = 15
    for p in doc["wormPlayers"]:
        p["StartingDeck"] = list(SHARED_DECK)
        p["StartingAgents"] = 3
        p["PlayerMatchSoloOptions"].update(BONUS)
    doc["Category"] = NAME_KEY
    doc["Description"] = DESC_KEY
    doc["FlavorText"] = FLAVOR_KEY
    return doc


def build_localization():
    env = UnityPy.load(LOC_SRC)
    anchor = "scenario.ImperialBeneficence.name="
    touched = 0
    for o in env.objects:
        if o.type.name != "TextAsset":
            continue
        ta = o.read()
        t = ta.m_Script
        if isinstance(t, bytes):
            t = t.decode("utf-8", "replace")
        if anchor not in t:
            continue
        if NAME_KEY + "=" in t:
            continue
        extra = "".join(f"\n{k}={v}" for k, v in TEXTS.items())
        ta.m_Script = t.rstrip("\n") + extra + "\n"
        ta.save()
        touched += 1
    if touched == 0:
        raise SystemExit("没有命中任何语言表，键名可能不匹配")
    os.makedirs(os.path.dirname(LOC_OUT), exist_ok=True)
    with open(LOC_OUT, "wb") as f:
        f.write(env.file.save(packer="lz4"))
    print(f"本地化已改写 {touched} 张语言表 -> {LOC_OUT} ({os.path.getsize(LOC_OUT)} B)")
    return LOC_OUT


def verify_loc(path):
    env = UnityPy.load(path)
    ok = 0
    for o in env.objects:
        if o.type.name != "TextAsset":
            continue
        ta = o.read()
        t = ta.m_Script
        if isinstance(t, bytes):
            t = t.decode("utf-8", "replace")
        for line in t.splitlines():
            if line.startswith(NAME_KEY + "="):
                ok += 1
                print(f"  回读: {line}")
                break
    print(f"含自定义名的语言表: {ok} 张")


if __name__ == "__main__":
    doc = build_doc()
    os.makedirs(VAR_DIR, exist_ok=True)
    jp = os.path.join(VAR_DIR, "scenario.json")
    with open(jp, "w", encoding="utf-8") as f:
        json.dump(doc, f, ensure_ascii=False, indent=1)
    with open(os.path.join(VAR_DIR, "meta.json"), "w", encoding="utf-8") as f:
        json.dump({
            "label": "v2c 对称强化 + 自定义条目名",
            "note": "占用 Campaign01 槽位，但显示名/描述改为自定义本地化键，需同时替换本地化 bundle",
            "target_bundle": BUNDLE,
            "textasset": TA,
            "extra_file": "DuneImperium_Data/StreamingAssets/Localization/win/localization",
            "localization_keys": TEXTS,
        }, f, ensure_ascii=False, indent=1)

    out = patch(jp, BUNDLE, TA, os.path.join(OUT_DIR, "v2c_" + BUNDLE))
    verify(out, TA, [])
    loc = build_localization()
    verify_loc(loc)
