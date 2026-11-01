# -*- coding: utf-8 -*-
"""把教练的每条输出整理成可审阅的页面。

每条 = 一次独立请求：阶段、建议全文、当时局势、当时待选（含可选目标与效果）、实选与判定。
按时间正序排列，便于顺着局势读决策链。

用法：
  python -X utf8 -B mods/tools/make_answers_page.py                       # 今天全部
  python -X utf8 -B mods/tools/make_answers_page.py --since "2026-11-01 21:00"
  python -X utf8 -B mods/tools/make_answers_page.py --out dump/reports/x.html
"""
import glob
import html
import json
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
OUT = os.path.join(ROOT, "dump", "reports", "教练建议-行动卡.html")


def esc(s):
    return html.escape(str(s or ""))


def load(since=None):
    rows = []
    for fn in sorted(glob.glob(os.path.join(ROOT, "dump", "llm", "coach_*.jsonl"))):
        for line in open(fn, encoding="utf-8"):
            line = line.strip()
            if not line:
                continue
            try:
                r = json.loads(line)
            except Exception:
                continue
            if since and (r.get("ts") or "") < since:
                continue
            rows.append(r)
    rows.sort(key=lambda r: (r.get("ts") or "", r.get("seq") or 0))
    return rows


def stage(r):
    """与 Turn.Classify 对齐：阶段标签。"""
    p = r.get("pending") or ""
    m = re.search(r"Kind (\w+)", p)
    kind = m.group(1) if m else ""
    m = re.search(r"Prompt (.+?)。", p)
    prompt = m.group(1) if m else ""
    if kind == "Combat" or "打出一张密牌" in prompt or "使用一项能力，或跳过" in prompt:
        return "战斗"
    if "打出一张牌" in prompt:
        return "打出"
    if "进入展示阶段" in prompt:
        return "展示确认"
    if kind == "RevealTurn" or "使用一项能力或获取一张牌" in prompt or "选择一项能力来使用" in prompt:
        return "展示"
    if kind == "AssignLeader":
        return "指派领袖"
    return "行动内"


def candidates(pending):
    """从待选里抽候选名。两种格式都要覆盖：
       1) 目标明细行：`- 名字(短ID)〔效果〕：…`
       2) 指派领袖的按钮：`Buttons: [10140-龙伯·维尔涅斯王子] [10000-名字(929a377a)]`
    """
    names = []
    p = pending or ""
    for line in p.split("\n"):
        if not line.startswith("- "):
            continue
        body = re.sub(r"\([0-9a-f]{8}\)", "", line[2:])
        body = body.split("〔")[0].split("：")[0].strip()
        if body and body not in names:
            names.append(body)

    for m in re.finditer(r"\[\d{3,6}-([^\]\[]+)\]", p):
        nm = re.sub(r"\([0-9a-f]{8}\)", "", m.group(1)).strip()
        if not nm or nm in names:
            continue
        # 过滤程序集/枚举这类非名字按钮
        if any(t in nm for t in ("System.", "Canis.", "worm.", "True", "False")):
            continue
        if re.fullmatch(r"[\d.\-]+", nm):
            continue
        names.append(nm)
    return names


def first_hit(text, names):
    best, pos = None, 10 ** 9
    for n in names:
        i = (text or "").find(n)
        if i >= 0 and i < pos:
            best, pos = n, i
    return best


def verdict(r):
    """一致判定：只比较“指向同一候选”，不谈对错。"""
    names = candidates(r.get("pending"))
    actual = (r.get("actual") or "").strip()
    adv = first_hit(r.get("answer"), names)
    if not actual:
        return adv, None, "noactual"
    if actual.startswith("Networking.") or "接口代理" in actual:
        return adv, None, "unparsed"
    if not names:
        return adv, None, "noopt"
    if not adv:
        return adv, actual, "noadv"
    act = first_hit(actual, names)
    if not act:
        return adv, actual, "unparsed"
    return adv, act, ("ok" if act == adv else "diff")


BADGE = {
    "ok": ('<span class="b ok">一致</span>', "建议与实际选择指向同一项（不代表建议正确）"),
    "diff": ('<span class="b diff">不一致</span>', "建议与实际选择不同"),
    "unparsed": ('<span class="b unk">实选未解析</span>', "actual 只有接口名或不在候选里"),
    "noactual": ('<span class="b unk">无实选</span>', "该条没有 actual（旧版本或配对失败）"),
    "noadv": ('<span class="b unk">建议未定位</span>', "答案里没找到任何候选名"),
    "noopt": ('<span class="b unk">无候选</span>', "待选列表里抽不出候选名"),
}

BADGE_TEXT = {"ok": "一致", "diff": "不一致", "unparsed": "实选未解析",
              "noactual": "无实选", "noadv": "建议未定位", "noopt": "无候选"}

STAGE_CLASS = {"打出": "k0", "展示": "k1", "展示确认": "k2", "战斗": "k3", "指派领袖": "k2", "行动内": "k0"}


def build_html(rows):
    stat = {}
    stage_cnt = {}
    cards = []
    # 局号：每次「指派领袖」视为新一局（seq 每局从 1 重数，单看 seq 会混淆）
    game_no = 0
    for r in rows:
        if stage(r) == "指派领袖":
            game_no += 1
        adv, act, v = verdict(r)
        stat[v] = stat.get(v, 0) + 1
        st = stage(r)
        stage_cnt[st] = stage_cnt.get(st, 0) + 1
        badge_html, badge_hint = BADGE[v]

        memo = r.get("memory") or ""
        state = r.get("state") or ""
        pending = r.get("pending") or ""

        cards.append("""
<article class="card" data-v="{v}" data-s="{st}">
  <header>
    <span class="g">第{g}局</span>
    <span class="seq">#{seq}</span>
    <span class="k {kcls}">{st}</span>
    <span class="ts">{ts}</span>
    <span class="who">{who}</span>
    <span class="meta">{ms}ms · http {http} · 待选 {plen} 字</span>
    {badge}
  </header>
  <div class="ans">{ans}</div>
  <div class="cmp">
    <div><b>建议对象</b><br>{adv}</div>
    <div><b>实际选择</b><br>{act}</div>
    <div class="hint">{hint}</div>
  </div>
  <details><summary>当时局势（{slen} 字）</summary><pre>{state}</pre></details>
  <details><summary>当时待选（{plen} 字，含可选目标与效果）</summary><pre>{pending}</pre></details>
  {memo}
</article>""".format(
            v=v, st=st, g=max(game_no, 1), kcls=STAGE_CLASS.get(st, "k0"),
            seq=esc(r.get("seq")), ts=esc((r.get("ts") or "")[11:]),
            who=esc(r.get("player")),
            ms=esc(r.get("ms")), http=esc(r.get("http")), plen=len(pending),
            badge=badge_html,
            ans=esc(r.get("answer") or "（无回答）"),
            adv=esc(adv or "—"), act=esc(act or "—"), hint=esc(badge_hint),
            slen=len(state), state=esc(state), pending=esc(pending),
            memo=('<details><summary>本局记忆</summary><pre>%s</pre></details>' % esc(memo)) if memo else "",
        ))

    total = len(rows)
    stat_line = "、".join("%s %d" % (BADGE_TEXT[k], stat[k]) for k in BADGE_TEXT if stat.get(k))
    stage_btns = "".join('<button data-f="s:%s">%s %d</button>' % (s, s, n) for s, n in stage_cnt.items())

    return """<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>教练建议 · 本轮 %(total)d 条</title>
<style>
  body{margin:0;padding:22px 20px 60px;background:#F7F7F5;color:#2C2C2A;
       font-family:-apple-system,"Segoe UI","Microsoft YaHei",system-ui,sans-serif;font-size:13px;line-height:1.65}
  h1{font-size:17px;font-weight:600;margin:0 0 4px}
  .lead{color:#5F5E5A;margin:0 0 12px}
  .bar{position:sticky;top:0;background:#F7F7F5;padding:8px 0 10px;z-index:5;
       border-bottom:1px solid #E3E2DC;margin-bottom:14px}
  button{font:inherit;font-size:12px;padding:4px 10px;margin:0 6px 6px 0;border-radius:7px;
         border:1px solid #D3D1C7;background:#fff;color:#2C2C2A;cursor:pointer}
  button.on{background:#EEEDFE;border-color:#AFA9EC;color:#3C3489}
  .stat{color:#5F5E5A;font-size:12px;margin-top:4px}
  .card{background:#fff;border:1px solid #E3E2DC;border-radius:10px;padding:12px 14px;margin-bottom:12px}
  .card header{display:flex;flex-wrap:wrap;align-items:center;gap:8px;margin-bottom:8px}
  .seq{font-family:ui-monospace,Menlo,Consolas,monospace;color:#3C3489;background:#EEEDFE;
       border-radius:6px;padding:1px 7px;font-size:12px}
  .g{font-size:11.5px;color:#5F5E5A;background:#F1EFE8;border-radius:99px;padding:1px 8px}
  .k{font-size:11.5px;padding:1px 8px;border-radius:99px;background:#EAF3DE;color:#27500A}
  .k1{background:#E6F1FB;color:#0C447C}.k2{background:#FAEEDA;color:#633806}.k3{background:#FBEAF0;color:#72243E}
  .ts{color:#888780;font-size:12px;font-family:ui-monospace,Menlo,Consolas,monospace}
  .who{font-weight:500}
  .meta{color:#888780;font-size:11.5px}
  .b{margin-left:auto;font-size:11.5px;padding:2px 8px;border-radius:99px}
  .b.ok{background:#EAF3DE;color:#27500A}.b.diff{background:#FCEBEB;color:#791F1F}
  .b.unk{background:#F1EFE8;color:#5F5E5A}
  .ans{white-space:pre-wrap;background:#FAFAF8;border:1px solid #EDECE7;border-radius:8px;
       padding:10px 12px;margin-bottom:8px;font-size:12.5px}
  .cmp{display:grid;grid-template-columns:1fr 1fr;gap:8px;font-size:12px;color:#444441}
  .cmp .hint{grid-column:1/-1;color:#888780;font-size:11.5px}
  details{margin-top:8px}
  summary{cursor:pointer;color:#185FA5;font-size:12px}
  pre{margin:6px 0 0;padding:10px 12px;background:#FAFAF8;border:1px solid #EDECE7;border-radius:8px;
      font-family:ui-monospace,Menlo,Consolas,monospace;font-size:11.5px;white-space:pre-wrap;
      word-break:break-word;color:#3C3C3A;max-height:340px;overflow:auto}
  @media (max-width:700px){.cmp{grid-template-columns:1fr}}
</style>
</head>
<body>
<h1>教练建议 · 本轮 %(total)d 条</h1>
<p class="lead">每条是一次独立请求：先看建议，再展开「当时局势」与「当时待选」核对它有没有读对。
一致标记只表示“建议与实际选择是否指向同一候选”，<b>不代表谁对谁错</b>。</p>
<div class="bar">
  <button class="on" data-f="all">全部 %(total)d</button>
  %(stage_btns)s
  <button data-f="v:diff">不一致 %(ndiff)d</button>
  <button data-f="v:unparsed">实选未解析 %(nunp)d</button>
  <div class="stat">%(range)s　·　%(stat_line)s</div>
</div>
%(cards)s
<script>
document.querySelectorAll('.bar button').forEach(function(b){
  b.onclick=function(){
    document.querySelectorAll('.bar button').forEach(function(x){x.classList.remove('on')});
    b.classList.add('on');
    var f=b.dataset.f;
    document.querySelectorAll('.card').forEach(function(c){
      var show;
      if(f==='all') show=true;
      else if(f.indexOf('s:')===0) show = (c.dataset.s===f.slice(2));
      else show = (c.dataset.v===f.slice(2));
      c.style.display = show?'':'none';
    });
  };
});
</script>
</body>
</html>""" % {
        "total": total,
        "stage_btns": stage_btns,
        "ndiff": stat.get("diff", 0),
        "nunp": stat.get("unparsed", 0) + stat.get("noactual", 0),
        "range": "%s → %s" % (esc(rows[0].get("ts")) if rows else "", esc(rows[-1].get("ts")) if rows else ""),
        "stat_line": stat_line,
        "cards": "\n".join(cards),
    }


def main():
    since = None
    out = OUT
    if "--since" in sys.argv:
        since = sys.argv[sys.argv.index("--since") + 1]
    if "--out" in sys.argv:
        out = sys.argv[sys.argv.index("--out") + 1]
    rows = load(since)
    if not rows:
        print("没有匹配的记录")
        return 1
    page = build_html(rows)
    os.makedirs(os.path.dirname(out), exist_ok=True)
    open(out, "w", encoding="utf-8").write(page)
    print("写出 %s（%d 条，%.0f KB）" % (out, len(rows), os.path.getsize(out) / 1024.0))
    return 0


if __name__ == "__main__":
    sys.exit(main())
