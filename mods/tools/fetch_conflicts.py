"""
抓取冲突牌奖励（1/2/3 名），产出可直接并入 mods/ai/kb_extra.tsv 的行。

现状：**未跑通**，留作后续接手用。已确认的坑：
  1. dunecardshub 的 /cards/<id>/<slug> 里，slug 与内容并不总对应。
     例如 /cards/438/dune-imperium-siege-of-carthag 返回的其实是 Grand Vision，
     所以按「英文名 -> slug 后缀匹配」批量取会取错页，必须校验页面内的卡名再采纳。
  2. 页面正文是 JS 渲染的，可靠信息在 <meta name="description">：
     "X is a conflict card in Dune Imperium. X rewards (Conflict II)1st place: …2nd place: …3rd place: …"
     解析这一段最稳，但要先确认 description 里的卡名 == 目标卡名。
  3. 同名冲突跨扩展重复（Skirmish ×4、Siege of Arrakeen ×2 等），必须用
     cards_runtime.json 的 sets 字段 + 页面卡名双重校验，不能只靠名字。

用法（修好后）：python mods/tools/fetch_conflicts.py --verify
输出：dump/tmp/conf_rows.tsv（GUID<TAB>奖励<TAB>来源URL），人工抽查后再并进 kb_extra.tsv。
"""
import html
import json
import os
import re
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
sys.path.insert(0, os.path.join(ROOT, "mods", "tools"))

import build_name_map as B

SITEMAP = os.path.join(ROOT, "dump", "tmp", "sm.xml")
CARDS = os.path.join(ROOT, "dump", "cards_runtime.json")
OUT = os.path.join(ROOT, "dump", "tmp", "conf_rows.tsv")

MAP = [("Victory Points", "胜利点"), ("Victory Point", "胜利点"), ("water", "水"), ("spice", "香料"),
       ("Solari", "太阳币"), ("troops", "部队"), ("troop", "部队"), ("Influence", "影响力"),
       ("Intrigue cards", "阴谋牌"), ("Intrigue card", "阴谋牌"), ("draw a card", "抽1张牌"),
       ("Control marker", "控制标记"), ("gain", "获得"), (" and ", "、")]


def norm(s):
    return re.sub(r"[^a-z0-9]+", "-", s.lower()).strip("-")


def zh_reward(s):
    parts = re.split(r"(?=(?:1st|2nd|3rd) place:)", re.sub(r"\s+", " ", s).strip())
    out = []
    for p in parts:
        m = re.match(r"(1st|2nd|3rd) place:/s*(.*)", p.strip())
        if not m:
            continue
        body = m.group(2)
        for a, b in MAP:
            body = body.replace(a, b)
        out.append(m.group(1) + "：" + body.strip(" .,"))
    return "；".join(out)


def main():
    en = B.load_locale("en_US")
    zh = B.load_locale()
    cards = json.load(open(CARDS, encoding="utf-8-sig"))
    img2card = {}
    for c in cards:
        if c.get("image"):
            img2card.setdefault(c["image"], []).append(c)

    sm = open(SITEMAP, encoding="utf-8").read()
    slugs = {}
    for u in re.findall(r"<loc>([^<]+)</loc>", sm):
        m = re.search(r"/cards/\d+/(.+)$", u)
        if m:
            slugs.setdefault(m.group(1), u)

    conf = {k.split(".")[2]: v.strip() for k, v in en.items()
            if k.startswith("card.conflict.") and k.count(".") == 2}
    conf_zh = {k.split(".")[2]: v.strip() for k, v in zh.items()
               if k.startswith("card.conflict.") and k.count(".") == 2}

    rows = []
    for img, en_name in conf.items():
        if len(en_name) < 4:
            continue
        cands = [(s, u) for s, u in slugs.items() if s.endswith(norm(en_name))]
        for _, url in cands:
            body = subprocess.run(["curl", "-s", "--max-time", "25", url],
                                  capture_output=True).stdout.decode("utf-8", "replace")
            m = re.search(r'<meta name="description" content="([^"]*)"', body)
            if not m:
                continue
            desc = html.unescape(m.group(1))
            # 关键校验：页面里的卡名必须与目标一致，否则换页
            if not desc.lower().startswith(en_name.lower()):
                continue
            i = desc.find("rewards")
            if i < 0:
                continue
            reward = zh_reward(desc[i + 7:])
            if not reward:
                continue
            gid = img2card.get(img, [{}])[0].get("id")
            if gid:
                rows.append((gid, reward, url))
                print(f"{conf_zh.get(img, img):12s} {reward[:70]}")
            break

    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    with open(OUT, "w", encoding="utf-8", newline="\n") as f:
        for a, b, c in rows:
            f.write(f"{a}\t{b}\t{c}\n")
    print(f"写出 {len(rows)} 条冲突奖励 -> {OUT}")


if __name__ == "__main__":
    main()
