"""把规则问答题库发给"带知识库的 AI"，收集答案，输出 markdown 对照稿。

系统提示与运行时完全一致（角色 + core_rules + current_mode + examples），
因此这份结果代表"当前部署的知识库版本"的真实水平，不是另写一套提示词。

用法：
  python -X utf8 -B mods/tools/quiz_knowledge.py
  python -X utf8 -B mods/tools/quiz_knowledge.py --only q7_tie_first,q8_tie_second_third
输出：mods/ai/eval/quiz_result_<日期>.md
"""
import json
import os
import sys
import time
import urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
AI = os.path.join(ROOT, "mods", "ai")
QUIZ = os.path.join(AI, "eval", "quiz_2026-11-01.json")
ENDPOINT = "https://api.deepseek.com/chat/completions"
MODEL = "deepseek-chat"

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import eval_rules as E  # 复用同一个系统提示组装逻辑


def ask(key, system, question, timeout=90):
    user = ("规则问答：只依据题目给出的条件作答，不自行添加领袖、技术、卡牌或其他加成。"
            "请给出结论、计算或规则依据；信息不足时明确说明缺少什么。\n\n" + question)
    body = json.dumps({
        "model": MODEL,
        "messages": [{"role": "system", "content": system},
                     {"role": "user", "content": user}],
        "max_tokens": 900,
        "temperature": 0.2,
        "stream": False,
    }, ensure_ascii=False).encode("utf-8")
    req = urllib.request.Request(ENDPOINT, data=body, method="POST",
                                 headers={"Content-Type": "application/json",
                                          "Authorization": "Bearer " + key})
    t0 = time.time()
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        data = json.loads(resp.read().decode("utf-8"))
    return data["choices"][0]["message"]["content"], time.time() - t0


def main():
    argv = sys.argv[1:]
    only = None
    if "--only" in argv:
        only = set(argv[argv.index("--only") + 1].split(","))

    key = E.read(os.path.join(AI, "deepseek_key.txt")).strip()
    if not key:
        raise SystemExit("缺少 mods/ai/deepseek_key.txt")

    quiz = json.load(open(QUIZ, encoding="utf-8"))
    system = E.build_knowledge_system()
    print("系统提示 %d 字，题数 %d" % (len(system), len(quiz["questions"])))

    out_path = os.path.join(AI, "eval", "quiz_result_2026-11-01.md")
    if only:
        # 只跑几题时另存，避免覆盖整份结果
        out_path = os.path.join(AI, "eval", "quiz_result_2026-11-01_%s.md" % "-".join(sorted(only)))
    lines = ["# 规则问答 · 知识库 AI 作答", "",
             "系统提示与运行时一致（角色 + core_rules + current_mode + examples），共 %d 字。" % len(system),
             "逐题独立提问（无历史），temperature 0.2。", ""]

    for q in quiz["questions"]:
        if only and q["id"] not in only:
            continue
        print("  提问 %s ..." % q["id"], flush=True)
        try:
            ans, cost = ask(key, system, q["text"])
        except Exception as e:
            ans, cost = "（调用失败：%s）" % e, 0
        lines.append("## %s（%s）" % (q["id"], q["group"]))
        lines.append("")
        lines.append("**问**：" + q["text"])
        lines.append("")
        lines.append("**答**（%.1fs）：" % cost)
        lines.append("")
        lines.append(ans.strip())
        lines.append("")

    open(out_path, "w", encoding="utf-8").write("\n".join(lines))
    print("写出 %s" % out_path)


if __name__ == "__main__":
    main()
