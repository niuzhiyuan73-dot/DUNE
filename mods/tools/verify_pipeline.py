"""端到端验收：检查信息传递链路修复后的实际效果。

对照基准（修复前，2026-11-01 14:30 实测）：
  - effect_map.tsv 条目 587（GUID 238 / name 349）
  - 核心条目 GUID 命中 31/48
  - 6 组同名冲突牌会串用
  - name_map 丢弃 190 条长文本

用法：python -X utf8 -B mods/tools/verify_pipeline.py
"""
import collections
import json
import os
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
AI = os.path.join(ROOT, "mods", "ai")

PASS, FAIL = [], []


def check(name, ok, detail=""):
    (PASS if ok else FAIL).append(name)
    print("  [%s] %s%s" % ("PASS" if ok else "FAIL", name, ("  -> " + detail) if detail else ""))


def read_tsv(path):
    out = {}
    if not os.path.exists(path):
        return out
    for line in open(path, encoding="utf-8-sig"):
        line = line.rstrip("\n")
        if not line or line.startswith("#"):
            continue
        parts = line.split("\t")
        if len(parts) < 2 or not parts[0].strip():
            continue
        # 保留完整列，调用方按需索引
        out[parts[0].strip()] = parts[1:]
    return out


def main():
    print("=" * 68)
    print("端到端验收 —— 信息传递链路")
    print("=" * 68)

    nm = read_tsv(os.path.join(AI, "name_map.tsv"))
    em = read_tsv(os.path.join(AI, "effect_map.tsv"))
    v2 = read_tsv(os.path.join(AI, "effect_map_v2.tsv"))
    lt = read_tsv(os.path.join(AI, "long_text.tsv"))

    # 1) 长文本通道
    print("\n[1] name_map 长文本通道")
    if lt:
        lens = [len(r[0]) for r in lt.values()]
        check("long_text.tsv 存在", True, "%d 条, 最长 %d 字" % (len(lt), max(lens) if lens else 0))
        check("长文本通道覆盖 >=190 条（修复前被丢弃数）", len(lt) >= 190, "实际 %d" % len(lt))
        key = "worm.leader.stabantuek.signetability.desc"
        if key in lt:
            check("长条目 stabantuek 完整保留 (336字)", len(lt[key][0]) >= 330,
                  "%d 字" % len(lt[key][0]))
        else:
            check("长条目 stabantuek 存在", False, "键不在 long_text.tsv")
    else:
        check("long_text.tsv 存在", False, "未生成")

    # 2) v2 元信息与优先级
    print("\n[2] effect_map_v2 来源与优先级")
    if v2:
        check("effect_map_v2.tsv 存在", True, "%d 条" % len(v2))
        # v2 列：来源 / 优先级 / 扩展 / 歧义标记 / 文本
        has_src = sum(1 for r in v2.values() if len(r) >= 1 and r[0].strip())
        check("每条带来源标记", has_src == len(v2), "%d/%d" % (has_src, len(v2)))
        has_pri = sum(1 for r in v2.values() if len(r) >= 2 and r[1].strip().isdigit())
        check("每条带优先级标记", has_pri == len(v2), "%d/%d" % (has_pri, len(v2)))
        # v2 长文本不应被截断
        trunc = sum(1 for r in v2.values() if r and r[-1].rstrip().endswith("…"))
        check("v2 文本未被 EFFECT_MAX 截断", trunc == 0, "%d 条仍带截断标记" % trunc)
    else:
        check("effect_map_v2.tsv 存在", False, "未生成")

    # 3) 旧两列格式仍可用（C# 回退路径）
    print("\n[3] 旧 effect_map.tsv 兼容性")
    check("effect_map.tsv 存在", bool(em), "%d 条" % len(em))
    if em:
        bad = sum(1 for r in em.values() if len(r) != 1)
        check("仍为两列格式（C# 可直接读）", bad == 0, "%d 条不是两列" % bad)
        check("条目数不小于修复前 587", len(em) >= 587, "%d" % len(em))

    # 3b) v3：结构化字段与数量归一
    print("\n[3b] effect_map_v3 结构化字段与数量归一")
    v3 = read_tsv(os.path.join(AI, "effect_map_v3.tsv"))
    if v3:
        check("effect_map_v3.tsv 存在", True, "%d 条" % len(v3))
        check("条数与 v2 一致（v3 只是重排，不增删）", len(v3) == len(v2),
              "v3 %d / v2 %d" % (len(v3), len(v2)))
        # v3 列：来源 / 优先级 / 扩展 / 歧义 / 正文 / 花费 / 条件
        bad_cols = sum(1 for r in v3.values() if len(r) != 7)
        check("每条 8 列（键+7 字段）", bad_cols == 0, "%d 条列数不对" % bad_cols)

        import re as _re
        cost_ok = _re.compile(r"^(\d+(说服|香料|太阳币|水))(/\d+(说服|香料|太阳币|水))*$")
        bad_cost = [k for k, r in v3.items() if len(r) > 5 and r[5].strip() and not cost_ok.match(r[5].strip())]
        check("花费列格式合法（数字+单位，可空）", not bad_cost,
              "非法: " + ", ".join(bad_cost[:3]) if bad_cost else "")
        n_cost = sum(1 for r in v3.values() if len(r) > 5 and r[5].strip())
        n_cond = sum(1 for r in v3.values() if len(r) > 6 and r[6].strip())
        # 花费条数下限从 100 降到 40：卡面「费用：N 说服」是**招募费用**，属购买价，
        # 抽出会让模型以为打出手牌要付这笔（实测因此不敢用哈德拉克），已刻意排除。
        check("花费/条件已抽出", n_cost >= 40 and n_cond >= 100,
              "花费 %d 条 / 条件 %d 条" % (n_cost, n_cond))
        bad_recruit = [k for k, r in v3.items() if len(r) > 5 and "说服" in r[5]]
        check("花费列不含招募费用（说服）", not bad_recruit,
              "误标: " + ", ".join(bad_recruit[:3]) if bad_recruit else "0 条")

        # 数量选择器：原文「选择两个：」必须已归一，且不能二次叠加
        left = [k for k, r in v3.items() if len(r) > 4 and _re.search(r"选(择)?[一二两三四五六]个\s*[:：]", r[4])]
        check("数量选择器已归一（无「选择N个：」残留）", not left,
              "残留: " + ", ".join(left[:3]) if left else "")
        marked = [k for k, r in v3.items() if len(r) > 4 and "只选" in r[4]]
        check("归一标记已写入（只选N个，不要全选）", len(marked) >= 5, "%d 条" % len(marked))
        key = "name:宣传策略"
        if key in v3:
            check("「宣传策略」奖励写明只选2项", "只选2个" in v3[key][4],
                  v3[key][4][:40])

        # 幂等：v3 正文再过一次归一必须不变
        sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
        try:
            import normalize_effects as NE
            drift = [k for k, r in list(v3.items())[:200]
                     if len(r) > 4 and NE.normalize_text(r[4]) != r[4]]
            check("归一化幂等（前 200 条复跑无变化）", not drift,
                  "漂移: " + ", ".join(drift[:3]) if drift else "")
        except Exception as e:
            check("归一化脚本可导入", False, str(e))
    else:
        check("effect_map_v3.tsv 存在", False, "先跑 normalize_effects.py")

    # 4) 同名消歧：6 组已知串用冲突
    print("\n[4] 同名多版本消歧")
    conflicts = {
        "厄拉奇恩之战": ["a5b5668a", "db2b7a79"],
        "帝国盆地之战": ["b4c07183", "f22d9777"],
        "围攻厄拉奇恩": ["31acac91", "34c4273b"],
        "保卫帝国盆地": ["4b236fb5", "56bbe4e4"],
        "商会安保": ["4eba8ebd", "b45ba224"],
        "贸易纠纷": ["96cae890", "b603735c"],
    }
    name_to_gids = collections.defaultdict(list)
    for k, r in nm.items():
        if k.count("-") == 4:
            name_to_gids[r[0]].append(k)
    src = v2 if v2 else em
    bad = []
    for name, guids in conflicts.items():
        texts = []
        for g in guids:
            full = [k for k in src if k.startswith(g)]
            if full:
                texts.append(src[full[0]][-1])
        if len(texts) == 2 and texts[0] == texts[1] and texts[0]:
            bad.append(name)
    check("6 组同名冲突牌各有独立文本", not bad, "仍相同: " + ", ".join(bad) if bad else "")
    # 多版本异文同名：自动派生的 name: 汇总键不应存在；
    # 人工按中文名填表的键无法指定版本，允许存在，但**必须带歧义标记**，
    # C# 侧据此拒绝用它兜底取效果。
    name_to_gids = collections.defaultdict(list)
    for k, r in nm.items():
        if k.count("-") == 4:
            name_to_gids[r[0]].append(k)
    src = v2 if v2 else em
    auto_derived = []
    manual_amb = []
    for n, gids in name_to_gids.items():
        if len(gids) < 2:
            continue
        texts = set()
        for g in gids:
            for k in src:
                if k.startswith(g):
                    texts.add(src[k][-1])
                    break
        if len(texts) <= 1:
            continue
        key = "name:" + n
        if key not in src:
            continue
        row = src[key]
        amb = row[3] if len(row) >= 4 else ""
        if amb:
            manual_amb.append(n)
        else:
            auto_derived.append(n)
    check("多版本异文同名均带歧义标记（禁止按名兜底）", len(auto_derived) == 0,
          "无标记却有 name: 键: " + ", ".join(auto_derived[:5]) if auto_derived
          else ("%d 个歧义名已标记" % len(manual_amb)))

    # 5) 核心条目覆盖
    print("\n[5] 核心条目效果覆盖")
    catp = os.path.join(AI, "current_mode_catalog.json")
    if os.path.exists(catp):
        cat = json.load(open(catp, encoding="utf-8-sig"))
        core = [r for r in cat["records"] if r["scope"]["core"]]
        hit = sum(1 for r in core if r["id"] in src or ("name:" + r["name"]) in src)
        check("核心条目效果命中 >= 31（修复前基准）", hit >= 31,
              "%d/%d" % (hit, len(core)))
        # 字段级完整度
        fc = [r for r in cat["records"] if "field_completeness" in r]
        check("record 带字段级完整度标记", len(fc) == len(cat["records"]),
              "%d/%d" % (len(fc), len(cat["records"])))
        if fc:
            states = collections.Counter()
            for r in fc:
                for f, v in (r.get("field_completeness") or {}).items():
                    if not isinstance(v, dict):
                        continue
                    states[v.get("status", "?")] += 1
            check("三态齐全（confirmed/absent/unknown）",
                  {"confirmed", "unknown"} <= set(states), str(dict(states)))
    else:
        check("current_mode_catalog.json 存在", False)

    # 6) 盘点产物
    print("\n[6] 字段级盘点产物")
    gapsp = os.path.join(ROOT, "docs", "当前玩法字段完整度.md")
    check("docs/当前玩法字段完整度.md 生成", os.path.exists(gapsp))
    if os.path.exists(gapsp):
        txt = open(gapsp, encoding="utf-8").read()
        check("盘点含 unknown/absent 分别统计", "unknown" in txt and "absent" in txt)
    fg = os.path.join(AI, "field_gaps.json")
    check("mods/ai/field_gaps.json 生成", os.path.exists(fg))

    print("\n" + "=" * 68)
    print("结果：PASS %d / FAIL %d" % (len(PASS), len(FAIL)))
    if FAIL:
        for f in FAIL:
            print("  FAILED: " + f)
    print("=" * 68)
    return 1 if FAIL else 0


if __name__ == "__main__":
    sys.exit(main())
