"""Build a lossless, source-labelled catalog for the installed scenario.

Requires UnityPy and the read-only catalog_probe export. Never edits game/save data.
python -X utf8 -B mods/tools/build_current_mode_catalog.py
"""
from __future__ import annotations

import collections
import hashlib
import json
from pathlib import Path
import re

import UnityPy

ROOT = Path(__file__).resolve().parents[2]
RAW = ROOT / "dump/current_mode"
OUT = ROOT / "mods/ai/current_mode_catalog.json"
BUNDLE = ROOT / "game/DuneImperium_Data/StreamingAssets/aa/StandaloneWindows64/b56aa8ad35f9c2e1694994f384e51bbe.bundle"
SAVES = ROOT / "profile/DireWolf/Dune_ Imperium/savedgames"
GENERIC = {"AgentAbility", "RevealAbility", "AcquireAbility", "ActiveAbility", "SpaceAbility"}
LABELS = {"Imperium": "帝国牌", "Tleilaxu": "泰莱拉牌", "Intrigue": "阴谋牌", "TechTile": "科技",
          "Leader": "领袖", "Space": "版图位置", "Conflict": "冲突", "ConflictReward": "冲突奖励",
          "Contract": "合约", "LeaderUpgrade": "领袖升级"}
RESOURCE = {"Spice": "香料", "Water": "水", "Solari": "太阳币", "Troops": "部队",
            "Persuasion": "说服", "Strength": "军力", "Specimen": "标本", "VictoryPoints": "胜利点",
            "IntrigueCard": "阴谋牌", "SignetIcons": "领袖戒指能力"}
ICONS = {"Emperor": "皇帝", "SpacingGuild": "宇航工会", "BeneGesserit": "姐妹会", "Fremen": "弗雷曼",
         "Pentagon": "兰兹拉德", "Circle": "城市", "Triangle": "香料贸易"}


def read_json(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def write_json(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def attrs(entity, defs):
    return {defs.get(str(a["name"]), str(a["name"])): a["value"] for a in entity.get("attributes", [])}


def walk_entities(value):
    # Deliberately exclude undo/event histories and pending-response payloads.
    if isinstance(value, dict):
        if "entityName" in value:
            yield value
            yield from walk_entities(value.get("children", []))
        else:
            for v in value.values():
                yield from walk_entities(v)
    elif isinstance(value, list):
        for v in value:
            yield from walk_entities(v)


def locales():
    result = {"zh_CN": {}, "en_US": {}}
    sources = {}
    for platform in ("gamecore", "win"):
        path = ROOT / f"game/DuneImperium_Data/StreamingAssets/Localization/{platform}/localization"
        if not path.exists():
            continue
        sources[str(path.relative_to(ROOT))] = sha(path)
        for obj in UnityPy.load(str(path)).objects:
            if obj.type.name != "TextAsset":
                continue
            ta = obj.read()
            if ta.m_Name not in result:
                continue
            text = ta.m_Script
            if isinstance(text, bytes):
                text = text.decode("utf-8")
            for line in text.splitlines():
                if "=" in line and not line.startswith("#"):
                    k, v = line.split("=", 1)
                    result[ta.m_Name][k.strip()] = v.strip()
    return result, sources


def sprite_map():
    result = {}
    for line in (ROOT / "mods/ai/sprite_map.tsv").read_text(encoding="utf-8-sig").splitlines():
        if line and not line.startswith("#"):
            k, _, v = line.partition("\t")
            result[k] = v
    result.update({"Landsraad": "兰兹拉德图标", "City": "城市图标", "CHOAM": "香料贸易图标",
                   "ControlNeutral": "控制标记", "FremenBond": "弗雷曼羁绊", "Signet": "领袖戒指能力"})
    return result


def clean(text, sprites):
    def replace(m):
        name = m.group(1).strip('"\' ')
        if name in sprites:
            return "【" + sprites[name] + "】"
        numeric = re.fullmatch(r"(Spice|Water|Solari|Persuasion|Strength|Troop|VictoryPoint)_(\d+)", name)
        if numeric:
            label = RESOURCE.get(numeric[1], {"Troop": "部队", "VictoryPoint": "胜利点"}.get(numeric[1], numeric[1]))
            return "【" + label + numeric[2] + "】"
        return "【未译图标:" + name + "】"
    text = re.sub(r"<sprite\s+name\s*=\s*[\"']?([^\"'<>]+?)[\"']?\s*/?>", replace, text, flags=re.I)
    # Many Chinese lines break inside a word; preserve raw text alongside this rendering.
    text = re.sub(r"<br\s*/?>", "", text, flags=re.I)
    text = re.sub(r"</?[a-z][^>]*>", "", text, flags=re.I)
    return re.sub(r"\s+", " ", text.replace("\\n", "；")).strip()


def loc_keys(value):
    if isinstance(value, dict):
        if isinstance(value.get("id"), str):
            yield value["id"]
        for v in value.values():
            yield from loc_keys(v)
    elif isinstance(value, list):
        for v in value:
            yield from loc_keys(v)


def load_scenario():
    for obj in UnityPy.load(str(BUNDLE)).objects:
        if obj.type.name == "TextAsset":
            ta = obj.read()
            if ta.m_Name == "StandardSolo":
                return json.loads(ta.m_Script)
    raise RuntimeError("StandardSolo missing")


def load_observations(defs):
    abilities = collections.defaultdict(dict)
    observed = collections.defaultdict(set)
    metadata = []
    latest_ids = set()
    for index, path in enumerate(sorted(SAVES.glob("*.json"), key=lambda p: p.stat().st_mtime, reverse=True)):
        d = read_json(path)
        tree = d.get("currentState", {}).get("value", {}).get("entities", {})
        es = list(walk_entities(tree))
        ids = set()
        for e in es:
            at = attrs(e, defs)
            gid = at.get("ArchID")
            if gid:
                ids.add(gid)
                observed[gid].add(path.name)
            for container in e.get("children", []):
                if container.get("entityName") != "Abilities":
                    continue
                for ab in container.get("children", []):
                    av = attrs(ab, defs)
                    aid = av.get("AbilityIDAttribute")
                    if not aid:
                        continue
                    name = ab["entityName"]
                    key = json.dumps([name, av], sort_keys=True)
                    abilities[aid][key] = {"class": name, "attributes": av, "source": path.name}
        metadata.append({"file": str(path.relative_to(ROOT)), "sha256": sha(path),
                         "archetypes_observed": len(ids), "turn_number": d.get("turnNumber")})
        if index == 0:
            latest_ids = ids
    return {k: list(v.values()) for k, v in abilities.items()}, observed, latest_ids, metadata


def supplements():
    rows = []
    for name in ("card_text_extra.tsv", "kb_extra.tsv", "kb_todo_fill.tsv"):
        for line in (ROOT / "mods/ai" / name).read_text(encoding="utf-8-sig").splitlines():
            if not line or line.startswith("#"):
                continue
            p = line.split("\t")
            if name == "kb_todo_fill.tsv":
                if len(p) < 4 or not p[3].strip():
                    continue
                rows.append({"key": p[0], "text": p[3], "source": p[4] if len(p) > 4 else "", "file": name})
            elif len(p) > 1:
                rows.append({"key": p[0], "text": p[1], "source": p[2] if len(p) > 2 else "", "file": name})
    return rows


def build():
    definitions = read_json(RAW / "attribute_definitions.json")
    defs = {str(x["key"]): x["name"] for x in definitions}
    raw = read_json(RAW / "archetypes.json")
    loc, loc_sources = locales()
    sprites = sprite_map()
    scenario = load_scenario()
    ability_map, observed, latest_ids, save_sources = load_observations(defs)
    extra = supplements()
    by_id = {x["id"]: attrs(x, defs) for x in raw}
    enabled = set(scenario["sets"])
    core_ids = set(scenario.get("ReserveDeck", []))
    starts = set()
    for player in scenario["wormPlayers"]:
        for field in ("StartingDeck", "StartingIntrigue", "StartingTechTiles"):
            starts.update(player.get(field, []))
    conflict_ids = set(scenario.get("MatchSoloOptions", {}).get("ConflictDeck", []))
    core_ids.update(starts | conflict_ids)
    reward_ids = {gid for cid in conflict_ids for gid in by_id[cid].get("ConflictRewardArchetypes", [])}
    core_ids.update(reward_ids)
    records = []
    for raw_record in raw:
        gid = raw_record["id"]
        at = by_id[gid]
        typ = at.get("EntityType", "Unknown")
        sets = at.get("SetList", [])
        if sets and not enabled.intersection(sets):
            continue
        if typ in ("Hagal", "Unknown"):
            continue
        if typ == "Conflict" and conflict_ids and gid not in conflict_ids:
            continue
        if typ == "ConflictReward" and conflict_ids and gid not in reward_ids:
            continue
        namekey = at.get("Name", {}).get("id", "")
        if not namekey:
            namekey = at.get("GameText", {}).get("id", "")
        image = at.get("Image", "")
        zh = loc["zh_CN"].get(namekey, "")
        en = loc["en_US"].get(namekey, "")
        name = zh or en or raw_record["class"].split(".")[-1]
        if typ == "ConflictReward":
            name = raw_record["class"].split(".")[-1]
        keys = set(loc_keys(at))
        if namekey:
            prefix = namekey.removesuffix(".title").removesuffix(".toplevel")
            keys.update(k for k in loc["zh_CN"] if k.startswith(prefix + "."))
        text_fields = []
        for key in sorted(keys):
            if key not in loc["zh_CN"] and key not in loc["en_US"]:
                continue
            text_fields.append({"key": key, "zh_raw": loc["zh_CN"].get(key), "en_raw": loc["en_US"].get(key),
                                "zh": clean(loc["zh_CN"].get(key, ""), sprites), "source": "game_localization"})
        aids = at.get("WormAbilityIDs", []) + at.get("CustomAbilityIDs", [])
        ability_records = []
        unresolved = []
        for aid in dict.fromkeys(aids):
            observations = ability_map.get(aid, [])
            evidence = {}
            for observation in observations:
                av = observation["attributes"]
                # Match the original GameText key. Never guess by stripping class suffixes.
                ak = set(loc_keys(av)) | {"button." + observation["class"], "worm.canis.abilities." + observation["class"]}
                for k in sorted(ak):
                    if k in loc["zh_CN"]:
                        evidence[k] = {"key": k, "zh_raw": loc["zh_CN"][k], "zh": clean(loc["zh_CN"][k], sprites)}
            classes = sorted({o["class"] for o in observations})
            timings = sorted({str(o["attributes"]["AbilityTiming"]) for o in observations if "AbilityTiming" in o["attributes"]})
            ability_records.append({"id": aid, "classes": classes, "observed_timings": timings,
                                    "observations": observations, "text_evidence": list(evidence.values())})
            if not classes or (not set(classes).issubset(GENERIC) and not evidence):
                unresolved.append({"id": aid, "classes": classes, "reason": "未观察到能力实体" if not classes else "缺完整语义证据"})
        legacy = [x for x in extra if x["key"] in (gid, zh, en, namekey)]
        rules = []
        if typ in ("Imperium", "Tleilaxu"):
            agent = [RESOURCE[k] + str(at["Agent" + k]) for k in RESOURCE if "Agent" + k in at]
            if at.get("SignetIcons"):
                agent.append("触发领袖戒指能力×" + str(at["SignetIcons"]))
            reveal = [RESOURCE[k] + str(at[k]) for k in RESOURCE if k in at and k not in ("SignetIcons", "VictoryPoints")]
            if agent:
                rules.append("特使基础收益：" + "、".join(agent))
            if reveal:
                rules.append("展示基础收益：" + "、".join(reveal))
            if "AcquireEffectList" in at:
                rules.append("购买效果图标：" + "、".join(at["AcquireEffectList"]))
        elif typ in ("Space", "ConflictReward"):
            values = [RESOURCE[k] + str(at[k]) for k in RESOURCE if k in at]
            if values:
                rules.append("直接收益属性：" + "、".join(values))
        if "IconList" in at:
            rules.insert(0, "进入图标：" + "、".join(ICONS.get(x, x) for x in at["IconList"]))
        for f in ("PersuasionCost", "SpiceCost", "WaterCost", "SolariCost", "SpecimenCost", "InfluenceRequirements"):
            if f in at:
                rules.insert(0, f + "=" + json.dumps(at[f], ensure_ascii=False))
        # Capture literal rule text separately from titles, prompts, logs and legacy guesses.
        for t in text_fields:
            k = t["key"]
            if re.search(r"\.(agent|reveal|text|ability|static|acquire|reward)(\.|\d|$)|ability\.desc$", k):
                rules.append(k + "：" + t["zh"])
        notes = []
        if legacy:
            notes.append("旧补充表仅作待核对证据，不自动覆盖游戏属性")
        if any(len(t["zh"]) > 70 for t in text_fields):
            notes.append("含超过旧70字上限的完整文本，已无损保留")
        if any("【未译图标:" in t["zh"] for t in text_fields):
            notes.append("含未翻译图标，原名及中英文原文已保留")
        if unresolved:
            notes.append("存在尚未解释的能力；有文本不等于全部规则已核实")
        records.append({"id": gid, "name": name, "english_name": en, "name_key": namekey, "image": image,
                        "type": typ, "sets": sets, "class": raw_record["class"],
                        "scope": {"starting_loadout": gid in starts, "fixed_conflict": gid in conflict_ids,
                                  "core": gid in core_ids, "observed_latest_save": gid in latest_ids,
                                  "pool_membership": "fixed" if gid in core_ids else "enabled_set_candidate"},
                        "attributes": at, "localized_fields": text_fields, "abilities": ability_records,
                        "structured_summary": rules, "legacy_evidence_unverified": legacy,
                        "unresolved_abilities": unresolved, "notes": notes})
    records.sort(key=lambda r: (not r["scope"]["core"], r["type"], r["name"], r["id"]))
    record_ids = {r["id"] for r in records}
    assert core_ids <= record_ids, "Missing installed starting/conflict archetypes"
    assert len(record_ids) == len(records), "Duplicate archetype IDs"
    # Export all game-local glossary/help text, with unmodified source beside rendering.
    glossary = [{"key": k, "zh_raw": v, "zh": clean(v, sprites), "en_raw": loc["en_US"].get(k)}
                for k, v in sorted(loc["zh_CN"].items())
                if k.startswith(("tooltip.", "hanger.", "help.", "rules.", "tutorial."))]
    players = []
    for i, p in enumerate(scenario["wormPlayers"]):
        players.append({"index": i, "is_human": p["isHuman"], "starting_deck": p["StartingDeck"],
                        "starting_intrigue": p["StartingIntrigue"], "starting_tech": p["StartingTechTiles"],
                        "workers": p["StartingAgents"] + int(p["PlayerMatchSoloOptions"].get("StartingSwordmaster", False)),
                        "options": p["PlayerMatchSoloOptions"], "deck_replacements": p["DeckReplacements"]})
    catalog = {"schema_version": 1, "scope_note": "固定开局/冲突为确定范围；其余为启用内容集候选，不保证同一局全部入池。",
               "sources": {"scenario_sha256": sha(BUNDLE), "archetypes_sha256": sha(RAW / "archetypes.json"),
                           "definitions_sha256": sha(RAW / "attribute_definitions.json"), "localizations": loc_sources,
                           "saves": save_sources},
               "scenario": {"sets": scenario["sets"], "endgame_score": scenario["EndgameTriggerScore"],
                            "options": scenario["MatchSoloOptions"], "players": players,
                            "other_rules": {k: scenario.get(k) for k in ("ReserveDeck", "LeaderAssignmentMode",
                                "RuleModAbilities", "GoTo11Gamemode", "draftDecks", "SkirmishRuleMods",
                                "SkirmishScoringMethods", "GamesToWin", "ClientConfig")}},
               "statistics": {"exported_archetypes": len(raw), "attribute_definitions": len(definitions),
                              "catalog_entities": len(records), "core_entities": len(core_ids),
                              "types": dict(collections.Counter(r["type"] for r in records)),
                              "observed_ability_ids": len(ability_map), "glossary_entries": len(glossary)},
               "records": records, "glossary": glossary}
    write_json(OUT, catalog)
    write_json(RAW / "localization.json", loc)
    write_json(RAW / "ability_observations.json", ability_map)
    report(catalog)
    print(json.dumps(catalog["statistics"], ensure_ascii=False, indent=2))


def report(catalog):
    records = catalog["records"]
    names = {r["id"]: r["name"] for r in records}
    core = [r for r in records if r["scope"]["core"]]
    lines = ["# 当前玩法信息盘点", "", "来源：实际安装的 StandardSolo、只读原型导出、官方本地化和现有存档。",
             "静态属性完整导出，不等于所有代码实现的条件能力均已解释。候选内容与实际入池分开统计。", "",
             f"- 胜利阈值：{catalog['scenario']['endgame_score']}；内容集：" + " / ".join(catalog['scenario']['sets']),
             f"- 原型 {catalog['statistics']['exported_archetypes']}；属性定义 {catalog['statistics']['attribute_definitions']}；本次收录 {len(records)}。",
             f"- 固定开局/冲突及引用奖励原型：{len(core)}；游戏内规则提示：{catalog['statistics']['glossary_entries']}。", "",
             "## 四家开局", ""]
    for p in catalog["scenario"]["players"]:
        lines += [f"### {'玩家' if p['is_human'] else 'AI'} {p['index'] + 1}", "",
                  f"工人 {p['workers']}；起始配置 `{json.dumps(p['options'], ensure_ascii=False)}`", "",
                  "牌组：" + "、".join(names.get(g, g) for g in p["starting_deck"]), "",
                  "科技：" + "、".join(names.get(g, g) for g in p["starting_tech"]), "",
                  "阴谋牌：" + "、".join(names.get(g, g) for g in p["starting_intrigue"]), ""]
    lines += ["## 固定冲突顺序", "", " → ".join(names.get(g, g) for g in catalog["scenario"]["options"].get("ConflictDeckOrder", [])), "",
              "## 各类范围", "", "| 类型 | 条目数 | 最新存档出现 | 尚有未解释能力的条目 |", "|---|---:|---:|---:|"]
    for typ, count in catalog["statistics"]["types"].items():
        subset = [r for r in records if r["type"] == typ]
        lines.append(f"| {LABELS.get(typ, typ)} | {count} | {sum(r['scope']['observed_latest_save'] for r in subset)} | {sum(bool(r['unresolved_abilities']) for r in subset)} |")
    lines += ["", "## 固定开局与冲突的待核对项", "", "这里按能力清单查缺，不把一段已有文字当成整张牌完整。",
              "内部实现能力可能被同卡官方文本覆盖；以下保守列出，不能直接视作确实缺失的效果数量。", "",
              "| 名称 | 类型 | 尚未解释的能力 |", "|---|---|---|"]
    for r in core:
        if r["unresolved_abilities"]:
            lines.append("| " + r["name"] + " | " + LABELS.get(r["type"], r["type"]) + " | " +
                         "；".join("/".join(u["classes"]) or u["id"] for u in r["unresolved_abilities"]) + " |")
    lines += ["", "## 文件与更新", "", "- 完整机器可读数据：`mods/ai/current_mode_catalog.json`。",
              "- 逐项规则与证据：`docs/当前玩法卡牌规则.md`。",
              "- 原型/字段定义/原始本地化/能力观察：`dump/current_mode/`。",
              "- 重建：`python -X utf8 -B mods/tools/build_current_mode_catalog.py`。",
              "- 无网络请求、无 API key 读取；生成器不写游戏或存档。",
              "- 本次未自动替换旧 effect_map；旧补充表存在错误，需逐项核实后接入。", ""]
    (ROOT / "docs/当前玩法信息盘点.md").write_text("\n".join(lines), encoding="utf-8")
    detail = ["# 当前玩法卡牌与规则证据", "", "按原型 GUID 区分版本；数值来自游戏原型，文本不截断。",
              "能力按钮可能只是执行步骤；不会把按钮提示直接宣称为完整规则。", ""]
    for r in records:
        detail += [f"## {r['name']} · {LABELS.get(r['type'], r['type'])}", "",
                   f"`{r['id']}` · {' / '.join(r['sets'])} · {'固定配置' if r['scope']['core'] else '内容集候选'}", ""]
        detail += ["- " + x for x in r["structured_summary"]]
        if r["notes"]:
            detail += ["", "注意：" + "；".join(r["notes"])]
        for ab in r["abilities"]:
            special = [n for n in ab["classes"] if n not in GENERIC]
            if special:
                detail += ["", "能力：" + "/".join(special) + "；存档标记阶段：" + "/".join(ab["observed_timings"])]
                detail += ["- `" + t["key"] + "`：" + t["zh"] for t in ab["text_evidence"]]
        if r["legacy_evidence_unverified"]:
            detail += ["", "旧表证据（未自动采信）："]
            detail += ["- " + x["text"] + "〔" + x["source"] + "〕" for x in r["legacy_evidence_unverified"]]
        detail += ["", "<details><summary>完整原型属性</summary>", "", "```json",
                   json.dumps(r["attributes"], ensure_ascii=False, indent=2), "```", "", "</details>", ""]
    (ROOT / "docs/当前玩法卡牌规则.md").write_text("\n".join(detail), encoding="utf-8")


if __name__ == "__main__":
    build()
