"""
汇总 DeepSeek 观察者（T1）这一局的请求质量。

关注三件事：
  1) 每条请求里还剩下多少「裸 ID」——即 Targets.cs 没能解析成中文名的实体；
  2) 模型有没有因为信息不足而说「无法判断」；
  3) 同一组选项是否被重复问（去重失效的直接证据）。

用法：
  python mods/tools/coach_report.py            # 最新一天的 jsonl
  python mods/tools/coach_report.py --all      # 所有日期
  python mods/tools/coach_report.py --tail 30  # 只看最后 30 条正文
"""
import glob
import io
import json
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
LLM_DIR = os.path.join(ROOT, "dump", "llm")

BARE_ID = re.compile(r"\b[0-9a-f]{8}\b")
UNDECIDED = ("无法判断", "无法确定", "信息不足", "缺少", "请补充")


def load(all_days=False):
    pattern = os.path.join(LLM_DIR, "coach_*.jsonl")
    files = sorted(glob.glob(pattern))
    if not all_days:
        files = files[-1:]
    rows = []
    for f in files:
        with io.open(f, encoding="utf-8") as fh:
            for line in fh:
                line = line.strip()
                if line:
                    try:
                        rows.append(json.loads(line))
                    except ValueError:
                        pass
    return rows


def main():
    args = sys.argv[1:]
    rows = load("--all" in args)
    tail = 0
    if "--tail" in args:
        i = args.index("--tail")
        tail = int(args[i + 1]) if i + 1 < len(args) else 10

    print("样本 %d 条" % len(rows))
    if not rows:
        return

    http_ok = sum(1 for r in rows if 200 <= (r.get("http") or 0) < 300)
    lat = [r.get("ms") or 0 for r in rows if r.get("ms")]
    print("HTTP 2xx: %d/%d   延迟 中位 %.0f ms  最大 %d ms"
          % (http_ok, len(rows), sorted(lat)[len(lat) // 2] if lat else 0, max(lat) if lat else 0))

    undecided = [r for r in rows if any(k in (r.get("answer") or "") for k in UNDECIDED)]
    print("含「无法判断/信息不足」: %d 条" % len(undecided))

    ids = [len(BARE_ID.findall(r.get("pending") or "")) for r in rows]
    if ids:
        print("每条剩余短 ID：中位 %d，最多 %d，总计 %d"
              % (sorted(ids)[len(ids) // 2], max(ids), sum(ids)))

    names = 0
    for r in rows:
        if "(" in (r.get("pending") or "") and ")" in (r.get("pending") or ""):
            names += 1
    print("含「名称(短ID)」格式的请求: %d 条" % names)

    if tail:
        for r in rows[-tail:]:
            print("\n--- #%s %s %sms http=%s" % (r.get("seq"), r.get("player"), r.get("ms"), r.get("http")))
            print("Q:", (r.get("pending") or "")[:600].replace("\n", " "))
            print("A:", (r.get("answer") or "")[:400].replace("\n", " "))


if __name__ == "__main__":
    main()
