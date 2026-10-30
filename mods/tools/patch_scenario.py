"""把剧本 JSON 写回指定 AssetBundle 的 TextAsset，生成可安装的补丁包。

用法:
    python patch_scenario.py <scenario.json> <bundle_name> <textasset_name> <out.bundle>

只读源 bundle，只写输出路径，不修改游戏目录。
"""
import os
import sys
import json
import UnityPy

GAME_AA = r"C:\Program Files (x86)\Steam\steamapps\common\Dune Imperium\DuneImperium_Data\StreamingAssets\aa\StandaloneWindows64"


def patch(scenario_json, bundle_name, textasset_name, out_path):
    with open(scenario_json, encoding="utf-8") as f:
        doc = json.load(f)

    src = os.path.join(GAME_AA, bundle_name)
    if not os.path.exists(src):
        raise SystemExit(f"源 bundle 不存在: {src}")

    env = UnityPy.load(src)
    hit = 0
    for obj in env.objects:
        if obj.type.name != "TextAsset":
            continue
        ta = obj.read()
        if getattr(ta, "m_Name", "") == textasset_name:
            payload = json.dumps(doc, ensure_ascii=False, indent=1)
            ta.m_Script = payload
            ta.save()
            hit += 1
    if hit != 1:
        raise SystemExit(f"期望命中 1 个 TextAsset[{textasset_name}]，实际 {hit}")

    os.makedirs(os.path.dirname(out_path) or ".", exist_ok=True)
    with open(out_path, "wb") as f:
        f.write(env.file.save(packer="lz4"))
    print(f"已生成 {out_path} ({os.path.getsize(out_path)} B)")
    return out_path


def verify(bundle_path, textasset_name, keys):
    env = UnityPy.load(bundle_path)
    for obj in env.objects:
        if obj.type.name != "TextAsset":
            continue
        ta = obj.read()
        if getattr(ta, "m_Name", "") == textasset_name:
            txt = ta.m_Script
            if isinstance(txt, bytes):
                txt = txt.decode("utf-8", "replace")
            d = json.loads(txt)
            for path in keys:
                cur = d
                for part in path.split("."):
                    cur = cur[int(part)] if part.isdigit() else cur[part]
            print(f"回读校验 OK: {textasset_name} 关键字段已就位")
            return d
    raise SystemExit("回读校验失败：找不到目标 TextAsset")


if __name__ == "__main__":
    if len(sys.argv) != 5:
        print(__doc__)
        raise SystemExit(1)
    out = patch(sys.argv[1], sys.argv[2], sys.argv[3], sys.argv[4])
    verify(out, sys.argv[3], [])
