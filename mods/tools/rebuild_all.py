"""
一次性重生成全部标准组合（switch_preset.ps1 菜单能选到的那些）。

覆盖范围：
  - marathon：12 种叠加组合
  - faction / faction_pro / faction_extra：各 4 阵营 × 12 种叠加 = 48
  - faction_ultra：4 阵营 × 2 种（自带跃迁+Ultra，可再叠议会）
共 164 套。三个实验包（_techx / _same / _hook_g5_wall）不在内，它们需要额外参数。

用法：python mods/tools/rebuild_all.py [--dry]
"""
import os
import sys
import traceback

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import build_presets as bp  # noqa: E402

COMBOS = [
    ("marathon", None, []),
    ("marathon", None, ["council"]),
    ("marathon", None, ["holtzman"]),
    ("marathon", None, ["holtzman", "council"]),
    ("marathon", None, ["holtzman", "lasttime"]),
    ("marathon", None, ["holtzman", "lasttime", "council"]),
    ("marathon", None, ["holtzman", "lasttime_ultra"]),
    ("marathon", None, ["holtzman", "lasttime_ultra", "council"]),
    ("marathon", None, ["lasttime"]),
    ("marathon", None, ["lasttime", "council"]),
    ("marathon", None, ["lasttime_ultra"]),
    ("marathon", None, ["lasttime_ultra", "council"]),
]

BASE_MOD_SETS = [
    [],
    ["council"],
    ["holtzman"],
    ["holtzman", "council"],
    ["holtzman", "lasttime"],
    ["holtzman", "lasttime", "council"],
    ["holtzman", "lasttime_ultra"],
    ["holtzman", "lasttime_ultra", "council"],
    ["lasttime"],
    ["lasttime", "council"],
    ["lasttime_ultra"],
    ["lasttime_ultra", "council"],
]

for mode in ("faction", "faction_pro", "faction_extra"):
    for fac in bp.FACTION_ORDER:
        for mods in BASE_MOD_SETS:
            COMBOS.append((mode, fac, list(mods)))

for fac in bp.FACTION_ORDER:
    COMBOS.append(("faction_ultra", fac, ["holtzman", "lasttime_ultra"]))
    COMBOS.append(("faction_ultra", fac, ["holtzman", "lasttime_ultra", "council"]))


def main():
    dry = "--dry" in sys.argv
    total = len(COMBOS)
    print(f"[rebuild] 待生成 {total} 套", flush=True)
    if dry:
        for i, (m, f, mods) in enumerate(COMBOS, 1):
            print(f"  {i:3d}. {m} {f or ''} {'+'.join(mods)}")
        return

    ok = 0
    failed = []
    for i, (mode, fac, mods) in enumerate(COMBOS, 1):
        tag = f"{mode}/{fac or '-'}/{'+'.join(mods) if mods else 'base'}"
        try:
            bp.build(mode, faction_key=fac, mods=list(mods))
            ok += 1
            print(f"[{i}/{total}] ok   {tag}", flush=True)
        except Exception as e:
            failed.append((tag, repr(e)))
            print(f"[{i}/{total}] FAIL {tag} -> {e}", flush=True)
            traceback.print_exc()

    print(f"[rebuild] 完成：成功 {ok}，失败 {len(failed)}", flush=True)
    for tag, err in failed:
        print("  FAILED:", tag, err, flush=True)


if __name__ == "__main__":
    main()
