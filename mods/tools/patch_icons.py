"""给 build_effect_map.py 注入「特使可去工位」前缀逻辑（v2，正确缩进）。

注入点：main() 内、写出 effect_map 之前。全部代码按 main 内部缩进（4 空格起）。
数据：dump/current_mode/archetypes.json 的 920004（特使图标/间谍图标）
      + 900013 里含 InfiltrateAbility（渗透）。
用法：python mods/tools/patch_icons.py   （幂等）
"""
import io
import os

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
TARGET = os.path.join(ROOT, "mods", "tools", "build_effect_map.py")

MARK = "# >>> 特使可去工位"
BLOCK_LINES = [
    "    # >>> 特使可去工位（catalog_probe 920004 + InfiltrateAbility）",
    '    AGENT_ICONS = {"Circle": "城市", "Triangle": "香料贸易", "Pentagon": "兰兹拉德",',
    '                   "Emperor": "皇帝", "SpacingGuild": "宇航公会", "BeneGesserit": "贝尼·杰瑟里特",',
    '                   "Fremen": "弗雷曼人", "Spy": "间谍"}',
    "    try:",
    '        import json as _json',
    '        _ar_p = os.path.join(ROOT, "dump", "current_mode", "archetypes.json")',
    '        _ab_p = os.path.join(ROOT, "dump", "current_mode", "ability_observations.json")',
    "        _img_icons, _img_infil = {}, set()",
    "        if os.path.exists(_ar_p):",
    '            _ar = _json.load(open(_ar_p, encoding="utf-8-sig"))',
    '            _ab = _json.load(open(_ab_p, encoding="utf-8-sig")) if os.path.exists(_ab_p) else {}',
    "            _infil_ids = set()",
    "            for _k, _v in _ab.items():",
    '                if _v and "Infiltrate" in str(_v[0].get("attributes", {}).get("Name")):',
    "                    _infil_ids.add(_k)",
    "            for _a in _ar:",
    '                _at = {x["name"]: x["value"] for x in (_a.get("attributes") or [])}',
    "                _img = _at.get(920003)",
    "                if not _img:",
    "                    continue",
    "                _ic = [AGENT_ICONS[t] for t in (_at.get(920004) or []) if t in AGENT_ICONS]",
    "                if _ic:",
    "                    _img_icons[_img] = _ic",
    "                if _infil_ids & set(_at.get(900013) or []):",
    "                    _img_infil.add(_img)",
    "",
    "        # 中文名 -> image（card.<set>.<image>.title 反查）",
    "        _zh2img = {}",
    "        for _k, _v in kv.items():",
    '            _m = re.match(r"^card\\.[a-z]+\\.([a-z0-9_]+)\\.title$", _k)',
    "            if _m:",
    "                _zh2img[_v.strip()] = _m.group(1)",
    "",
    "        _prefixed = 0",
    "        for _key in list(pairs.keys()):",
    '            if not _key.startswith("name:"):',
    "                continue",
    "            _txt = pairs[_key]",
    '            if _txt.startswith("特使可去"):',
    "                continue",
    "            _img = _zh2img.get(_key[5:])",
    "            if not _img:",
    "                continue",
    "            _icons = _img_icons.get(_img)",
    "            _parts = []",
    "            if _img in _img_infil:",
    '                _parts.append("特使可去：任意位置（渗透）")',
    "            elif _icons:",
    '                _parts.append("特使可去：" + "、".join(_icons))',
    "            else:",
    "                continue",
    '            if _icons and "间谍" in _icons:',
    '                _parts.append("打出派间谍")',
    '            pairs[_key] = "；".join(_parts) + "。" + _txt',
    "            _prefixed += 1",
    '        print("特使可去前缀：%d 条（图标 %d / 渗透 %d）" % (_prefixed, len(_img_icons), len(_img_infil)))',
    "    except Exception as _e:",
    '        print("特使图标前缀失败（不影响主流程）:", _e)',
    "    # <<< 特使可去工位",
]

s = io.open(TARGET, encoding="utf-8").read()
if MARK in s:
    print("已注入过，跳过")
else:
    anchor = '    with open(OUT, "w", encoding="utf-8", newline="\\n") as f:'
    assert anchor in s, "找不到写出锚点"
    s = s.replace(anchor, "\n".join(BLOCK_LINES) + "\n" + anchor, 1)
    io.open(TARGET, "w", encoding="utf-8", newline="\n").write(s)
    print("已注入")
