"""把历史日志里的每个局面用「行动卡」格式重新生成，输出新旧对照页。

为什么不直接改渲染：历史 answer 是散文，字段无法从文本反推，只能拿当时的
state + pending 重新请求一次。请求用的系统提示与运行时完全一致。

用法：
  python -X utf8 -B mods/tools/regen_answers.py                 # 今天全部
  python -X utf8 -B mods/tools/regen_answers.py --date 20261031
  python -X utf8 -B mods/tools/regen_answers.py --limit 20      # 先试 20 条
  python -X utf8 -B mods/tools/regen_answers.py --workers 4
输出：
  dump/reports/regen_<date>.jsonl   逐条结果（可断点续跑）
  dump/reports/教练建议-行动卡.html  对照页
"""
import concurrent.futures as cf
import glob
import html
import json
import os
import re
import sys
import time
import urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
AI = os.path.join(ROOT, "mods", "ai")
LLM = os.path.join(ROOT, "dump", "llm")
REPORTS = os.path.join(ROOT, "dump", "reports")
ENDPOINT = "https://api.deepseek.com/chat/completions"
MODEL = "deepseek-chat"
FIELDS = ["选择", "行动", "打出", "去处", "支付", "获得", "派兵", "本步目标", "缺"]

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import format_check as F


def load_rows(date):
    rows = []
    for fn in sorted(glob.glob(os.path.join(LLM, "coach_%s.jsonl" % date))):
        for line in open(fn, encoding="utf-8"):
            line = line.strip()
            if not line:
                continue
            try:
                r = json.loads(line)
            except Exception:
                continue
            if not (r.get("pending") or "").strip():
                continue
            r["_file"] = os.path.basename(fn)
            rows.append(r)
    return rows


def ask(key, system, r):
    user = "AI 玩家：%s（第 %s 号选择）\n%s\n待选列表：\n%s" % (
        r.get("player"), r.get("seq"), r.get("state") or "", r.get("pending") or "")
    body = json.dumps({"model": MODEL,
                       "messages": [{"role": "system", "content": system},
                                    {"role": "user", "content": user}],
                       "max_tokens": 500, "temperature": 0.2, "stream": False},
                      ensure_ascii=False).encode("utf-8")
    req = urllib.request.Request(ENDPOINT, data=body, method="POST",
                                 headers={"Content-Type": "application/json",
                                          "Authorization": "Bearer " + key})
    t0 = time.time()
    with urllib.request.urlopen(req, timeout=120) as resp:
        d = json.loads(resp.read().decode("utf-8"))
    return d["choices"][0]["message"]["content"], time.time() - t0


def fields_of(ans):
    return [f for f in FIELDS if re.search(r"^\s*%s\s*[:：]" % f, ans or "", re.M)]


def main():
    date = "20261101"
    limit = None
    workers = 4
    argv = sys.argv[1:]
    if "--date" in argv:
        date = argv[argv.index("--date") + 1]
    if "--limit" in argv:
        limit = int(argv[argv.index("--limit") + 1])
    if "--workers" in argv:
        workers = int(argv[argv.index("--workers") + 1])

    key = F.read(os.path.join(AI, "deepseek_key.txt")).strip()
    if not key:
        raise SystemExit("缺少 mods/ai/deepseek_key.txt")

    os.makedirs(REPORTS, exist_ok=True)
    cache_path = os.path.join(REPORTS, "regen_%s.jsonl" % date)
    done = {}
    if os.path.exists(cache_path):
        for line in open(cache_path, encoding="utf-8"):
            try:
                d = json.loads(line)
                done[(d["file"], d["seq"], d.get("ts"))] = d
            except Exception:
                pass

    rows = load_rows(date)
    todo = [r for r in rows if (r["_file"], r["seq"], r.get("ts")) not in done]
    if limit:
        todo = todo[:limit]
    print("共 %d 条，已完成 %d，本次要跑 %d（并发 %d）" % (len(rows), len(done), len(todo), workers))

    system = F.build_system()
    print("系统提示 %d 字" % len(system))

    out = open(cache_path, "a", encoding="utf-8")

    def work(r):
        try:
            ans, cost = ask(key, system, r)
        except Exception as e:
            ans, cost = "", -1
            print("  失败 #%s：%s" % (r.get("seq"), e))
        rec = {"file": r["_file"], "seq": r.get("seq"), "ts": r.get("ts"),
               "player": r.get("player"), "old": r.get("answer") or "",
               "new": ans, "ms": int(cost * 1000),
               "actual": r.get("actual") or "", "pending": r.get("pending") or "",
               "state": r.get("state") or "", "memory": r.get("memory") or ""}
        return rec

    t0 = time.time()
    n = 0
    with cf.ThreadPoolExecutor(max_workers=workers) as ex:
        for rec in ex.map(work, todo):
            # 失败（空回答）不落缓存，下次续跑会重试
            if rec["new"]:
                out.write(json.dumps(rec, ensure_ascii=False) + "\n")
                out.flush()
            n += 1
            if n % 20 == 0:
                print("  进度 %d/%d（%.0fs）" % (n, len(todo), time.time() - t0), flush=True)
    out.close()

    # ---- 汇总并渲染 ----
    recs = {}
    for line in open(cache_path, encoding="utf-8"):
        try:
            d = json.loads(line)
            recs[(d["file"], d["seq"], d.get("ts"))] = d
        except Exception:
            pass
    recs = sorted(recs.values(), key=lambda d: (d.get("ts") or "", d.get("seq") or 0), reverse=True)

    ok = sum(1 for d in recs if len(fields_of(d["new"])) == len(FIELDS))
    lens = [len(d["new"]) for d in recs if d["new"]]
    print("已生成 %d 条，字段齐全 %d 条，平均长度 %.0f 字" % (len(recs), ok, sum(lens) / max(1, len(lens))))

    cards = []
    for d in recs:
        have = fields_of(d["new"])
        full = len(have) == len(FIELDS)
        body = d["new"] or "（无输出）"
        cards.append("""
<article class="card %s">
  <header>
    <span class="seq">#%s</span><span class="ts">%s</span><span class="who">%s</span>
    <span class="badge">%s 字段 · %d 字</span>
  </header>
  <pre class="new">%s</pre>
  <details><summary>原建议（改造前）</summary><pre class="old">%s</pre></details>
  <details><summary>当时的待选</summary><pre>%s</pre></details>
</article>""" % ("ok" if full else "bad", html.escape(str(d["seq"])), html.escape(d.get("ts") or ""),
                 html.escape(d.get("player") or ""), "9/9" if full else "%d/9" % len(have),
                 len(body), html.escape(body), html.escape(d["old"]), html.escape(d["pending"])))

    page = """<!DOCTYPE html>
<html lang="zh-CN"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>教练建议 · 行动卡重生成（%d 条）</title>
<style>
 body{margin:0;padding:24px 22px 60px;background:#F7F7F5;color:#2C2C2A;
      font-family:-apple-system,"Segoe UI","Microsoft YaHei",system-ui,sans-serif;font-size:13px;line-height:1.65}
 h1{font-size:17px;font-weight:600;margin:0 0 4px}
 .lead{color:#5F5E5A;margin:0 0 12px}
 .stat{background:#fff;border:1px solid #E3E2DC;border-radius:10px;padding:10px 14px;margin-bottom:14px;font-size:12px;color:#444441}
 .card{background:#fff;border:1px solid #E3E2DC;border-radius:10px;padding:12px 14px;margin-bottom:12px}
 .card.bad{border-color:#F7C1C1;background:#FFFBFB}
 header{display:flex;flex-wrap:wrap;gap:8px;align-items:center;margin-bottom:8px}
 .seq{font-family:ui-monospace,Menlo,Consolas,monospace;color:#3C3489;background:#EEEDFE;border-radius:6px;padding:1px 7px;font-size:12px}
 .ts{color:#888780;font-size:12px;font-family:ui-monospace,Menlo,Consolas,monospace}
 .who{font-weight:500}
 .badge{margin-left:auto;font-size:11.5px;color:#5F5E5A;background:#F1EFE8;border-radius:99px;padding:2px 8px}
 pre{margin:0;padding:10px 12px;background:#FAFAF8;border:1px solid #EDECE7;border-radius:8px;
     font-family:ui-monospace,Menlo,Consolas,monospace;font-size:12px;white-space:pre-wrap;
     word-break:break-word;color:#3C3C3A;max-height:340px;overflow:auto}
 pre.new{background:#F4F8EC;border-color:#DCE8C6}
 pre.old{background:#FBF3EF;border-color:#F0DCD2;color:#5F5E5A}
 details{margin-top:8px}
 summary{cursor:pointer;color:#185FA5;font-size:12px}
</style></head><body>
<h1>教练建议 · 按行动卡重新生成</h1>
<p class="lead">用当时的局势与待选，按新的 9 字段格式重新请求一次；下方保留原建议以便对照。</p>
<div class="stat">共 %d 条　·　字段齐全 %d 条（%.0f%%）　·　平均 %d 字　·　
数据 dump/reports/regen_%s.jsonl</div>
%s
</body></html>""" % (len(recs), len(recs), ok, 100.0 * ok / max(1, len(recs)),
                     int(sum(lens) / max(1, len(lens))), date, "\n".join(cards))

    page_path = os.path.join(REPORTS, "教练建议-行动卡.html")
    open(page_path, "w", encoding="utf-8").write(page)
    print("写出 %s（%.0f KB）" % (page_path, os.path.getsize(page_path) / 1024.0))


if __name__ == "__main__":
    main()
