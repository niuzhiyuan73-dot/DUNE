"""生成 StandardSolo 玩法预设（纯数据，不改代码）。

两个模式：
    marathon  马拉松：20 分胜利，1 水 / 0 钱 / 0 香料，牌组额外多一张「控制香料」
    faction   分阵营：正常 10 分胜利，标准起始资源；玩家选一个阵营，
              三个 AI 随机分到剩下的三个阵营（同局四家阵营各不相同）

用法：
    python build_presets.py                                   # 生成马拉松 + 四个阵营版本
    python build_presets.py --mode faction --faction fremen --install
    python build_presets.py --mode marathon --install
    python build_presets.py --list
"""
import io
import os
import sys
import json
import random
import shutil
import UnityPy

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
OUT_DIR = os.path.join(ROOT, "mods", "out")
ORIG = os.path.join(ROOT, "backup", "game", "scenario_bundle.orig")
BUNDLE = "b56aa8ad35f9c2e1694994f384e51bbe.bundle"
TA_NAME = "StandardSolo"
TARGET = os.path.join(ROOT, "game", "DuneImperium_Data", "StreamingAssets", "aa",
                      "StandaloneWindows64", BUNDLE)

COLORS = ["Green", "Red", "Yellow", "Blue"]
SETS_ALL = ["BaseSet", "RiseOfIx", "Immortality", "Uprising", "CHOAMModule"]

# ---------------------------------------------------------------- 卡牌 GUID
# 起始牌组 10 张（官方构成：说服×2 匕首×2 沙丘×2 外交 侦查 寻求盟友 印章戒指）
CARD = {
    "ConvincingArgument": "8925fb5e-a8d3-49c7-8543-82537b4cb009",   # 说服
    "Dagger":             "8a8ec96e-5c0e-48de-b90d-6ec17e855922",   # 匕首
    "Dune":               "8fe820e9-79ee-492e-b4f1-b82b8c0997fa",   # 沙丘，沙漠行星
    "Diplomacy":          "8cdce934-fa2e-4d87-9e5e-c59eb5ed8b5e",   # 外交
    "Reconnaissance":     "966262f7-3078-4820-a0ca-804b84d62d3b",   # 侦查
    "SeekAllies":         "9828e28d-2ee7-43db-baa5-96b3218e6cbb",   # 寻求盟友
    "SignetRing":         "98753757-bceb-4cf7-8a9c-f5a00ff99dcd",   # 印章戒指
    "Experimentation":    "ed9924f1-4296-48ed-b526-8ac52e0d4df7",   # 实验（不朽，按规则顶替沙丘）

    "ControlTheSpice":    "7739751b-b378-42a1-8ebc-39c3b726bae1",   # 控制香料（伊克斯）
    "CrysknifeBase":      "1fc48973-aac9-4a36-a830-5624b0a85033",   # 晶牙匕·基础版
    "CrysknifeUprising":  "1db40eaa-32cd-451a-b593-22bc0de22f33",   # 晶牙匕·崛起版

    # 弗雷曼
    "MaulaPistol":        "55f17457-a0e3-4ab4-b9af-47aa4b1b90cf",   # 毛拉枪（崛起）
    "Jamis":              "12c54819-5594-4be4-b3a6-71af949d7cbc",   # 詹米（伊克斯）
    # 姐妹会
    "PrepareTheWay":      "c83f1b8e-9016-4e28-842c-912afa3ed28e",   # 暗中准备（崛起）
    "WeirdingWoman":      "0f5fc1e6-3b30-480b-872e-c11d125b916e",   # 奇异女士（崛起）
    "GeneManipulation":   "29a3e722-470a-48c8-9d2e-029d5ce7c294",   # 基因操控（基础）
    # 宇航工会
    "HighPriorityTravel": "11440b93-75c5-4bc3-a5ce-1a74f9fb4c57",   # 私人航班（不朽）
    "LandingRights":      "5608f37c-0690-4a3c-81a4-7713e3fa1fd6",   # 降落许可（伊克斯）
    "SmugglersHarvester": "012ad406-c96d-4857-b4c3-4fb6f3663352",   # 走私者爬虫机车（崛起）
    "SpaceTravel":        "2efba790-351c-4161-949e-c78177463857",   # 太空旅行（基础）
    "SatelliteBan":       "66905223-8751-4d8e-837e-db27285052b7",   # 卫星禁令（伊克斯）
    "IxGuildCompact":     "3132b0e5-d0c2-4b10-9274-e3899d814e9b",   # 伊克斯舰组（伊克斯）
    # 皇帝
    "DoubleAgent":        "48acf8d7-7c8f-40cd-9a3c-0ca7f8173b16",   # 双面间谍（崛起）
    "SardaukarSoldier":   "00438c41-0a06-40f8-9fa3-b13106e78f85",   # 萨多卡士兵（崛起）
    "ImperialShocktrooper": "3378b58e-84f1-4d3e-b6b3-7f1d6a879db1",  # 帝国镇暴兵（伊克斯）

    # Pro 版额外牌
    "ShaiHulud":          "75fc2bf0-e355-4ba5-a4c6-c5bfe98f4717",   # 巨沙虫夏胡鲁（伊克斯）
    "ForHumanity":        "db22f8b8-f11f-4ce2-9bbe-9c752145a4de",   # 为了全人类（不朽）
    "GuildAccord":        "7314a8fa-564d-431a-a564-63658f4448fc",   # 公会协约（伊克斯）
    "GuildEnvoy":         "4dda6b6b-40e2-4c63-8939-53b23a15a740",   # 公会使者（崛起）
    "FullScaleAssault":   "76a4030f-1c98-4075-baa4-239434454588",   # 全面进攻（伊克斯）

    # Extra 版额外牌
    "WormRiders":         "86381358-e763-49fa-a5d3-0a50acc6be5d",   # 沙虫骑士（基础）
    "LongLiveFighters":   "9dbdb78e-a792-417b-b7cd-49173e0dfbd8",   # 为勇士唱赞歌！（崛起）
    "KwisatzHaderach":    "86dea602-1c1e-4ad3-a1b1-eb3cb7fbab21",   # 魁萨茨·哈德拉克（基础）
    "Steersman":          "a3c2d1da-82e3-4e5d-bd42-bf70a61f5256",   # 公会领航员（崛起）
    "Opulence":           "772456a8-05ea-44fd-b599-abc1e70a1e97",   # 奢靡（基础）
    "SardaukarCoord":     "5f57324b-8d5d-43cf-99c7-ed018f01dde3",   # 萨多卡协同作战（崛起）
}

BASE_DECK = ([CARD["Dagger"]] * 2 + [CARD["Dune"]] * 2 + [CARD["ConvincingArgument"]] * 2
             + [CARD["Diplomacy"], CARD["Reconnaissance"], CARD["SeekAllies"], CARD["SignetRing"]])

# ---------------------------------------------------------------- 冲突牌（Pro 版用）
# 等级来源：存档里的 920007（ConflictLevel）。
# 其中 11 张在存档里没出场过，按约定归类：金融霸权 / 香料运输 记三级，
# 遭遇战（3 个变体）按一级处理不进牌组，其余记二级。
# ---------------------------------------------------------------- 科技牌（起始科技用）
TECH = {
    "HoltzmanEngine":   "db3857e8-8373-4f1a-8420-2669fc684204",   # 霍兹曼引擎（伊克斯）
    "RestrictedOrdnance": "aae7fdc9-dd0a-498a-8491-dddd6c6d583d",  # 弹药制约（伊克斯）
}

# ---------------------------------------------------------------- 叠加模组
# 每个模组只描述「在任意模式之上追加什么」，因此可以和所有模式 / 阵营复合。
MODS = {
    "holtzman": {
        "label": "霍尔兹曼跃迁",
        "note": "每人起始带霍兹曼引擎科技；寻求盟友换成伊克斯舰组（若已被移除则只加）",
        "techs": [TECH["HoltzmanEngine"]],
        "replace": [(CARD["SeekAllies"], CARD["IxGuildCompact"])],
        "require_sets": ["RiseOfIx"],
        "suffix": "holtz",
    },
    "lasttime": {
        "label": "最后的时代",
        # 实测语义（重要）：剑术大师自身会再解锁 1 个工人，实际可用特工数
        # = StartingAgents + (1 if StartingSwordmaster else 0)。
        # 所以「开局 3 个工人」必须写成 2 + 剑术大师；写 3 会变成 4 次行动。
        "note": "开局 3 个工人：起始特工 2 + 自带剑术大师（剑术大师本身再解锁 1 个）",
        "agents": 2,
        "swordmaster": True,
        "techs": [],
        "replace": [],
        "require_sets": [],
        "suffix": "last",
    },
    "lasttime_ultra": {
        "label": "最后的时代 Ultra",
        # 4 个工人：agents 3 + 剑术大师 1。这是最早那版「写满」的配置，
        # 当时被当成 bug；现在作为独立的 Ultra 档位保留，与 lasttime 互斥。
        "note": "开局 4 个工人：起始特工 3 + 自带剑术大师（剑术大师本身再解锁 1 个）",
        "agents": 3,
        "swordmaster": True,
        "techs": [],
        "replace": [],
        "require_sets": [],
        "suffix": "ultra",
        "excludes": ["lasttime"],
    },
    "council": {
        "label": "议会霸权",
        "note": "每人多一块弹药制约科技（高议会向），并在基础资源上再加 1 太阳币",
        "agents": None,
        "swordmaster": False,
        "techs": [TECH["RestrictedOrdnance"]],
        "replace": [],
        "solari": 1,
        "require_sets": ["RiseOfIx"],
        "suffix": "council",
    },
}

# 叠加时的固定顺序（决定文件名后缀顺序，也保证产物可复现）
# lasttime 与 lasttime_ultra 互斥，不会同时出现在同一个包里
MOD_ORDER = ["holtzman", "lasttime", "lasttime_ultra", "council"]

CONFLICT_T3_POOL = [
    "a5b5668a-706c-43dd-a6eb-a6b3abfb6907",   # 厄拉奇恩之战（基础）
    "db2b7a79-95a7-4f2d-9a3b-7c8c4ec6aa2c",   # 厄拉奇恩之战（崛起）
    "b409ad68-69c3-442c-b355-d671ca89ddd3",   # 迦太格之战（基础）
    "b4c07183-38d9-41df-a833-2ddb5580b00b",   # 帝国盆地之战（基础）
    "f22d9777-a59b-4655-aa4c-e2e96c90df73",   # 帝国盆地之战（崛起）
    "f17f8472-8f02-47b0-8e1d-0c20af9de20b",   # 香料加工厂之战（崛起）
    "c0f3e883-8540-40d6-a62e-101a69ecba0a",   # 远大目标（基础）
    "fa49748c-0b90-4f5a-821a-7bf96d241250",   # 宣传策略（崛起）
    "ad0d6867-133f-4f22-b8cf-7533837eeb9b",   # 金融霸权（伊克斯，约定为三级）
    "6f437fac-c60b-4783-aee4-304f8e5869ac",   # 香料运输（崛起，约定为三级）
]

CONFLICT_T2_POOL = [
    "9881835c-47a3-4079-b6fa-745fe340193d",   # 斗篷与匕首（基础）
    "559caca7-9b1b-4ed5-b86d-d9ebf71fe89d",   # 阴谋（基础）
    "4f2665a5-ba30-4ead-97da-4ff1132d4ee5",   # 突袭公会银行（基础）
    "8b63a1c7-7454-4ca8-9c81-739f1829965f",   # 突袭仓库（基础）
    "87f3b691-9d7b-4d71-8788-e2e1f5d796e8",   # 拨乱反正（基础）
    "34c4273b-8957-4503-a110-af8f81ba7841",   # 围攻厄拉奇恩（基础）
    "31acac91-5725-415e-95c2-b99965871445",   # 围攻厄拉奇恩（崛起）
    "3b65a9c6-7df7-4ee2-92c4-b9266e48d805",   # 围攻迦太格（基础）
    "4b236fb5-9347-4311-908f-96412471f3be",   # 保卫帝国盆地（基础）
    "56bbe4e4-9125-4587-851c-194570de0c1e",   # 保卫帝国盆地（崛起）
    "8570e6e9-3168-4f04-85c5-9e2e4467782a",   # 忠诚考验（崛起）
    "4cbbf7e5-c57e-474c-817f-6af4b0ecbd0b",   # 占领香料加工厂（崛起）
    "b155ad05-0c87-4ce7-8f6b-0a16db3c0ff8",   # 保护穴地（崛起）
    "55719bee-ea48-491c-9df1-0a90133b33c1",   # 恶意（基础，约定为二级）
    "96cae890-d20b-4322-b315-15ebc2496e9c",   # 贸易纠纷（崛起，约定为二级）
    "b603735c-013e-427f-8b5e-be8199412b87",   # 贸易纠纷（商会，约定为二级）
    "4eba8ebd-f199-4b13-acd2-3807bfd5d13f",   # 商会安保（崛起，约定为二级）
    "b45ba224-212c-48d3-b2b8-43927b4f9d69",   # 商会安保（商会，约定为二级）
    "abee3d39-8543-424d-b211-4b48f733202d",   # 贸易垄断（伊克斯，约定为二级）
    "75d73897-ec23-4c02-afaa-66c8d02d975a",   # 暗中竞赛（崛起，约定为二级）
]

# 遭遇战三个变体按一级处理，不进 Pro 牌组
CONFLICT_SKIRMISH = [
    "042e7cea-971d-475e-a129-3e7e6b5385d8",
    "2c39937d-0b0f-47d8-b9e1-6629bea11557",
]

# Pro 牌组：5 张二级 -> 5 张三级（兼顾基础 / 崛起 / 伊克斯，且前五场强度一致）
CONFLICT_T2 = [
    "9881835c-47a3-4079-b6fa-745fe340193d",   # 斗篷与匕首（基础）
    "559caca7-9b1b-4ed5-b86d-d9ebf71fe89d",   # 阴谋（基础）
    "4f2665a5-ba30-4ead-97da-4ff1132d4ee5",   # 突袭公会银行（基础）
    "8570e6e9-3168-4f04-85c5-9e2e4467782a",   # 忠诚考验（崛起，带间谍）
    "4cbbf7e5-c57e-474c-817f-6af4b0ecbd0b",   # 占领香料加工厂（崛起，带间谍）
]
CONFLICT_T3 = [
    "a5b5668a-706c-43dd-a6eb-a6b3abfb6907",   # 厄拉奇恩之战（基础）
    "b409ad68-69c3-442c-b355-d671ca89ddd3",   # 迦太格之战（基础）
    "b4c07183-38d9-41df-a833-2ddb5580b00b",   # 帝国盆地之战（基础）
    "c0f3e883-8540-40d6-a62e-101a69ecba0a",   # 远大目标（基础）
    "6f437fac-c60b-4783-aee4-304f8e5869ac",   # 香料运输（崛起，约定为三级）
]
CONFLICT_PRO = CONFLICT_T2 + CONFLICT_T3      # 5 张二级在前，5 张三级在后

# ---------------------------------------------------------------- 两个模式
MODES = {
    "marathon": {
        "label": "马拉松",
        "endgame": 20,
        "water": 1, "spice": 0, "solari": 0,
        "extra_deck": [CARD["ControlTheSpice"]],
        "use_faction": False,
        "conflict_shuffle_repeat": True,   # 10 场冲突打完继续洗牌重发
        "conflict_repeat_last": 0,
        "note": "20 分胜利，1 水开局，牌组多一张控制香料；冲突牌堆耗尽后重洗继续",
    },
    "faction": {
        "label": "分阵营",
        "endgame": 10,
        "water": 1, "spice": 0, "solari": 0,
        "extra_deck": [],
        "use_faction": True,
        "note": "10 分胜利，标准开局；你选一家，三个 AI 随机分到另外三家",
    },
    "faction_pro": {
        "label": "分阵营 Pro",
        "endgame": 12,
        "water": 1, "spice": 0, "solari": 0,
        "extra_deck": [],
        "use_faction": True,
        "conflict_deck": CONFLICT_PRO,
        "conflict_order": "natural",   # natural=交给游戏洗 / fixed=写死顺序 / shuffle=生成时随机
        "conflict_shuffle_repeat": False,
        "conflict_repeat_last": 0,
        "note": "12 分胜利，冲突牌堆改为 5 张二级 + 5 张三级（无一级冲突）",
    },
    "faction_extra": {
        "label": "分阵营 Extra",
        "endgame": 14,
        "water": 1, "spice": 0, "solari": 0,
        "extra_deck": [],
        "use_faction": True,
        "conflict_deck": CONFLICT_T3_POOL,   # 全部 10 张三级冲突
        "conflict_order": "natural",
        "conflict_shuffle_repeat": False,
        "conflict_repeat_last": 0,
        "note": "14 分胜利，10 张冲突全部为三级；Pro 加牌之上再各加两张",
    },
}

# ---------------------------------------------------------------- 四个阵营
FACTIONS = {
    "fremen": {
        "label": "弗雷曼",
        "add": [CARD["CrysknifeBase"], CARD["MaulaPistol"], CARD["Jamis"]],
        "remove": [CARD["Dune"], CARD["Dune"]],
        "water": 0, "spice": 0, "solari": 0,
        "random_intrigue": 0,
        "require_sets": ["RiseOfIx", "Uprising"],
        "pro_add": [CARD["ShaiHulud"]],
        "pro_remove": [],
        "extra_add": [CARD["WormRiders"], CARD["LongLiveFighters"]],
        "extra_remove": [CARD["Reconnaissance"], CARD["Jamis"]],
        "note": "+晶牙匕(基础)+毛拉枪+詹米，-2 沙丘",
    },
    "bene": {
        "label": "姐妹会",
        "add": [CARD["PrepareTheWay"], CARD["WeirdingWoman"], CARD["GeneManipulation"]],
        "remove": [CARD["Reconnaissance"], CARD["Dune"], CARD["Dagger"]],
        "water": 0, "spice": 0, "solari": 0,
        "random_intrigue": 1,
        "require_sets": ["Uprising"],
        "pro_add": [CARD["ForHumanity"]],
        "pro_remove": [],
        "extra_add": [CARD["KwisatzHaderach"]],
        "extra_remove": [CARD["Dagger"]],
        "note": "+1 随机密卡，+暗中准备+奇异女士+基因操控，-侦查-沙丘-匕首",
    },
    "guild": {
        "label": "宇航工会",
        "add": [CARD["HighPriorityTravel"], CARD["LandingRights"],
                CARD["SpaceTravel"], CARD["SatelliteBan"]],
        "remove": [CARD["Dagger"], CARD["Dune"], CARD["Reconnaissance"], CARD["SeekAllies"]],
        "water": 0, "spice": 1, "solari": 0,
        "random_intrigue": 0,
        "require_sets": ["RiseOfIx", "Immortality", "Uprising"],
        "pro_add": [CARD["GuildAccord"], CARD["GuildEnvoy"]],
        "pro_remove": [CARD["SpaceTravel"]],
        "extra_add": [CARD["Steersman"]],
        "extra_remove": [CARD["ConvincingArgument"]],   # 一张说服换回沙丘（沙丘不再被移除）
        "note": "+1 香料，+私人航班+降落许可+太空旅行+卫星禁令，-匕首-沙丘-侦查-寻求盟友",
    },
    "emperor": {
        "label": "皇帝",
        "add": [CARD["DoubleAgent"], CARD["SardaukarSoldier"], CARD["ImperialShocktrooper"]],
        "remove": [CARD["ConvincingArgument"], CARD["Dagger"], CARD["Reconnaissance"]],
        "water": 0, "spice": 0, "solari": 1,
        "random_intrigue": 0,
        "require_sets": ["RiseOfIx", "Uprising"],
        "pro_add": [CARD["FullScaleAssault"]],
        "pro_remove": [],
        "extra_add": [CARD["Opulence"], CARD["SardaukarCoord"]],
        "extra_remove": [CARD["ConvincingArgument"], CARD["Dagger"]],
        "note": "+1 太阳币，+双面间谍+萨多卡士兵+帝国镇暴兵，-说服-匕首-侦查",
    },
}

FACTION_ORDER = ["fremen", "bene", "guild", "emperor"]


# ---------------------------------------------------------------- 名称表（日志用）
_TITLES = None
_TYPES = {}
_CARD_CACHE = None


def titles():
    global _TITLES
    if _TITLES is not None:
        return _TITLES
    out = {}
    path = os.path.join(ROOT, "game", "DuneImperium_Data", "StreamingAssets",
                        "Localization", "win", "localization")
    if os.path.exists(path):
        env = UnityPy.load(path)
        # 语言包按顺序排列（繁体在前），优先挑简体那一张
        tables = []
        for o in env.objects:
            if o.type.name != "TextAsset":
                continue
            raw = o.read().m_Script
            if isinstance(raw, bytes):
                raw = raw.decode("utf-8", "replace")
            if "card.imperium.dagger.title" in raw:
                tables.append(raw)
        tables.sort(key=lambda t: 0 if "该帐号已存在" in t else 1)
        for raw in tables[:1]:
            for line in raw.splitlines():
                if not line.startswith("card.") or "=" not in line:
                    continue
                key, _, val = line.partition("=")
                parts = key.split(".")
                if len(parts) == 4 and parts[-1] == "title":
                    out[parts[2].lower()] = val.strip()
                    _TYPES.setdefault(parts[2].lower(), parts[1])
                elif len(parts) == 3:
                    out[parts[2].lower()] = val.strip()
                    _TYPES.setdefault(parts[2].lower(), parts[1])
            break
    _TITLES = out
    return out


def _cards_by_id():
    global _CARD_CACHE
    if _CARD_CACHE is None:
        p = os.path.join(ROOT, "dump", "cards_runtime.json")
        if os.path.exists(p):
            with open(p, encoding="utf-8-sig") as f:
                _CARD_CACHE = {c["id"]: c for c in json.load(f)}
        else:
            _CARD_CACHE = {}
    return _CARD_CACHE


def name_of(guid):
    """GUID -> 中文卡名；找不到就回退成短 ID。"""
    c = _cards_by_id().get(guid)
    if not c:
        return "?" + guid[:8]
    return titles().get(c["image"].lower(), c["image"])


def _is_intrigue(card):
    """type 字段在 interop 下读不出来，用本地化键的类别段判断。"""
    titles()
    return _TYPES.get(card["image"].lower()) == "intrigue"


def pick_random_intrigue(sets):
    cards = [c for c in _cards_by_id().values() if _is_intrigue(c)]
    pool = [c for c in cards if not sets or set(c.get("sets", [])) & set(sets)]
    if not pool:
        pool = cards
    if not pool:
        return None
    return random.choice(pool)["id"]


# ---------------------------------------------------------------- 阵营 -> 玩家配置
def faction_loadout(mode, faction_key, sets):
    """按模式和阵营算出这一家的牌组 / 资源 / 密卡。"""
    m = MODES[mode]
    fac = FACTIONS[faction_key] if faction_key else None

    deck = list(BASE_DECK) + list(m["extra_deck"])
    removed = []
    if fac:
        for gid in fac["remove"]:
            if gid in deck:
                deck.remove(gid)
                removed.append(gid)
            else:
                print(f"      [警告] 待删除卡不在牌组中: {name_of(gid)}")
        deck.extend(fac["add"])
        if mode in ("faction_pro", "faction_extra"):
            for gid in fac.get("pro_remove", []):
                if gid in deck:
                    deck.remove(gid)
                    removed.append(gid)
            deck.extend(fac.get("pro_add", []))
        if mode == "faction_extra":
            for gid in fac.get("extra_remove", []):
                if gid in deck:
                    deck.remove(gid)
                    removed.append(gid)
                else:
                    print(f"      [警告] Extra 待删除卡不在牌组中: {name_of(gid)}")
            deck.extend(fac.get("extra_add", []))

    # 注意：实测 StartingWater 被游戏忽略（四家都停在 1），
    # 所以水走 BonusStartingWater，绝对值仍写基础值。
    res = {
        "water": m["water"],
        "bonus_water": (fac["water"] if fac else 0),
        "spice": m["spice"] + (fac["spice"] if fac else 0),
        "solari": m["solari"] + (fac["solari"] if fac else 0),
    }
    intrigue = []
    if fac and fac["random_intrigue"]:
        for _ in range(fac["random_intrigue"]):
            gid = pick_random_intrigue(sets)
            if gid:
                intrigue.append(gid)
    # 不朽启用时，按官方规则用「实验」顶替沙丘
    swapped = 0
    if "Immortality" in sets:
        swapped = deck.count(CARD["Dune"])
        if swapped:
            deck = [CARD["Experimentation"] if g == CARD["Dune"] else g for g in deck]

    return deck, res, intrigue, removed, swapped


def effective_workers(agents, swordmaster):
    """实际可用特工数。

    游戏里 StartingAgents 是「起始特工数」，剑术大师（StartingSwordmaster）
    会在其之上再解锁 1 个。因此 3 个工人 = agents 2 + swordmaster True。
    早期版本写成 agents 3 + swordmaster True，导致实际能行动 4 次。
    """
    return agents + (1 if swordmaster else 0)


def make_player(idx, human, deck, intrigue, res, techs=None, agents=2, swordmaster=False,
                seat=None):
    # seat 是「座位号」（0 = 先手）。它同时决定 StartingOrder 与 PlayerColor，
    # 早期版本固定写成 idx，导致人类玩家（idx 0）永远是先手。
    seat = idx if seat is None else seat
    return {
        "StartingDeck": list(deck),
        "DeckReplacements": [
            {"Replace": CARD["CrysknifeUprising"], "With": CARD["CrysknifeBase"]},
        ],
        "StartingIntrigue": list(intrigue),
        "LeaderOptions": [],
        "AllianceTokenVP": 1,
        "StartingAgents": agents,
        "StartingOrder": seat + 1,
        "PlayerColor": COLORS[seat % len(COLORS)],
        "HagalPlayer": False,
        "StartingTechTiles": list(techs or []),
        "PreferredLeaders": [],
        "PlayerMatchSoloOptions": {
            "StartingDiscard": [], "StartingDeckOrder": [],
            "Reshuffle1DeckOrder": [], "Reshuffle2DeckOrder": [], "Reshuffle3DeckOrder": [],
            "BonusStartingGarrison": 0, "ControlLocations": [],
            "BonusStartingVP": 0,
            "StartingSwordmaster": swordmaster,
            "StartingHighCouncil": False,
            "StartingSolari": res["solari"],
            "StartingSpice": res["spice"],
            "StartingWater": res["water"],
            "BonusStartingWater": res.get("bonus_water", 0),
            "StartingGarrison": 0, "StartingSpecimens": 0,
        },
        "name": "",
        "isHuman": human,
        "aiLevel": 0 if human else 1,
        "metadata": {},
        "accountID": f"00000000-0000-4000-8000-0000000000{idx:02d}",
    }


def load_base():
    env = UnityPy.load(ORIG)
    for o in env.objects:
        if o.type.name != "TextAsset":
            continue
        ta = o.read()
        if getattr(ta, "m_Name", "") == TA_NAME:
            t = ta.m_Script
            if isinstance(t, bytes):
                t = t.decode("utf-8", "replace")
            return json.loads(t)
    raise SystemExit("原始包中找不到 StandardSolo")


def build(mode, faction_key=None, sets_list=None, install=False, seed=None,
          conflict_order=None, conflict_repeat=None, mods=None, first_order=None):
    m = MODES[mode]
    mods = list(mods or [])
    for mk in mods:
        if mk not in MODS:
            raise SystemExit(f"未知模组：{mk}（可选：{', '.join(MODS)}）")
    for mk in mods:
        for bad in MODS[mk].get("excludes", []):
            if bad in mods:
                raise SystemExit(
                    f"「{MODS[mk]['label']}」与「{MODS[bad]['label']}」互斥，只能选一个")
    if m["use_faction"] and faction_key not in FACTIONS:
        raise SystemExit(f"{m['label']} 需要指定阵营：{', '.join(FACTION_ORDER)}")
    if seed is not None:
        random.seed(seed)

    # ---- 座位（决定 StartingOrder 与 PlayerColor）----
    # 早期版本座位固定 = 玩家下标，人类是 0 号 -> 每局都是先手。
    # 现在默认随机排座，也可以用 --first human / --first 2..4 钉死。
    seats = [0, 1, 2, 3]
    fo = (first_order or "random").strip().lower()
    if fo == "human":
        pass                                  # 人类坐 0 号，即先手
    elif fo == "random":
        random.shuffle(seats)
    elif fo.isdigit() and 1 <= int(fo) <= 4:
        target = int(fo) - 1                  # 1=你，2..4=AI1..AI3
        seats = [target] + [i for i in range(4) if i != target]
    else:
        raise SystemExit(f"未知先手设置：{first_order}（可选：random / human / 1-4）")
    first_idx = seats.index(0)                # 坐 0 号座位的人 = 先手

    # ---- 阵营分配：玩家选的那家给 0 号，AI 随机分剩下的三家 ----
    if m["use_faction"]:
        rest = [k for k in FACTION_ORDER if k != faction_key]
        random.shuffle(rest)
        assign = [faction_key] + rest
    else:
        assign = [None, None, None, None]

    # ---- 内容集：取所有参战阵营的必需集合并集 ----
    sets = list(sets_list) if sets_list is not None else list(SETS_ALL)
    for fk in assign:
        for need in (FACTIONS[fk]["require_sets"] if fk else []):
            if need not in sets:
                sets.append(need)
    for mk in mods:
        for need in MODS[mk]["require_sets"]:
            if need not in sets:
                sets.append(need)
                print(f"     [模组] {MODS[mk]['label']} 需要 {need}，已自动补上")
    if "BaseSet" not in sets:
        sets.insert(0, "BaseSet")

    doc = load_base()
    players = []
    loadouts = []
    for i, fk in enumerate(assign):
        deck, res, intrigue, removed, swapped = faction_loadout(mode, fk, sets)
        techs = []
        mod_log = []
        agents = 2
        swordmaster = False
        for mk in mods:
            md = MODS[mk]
            techs.extend(md.get("techs", []))
            if md.get("agents"):
                agents = md["agents"]
            if md.get("swordmaster"):
                swordmaster = True
            if md.get("solari"):
                res["solari"] += md["solari"]
            for old_gid, new_gid in md["replace"]:
                if old_gid in deck:
                    deck[deck.index(old_gid)] = new_gid
                    mod_log.append(("替换", old_gid, new_gid))
                    if old_gid in removed:
                        removed[removed.index(old_gid)] = new_gid
                else:
                    deck.append(new_gid)
                    mod_log.append(("追加", None, new_gid))
        players.append(make_player(i, i == 0, deck, intrigue, res, techs,
                                   agents=agents, swordmaster=swordmaster, seat=seats[i]))
        loadouts.append({"faction": fk, "deck": deck, "resources": res,
                         "intrigue": intrigue, "removed": removed, "swapped": swapped,
                         "techs": techs, "mod_log": mod_log,
                         "agents": agents, "swordmaster": swordmaster,
                         "seat": seats[i]})

    doc["sets"] = sets
    doc["EndgameTriggerScore"] = m["endgame"]
    doc["LeaderAssignmentMode"] = "Random"
    doc["wormPlayers"] = players

    mso_all = doc.setdefault("MatchSoloOptions", {})
    repeat_last = int(m.get("conflict_repeat_last", 0)) if conflict_repeat is None else int(conflict_repeat)
    repeat_shuffle = True if conflict_repeat is not None and conflict_repeat > 0 else bool(m.get("conflict_shuffle_repeat", False))
    mso_all["ConflictShuffleRepeat"] = repeat_shuffle
    mso_all["ConflictRepeatLast"] = repeat_last

    if m.get("conflict_deck"):
        mso = doc.setdefault("MatchSoloOptions", {})
        mso["ConflictDeck"] = list(m["conflict_deck"])
        order_mode = conflict_order or m.get("conflict_order", "natural")
        if order_mode == "fixed":
            mso["ConflictDeckOrder"] = list(m["conflict_deck"])
        elif order_mode == "shuffle":
            mixed = list(m["conflict_deck"])
            random.shuffle(mixed)
            mso["ConflictDeckOrder"] = mixed
        else:                       # natural：只给集合，顺序交给游戏
            mso["ConflictDeckOrder"] = []
        mso["ConflictShuffleRepeat"] = repeat_shuffle
        mso["ConflictRepeatLast"] = repeat_last

    env = UnityPy.load(ORIG)
    hit = False
    for o in env.objects:
        if o.type.name != "TextAsset":
            continue
        ta = o.read()
        if getattr(ta, "m_Name", "") != TA_NAME:
            continue
        ta.m_Script = json.dumps(doc, ensure_ascii=False, indent=1)
        ta.save()
        hit = True
        break
    if not hit:
        raise SystemExit("改写失败：未定位到 StandardSolo")

    os.makedirs(OUT_DIR, exist_ok=True)
    suffix = ("_" + faction_key) if m["use_faction"] else ""
    stem = f"preset_{mode}{suffix}"
    for mk in MOD_ORDER:
        if mk in mods:
            stem += "_" + MODS[mk]["suffix"]
    out = os.path.join(OUT_DIR, f"{stem}_{BUNDLE}")
    with open(out, "wb") as f:
        f.write(env.file.save(packer="lz4"))

    # ---- 回读校验 ----
    env2 = UnityPy.load(out)
    for o in env2.objects:
        if o.type.name != "TextAsset":
            continue
        ta = o.read()
        if getattr(ta, "m_Name", "") != TA_NAME:
            continue
        raw = ta.m_Script
        d = json.loads(raw if isinstance(raw, str) else raw.decode("utf-8", "replace"))
        assert d["EndgameTriggerScore"] == m["endgame"], "阈值回读不符"
        if m.get("conflict_deck"):
            got = d["MatchSoloOptions"]
            assert got["ConflictDeck"] == m["conflict_deck"], "冲突牌堆回读不符"
            om = conflict_order or m.get("conflict_order", "natural")
            if om == "fixed":
                assert got["ConflictDeckOrder"] == m["conflict_deck"], "冲突顺序回读不符"
            elif om == "natural":
                assert got["ConflictDeckOrder"] == [], "冲突顺序应为空（交给游戏洗）"
            else:
                assert sorted(got["ConflictDeckOrder"]) == sorted(m["conflict_deck"]), "冲突顺序内容不符"
        assert len(d["wormPlayers"]) == 4, "玩家数回读不符"
        for i, wp in enumerate(d["wormPlayers"]):
            lo = loadouts[i]
            assert wp["StartingDeck"] == lo["deck"], f"玩家{i}牌组回读不符"
            o2 = wp["PlayerMatchSoloOptions"]
            assert o2["StartingWater"] == lo["resources"]["water"], f"玩家{i}水回读不符"
            assert o2["BonusStartingWater"] == lo["resources"].get("bonus_water", 0), f"玩家{i}补水回读不符"
            assert o2["StartingSolari"] == lo["resources"]["solari"], f"玩家{i}太阳币回读不符"
            assert o2["StartingSpice"] == lo["resources"]["spice"], f"玩家{i}香料回读不符"
            assert wp["StartingIntrigue"] == lo["intrigue"], f"玩家{i}密卡回读不符"
            assert wp["StartingTechTiles"] == lo["techs"], f"玩家{i}起始科技回读不符"
            assert wp["StartingAgents"] == lo["agents"], f"玩家{i}工人数回读不符"
            assert o2["StartingSwordmaster"] == lo["swordmaster"], f"玩家{i}剑术大师回读不符"
            # 实际可用特工 = StartingAgents + 剑术大师。这是本 bug 的回归防线：
            # 「3 个工人」必须是 2 + 剑术大师，而不是 3 + 剑术大师。
            assert effective_workers(wp["StartingAgents"], o2["StartingSwordmaster"]) == \
                effective_workers(lo["agents"], lo["swordmaster"]), f"玩家{i}实际工人数不符"
        orders = [wp["StartingOrder"] for wp in d["wormPlayers"]]
        assert sorted(orders) == [1, 2, 3, 4], "座位回读不符（应恰好是 1-4 的一个排列）"
        break

    title = m["label"] + (f"（你 = {FACTIONS[faction_key]['label']}）" if m["use_faction"] else "")
    if mods:
        title += " + " + " + ".join(MODS[mk]["label"] for mk in mods)
    print(f"[{title}] {os.path.basename(out)} ({os.path.getsize(out)} B)")
    print(f"     内容集：{', '.join(sets)}")
    print(f"     胜利点 {m['endgame']}")
    first_name = "你" if first_idx == 0 else f"AI{first_idx}"
    print(f"     先手：{first_name}（{'随机排座' if fo == 'random' else '指定'}）")
    if repeat_shuffle or repeat_last:
        print(f"     冲突牌堆：耗尽后继续（ShuffleRepeat={repeat_shuffle}, RepeatLast={repeat_last}）")
    if m.get("conflict_deck"):
        om = conflict_order or m.get("conflict_order", "natural")
        label = {"fixed": "固定顺序", "shuffle": "生成时随机顺序", "natural": "顺序交给游戏洗"}[om]
        n3 = sum(1 for g in m["conflict_deck"] if g in CONFLICT_T3_POOL)
        n2 = sum(1 for g in m["conflict_deck"] if g in CONFLICT_T2_POOL)
        comp = (f"{n2} 张二级 + {n3} 张三级" if n2 and n3
                else ("全部三级" if n3 else "全部二级"))
        print(f"     冲突牌堆：{comp}（共 {len(m['conflict_deck'])} 张）｜{label}")
        print(f"       { '、'.join(name_of(g) for g in m['conflict_deck']) }")
    for i, lo in enumerate(loadouts):
        who = "你  " if i == 0 else f"AI{i} "
        label = FACTIONS[lo["faction"]]["label"] if lo["faction"] else "—"
        r = lo["resources"]
        water = r["water"] + r.get("bonus_water", 0)
        extra = f" 密卡×{len(lo['intrigue'])}" if lo["intrigue"] else ""
        workers = (f" | 工人 {effective_workers(lo['agents'], lo['swordmaster'])}"
                   if lo["agents"] != 2 or lo["swordmaster"] else "")
        print(f"     {who}{label:5s} 牌组 {len(lo['deck']):2d} 张 | 水 {water} "
              f"钱 {r['solari']} 香 {r['spice']}{workers}{extra}")
        if i == 0:
            print(f"           { '、'.join(name_of(g) for g in lo['deck']) }")
            if lo["removed"]:
                print(f"           移除：{ '、'.join(name_of(g) for g in lo['removed']) }")
            if mode in ("faction_pro", "faction_extra") and FACTIONS[lo["faction"]].get("pro_add"):
                print(f"           Pro 加牌：{ '、'.join(name_of(g) for g in FACTIONS[lo['faction']]['pro_add']) }")
            if mode == "faction_extra" and FACTIONS[lo["faction"]].get("extra_add"):
                print(f"           Extra 加牌：{ '、'.join(name_of(g) for g in FACTIONS[lo['faction']]['extra_add']) }")
            if lo.get("swapped"):
                print(f"           不朽启用：{lo['swapped']} 张沙丘 → 实验")
            if lo["intrigue"]:
                print(f"           起始密卡：{ '、'.join(name_of(g) for g in lo['intrigue']) }")
            if lo.get("techs"):
                print(f"           起始科技：{ '、'.join(name_of(g) for g in lo['techs']) }")
            if any(MODS[mk].get("solari") for mk in mods):
                bonus = sum(MODS[mk].get("solari", 0) for mk in mods)
                print(f"           模组资源：+{bonus} 太阳币")
            if lo.get("agents", 2) != 2 or lo.get("swordmaster"):
                eff = effective_workers(lo["agents"], lo["swordmaster"])
                print(f"           起始工人：{eff} 个"
                      f"（StartingAgents {lo['agents']}"
                      + (" + 剑术大师 1）" if lo["swordmaster"] else "）"))
            for act, a, b in lo.get("mod_log", []):
                if act == "替换":
                    print(f"           模组替换：{name_of(a)} → {name_of(b)}")
                else:
                    print(f"           模组加牌：{name_of(b)}（原牌组无 {name_of(MODS[mods[0]]['replace'][0][0])}）")

    meta = {
        "mode": mode, "mode_label": m["label"],
        "faction": faction_key,
        "faction_label": FACTIONS[faction_key]["label"] if m["use_faction"] else None,
        "note": m["note"], "endgame": m["endgame"], "sets": sets,
        "mods": mods, "mod_labels": [MODS[mk]["label"] for mk in mods],
        "first": "你" if first_idx == 0 else f"AI{first_idx}",
        "first_index": first_idx,
        "players": [{"index": i, "is_human": i == 0,
                     "faction": lo["faction"],
                     "faction_label": FACTIONS[lo["faction"]]["label"] if lo["faction"] else None,
                     "deck": lo["deck"], "resources": lo["resources"],
                     "intrigue": lo["intrigue"], "techs": lo["techs"],
                     "agents": lo["agents"], "swordmaster": lo["swordmaster"],
                     "workers": effective_workers(lo["agents"], lo["swordmaster"]),
                     "seat": lo["seat"]}
                    for i, lo in enumerate(loadouts)],
        "bundle": os.path.basename(out),
    }
    with open(os.path.join(OUT_DIR, f"{stem}.json"), "w", encoding="utf-8") as f:
        json.dump(meta, f, ensure_ascii=False, indent=1)

    if install:
        if not os.path.exists(TARGET):
            raise SystemExit("游戏剧本包不存在: " + TARGET)
        shutil.copy2(out, TARGET)
        print(f"[install] 已安装到游戏：{TARGET}")
    print()


if __name__ == "__main__":
    args = sys.argv[1:]

    # --log PATH：把输出写成 UTF-8 文件（供 PowerShell 以 UTF8 读取，避免控制台代码页乱码）
    log_path = None
    if "--log" in args:
        i = args.index("--log")
        if i + 1 < len(args):
            log_path = args[i + 1]
            del args[i:i + 2]

    if "--list" in args:
        for k, m in MODES.items():
            print(f"{k:9s} {m['label']:5s} {m['note']}")
        for k in FACTION_ORDER:
            f = FACTIONS[k]
            print(f"  faction {k:9s} {f['label']:5s} {f['note']}")
        for k, md in MODS.items():
            print(f"  mod     {k:9s} {md['label']:5s} {md['note']}")
        raise SystemExit(0)

    install = "--install" in args

    def take(flag):
        global args
        if flag in args:
            i = args.index(flag)
            if i + 1 < len(args):
                val = args[i + 1].strip()
                del args[i:i + 2]
                return val
        return None

    repeat_arg = take("--repeat")         # 打完 10 场后重复最后一场的次数
    repeat_val = int(repeat_arg) if (repeat_arg and repeat_arg.isdigit()) else None
    conflict_order = take("--conflict")   # fixed | shuffle | natural
    if conflict_order and conflict_order not in ("fixed", "shuffle", "natural"):
        print(f"未知冲突顺序：{conflict_order}（可选：fixed / shuffle / natural）")
        raise SystemExit(1)
    first_arg = take("--first")           # random | human | 1-4（1=你）
    sets_arg = take("--sets")
    if sets_arg:
        sets_arg = [x.strip() for x in sets_arg.split(",") if x.strip()]
    mode_arg = take("--mode")
    faction = take("--faction")
    mods_arg = take("--mod")
    mods = [x.strip() for x in mods_arg.split(",") if x.strip()] if mods_arg else []
    for mk in mods:
        if mk not in MODS:
            print(f"未知模组：{mk}（可选：{', '.join(MODS)}）")
            raise SystemExit(1)

    real_stdout = sys.stdout
    if log_path:
        sys.stdout = io.open(log_path, "w", encoding="utf-8")

    todo = []
    if mode_arg is None and faction is None:
        todo.append(("marathon", None))
        todo += [("faction", k) for k in FACTION_ORDER]
        todo += [("faction_pro", k) for k in FACTION_ORDER]
        todo += [("faction_extra", k) for k in FACTION_ORDER]
    else:
        if mode_arg in (None, "marathon"):
            todo.append(("marathon", None))
        for md in ("faction", "faction_pro", "faction_extra"):
            if mode_arg not in (None, md):
                continue
            keys = [faction] if faction else FACTION_ORDER
            for k in keys:
                if k not in FACTIONS:
                    print(f"未知阵营：{k}（可选：{', '.join(FACTION_ORDER)}）")
                    continue
                todo.append((md, k))
    if not todo:
        print(f"未知模式：{mode_arg}（可选：{', '.join(MODES)}）")
        raise SystemExit(1)

    try:
        for mode, key in todo:
            build(mode, faction_key=key, sets_list=sets_arg, install=install,
                  conflict_order=conflict_order, conflict_repeat=repeat_val, mods=mods,
                  first_order=first_arg)
    finally:
        if log_path:
            sys.stdout.flush()
            sys.stdout.close()
            sys.stdout = real_stdout
