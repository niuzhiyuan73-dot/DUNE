"""给 Mod 版游戏的本地化表追加卡名键（离线补丁，可回滚）。

用法：
    python add_card_text.py            # 打补丁（首次自动备份原文件）
    python add_card_text.py --restore  # 从备份还原

键格式与游戏一致：card.<类别>.<image 键>.title
"""
import os
import shutil
import sys
import UnityPy

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
LOC = os.path.join(ROOT, "game", "DuneImperium_Data", "StreamingAssets",
                   "Localization", "win", "localization")
BACKUP = os.path.join(ROOT, "backup", "localization.orig")

# 键 -> (简体, 繁体, 英文)
TEXTS = {
    "card.imperium.spiceraider.title": ("香料掠夺者", "香料掠奪者", "Spice Raider"),
}


def detect_lang(text: str) -> str:
    if "賬" in text or "稱" in text:
        return "zh-Hant"
    if "账" in text or "称" in text:
        return "zh-Hans"
    return "en"


def patch() -> None:
    if not os.path.exists(BACKUP):
        os.makedirs(os.path.dirname(BACKUP), exist_ok=True)
        shutil.copy2(LOC, BACKUP)
        print(f"[backup] {BACKUP}")

    env = UnityPy.load(LOC)
    touched, skipped = 0, 0
    for obj in env.objects:
        if obj.type.name != "TextAsset":
            continue
        ta = obj.read()
        body = ta.m_Script
        if isinstance(body, bytes):
            body = body.decode("utf-8", "replace")

        if "card.imperium.dagger.title" not in body:
            skipped += 1
            continue

        lang = detect_lang(body)
        add = []
        for key, (hans, hant, en) in TEXTS.items():
            if f"{key}=" in body:
                continue
            value = hant if lang == "zh-Hant" else (hans if lang == "zh-Hans" else en)
            add.append(f"{key}={value}")

        if not add:
            continue
        ta.m_Script = body.rstrip("\n") + "\n" + "\n".join(add) + "\n"
        ta.save()
        touched += 1
        print(f"[patch] {lang}: +{len(add)} 键")

    if touched == 0:
        raise SystemExit(f"没有命中任何语言表（跳过 {skipped} 个非语言表）")

    with open(LOC, "wb") as f:
        f.write(env.file.save(packer="lz4"))
    print(f"[done] 更新 {touched} 张语言表 -> {LOC} ({os.path.getsize(LOC)} B)")


def verify() -> None:
    env = UnityPy.load(LOC)
    for obj in env.objects:
        if obj.type.name != "TextAsset":
            continue
        body = obj.read().m_Script
        if isinstance(body, bytes):
            body = body.decode("utf-8", "replace")
        for line in body.splitlines():
            if line.startswith("card.imperium.spiceraider.title="):
                print(f"[verify] {detect_lang(body)}: {line}")


def restore() -> None:
    if not os.path.exists(BACKUP):
        raise SystemExit("没有备份可还原")
    shutil.copy2(BACKUP, LOC)
    print(f"[restore] {LOC} <- {BACKUP}")


if __name__ == "__main__":
    if "--restore" in sys.argv:
        restore()
    else:
        patch()
        verify()
