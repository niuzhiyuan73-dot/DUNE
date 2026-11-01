# ===========================================================================
# 字段级完整度引擎（替换 build_current_mode_catalog.py 中的同名区块）
# ===========================================================================

FIELD_KEYS = ["cost", "faction", "spaces", "deploy_factions", "agent_effect", "reveal_effect",
              "purchase_effect", "passive_effect", "card_effect", "conditions", "limits"]
FIELD_LABELS = {"cost": "费用", "faction": "阵营", "spaces": "可进入空间", "deploy_factions": "可派遣特使图标",
                "agent_effect": "特使效果", "reveal_effect": "展示效果", "purchase_effect": "购买效果",
                "passive_effect": "被动/触发效果", "card_effect": "卡面主效果文本", "conditions": "条件",
                "limits": "次数限制"}
# 直接决定卡牌怎么打的字段，缺口排序时权重更高；conditions/limits 是派生字段。
CORE_FIELDS = ["cost", "faction", "spaces", "agent_effect", "reveal_effect", "purchase_effect",
               "passive_effect", "card_effect"]
FIELD_LABELS_ORDER = FIELD_LABELS

COST_KEYS = ("PersuasionCost", "SpiceCost", "WaterCost", "SolariCost", "SpecimenCost")
# 展示（揭示）栏印刷的图标数值。
REVEAL_RESOURCE_KEYS = ("Spice", "Water", "Solari", "Troops", "Persuasion", "Strength", "Specimen")
# 特使栏印刷的图标数值。
AGENT_RESOURCE_KEYS = ("AgentSpice", "AgentWater", "AgentSolari", "AgentTroops", "AgentSpecimen")
SPACE_ICONS = ("Circle", "Triangle", "Pentagon")
FACTION_ICONS = ("Emperor", "SpacingGuild", "BeneGesserit", "Fremen", "Spy")
GENERIC_ABILITY_CLASSES = {"AgentAbility", "RevealAbility", "AcquireAbility", "ActiveAbility", "SpaceAbility"}

# 没有费用概念的卡型：冲突/冲突奖励/合约/领袖都不是"花影响力买"的牌。
COST_LESS_TYPES = ("Conflict", "ConflictReward", "Contract", "Leader", "LeaderUpgrade")
# 不接收特使、因此没有"可进入空间"的卡型。
SPACE_LESS_TYPES = ("Conflict", "ConflictReward", "Intrigue", "Contract", "Leader", "LeaderUpgrade",
                    "TechTile")

# 每个卡型有哪些字段位。键为字段，值为该字段的判定方式：
#   "attr:<键>"     从原型属性取（缺失即无该属性）
#   "attrlist:<键>"  从原型列表属性取（缺失即空列表）
#   "text:<槽位>"    从官方本地化印刷文本取
#   "effect"         由存档观察到的能力文本推导
#   "—"              该卡型没有这个字段位
TYPE_FIELD_PLAN = {
    "Imperium": {"cost": "attr:PersuasionCost", "faction": "attrlist:FactionList",
                 "spaces": "attrlist:IconList", "deploy_factions": "attrlist:IconList",
                 "agent_effect": "text:agent", "reveal_effect": "text:reveal",
                 "purchase_effect": "attrlist:AcquireEffectList", "passive_effect": "text:static",
                 "card_effect": "text:agent", "conditions": "derived", "limits": "derived"},
    "Tleilaxu": {"cost": "attr:SpecimenCost", "faction": "attrlist:FactionList",
                  "spaces": "attrlist:IconList", "deploy_factions": "attrlist:IconList",
                  "agent_effect": "text:agent", "reveal_effect": "text:reveal",
                  "purchase_effect": "attrlist:AcquireEffectList", "passive_effect": "text:static",
                  "card_effect": "text:agent", "conditions": "derived", "limits": "derived"},
    "Intrigue": {"cost": "attr:Rating", "faction": "—", "spaces": "—", "deploy_factions": "—",
                 "agent_effect": "—", "reveal_effect": "—", "purchase_effect": "—",
                 "passive_effect": "—", "card_effect": "text:text", "conditions": "derived",
                 "limits": "derived"},
    "TechTile": {"cost": "attr:SpiceCost", "faction": "—", "spaces": "—", "deploy_factions": "—",
                 "agent_effect": "—", "reveal_effect": "—", "purchase_effect": "attrlist:AcquireEffectList",
                 "passive_effect": "text:ability", "card_effect": "text:ability",
                 "conditions": "derived", "limits": "derived"},
    "Leader": {"cost": "—", "faction": "attr:LeaderHouse", "spaces": "—", "deploy_factions": "—",
               "agent_effect": "—", "reveal_effect": "—", "purchase_effect": "—",
               "passive_effect": "text:leader", "card_effect": "text:leader",
               "conditions": "derived", "limits": "derived"},
    "LeaderUpgrade": {"cost": "—", "faction": "attr:LeaderHouse", "spaces": "—", "deploy_factions": "—",
                      "agent_effect": "—", "reveal_effect": "—", "purchase_effect": "—",
                      "passive_effect": "text:leader", "card_effect": "text:leader",
                      "conditions": "derived", "limits": "derived"},
    "Space": {"cost": "attr:SpaceCost", "faction": "attr:SpaceFaction", "spaces": "—",
              "deploy_factions": "attr:AgentIcon", "agent_effect": "—", "reveal_effect": "—",
              "purchase_effect": "—", "passive_effect": "text:space", "card_effect": "text:space",
              "conditions": "derived", "limits": "derived"},
    "Contract": {"cost": "attr:SpiceCost", "faction": "—", "spaces": "—", "deploy_factions": "—",
                 "agent_effect": "—", "reveal_effect": "—", "purchase_effect": "—",
                 "passive_effect": "—", "card_effect": "—", "conditions": "derived", "limits": "derived"},
    "Conflict": {"cost": "—", "faction": "—", "spaces": "—", "deploy_factions": "—",
                 "agent_effect": "—", "reveal_effect": "—", "purchase_effect": "—",
                 "passive_effect": "—", "card_effect": "—", "conditions": "derived", "limits": "derived"},
    "ConflictReward": {"cost": "—", "faction": "—", "spaces": "—", "deploy_factions": "—",
                       "agent_effect": "—", "reveal_effect": "—", "purchase_effect": "—",
                       "passive_effect": "—", "card_effect": "text:reward",
                       "conditions": "derived", "limits": "derived"},
}

# 本地化键 -> 印刷槽位。
_SLOT_RULES = (
    ("agent", re.compile(r"\.agent\d*(?:\.hagal)?$")),
    ("reveal", re.compile(r"\.reveal\d*(?:\.hagal)?$")),
    ("static", re.compile(r"\.static$")),
    ("ability", re.compile(r"\.ability$")),
    ("text", re.compile(r"\.text\d*(?:\.hagal)?$")),
    ("leader", re.compile(r"\.(?:staticability|signetability)\.desc$")),
    ("desc", re.compile(r"\.desc$")),
    ("reward", re.compile(r"\.reward\.\d+$")),
    ("space", re.compile(r"^tooltip\.space\.")),
)
_CONDITION_CLAUSE = re.compile(r"[^。；;]{0,50}?(?:若|如果|假如|除非|前提|要求|需要|每当|当[^。；;]{0,12}时)[^。；;]{0,60}")
_LIMIT_CLAUSE = re.compile(r"[^。；;]{0,50}?(?:最多|至多|不超过|仅能|只能|一次|每局游戏|每回合|每轮|每次|每名|每个)[^。；;]{0,50}")
# 条件/限制也可以由原型属性直接给出（影响力要求、影响力门槛）。
_ATTR_CONDITION_KEYS = ("InfluenceRequirements", "FactionInfluence")


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


def _slot_of(key):
    """判断本地化键印刷在哪个效果槽位。"""
    for slot, rx in _SLOT_RULES:
        if rx.search(key):
            return slot
    return None


def _ability_slot(classes, timings):
    """能力类名 + 存档阶段 -> 该能力驱动哪个效果字段。

    存档里的 AbilityTiming 是引擎自己的阶段标记，比类名更可靠；
    类名只在没有阶段记录时兜底。
    """
    joined = " ".join(classes)
    for timing, slot in (("Agent", "agent_effect"), ("Reveal", "reveal_effect"),
                         ("Combat", "passive_effect"), ("CombatResolution", "passive_effect"),
                         ("Endgame", "passive_effect")):
        if timing in timings:
            return slot
    for marker, slot in (("Static", "passive_effect"), ("Signet", "passive_effect"),
                         ("Bond", "passive_effect"), ("Discard", "passive_effect"),
                         ("Acquire", "purchase_effect"), ("Reveal", "reveal_effect"),
                         ("Agent", "agent_effect")):
        if marker in joined:
            return slot
    return "passive_effect"


def space_tooltip_key(image, name_key, loc):
    """版图位置的印刷规则文本键（tooltip.space.*）。

    同一位置在不同扩展下有不同规则（.up/.immo/.choam 后缀），必须按图��名精确对应，
    找不到就返回 None —— 不能拿基础版规则冒充扩展版。
    """
    candidates = []
    for source in (image or "", (name_key or "").replace("worm.space.", "")):
        if not source:
            continue
        for variant, suffix in (("up", ".up"), ("immortality", ".immo"), ("choam", ".choam")):
            if source.endswith(variant):
                candidates.append("tooltip.space." + source[:-len(variant)] + suffix)
        candidates.append("tooltip.space." + source)
    for key in candidates:
        if key in loc:
            return key
    return None


def assess_fields(typ, at, text_fields, ability_records, unresolved):
    """逐字段判定完整度。

    三态语义（用户明确要求，务必区分）：
      confirmed —— 已从可靠来源（游戏本地化 / 原型属性 / 存档能力）取到内容。
      absent    —— 已确认确实没有该效果，且能举出结构性依据。
      unknown   —— 尚未采集到：既没找到文本，也无法确认其不存在。

    效果类字段找不到文本时**默认 unknown**，不武断标 absent；
    只有能由原型结构证明该效果不存在时才标 absent。
    """
    plan = TYPE_FIELD_PLAN.get(typ)
    if plan is None:
        plan = {f: "—" for f in FIELD_KEYS}
    out = {}

    # 官方本地化印刷文本，按槽位归类。
    slot_text = {}
    for t in text_fields:
        slot = _slot_of(t["key"])
        zh = (t.get("zh") or "").strip()
        if not slot or not zh:
            continue
        slot_text.setdefault(slot, []).append((t["key"], zh))

    # 存档观察到的能力文本。通用脚手架能力（AgentAbility/RevealAbility/AcquireAbility…）
    # 的文本是交互提示（"把获取的那张牌放到什么位置？"），不是卡面规则，不能当效果证据。
    ability_text = {}
    for ab in ability_records:
        classes = ab.get("classes") or []
        if not classes or set(classes) <= GENERIC_ABILITY_CLASSES:
            continue
        slot = _ability_slot(classes, ab.get("observed_timings") or [])
        for ev in ab.get("text_evidence", []):
            zh = (ev.get("zh") or "").strip()
            if not zh:
                continue
            ability_text.setdefault(slot, []).append((ev["key"], zh))

    def _text_of(slots, allow_ability=True):
        """取指定印刷槽位的文本；allow_ability 时并入存档能力文本。"""
        out_items = []
        for slot in slots:
            out_items.extend(slot_text.get(slot) or [])
        if allow_ability:
            for slot in slots:
                out_items.extend(ability_text.get(slot) or [])
        return out_items

    def _join(items):
        return "；".join(z for _, z in items) if items else None

    def _slots_for(field):
        """该字段由哪些印刷槽位/能力槽位支撑。"""
        mapping = {
            "agent_effect": ["agent"],
            "reveal_effect": ["reveal"],
            "passive_effect": ["static", "leader", "desc", "ability"],
            "card_effect": ["agent", "reveal", "static", "leader", "text", "reward", "space", "ability"],
            "purchase_effect": [],
        }
        return mapping.get(field, [])

    for field in FIELD_KEYS:
        rule = plan.get(field, "—")
        if rule == "—":
            out[field] = _field("absent", source="archetype",
                                note="%s 类实体没有这个效果位" % typ)
            continue
        kind, _, arg = rule.partition(":")

        if kind == "attr":
            if arg in at and at[arg] not in (None, [], {}, ""):
                out[field] = _field("confirmed", value=at[arg], source="game_attribute",
                                    note="原型属性 " + arg)
            elif arg in at:
                out[field] = _field("absent", source="game_attribute",
                                    note="原型属性 " + arg + " 为空")
            elif typ in COST_LESS_TYPES and field == "cost":
                out[field] = _field("absent", source="archetype",
                                    note="%s 类实体没有费用概念" % typ)
            else:
                out[field] = _field("unknown", source="none",
                                    note="原型属性表里没有 " + arg + "，该字段未采集到")
            continue

        if kind == "attrlist":
            if arg not in at:
                # 该键在本卡型的其它原型上出现过，说明它是可选字段；本原型未设置即为空。
                out[field] = _field("absent", source="game_attribute",
                                    note="原型属性表里没有 " + arg + "（导出省略空列表），即为空")
                continue
            raw = at[arg] or []
            if field == "spaces":
                picked = [i for i in raw if i in SPACE_ICONS]
                if not picked:
                    out[field] = _field("absent", source="game_attribute",
                                        note="原型 IconList 只有派系图标，没有空间图标")
                    continue
                out[field] = _field("confirmed", value=picked, source="game_attribute",
                                    note="原型 IconList 中的空间图标")
                continue
            if field == "deploy_factions":
                picked = [i for i in raw if i in FACTION_ICONS]
                if not picked:
                    out[field] = _field("absent", source="game_attribute",
                                        note="原型 IconList 只有空间图标，没有派系图标")
                    continue
                out[field] = _field("confirmed", value=picked, source="game_attribute",
                                    note="原型 IconList 中的派系图标")
                continue
            out[field] = _field("confirmed", value=list(raw), source="game_attribute",
                                note="原型属性 " + arg)
            continue

        if kind == "text":
            items = _text_of([arg])
            text = _join(items)
            if text:
                out[field] = _field("confirmed", text=text, source="game_localization",
                                    note="本地化键 " + items[0][0])
            elif field == "card_effect" and _slots_for(field):
                items = _text_of(_slots_for(field))
                text = _join(items)
                if text:
                    out[field] = _field("confirmed", text=text, source="game_localization",
                                        note="本地化键 " + items[0][0])
                else:
                    out[field] = _field("unknown", source="none",
                                        note="该卡的印刷文本尚未采集到")
            else:
                out[field] = _field("unknown", source="none",
                                    note="该卡的印刷文本尚未采集到")
            continue

        if kind == "derived":
            continue

    # ---- 效果类字段：印刷文本 + 存档能力文本 ----
    for field in ("agent_effect", "reveal_effect", "purchase_effect", "passive_effect"):
        if out.get(field, {}).get("status") == "absent":
            continue
        items = _text_of(_slots_for(field))
        text = _join(items)
        if text:
            out[field] = _field("confirmed", text=text,
                                source="save_observation" if all(
                                    k in ability_text.get(s, []) for k, _ in items) else "game_localization",
                                note=("本地化键 " if items[0][0].startswith("card.") else "存档能力文本 ")
                                     + items[0][0])
        elif field == "purchase_effect" and "AcquireEffectList" in at:
            out[field] = _field("confirmed", value=list(at["AcquireEffectList"]),
                                source="game_attribute", note="原型 AcquireEffectList（获取时附带的图标效果）")
        else:
            out[field] = _field("unknown", source="none",
                                note="该卡的这一栏效果文本尚未采集到（可能存在，只是没取到）")

    # ---- 卡面主效果文本 ----
    if out.get("card_effect", {}).get("status") != "absent":
        items = _text_of(_slots_for("card_effect"))
        text = _join(items)
        if text:
            out["card_effect"] = _field("confirmed", text=text, source="game_localization",
                                        note="本地化键 " + items[0][0])
        else:
            out["card_effect"] = _field("unknown", source="none",
                                        note="该卡的印刷正文尚未采集到")

    # ---- 条件 / 次数限制：从已确认的规则文本与原型属性里识别 ----
    confirmed_text = "；".join(filter(None, [
        out.get("agent_effect", {}).get("text"), out.get("reveal_effect", {}).get("text"),
        out.get("purchase_effect", {}).get("text"), out.get("passive_effect", {}).get("text"),
        out.get("card_effect", {}).get("text")]))
    attr_conditions = {k: at[k] for k in _ATTR_CONDITION_KEYS if k in at and at[k]}
    cond = _CONDITION_CLAUSE.search(confirmed_text)
    lim = _LIMIT_CLAUSE.search(confirmed_text)
    if cond:
        out["conditions"] = _field("confirmed", text=cond.group(0), source="game_localization",
                                   note="从已采集的规则文本中识别到条件子句")
    elif attr_conditions:
        out["conditions"] = _field("confirmed", value=attr_conditions, source="game_attribute",
                                   note="原型属性: " + ", ".join(attr_conditions))
    elif confirmed_text:
        out["conditions"] = _field("unknown", source="none",
                                   note="已采集的规则文本中未出现条件子句；其余效果尚未采集，不能断定无条件")
    else:
        out["conditions"] = _field("unknown", source="none", note="尚无可用规则文本，条件未采集到")
    if lim:
        out["limits"] = _field("confirmed", text=lim.group(0), source="game_localization",
                               note="从已采集的规则文本中识别到次数/回合限制子句")
    elif confirmed_text:
        out["limits"] = _field("unknown", source="none",
                               note="已采集的规则文本中未出现次数限制子句；其余效果尚未采集，不能断定无限制")
    else:
        out["limits"] = _field("unknown", source="none", note="尚无可用规则文本，次数限制未采集到")

    # 未解析能力会拉低整体可信度，标注出来但不改变字段状态。
    if unresolved:
        out["_unresolved_abilities"] = len(unresolved)
    return out
