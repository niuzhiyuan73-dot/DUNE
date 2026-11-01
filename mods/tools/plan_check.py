"""验证「本回合行动链」格式：字段齐全、无条件句、无「下一步」、步骤数受控。

只取日志里 Kind=PlayerTurn 的记录（回合起点）回放，因为这些才是真正会提问的时刻。
不依赖游戏运行。

用法：python -X utf8 -B mods/tools/plan_check.py [--n 5]
"""
import json
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import coach_agreement as A
import format_check as F

BLOCKS = ["本回合行动", "派兵", "保留", "本回合目的", "预计收益", "缺"]
BAN_COND = re.compile(r"[若如]果|若[^，。\n]{0,10}|则|万一|一旦")


def check(ans):
    problems = []
    for b in BLOCKS:
        if not re.search(r"^\s*%s\s*[:：]" % b, ans, re.M):
            problems.append("缺字段「%s」" % b)
    # 行动段（从「本回合行动」到下一个块）里不许有条件句
    m = re.search(r"本回合行动[:：]([\s\S]*?)(?=\n\s*(?:%s)\s*[:：])" % "|".join(BLOCKS[1:]), ans)
    seg = m.group(1) if m else ans
    hits = [w for w in ("若", "如果", "则", "万一", "一旦") if w in seg]
    if hits:
        problems.append("行动段含条件词：%s" % "、".join(hits))
    if "下一步" in ans:
        problems.append("出现「下一步」（打牌不该单列）")
    steps = len(re.findall(r"^\s*\d+\s*[)）]", ans, re.M))
    if steps == 0:
        problems.append("没有编号步骤")
    if steps > 5:
        problems.append("步骤过多（%d）" % steps)
    if len(ans) > 320:
        problems.append("过长（%d 字）" % len(ans))
    return problems, steps


def main():
    n = 5
    if "--n" in sys.argv:
        n = int(sys.argv[sys.argv.index("--n") + 1])

    key = F.read(os.path.join(ROOT, "mods", "ai", "deepseek_key.txt")).strip()
    rows = [r for r in A.load() if "Kind PlayerTurn" in (r.get("pending") or "") and r.get("state")]
    rows.sort(key=lambda r: r.get("ts") or "")
    rows = rows[-n:]

    system = F.build_system()
    print("系统提示 %d 字（行动链版）\n" % len(system))

    ok = 0
    for r in rows:
        user = "AI 玩家：%s（第 %s 号选择）\n%s\n待选列表：\n%s" % (
            r.get("player"), r.get("seq"), r.get("state"), r.get("pending"))
        ans, cost = F.ask(key, system, user)
        problems, steps = check(ans)
        ok += 0 if problems else 1
        print("=" * 72)
        print("#%s %s（%.1fs，%d 步，%d 字%s）" % (
            r.get("seq"), r.get("player"), cost, steps, len(ans),
            "" if not problems else "，问题：" + "；".join(problems)))
        print(ans.strip())

    print("\n" + "=" * 72)
    print("格式合规：%d/%d" % (ok, len(rows)))


if __name__ == "__main__":
    main()
