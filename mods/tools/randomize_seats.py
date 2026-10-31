"""
每次启动游戏时重新随机排座。

背景（实测结论）：
  - 游戏按 accountID 排列玩家，不按 wormPlayers 数组、也不完全按 StartingOrder；
  - 而人类必须留在数组第 0 位，否则会拿到别人的阵营牌组。

所以这里只做一件事：把四个玩家的「座位号」重新洗一遍——
  StartingOrder = seat + 1
  accountID 尾号 = seat
  PlayerColor  = COLORS[seat]
数组顺序完全不动。

用法（由 launch_mod.ps1 在启动前调用）：
  python mods/tools/randomize_seats.py [--seed N] [--quiet]
"""
import json
import os
import random
import sys

import UnityPy

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
BUNDLE = os.path.join(ROOT, "game", "DuneImperium_Data", "StreamingAssets", "aa",
                      "StandaloneWindows64", "b56aa8ad35f9c2e1694994f384e51bbe.bundle")
COLORS = ["Green", "Red", "Yellow", "Blue"]
TA_NAME = "StandardSolo"


def load_doc(env):
    for o in env.objects:
        if o.type.name != "TextAsset":
            continue
        ta = o.read()
        if getattr(ta, "m_Name", "") != TA_NAME:
            continue
        txt = ta.m_Script
        if isinstance(txt, bytes):
            txt = txt.decode("utf-8", "replace")
        return o, ta, json.loads(txt)
    raise SystemExit("剧本包里找不到 StandardSolo")


def main():
    args = sys.argv[1:]
    quiet = "--quiet" in args
    seed = None
    if "--seed" in args:
        i = args.index("--seed")
        if i + 1 < len(args):
            seed = args[i + 1]
    if seed is not None:
        random.seed(seed)

    if not os.path.exists(BUNDLE):
        raise SystemExit("找不到剧本包：" + BUNDLE)

    env = UnityPy.load(BUNDLE)
    obj, ta, doc = load_doc(env)

    players = doc.get("wormPlayers") or []
    if len(players) != 4:
        raise SystemExit(f"玩家数不是 4（{len(players)}），不做随机排座")

    seats = [0, 1, 2, 3]
    random.shuffle(seats)

    for i, p in enumerate(players):
        seat = seats[i]
        p["StartingOrder"] = seat + 1
        p["PlayerColor"] = COLORS[seat % len(COLORS)]
        aid = p.get("accountID") or ""
        if len(aid) >= 2:
            p["accountID"] = aid[:-2] + f"{seat:02d}"

    ta.m_Script = json.dumps(doc, ensure_ascii=False)
    ta.save()
    with open(BUNDLE, "wb") as f:
        f.write(env.file.save(packer="lz4"))

    human = next((p for p in players if p.get("isHuman")), None)
    if not quiet:
        order = [(i, p["StartingOrder"], p["PlayerColor"]) for i, p in enumerate(players)]
        print("[seats] 本次座位：" + "  ".join(f"#{i}->{o}({c})" for i, o, c in order))
        if human:
            print(f"[seats] 你是第 {human['StartingOrder']} 位行动（{human['PlayerColor']}）")


if __name__ == "__main__":
    main()
