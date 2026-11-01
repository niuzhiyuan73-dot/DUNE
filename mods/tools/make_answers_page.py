"""把教练的全部生成结果导出成一页可读报告（供侧边栏审阅）。

内容：每次请求的建议全文 + 当时的待选 + 实际选择 + 一致标记。
一致判定复用 coach_agreement 的规则（候选名匹配 + 否定守卫不在本页处理，
这里只做"是否指向同一候选"的客观比对，不做对错判断）。

用法：python -X utf8 -B mods/tools/make_answers_page.py [--since "2026-11-01 18:55"]
输出：dump/reports/教练建议-全部.html
"""
import html
import json
import os
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import coach_agreement as A

OUT = os.path.join(ROOT, "dump", "reports", "教练建议-全部.html")


def esc(s):
    return html.escape(s or "", quote=False)


def verdict(r):
    """返回 (建议对象, 实际对象, 标记)：ok / diff / unknown"""
    names = A.candidates(r.get("pending") or "")
    if not names:
        return None, None, "noopt"
    adv = A.first_hit(r.get("answer") or "", names)
    act_raw = r.get("actual") or ""
    act = A.first_hit(act_raw, names) if act_raw.strip() else None
    if not act_raw.strip():
        return adv, None, "noactual"
    if not act:
        return adv, None, "unparsed"
    if not adv:
        return None, act, "noadv"
    return adv, act, ("ok" if adv == act else "diff")


def main():
    since = None
    if "--since" in sys.argv:
        since = sys.argv[sys.argv.index("--since") + 1]

    rows = A.load()
    rows.sort(key=lambda r: r.get("ts") or "")
    if since:
        rows = [r for r in rows if (r.get("ts") or "") >= since]

    stat = {"ok": 0, "diff": 0, "noactual": 0, "unparsed": 0, "noadv": 0, "noopt": 0}
    cards = []
    for r in reversed(rows):  # 最新在前
        adv, act, v = verdict(r)
        stat[v] = stat.get(v, 0) + 1
        badge = {
            "ok": ('<span class="b ok">一致</span>', "建议与实际选择指向同一项"),
            "diff": ('<span class="b diff">不一致</span>', "建议与实际选择不同"),
            "unparsed": ('<span class="b unk">实选未解析</span>', "actual 只有接口名，未还原成候选"),
            "noactual": ('<span class="b unk">无实选</span>', "该条没有 actual（旧版本或配对失败）"),
            "noadv": ('<span class="b unk">建议未定位</span>', "答案里没找到任何候选名"),
            "noopt": ('<span class="b unk">无候选</span>', "待选列表里抽不出候选名"),
        }[v]

        memo = r.get("memory") or ""
        cards.append("""
<article class="card" data-v="%s" data-new="%s">
  <header>
    <span class="seq">#%s</span>
    <span class="ts">%s</span>
    <span class="who">%s</span>
    <span class="meta">%sms · http %s</span>
    %s
  </header>
  <div class="ans">%s</div>
  <div class="cmp">
    <div><b>建议</b> %s</div>
    <div><b>实际</b> %s</div>
    <div class="hint">%s</div>
  </div>
  <details><summary>待选列表（%d 字）</summary><pre>%s</pre></details>
  %s
</article>""" % (
            v, "1" if (r.get("ts") or "") >= "2026-11-01 18:55" else "0", esc(str(r.get("seq"))), esc(r.get("ts")), esc(r.get("player")),
            esc(str(r.get("ms"))), esc(str(r.get("http"))), badge[0],
            esc(r.get("answer") or "（无回答）"),
            esc(adv or "—"), esc(act or (r.get("actual") or "—")[:40]),
            badge[1],
            len(r.get("pending") or ""), esc(r.get("pending") or ""),
            ('<details><summary>本局记忆</summary><pre>%s</pre></details>' % esc(memo)) if memo else "",
        ))

    page = """<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>教练建议 · 全部生成结果（%d 条）</title>
<style>
  body { margin:0; padding:24px 22px 60px; background:#F7F7F5; color:#2C2C2A;
         font-family:-apple-system,"Segoe UI","Microsoft YaHei",system-ui,sans-serif; font-size:13px; line-height:1.65; }
  h1 { font-size:17px; font-weight:600; margin:0 0 4px; }
  .lead { color:#5F5E5A; margin:0 0 10px; }
  .bar { position:sticky; top:0; background:#F7F7F5; padding:8px 0 10px; z-index:5;
         border-bottom:1px solid #E3E2DC; margin-bottom:14px; }
  button { font:inherit; font-size:12px; padding:4px 10px; margin-right:6px; border-radius:7px;
           border:1px solid #D3D1C7; background:#fff; color:#2C2C2A; cursor:pointer; }
  button.on { background:#EEEDFE; border-color:#AFA9EC; color:#3C3489; }
  .stat { color:#5F5E5A; font-size:12px; margin-top:6px; }
  .card { background:#fff; border:1px solid #E3E2DC; border-radius:10px; padding:12px 14px; margin-bottom:12px; }
  .card header { display:flex; flex-wrap:wrap; align-items:center; gap:8px; margin-bottom:8px; }
  .seq { font-family:ui-monospace,Menlo,Consolas,monospace; color:#3C3489; background:#EEEDFE;
         border-radius:6px; padding:1px 7px; font-size:12px; }
  .ts { color:#888780; font-size:12px; font-family:ui-monospace,Menlo,Consolas,monospace; }
  .who { font-weight:500; }
  .meta { color:#888780; font-size:11.5px; }
  .b { margin-left:auto; font-size:11.5px; padding:2px 8px; border-radius:99px; }
  .b.ok { background:#EAF3DE; color:#27500A; }
  .b.diff { background:#FCEBEB; color:#791F1F; }
  .b.unk { background:#F1EFE8; color:#5F5E5A; }
  .ans { white-space:pre-wrap; background:#FAFAF8; border:1px solid #EDECE7; border-radius:8px;
         padding:10px 12px; margin-bottom:8px; }
  .cmp { display:grid; grid-template-columns:1fr 1fr; gap:8px; font-size:12px; color:#444441; }
  .cmp .hint { grid-column:1/-1; color:#888780; font-size:11.5px; }
  details { margin-top:8px; }
  summary { cursor:pointer; color:#185FA5; font-size:12px; }
  pre { margin:6px 0 0; padding:10px 12px; background:#FAFAF8; border:1px solid #EDECE7; border-radius:8px;
        font-family:ui-monospace,Menlo,Consolas,monospace; font-size:11.5px; white-space:pre-wrap;
        word-break:break-word; color:#3C3C3A; max-height:320px; overflow:auto; }
  @media (max-width:700px){ .cmp { grid-template-columns:1fr; } }
</style>
</head>
<body>
<h1>教练建议 · 全部生成结果</h1>
<p class="lead">每条是一次独立请求的完整回答。一致标记只表示“建议与实际选择是否指向同一候选”，
不代表谁对谁错。</p>
<div class="bar">
  <button class="on" data-f="all">全部 %d</button>
  <button data-f="new">仅 build u（18:55 后）%d</button>
  <button data-f="diff">不一致 %d</button>
  <button data-f="ok">一致 %d</button>
  <button data-f="unk">无实选/未解析 %d</button>
  <div class="stat">时间范围 %s → %s　·　共 %d 条　·　生成脚本 mods/tools/make_answers_page.py</div>
</div>
%s
<script>
document.querySelectorAll('.bar button').forEach(function(b){
  b.onclick = function(){
    document.querySelectorAll('.bar button').forEach(function(x){x.classList.remove('on')});
    b.classList.add('on');
    var f = b.dataset.f;
    document.querySelectorAll('.card').forEach(function(c){
      var v = c.dataset.v;
      var show = f === 'all' ? true
        : f === 'new' ? (c.dataset.new === '1')
        : (f === 'unk' ? (v === 'unparsed' || v === 'noactual') : v === f);
      c.style.display = show ? '' : 'none';
    });
  };
});
</script>
</body>
</html>""" % (len(rows), len(rows), sum(1 for r in rows if (r.get("ts") or "") >= "2026-11-01 18:55"),
              stat.get("diff", 0), stat.get("ok", 0),
              stat.get("unparsed", 0) + stat.get("noactual", 0),
              esc(rows[0].get("ts") if rows else ""), esc(rows[-1].get("ts") if rows else ""),
              len(rows), "\n".join(cards))

    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    open(OUT, "w", encoding="utf-8").write(page)
    print("写出 %s（%d 条，%.0f KB）" % (OUT, len(rows), os.path.getsize(OUT) / 1024.0))
    print("统计：一致 %d / 不一致 %d / 实选未解析 %d / 无实选 %d"
          % (stat.get("ok", 0), stat.get("diff", 0), stat.get("unparsed", 0), stat.get("noactual", 0)))


if __name__ == "__main__":
    main()
