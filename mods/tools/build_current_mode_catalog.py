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

# ---------------------------------------------------------------------------
# 字段级完整度（field-level completeness）
# 三态：confirmed 已从可靠来源取到内容；absent 有结构证据证明确实没有该效果；
#       unknown 该效果可能存在但尚未采集到内容。
# 来源分层：game_localization 官方本地化；game_attribute 原型属性值；
#           archetype 原型结构证据；save_observation 存档能力实体/阶段；
#           legacy_unverified 旧补充表（只作待核对线索，不足以确认）；none 无来源。
FIELD_KEYS = ["cost", "faction", "spaces", "deploy_factions", "agent_effect", "reveal_effect",
              "purchase_effect", "passive_effect", "card_effect", "conditions", "limits"]
FIELD_LABELS = {"cost": "费用", "faction": "阵营", "spaces": "可进入空间", "deploy_factions": "可派遣特使图标",
                "agent_effect": "特使效果", "reveal_effect": "展示效果", "purchase_effect": "购买效果",
                "passive_effect": "被动/触发效果", "card_effect": "卡面主效果文本", "conditions": "条件",
                "limits": "次数限制"}
# 直接决定卡牌可玩性的核心字段（缺口排序权重高）；conditions/limits 为派生字段。
CORE_FIELDS = ["cost", "faction", "spaces", "agent_effect", "reveal_effect", "purchase_effect",
               "passive_effect", "card_effect"]
SPACE_ICONS = ("Circle", "Triangle", "Pentagon")
FACTION_ICONS = ("Emperor", "SpacingGuild", "BeneGesserit", "Fremen", "Spy")
REVEAL_RESOURCES = ("Spice", "Water", "Solari", "Troops", "Specimen", "Strength", "Persuasion")
COST_KEYS = ("PersuasionCost", "SpiceCost", "WaterCost", "SolariCost", "SpecimenCost")
# 完全没有费用概念的卡型（冲突/奖励/合约/领袖等不是"花影响力买"的牌）。
COST_LESS_TYPES = ("Conflict", "ConflictReward", "Contract", "Leader", "LeaderUpgrade")
# 不落在版图空间上的卡型（它们没有"可进入空间"这一维度）。
SPACE_LESS_TYPES = ("Conflict", "ConflictReward", "Intrigue", "Contract", "Leader", "LeaderUpgrade",
                    "TechTile")
GENERIC_ABILITY_CLASSES = {"AgentAbility", "RevealAbility", "AcquireAbility", "ActiveAbility", "SpaceAbility"}

_SLOT_RULES = (
    ("agent", re.compile(r"\.agent\d*(?:\.hagal)?$")),
    ("reveal", re.compile(r"\.reveal\d*(?:\.hagal)?$")),
    ("static", re.compile(r"\.static$")),
    ("ability", re.compile(r"\.ability$")),
    # 规则正文：.text / .text1 / .text2 以及领袖能力的 .desc
    # （实测本地化后缀分布：title 362 / agent 108 / reveal 82 / desc 49 / text 48，
    #   .desc 是领袖戒指能力等规则文本的主要来源，不能漏掉。）
    ("text", re.compile(r"\.text\d*(?:\.hagal)?$")),
    ("leader", re.compile(r"\.(?:staticability|signetability)\.desc$")),
    ("desc", re.compile(r"\.desc$")),
    ("reward", re.compile(r"\.reward\.\d+$")),
)
_CONDITION_CLAUSE = re.compile(r"[^。；;]{0,50}?(?:若|如果|假如|除非|前提|要求|需要|每当|当[^。；;]{0,12}时)[^。；;]{0,60}")
_LIMIT_CLAUSE = re.compile(r"[^。；;]{0,50}?(?:最多|至多|不超过|仅能|只能|一次|每局游戏|每回合|每轮|每次|每名|每个)[^。；;]{0,50}")


def read_json(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def _slot_of(key):
    """判断本地化键属于哪个规则槽位（特使/展示/主效果/被动…）。"""
    for slot, rx in _SLOT_RULES:
        if rx.search(key):
            return slot
    return None


def _ability_slot(ability):
    """按能力类名与存档阶段判断该能力驱动哪个效果槽位。

    存档里的 AbilityTiming 是引擎自己的阶段标记；类名则直接写着这张能力
    属于哪一栏（…RevealAbility / …AgentAbility / …AcquireAbility）。
    两者结合比单看一个可靠。通用脚手架能力（AgentAbility 等）只说明
    "这一栏存在"，其文本是交互提示，不作为规则正文。
    """
    classes = ability.get("classes") or []
    timings = ability.get("observed_timings") or []
    joined = " ".join(classes)
    for marker, slot in (("Reveal", "reveal_effect"), ("Agent", "agent_effect"),
                         ("Acquire", "purchase_effect"), ("Static", "passive_effect"),
                         ("Signet", "passive_effect"), ("Bond", "passive_effect"),
                         ("Discard", "passive_effect")):
        if marker in joined:
            return slot
    if "Agent" in timings:
        return "agent_effect"
    if "Reveal" in timings:
        return "reveal_effect"
    if {"Combat", "CombatResolution", "Endgame"} & set(timings):
        return "passive_effect"
    return None


def _field(status, value=None, text=None, source=None, note=None):
    item = {"status": status}
    if value is not None:
        item["value"] = value
    if text is not None:
        item["text"] = text
    item["source"] = source or "none"
    if note:
        item["note"] = note
    return item


def assess_fields(typ, at, text_fields, ability_records, unresolved):
    """逐字段判定完整度。

    三态语义（用户明确要求，务必区分）：
      confirmed —— 已从可靠来源（游戏本地化 / 原型属性）取到内容，确认存在。
      absent    —— 已确认确实没有该效果，且能举出结构性依据。
      unknown   —— 尚未采集到：既没找到文本，也无法确认其不存在。

    效果类字段找不到文本时**默认 unknown**，不武断标 absent；
    只有能由原型结构证明该效果不存在时才标 absent。
    """
    out = {}

    # ---- 属性类：费用 / 阵营 / 可进入空间 / 可派遣图标 ----
    cost_parts = {}
    for f in COST_KEYS:
        if f in at:
            cost_parts[f] = at[f]
    if cost_parts:
        out["cost"] = _field("confirmed", value=cost_parts, source="game_attribute",
                              note="；".join("%s=%s" % (k.replace("Cost", ""),
                                                json.dumps(v, ensure_ascii=False))
                                            for k, v in cost_parts.items()))
    else:
        # 原型属性表里没有费用字段。费用是该卡型的一等属性时判 absent，
        # 否则只能判 unknown（无法区分"确实免费"与"尚未采集"）。
        out["cost"] = _field("absent" if typ in COST_LESS_TYPES else "unknown",
                              source="archetype" if typ in COST_LESS_TYPES else "none",
                              note=("%s 类实体没有费用概念（原型属性表无任何费用字段）" % typ
                                    if typ in COST_LESS_TYPES else "原型属性里没有费用字段，未采集到"))

    # 阵营：卡牌的 FactionList / 领袖的 LeaderHouse / 版图位置的 Faction。
    faction_val = None
    for f in ("FactionList", "LeaderHouse", "Faction", "Factions", "DeployFactions"):
        if f in at and at[f]:
            faction_val = at[f]
            break
    if faction_val is not None:
        out["faction"] = _field("confirmed", value=faction_val, source="game_attribute")
    else:
        # 无阵营属性的原型（冲突/空间/科技等）确实没有"阵营"这一维度 —— 但只有当
        # 该类型本就不属于"有阵营的卡"时才判 absent，否则判 unknown。
        if typ in ("Conflict", "ConflictReward", "Space", "TechTile", "Contract", "Tleilaxu"):
            out["faction"] = _field("absent", source="archetype",
                                    note="%s 类实体本身没有阵营属性" % typ)
        else:
            out["faction"] = _field("unknown", source="none", note="未采集到阵营信息")

    # IconList 同时装了空间图标（Circle/Triangle/Pentagon）与派系图标
    # （Emperor/SpacingGuild/…）。"可进入空间"和"可派遣派系"是两个不同的问题，
    # 不能把同一份列表同时填进两个字段。
    icons = list(at.get("IconList", []) or [])
    space_icons = [i for i in icons if i in SPACE_ICONS]
    deploy_icons = [i for i in icons if i in FACTION_ICONS]
    if icons:
        out["spaces"] = _field("confirmed", value=space_icons, source="game_attribute",
                               note=("原型 IconList 中的空间图标" if space_icons
                                     else "原型 IconList 只有派系图标，没有空间图标"))
        out["deploy_factions"] = _field("confirmed", value=deploy_icons, source="game_attribute",
                                        note=("原型 IconList 中的派系图标" if deploy_icons
                                              else "原型 IconList 只有空间图标，没有派系图标"))
    elif at.get("AgentIcon"):
        # 版图位置用 AgentIcon 表示"可派遣的特使图标"。
        out["spaces"] = _field("absent", source="archetype",
                               note="版图位置实体本身不是可进入的空间")
        out["deploy_factions"] = _field("confirmed", value=[at["AgentIcon"]], source="game_attribute",
                                        note="原型 AgentIcon（该位置可部署的特使图标）")
    else:
        out["spaces"] = _field("absent" if typ in SPACE_LESS_TYPES else "unknown",
                               source="archetype" if typ in SPACE_LESS_TYPES else "none",
                               note=("%s 类实体不在版图空间上" % typ if typ in SPACE_LESS_TYPES
                                     else "原型属性里没有空间图标字段，未采集到"))
        out["deploy_factions"] = _field("absent" if typ in SPACE_LESS_TYPES else "unknown",
                                        source="archetype" if typ in SPACE_LESS_TYPES else "none",
                                        note=("%s 类实体没有可派遣特使图标" % typ if typ in SPACE_LESS_TYPES
                                              else "原型属性里没有可派遣图标字段，未采集到"))

    # ---- 效果类：按本地化槽位归类 ----
    slot_text = {}
    for t in text_fields:
        slot = _slot_of(t["key"])
        if not slot:
            continue
        zh = (t.get("zh") or "").strip()
        if not zh:
            continue
        slot_text.setdefault(slot, []).append((t["key"], zh))

    # 存档里观察到的能力文本。通用脚手架能力（AgentAbility/RevealAbility/
    # AcquireAbility…）的文本只是交互提示（例："把获取的那张牌放到什么位置？"），
    # 不是规则正文，不能当效果证据；只有卡牌专属能力才算。
    ability_text = {}
    for ab in ability_records:
        classes = ab.get("classes") or []
        if not classes or set(classes) <= GENERIC_ABILITY_CLASSES:
            continue
        slot = _ability_slot(ab)
        if not slot:
            continue
        for ev in ab.get("text_evidence", []):
            zh = (ev.get("zh") or "").strip()
            if zh:
                ability_text.setdefault(slot, []).append((ev["key"], zh))

    def _joined(slot):
        items = slot_text.get(slot) or []
        if not items:
            return None
        return "；".join(z for _, z in items)

    def _joined_ability(slot):
        items = ability_text.get(slot) or []
        if not items:
            return None
        return "；".join(z for _, z in items)

    def _effect(slot, label):
        """一个效果栏的判定：先看官方印刷文本，再看存档里的卡牌专属能力文本。"""
        text = _joined(slot)
        if text:
            return _field("confirmed", text=text, source="game_localization",
                          note="本地化键 " + slot_text[slot][0][0])
        text = _joined_ability(slot)
        if text:
            return _field("confirmed", text=text, source="save_observation",
                          note="存档中该卡专属能力的文本证据: " + ability_text[slot][0][0])
        if slot in slot_text or slot in ability_text:
            return _field("unknown", source="none", note="已定位到该栏文本但内容为空")
        return _field("unknown", source="none",
                      note="未采集到%s文本（可能是纯数值牌，也可能尚未提取）" % label)

    out["agent_effect"] = _effect("agent", "特使效果")
    out["reveal_effect"] = _effect("reveal", "展示效果")

    # 购买效果：卡面印刷的获取图标（AcquireEffectList），属于属性证据。
    if at.get("AcquireEffectList"):
        out["purchase_effect"] = _field("confirmed", value=list(at["AcquireEffectList"]),
                                        source="game_attribute",
                                        note="原型 AcquireEffectList（获取时附带的图标效果）")
    else:
        out["purchase_effect"] = _effect("ability", "购买效果")
        if out["purchase_effect"]["status"] == "unknown" and typ in PURCHASE_LESS_TYPES:
            out["purchase_effect"] = _field("absent", source="archetype",
                                            note="%s 类实体没有购买效果" % typ)

    else:
        out["purchase_effect"] = _field("unknown", source="none",
                                        note="未采集到购买效果文本")

    # 被动/触发效果：.static / 领袖戒指能力 / 其它 .desc 能力描述
    passive = _joined("static") or _joined("leader") or _joined("desc") or None
    if passive:
        out["passive_effect"] = _field("confirmed", text=passive, source="game_localization")
    elif typ in ("Conflict", "ConflictReward", "Space", "Contract"):
        out["passive_effect"] = _field("absent", source="archetype",
                                       note="%s 类实体没有被动/触发效果" % typ)
    else:
        out["passive_effect"] = _field("unknown", source="none",
                                       note="未采集到被动/触发效果文本")

    # 卡面主效果文本：.text 槽（阴谋牌等）
    card_text = _joined("text")
    if card_text:
        out["card_effect"] = _field("confirmed", text=card_text, source="game_localization")
    elif typ in ("Leader", "LeaderUpgrade"):
        # 领袖没有 .text 槽，规则写在 .desc 能力描述里
        out["card_effect"] = _field("confirmed", text=_joined("leader") or _joined("desc") or "",
                                    source="game_localization")
    else:
        out["card_effect"] = _field("unknown", source="none", note="未采集到卡面主效果文本")

    # ---- 派生类：条件 / 次数限制（从已确认的效果文本里识别）----
    all_text = "；".join([agent_text or "", reveal_text or "", purchase_text or "",
                           passive or "", card_text or ""]).strip()
    if all_text:
        cond = _CONDITION_CLAUSE.search(all_text)
        lim = _LIMIT_CLAUSE.search(all_text)
        out["conditions"] = (_field("confirmed", text=cond.group(0), source="game_localization")
                             if cond else _field("absent", source="archetype",
                                                 note="已确认的规则文本里没有条件子句"))
        out["limits"] = (_field("confirmed", text=lim.group(0), source="game_localization")
                         if lim else _field("absent", source="archetype",
                                            note="已确认的规则文本里没有次数/上限子句"))
    else:
        out["conditions"] = _field("unknown", source="none", note="无可用文本，条件未采集到")
        out["limits"] = _field("unknown", source="none", note="无可用文本，次数限制未采集到")

    # 未解析能力会拉低整体可信度，标注在 note 上但不改变字段状态
    if unresolved:
        out["_unresolved_abilities"] = len(unresolved)
    return out


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
        field_completeness = assess_fields(typ, at, text_fields, ability_records, unresolved)
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
                        "unresolved_abilities": unresolved, "field_completeness": field_completeness,
                        "notes": notes})
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
    field_report(catalog)
    print(json.dumps(catalog["statistics"], ensure_ascii=False, indent=2))


def field_report(catalog):
    """按卡、按字段统计完整度，产出缺口清单。

    旧 gaps.txt 只检查"有没有文本"，既不代表全卡库完整度，也无法区分
    "确认没有该效果"与"该效果尚未采集到"。这里按字段重建口径。
    """
    records = catalog["records"]

    # 按类型统计完整率
    by_type = {}
    for r in records:
        fc = r.get("field_completeness") or {}
        d = by_type.setdefault(r["type"], collections.defaultdict(int))
        for f in FIELD_KEYS:
            st = (fc.get(f) or {}).get("status", "unknown")
            d[st] += 1
            d["total"] += 1

    # 逐条缺失（只列 unknown / absent）
    gaps = []
    for r in records:
        fc = r.get("field_completeness") or {}
        missing = []
        for f in FIELD_KEYS:
            st = (fc.get(f) or {}).get("status", "unknown")
            if st == "confirmed":
                continue
            item = fc.get(f) or {}
            missing.append({"field": f, "label": FIELD_LABELS.get(f, f),
                            "status": st, "note": item.get("note", "")})
        if missing:
            gaps.append({"id": r["id"], "name": r["name"], "type": r["type"],
                         "sets": r["sets"], "core": r["scope"]["core"],
                         "missing": missing})

    # 缺口排序：核心条目优先、核心字段缺失多的优先
    def _rank(g):
        core_missing = sum(1 for m in g["missing"]
                           if m["status"] == "unknown" and m["field"] in CORE_FIELDS)
        return (0 if g["core"] else 1, -core_missing, g["type"], g["name"])

    gaps.sort(key=_rank)

    payload = {"schema_version": 1,
               "field_labels": FIELD_LABELS,
               "status_meaning": {"confirmed": "已确认存在（有可靠来源）",
                                  "absent": "已确认没有该效果（有结构证据）",
                                  "unknown": "尚未采集到（无法确认存在与否）"},
               "statistics": {"records": len(records),
                              "records_with_missing": len(gaps),
                              "by_type": {k: dict(v) for k, v in by_type.items()}},
               "gaps": gaps}
    write_json(ROOT / "mods/ai/field_gaps.json", payload)

    # 人类可读文档
    lines = ["# 当前玩法字段完整度盘点", "",
             "来源：只读原型导出 + 官方本地化 + 现有存档。字段状态三态严格区分：", "",
             "- `confirmed` 已确认存在（有可靠来源）",
             "- `absent` 已确认没有该效果（有结构证据）",
             "- `unknown` 尚未采集到（无法确认存在与否）", "",
             "> 旧 gaps.txt 只统计运行时遇到的「有没有文本」，不能代表全卡库完整度；",
             "> 本表按卡、按字段重建口径，数字全部来自本次重新统计。", "",
             "## 按类型的字段完整率", "",
             "| 类型 | 条目 | confirmed | absent | unknown | 完整率 |", "|---|---:|---:|---:|---:|---:|"]
    for typ in sorted(by_type):
        d = by_type[typ]
        total = d["total"] or 1
        rate = 100.0 * d["confirmed"] / total
        lines.append("| %s | %d | %d | %d | %d | %.0f%% |"
                     % (LABELS.get(typ, typ), total // max(1, len(FIELD_KEYS)),
                        d["confirmed"], d["absent"], d["unknown"], rate))
    lines += ["", "## 待采集清单", "",
              "按「固定开局/冲突的核心条目优先、核心字段缺失多的优先」排序。", "",
              "| 名称 | 类型 | 扩展 | 核心 | 缺失字段（状态） |", "|---|---|---|---|---|"]
    for g in gaps:
        detail = "；".join("%s(%s)" % (m["label"], m["status"]) for m in g["missing"])
        lines.append("| %s | %s | %s | %s | %s |"
                     % (g["name"], LABELS.get(g["type"], g["type"]),
                        "/".join(g["sets"]) or "-", "是" if g["core"] else "", detail))
    lines += ["", "## 说明", "",
              "- `absent` 只在能举出结构性依据时标注（例如冲突/空间/科技类实体本就没有购买效果）。",
              "- 效果类字段找不到文本时一律标 `unknown`，不武断标 `absent` ——",
              "  一张只提取到半张效果的卡必须显示为不完整，不能被误判成信息齐全。",
              "- 机器可读数据：`mods/ai/field_gaps.json`。",
              "- 重建：`python -X utf8 -B mods/tools/build_current_mode_catalog.py`。", ""]
    (ROOT / "docs/当前玩法字段完整度.md").write_text("\n".join(lines), encoding="utf-8")


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
