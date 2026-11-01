"""固定题集评测：接入规则手册前后，规则性错误率有没有下降。

做法：
  - 同一批题、同一个用户消息，只有「系统提示」不同：
      baseline  = 改造前的提示词（只有角色要求，见 BASELINE_SYSTEM）
      knowledge = 角色 + core_rules.md + current_mode.md + examples.tsv（+ 按需机制）
  - 每题给 must_any / must_not 两个判据：命中前者且不踩后者才算通过。
  - 输出逐题对比与错误率，失败项打印模型原话便于人工复核。

用法：
  python -X utf8 -B mods/tools/eval_rules.py            # 全部题
  python -X utf8 -B mods/tools/eval_rules.py --only propaganda_pick_two,tie_for_first
  python -X utf8 -B mods/tools/eval_rules.py --repeat 2 # 每题跑 2 次看稳定性

注意：会真实调用 DeepSeek（读 mods/ai/deepseek_key.txt），8 题 × 2 版 = 16 次请求。
"""
import io
import json
import os
import re
import sys
import time
import urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
AI = os.path.join(ROOT, "mods", "ai")
KNOW = os.path.join(AI, "knowledge")
CASES = os.path.join(AI, "eval", "cases.json")
LOG_DIR = os.path.join(ROOT, "dump", "llm")
ENDPOINT = "https://api.deepseek.com/chat/completions"
MODEL = "deepseek-chat"

# 改造前的系统提示（逐字取自 e329a3e 之前的 Coach.cs）
BASELINE_SYSTEM = (
    "你是《沙丘：帝国》(Dune: Imperium) 的资深教练。玩家会给你一份游戏原生导出的待选列表"
    "（Pending Selection，含有 Kind 与若干 Buttons，按钮里带标题、费用等属性）。"
    "实体已译成「中文名(短ID)」的形式，短 ID 只用于区分同名实体，回答时直接用中文名即可。"
    "请判断哪一项对最终胜利最有利，并给出 2-3 句理由。要求："
    "1) 只讨论给出的选项，不要发明新选项；2) 优先考虑胜利点与后期兑现能力，"
    "而不是短期资源堆叠；3) 用中文，先给结论（选项标题或序号），再给理由。"
    "4) 若某项信息确实缺失（例如只给了 ID 没有名字），明确说缺什么，不要臆测牌名。"
    "5) 〔〕里是该牌/该目标的规则说明；选项与子目标都带〔〕。请据此比较，"
    "注意有些牌的费用、条件、次数限制会直接决定它此刻值不值得打。"
)

# 与 Coach.cs 的 BaseSystemPrompt 保持一致
BASE_SYSTEM = (
    "你是《沙丘：帝国》(Dune: Imperium) 的教练，熟悉官方规则书与本次自定义剧本。"
    "玩家会给你一份游戏原生导出的待选列表"
    "（Pending Selection，含有 Kind 与若干 Buttons，按钮里带标题、费用等属性）。"
    "实体已译成「中文名(短ID)」的形式，短 ID 只用于区分同名实体，回答时直接用中文名即可。"
    "请判断哪一项对最终胜利最有利，并给出 2-3 句理由。要求："
    "1) 只讨论给出的选项，不要发明新选项；2) 优先考虑胜利点与后期兑现能力，"
    "而不是短期资源堆叠；3) 用中文，先给结论（选项标题或序号），再给理由。"
    "4) 若某项信息确实缺失，明确说缺什么，不要臆测牌名或数值。"
    "5) 〔〕里是该牌/该目标的规则说明，｜后面是抽出来的花费与条件。"
    "6) 下面是本局生效的规则与已知错误示例；与你的先验冲突时，以下文为准。"
)

MAX_MECHANICS = 3


def read(p):
    return open(p, encoding="utf-8").read() if os.path.exists(p) else ""


def build_knowledge_system():
    sb = [BASE_SYSTEM]
    core = read(os.path.join(KNOW, "core_rules.md")).strip()
    if core:
        sb.append(core)
    mode = read(os.path.join(KNOW, "current_mode.md")).strip()
    if mode:
        sb.append(mode)
    ex = read(os.path.join(KNOW, "examples.tsv"))
    if ex:
        lines = ["## 已知错误与正确判断（必须遵守）"]
        for t in ex.splitlines():
            if not t or t.startswith("#"):
                continue
            c = t.split("\t")
            if len(c) < 4:
                continue
            lines.append("· %s → 错误说法：%s；正确：%s" % (c[1], c[2], c[3]))
        sb.append("\n".join(lines))
    return "\n\n".join(sb)


def load_mechanics():
    out = []
    for t in read(os.path.join(KNOW, "mechanics.tsv")).splitlines():
        if not t or t.startswith("#"):
            continue
        c = t.split("\t")
        if len(c) < 5:
            continue
        keys = [k.strip() for k in c[2].split("|") if len(k.strip()) >= 2]
        if not keys:
            continue
        text = c[3]
        if c[4].strip() and len(c[4]) <= 140:
            text = text + "（" + c[4] + "）"
        out.append((keys, c[1] + "：" + text))
    return out


def match_mechanics(hay, mechs):
    scored = []
    for i, (keys, text) in enumerate(mechs):
        sc = sum(1 for k in keys if k in hay)
        if sc:
            scored.append((-sc, i, text))
    scored.sort()
    return [t for _, _, t in scored[:MAX_MECHANICS]]


def load_log_rows():
    rows = []
    for fn in sorted(os.listdir(LOG_DIR)):
        if not (fn.startswith("coach_") and fn.endswith(".jsonl")):
            continue
        for line in open(os.path.join(LOG_DIR, fn), encoding="utf-8"):
            line = line.strip()
            if line:
                try:
                    rows.append(json.loads(line))
                except Exception:
                    pass
    return rows


def resolve_case(case, rows):
    """把 from_log 的引用换成真实 state/pending。"""
    ref = case.get("from_log")
    if ref:
        pref = ref.get("player_prefix", "")
        cands = [r for r in rows if r.get("seq") == ref.get("seq") and str(r.get("player", "")).startswith(pref)]
        if not cands:
            return None
        r = cands[-1]
        return r.get("state") or "", r.get("pending") or ""
    return case.get("state", ""), case.get("pending", "")


def build_user(state, pending, mechs):
    user = "AI 玩家：测试者（第 1 号选择）\n"
    if state:
        user += state + "\n"
    m = match_mechanics(pending + "\n" + state, mechs)
    if m:
        user += "【相关机制】\n" + "".join("· " + t + "\n" for t in m)
    user += "待选列表：\n" + pending
    return user


def ask(key, system, user, timeout=60):
    body = json.dumps({
        "model": MODEL,
        "messages": [{"role": "system", "content": system},
                     {"role": "user", "content": user}],
        "max_tokens": 400,
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


NEG = "不非没无别勿"


def _negated(text, m):
    """判断某处匹配是否处在否定语境里：往前 6 字内出现否定词即视为「说的是相反的话」。
    例：『不会永久控制』不应判为踩雷『永久控制』。"""
    s = max(0, m.start() - 6)
    return any(ch in NEG for ch in text[s:m.start()])


def judge(text, case):
    """must_any 为空表示这道题只判"不许说错"，不要求必须展开某个点。"""
    want = case.get("must_any") or []
    hit = [p for p in want if re.search(p, text)]
    bad = []
    for p in case["must_not"]:
        ms = list(re.finditer(p, text))
        if not ms:
            continue
        # 只要有一处是"未被否定的断言"，才算踩雷
        if any(not _negated(text, m) for m in ms):
            bad.append(p)
    if not want:
        hit = ["(不限)"]
    return (bool(hit) and not bad), hit, bad


def main():
    argv = sys.argv[1:]
    only = None
    if "--only" in argv:
        only = set(argv[argv.index("--only") + 1].split(","))
    repeat = 1
    if "--repeat" in argv:
        repeat = int(argv[argv.index("--repeat") + 1])

    key = read(os.path.join(AI, "deepseek_key.txt")).strip()
    if not key:
        raise SystemExit("缺少 mods/ai/deepseek_key.txt")

    cases = json.load(open(CASES, encoding="utf-8"))["cases"]
    rows = load_log_rows()
    mechs = load_mechanics()
    sys_new = build_knowledge_system()

    print("=" * 76)
    print("规则手册接入评测")
    print("  基线系统提示 %d 字 / 接入后 %d 字" % (len(BASELINE_SYSTEM), len(sys_new)))
    print("  机制索引 %d 条，题库 %d 道，每题 %d 次" % (len(mechs), len(cases), repeat))
    print("=" * 76)

    detail = []
    for case in cases:
        if only and case["id"] not in only:
            continue
        got = resolve_case(case, rows)
        if not got:
            print("[跳过] %s：日志里找不到原始请求" % case["id"])
            continue
        state, pending = got

        rec = {"id": case["id"], "note": case["note"], "source": case.get("source", ""),
               "baseline": [], "knowledge": []}
        for variant, sysprompt in (("baseline", BASELINE_SYSTEM), ("knowledge", sys_new)):
            user = build_user(state, pending, mechs if variant == "knowledge" else [])
            for i in range(repeat):
                try:
                    ans, cost = ask(key, sysprompt, user)
                except Exception as e:
                    rec[variant].append({"ok": False, "answer": "", "err": str(e), "ms": 0})
                    continue
                ok, hit, bad = judge(ans, case)
                rec[variant].append({"ok": ok, "answer": ans, "hit": hit, "bad": bad,
                                     "ms": int(cost * 1000)})
        detail.append(rec)
        b = rec["baseline"]
        k = rec["knowledge"]
        print("\n[%s] %s" % (case["id"], case["note"]))
        print("  基线   %s" % "".join("PASS " if x["ok"] else "FAIL " for x in b))
        print("  接入后 %s" % "".join("PASS " if x["ok"] else "FAIL " for x in k))
        for x in b:
            if not x["ok"]:
                print("    基线失分：踩雷 %s / 命中 %s" % (x.get("bad"), x.get("hit")))
                print("      %s" % (x["answer"] or x.get("err", ""))[:220].replace("\n", " "))
        for x in k:
            if not x["ok"]:
                print("    接入后失分：踩雷 %s / 命中 %s" % (x.get("bad"), x.get("hit")))
                print("      %s" % (x["answer"] or x.get("err", ""))[:220].replace("\n", " "))

    if not detail:
        raise SystemExit("没有可评测的题")

    def rate(key_):
        allr = [x for rec in detail for x in rec[key_]]
        ok = sum(1 for x in allr if x["ok"])
        return ok, len(allr)

    print("\n" + "=" * 76)
    bo, bn = rate("baseline")
    ko, kn = rate("knowledge")
    print("基线    通过 %d/%d  (正确率 %.0f%%)" % (bo, bn, 100.0 * bo / max(1, bn)))
    print("接入后  通过 %d/%d  (正确率 %.0f%%)" % (ko, kn, 100.0 * ko / max(1, kn)))
    print("=" * 76)

    out = os.path.join(AI, "eval", "last_run.json")
    json.dump({"baseline": [bo, bn], "knowledge": [ko, kn], "detail": detail},
              open(out, "w", encoding="utf-8"), ensure_ascii=False, indent=1)
    print("明细已写入 %s" % out)
    return 0


if __name__ == "__main__":
    sys.exit(main())
