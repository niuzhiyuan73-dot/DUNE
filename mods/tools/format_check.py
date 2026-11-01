"""验证「行动卡」输出格式是否真的被遵守。

做法：取日志里真实的待选/局势，用与运行时一致的提示词请求，检查 9 个字段是否齐全、
长度是否受控。不需要开游戏。

用法：
  python -X utf8 -B mods/tools/format_check.py            # 最近 3 条
  python -X utf8 -B mods/tools/format_check.py --n 5
"""
import json
import os
import re
import sys
import time
import urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
AI = os.path.join(ROOT, "mods", "ai")
KNOW = os.path.join(AI, "knowledge")
ENDPOINT = "https://api.deepseek.com/chat/completions"
MODEL = "deepseek-chat"

FIELDS = ["选择", "行动", "打出", "去处", "支付", "获得", "派兵", "本步目标", "缺"]

# 与 Coach.cs 的 BaseSystemPrompt 保持一致
BASE = (
    "你是《沙丘：帝国》(Dune: Imperium) 的教练，职责是告诉玩家这一步**具体怎么操作**，"
    "不是写分析文章。玩家会给你一份游戏原生导出的待选列表（Pending Selection）。"
    "实体已译成「中文名(短ID)」的形式，短 ID 只用于区分同名实体。要求："
    "1) 只讨论给出的选项，不要发明新选项；"
    "2) **必须严格按文末【输出格式】的字段逐行输出**，不写结论段、不写理由段、不做对比分析；"
    "3) 数值只写能确认的，算不准就写「未知」，不要给估算；"
    "4) 用玩家可以直接照着做的话，不要行话（如 兑现、边际、期望值 这类词）；"
    "5) 〔〕里是该牌/该目标的规则说明，｜后面是抽出来的花费与条件；"
    "6) 后文的规则与你的先验冲突时，以规则为准。"
)


def read(p):
    return open(p, encoding="utf-8-sig").read() if os.path.exists(p) else ""


def build_system():
    parts = [BASE,
             read(os.path.join(KNOW, "core_rules.md")).strip(),
             read(os.path.join(KNOW, "current_mode.md")).strip()]
    ex = read(os.path.join(KNOW, "examples.tsv"))
    if ex:
        ls = ["## 已知错误与正确判断（必须遵守）"]
        for t in ex.splitlines():
            if not t or t.startswith("#"):
                continue
            c = t.split("\t")
            if len(c) >= 4:
                ls.append("· %s → 错误说法：%s；正确：%s" % (c[1], c[2], c[3]))
        parts.append("\n".join(ls))
    parts.append(read(os.path.join(KNOW, "output_format.md")).strip())
    return "\n\n".join([p for p in parts if p])


def ask(key, system, user, timeout=90):
    body = json.dumps({"model": MODEL,
                       "messages": [{"role": "system", "content": system},
                                    {"role": "user", "content": user}],
                       "max_tokens": 500, "temperature": 0.2, "stream": False},
                      ensure_ascii=False).encode("utf-8")
    req = urllib.request.Request(ENDPOINT, data=body, method="POST",
                                 headers={"Content-Type": "application/json",
                                          "Authorization": "Bearer " + key})
    t0 = time.time()
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        d = json.loads(resp.read().decode("utf-8"))
    return d["choices"][0]["message"]["content"], time.time() - t0


def main():
    n = 3
    if "--n" in sys.argv:
        n = int(sys.argv[sys.argv.index("--n") + 1])

    key = read(os.path.join(AI, "deepseek_key.txt")).strip()
    if not key:
        raise SystemExit("缺少 mods/ai/deepseek_key.txt")

    # 复用离线评测的候选/机制逻辑
    sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
    import coach_agreement as A

    rows = [r for r in A.load() if r.get("state") and len(r.get("pending") or "") > 200]
    rows.sort(key=lambda r: r.get("ts") or "")
    rows = rows[-n:]

    system = build_system()
    print("系统提示 %d 字（含输出格式）\n" % len(system))

    total_ok = 0
    for r in rows:
        user = "AI 玩家：%s（第 %s 号选择）\n%s\n待选列表：\n%s" % (
            r.get("player"), r.get("seq"), r.get("state"), r.get("pending"))
        try:
            ans, cost = ask(key, system, user)
        except Exception as e:
            print("  调用失败：%s" % e)
            continue
        have = [f for f in FIELDS if re.search(r"^\s*%s\s*[:：]" % f, ans, re.M)]
        ok = len(have) == len(FIELDS)
        total_ok += 1 if ok else 0
        print("=" * 72)
        print("#%s %s  (%.1fs, 长度 %d, 字段 %d/9%s)" % (
            r.get("seq"), r.get("player"), cost, len(ans), len(have), "" if ok else "，缺：" + "、".join(set(FIELDS) - set(have))))
        print(ans.strip())

    print("\n" + "=" * 72)
    print("格式合规：%d/%d 条字段齐全" % (total_ok, len(rows)))


if __name__ == "__main__":
    main()
