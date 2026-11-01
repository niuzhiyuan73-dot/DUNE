"""把 mods/ai/eval/cases.json 渲染成可读的审阅页（供侧边栏预览）。

真实日志题会从 dump/llm/*.jsonl 里取回原始 state / pending，避免只看到 id 猜内容。
输出：docs/题集-规则评测.html

用法：python -X utf8 -B mods/tools/make_cases_page.py
"""
import html
import json
import os

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
CASES = os.path.join(ROOT, "mods", "ai", "eval", "cases.json")
LOG_DIR = os.path.join(ROOT, "dump", "llm")
OUT = os.path.join(ROOT, "docs", "题集-规则评测.html")


def load_log_rows():
    rows = []
    if not os.path.isdir(LOG_DIR):
        return rows
    for fn in sorted(os.listdir(LOG_DIR)):
        if not (fn.startswith("coach_") and fn.endswith(".jsonl")):
            continue
        for line in open(os.path.join(LOG_DIR, fn), encoding="utf-8"):
            line = line.strip()
            if not line:
                continue
            try:
                rows.append(json.loads(line))
            except Exception:
                pass
    return rows


def resolve(case, rows):
    ref = case.get("from_log")
    if not ref:
        return case.get("state", ""), case.get("pending", ""), None
    pref = ref.get("player_prefix", "")
    cands = [r for r in rows if r.get("seq") == ref.get("seq") and str(r.get("player", "")).startswith(pref)]
    if not cands:
        return "", "", "日志里找不到该条"
    r = cands[-1]
    return r.get("state") or "", r.get("pending") or "", r.get("answer") or ""


def esc(s):
    return html.escape(s or "", quote=False)


def chips(items, cls):
    if not items:
        return '<span class="none">（不要求命中，只判“不许说错”）</span>'
    return "".join('<code class="%s">%s</code>' % (cls, esc(i)) for i in items)


def main():
    data = json.load(open(CASES, encoding="utf-8"))
    rows = load_log_rows()
    cases = data["cases"]

    cards = []
    for idx, c in enumerate(cases, 1):
        state, pending, logged = resolve(c, rows)
        kind = "真实日志" if c.get("from_log") else "构造题"
        src = c.get("source", "")
        body = []
        if state:
            body.append('<div class="lbl">局势（state）</div><pre>%s</pre>' % esc(state.strip()))
        if pending:
            body.append('<div class="lbl">待选（pending）</div><pre>%s</pre>' % esc(pending.strip()))
        if c.get("from_log") and not state:
            body.append('<div class="warn">日志样本缺失，评测时会跳过该题。</div>')
        if logged:
            body.append('<div class="lbl">当时模型的原话（基线，供对照）</div><pre class="quote">%s</pre>'
                        % esc(logged.strip()[:600]))

        cards.append("""
<article class="card">
  <header>
    <span class="num">%d</span>
    <h2>%s</h2>
    <span class="tag %s">%s</span>
  </header>
  <p class="note">%s</p>
  <p class="src">%s</p>
  %s
  <div class="judge">
    <div><div class="lbl">必须提到（must_any）</div>%s</div>
    <div><div class="lbl">不许出现（must_not）</div>%s</div>
  </div>
</article>""" % (
            idx, esc(c["id"]), "real" if c.get("from_log") else "mock", kind,
            esc(c.get("note", "")), esc(src),
            "".join(body),
            chips(c.get("must_any") or [], "yes"),
            chips(c.get("must_not") or [], "no"),
        ))

    page = """<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>规则评测题集（%d 道）</title>
<style>
  body { margin:0; padding:28px 24px 64px; background:#F7F7F5; color:#2C2C2A;
         font-family: -apple-system, "Segoe UI", "Microsoft YaHei", system-ui, sans-serif;
         font-size:13px; line-height:1.65; }
  h1 { font-size:18px; font-weight:600; margin:0 0 6px; }
  .lead { color:#5F5E5A; margin:0 0 4px; }
  .meta { color:#888780; font-size:12px; margin:0 0 20px; }
  .rule { background:#fff; border:1px solid #E3E2DC; border-radius:10px;
          padding:14px 16px; margin:0 0 20px; }
  .rule b { font-weight:600; }
  .rule ul { margin:6px 0 0; padding-left:18px; color:#444441; }
  .grid { display:grid; grid-template-columns:repeat(auto-fill, minmax(370px, 1fr)); gap:14px; }
  .card { background:#fff; border:1px solid #E3E2DC; border-radius:10px; padding:14px 16px 16px;
          break-inside:avoid; }
  .card header { display:flex; align-items:center; gap:8px; margin-bottom:6px; }
  .num { flex:none; width:22px; height:22px; border-radius:6px; background:#EEEDFE; color:#3C3489;
         font-size:12px; font-weight:600; display:flex; align-items:center; justify-content:center; }
  .card h2 { font-size:13px; font-weight:600; margin:0; font-family:ui-monospace, Menlo, Consolas, monospace; }
  .tag { margin-left:auto; font-size:11px; padding:2px 7px; border-radius:99px; flex:none; }
  .tag.real { background:#E6F1FB; color:#185FA5; }
  .tag.mock { background:#EAF3DE; color:#3B6D11; }
  .note { margin:4px 0 2px; color:#2C2C2A; font-weight:500; }
  .src { margin:0 0 10px; color:#888780; font-size:12px; }
  .lbl { color:#888780; font-size:11px; margin:10px 0 4px; letter-spacing:.02em; }
  pre { margin:0; padding:10px 12px; background:#FAFAF8; border:1px solid #EDECE7; border-radius:8px;
        font-family:ui-monospace, Menlo, Consolas, monospace; font-size:12px; line-height:1.6;
        white-space:pre-wrap; word-break:break-word; color:#3C3C3A; max-height:260px; overflow:auto; }
  pre.quote { background:#FBF3EF; border-color:#F0DCD2; }
  .judge { display:grid; grid-template-columns:1fr 1fr; gap:10px; margin-top:12px; }
  code { display:inline-block; font-family:ui-monospace, Menlo, Consolas, monospace; font-size:11.5px;
         padding:2px 7px; border-radius:5px; margin:0 4px 4px 0; }
  code.yes { background:#EAF3DE; color:#27500A; border:1px solid #C0DD97; }
  code.no { background:#FCEBEB; color:#791F1F; border:1px solid #F7C1C1; }
  .none { color:#888780; font-size:12px; }
  .warn { background:#FAEEDA; border:1px solid #FAC775; color:#633806; border-radius:8px;
          padding:8px 10px; font-size:12px; margin:8px 0; }
  footer { margin-top:22px; color:#888780; font-size:12px; }
  @media (max-width:760px) { .grid { grid-template-columns:1fr; } .judge { grid-template-columns:1fr; } }
</style>
</head>
<body>
<h1>规则评测题集</h1>
<p class="lead">同一道题、同一份用户消息，只换系统提示（仅角色要求 vs 角色+规则手册），每题跑 3 次。</p>
<p class="meta">来源：mods/ai/eval/cases.json　·　题数 %d　·　真实日志 %d 道 / 构造 %d 道　·　
生成：python -X utf8 -B mods/tools/make_cases_page.py</p>

<div class="rule">
  <b>判分规则</b>
  <ul>
    <li><b>must_any</b>：至少命中一条；留空表示这题只判“不许说错”，不要求必须展开某点。</li>
    <li><b>must_not</b>：一条都不能命中。带否定守卫——匹配点前 6 字含「不/没/非/无」视为否定句，不算踩雷。</li>
    <li>“不许说错”类判据要写得窄，否则会误伤其他选项的正常表述（历史上踩过两次）。</li>
  </ul>
</div>

<div class="grid">%s</div>

<footer>改题：编辑 mods/ai/eval/cases.json 后重新生成本页；只测某几题用
eval_rules.py --only &lt;id&gt;。</footer>
</body>
</html>""" % (len(cases), len(cases),
              sum(1 for c in cases if c.get("from_log")),
              sum(1 for c in cases if not c.get("from_log")),
              "\n".join(cards))

    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    open(OUT, "w", encoding="utf-8").write(page)
    print("写出 %s（%d 道题）" % (OUT, len(cases)))


if __name__ == "__main__":
    main()
