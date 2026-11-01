"""从 catalog_probe 快照提取冲突牌三档奖励。

链路（全只读）：
  archetypes.json
    冲突牌原型（900018=="Conflict"）
      10140 = card.conflict.<image>      名字/版本键
      920007 = 冲突等级（I/II/III 对应 1/2/3）
      <某负数属性> = [3 个 ConflictReward 原型 ID]
    奖励原型（900018=="ConflictReward"）
      class 后缀 First/Second/Third      名次
      500022 = card.conflict.<image>.reward.<n>  本地化键（图标序列）

为什么走这条链：按原型 ID 区分版本（同名冲突跨扩展大量重复），
且奖励文本是游戏自己的本地化，比外部卡表准确。

输出：追加到 mods/ai/kb_extra.tsv（GUID 作键），已有条目跳过。
用法：python mods/tools/join_conflicts_catalog.py [--write]
"""
import io
import json
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
sys.path.insert(0, os.path.join(ROOT, "mods", "tools"))
import build_name_map as B
import join_cards_catalog as JC  # 复用图标翻译

MODE = os.path.join(ROOT, "dump", "current_mode")
KB = os.path.join(ROOT, "mods", "ai", "kb_extra.tsv")
EFFECT = os.path.join(ROOT, "mods", "ai", "effect_map.tsv")
CARDS = os.path.join(ROOT, "dump", "cards_runtime.json")

RANK_BY_CLASS = {"First": "1st", "Second": "2nd", "Third": "3rd"}


def main():
    write = "--write" in sys.argv
    global sprites_global
    JC.sprites_global = JC.load_sprites()
    zh = B.load_locale()
    print("图标语义 %d 条" % len(JC.sprites_global))

    ar = json.load(io.open(os.path.join(MODE, "archetypes.json"), encoding="utf-8-sig"))
    by_id = {a["id"]: a for a in ar}

    # image -> GUID（冲突牌唯一键）
    cards = json.load(io.open(CARDS, encoding="utf-8-sig"))
    img2guid = {}
    for c in cards:
        if c.get("image") and c.get("id"):
            img2guid.setdefault(c["image"], c["id"])

    eff_map = {}
    for line in io.open(EFFECT, encoding="utf-8"):
        k, _, v = line.rstrip("\n").partition("\t")
        eff_map[k] = v
    kb_keys = {l.split("\t")[0].strip() for l in io.open(KB, encoding="utf-8")
               if l.strip() and not l.startswith("#")}

    def attrs(a):
        return {x["name"]: x["value"] for x in (a.get("attributes") or [])}

    rows, stats = [], {"total": 0, "have": 0, "built": 0, "partial": 0, "none": 0}
    for a in ar:
        at = attrs(a)
        if at.get(900018) != "Conflict":
            continue
        stats["total"] += 1
        key10140 = at.get(10140)
        if not isinstance(key10140, dict):
            continue
        img = key10140.get("id", "").replace("card.conflict.", "")
        zh_name = zh.get("card.conflict." + img, "")
        guid = img2guid.get(img, "")
        level = at.get(920007)
        if not guid or guid in kb_keys or guid in eff_map:
            stats["have"] += 1
            continue

        # 找「指向 ConflictReward 原型的 GUID 列表」属性（负数 key）
        reward_ids = None
        for k, v in at.items():
            if not isinstance(v, list) or len(v) != 3 or k == 900013:
                continue
            if all(isinstance(x, str) and x in by_id
                   and attrs(by_id[x]).get(900018) == "ConflictReward" for x in v):
                reward_ids = v
                break
        if not reward_ids:
            stats["none"] += 1
            print("  [无奖励关联] %s (%s)" % (zh_name, img))
            continue

        segs = []
        ok = 0
        for rid in reward_ids:
            rat = attrs(by_id[rid])
            cls = a_class = by_id[rid]["class"].split(".")[-1]
            rank = None
            for suf, tag in RANK_BY_CLASS.items():
                if cls.endswith(suf):
                    rank = tag
                    break
            key500022 = rat.get(500022)
            raw = zh.get(key500022.get("id")) if isinstance(key500022, dict) else None
            if not raw:
                continue
            t = JC.icons_to_text(raw)
            if not t:
                t = JC.clean_text(raw)
            if not t:
                continue
            ok += 1
            segs.append("%s：%s" % (rank or "?", t))

        if ok < 3:
            stats["partial"] += 1
            print("  [奖励不全 %d/3] %s (%s)" % (ok, zh_name, img))
            if ok == 0:
                continue
        else:
            stats["built"] += 1

        lv = {"1": "I", "2": "II", "3": "III"}.get(str(level), str(level))
        text = "（%s级冲突）" % lv + "；".join(segs)
        rows.append((guid, zh_name, text))

    print("冲突原型 %d：已有 %d、完整还原 %d、部分 %d、无关联 %d"
          % (stats["total"], stats["have"], stats["built"], stats["partial"], stats["none"]))
    for g, n, t in rows:
        print("  %-12s %s" % (n, t[:72]))

    if write and rows:
        s = io.open(KB, encoding="utf-8").read()
        if not s.endswith("\n"):
            s += "\n"
        s += "\n# 冲突奖励：catalog_probe 原型链（冲突 900018=Conflict -> 奖励原型 500022 文本键）。\n"
        s += "# 按原型 ID 区分版本；名次取自奖励原型类名 First/Second/Third。\n"
        s += "\n".join("%s\t%s\t%s" % (g, t, "冲突奖励原型链") for g, n, t in rows
                      if g and ("name:" + n) not in eff_map and g not in kb_keys) + "\n"
        io.open(KB, "w", encoding="utf-8", newline="\n").write(s)
        print("\n写入；接着跑 python mods/tools/build_effect_map.py")


if __name__ == "__main__":
    main()
