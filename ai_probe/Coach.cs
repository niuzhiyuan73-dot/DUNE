using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using BepInEx.Logging;

namespace DuneAiProbe
{
    /// <summary>
    /// T1 观察者：把 AI 面对的待选列表发给 DeepSeek，拿回"它会怎么选 + 为什么"，写日志。
    ///
    /// 设计约束：
    ///   - 全异步，不阻塞对局线程（AI 决策只有 0-400ms，绝不能被网络拖住）
    ///   - 只读：不修改任何选择，不影响对局结果
    ///   - 有配额：mods/ai/coach.txt 里写数字 N = 本次会话最多发 N 次
    ///   - Key 从 mods/ai/deepseek_key.txt 读，不入库
    /// </summary>
    internal static class Coach
    {
        private const string Endpoint = "https://api.deepseek.com/chat/completions";
        private const string Model = "deepseek-chat";

        private static ManualLogSource _log;
        private static HttpClient _http;
        private static string _key;          // 兼容旧单 key 文件
        private static string[] _keys;       // 多 key 轮询（deepseek_keys.txt，每行一个）
        private static int _keyCursor;
        private static string _outDir;
        private static int _max;
        private static int _sent;
        private static readonly object _lock = new object();
        private static readonly object _fileLock = new object();
        private static readonly Dictionary<string, string> _names = new Dictionary<string, string>();
        private static readonly HashSet<string> _seen = new HashSet<string>();
        private static readonly Regex GuidRe = new Regex(
            "[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", RegexOptions.Compiled);

        public static bool Enabled { get; private set; }

        /// 角色与输出格式要求。**只保留不变的部分**——具体规则、当前玩法、错误示例
        /// 从 mods/ai/knowledge/ 读取后拼在后面（见 LoadKnowledge）。
        private const string BaseSystemPrompt =
            "你是《沙丘：帝国》(Dune: Imperium) 的教练，职责是告诉玩家这一步**具体怎么操作**，" +
            "不是写分析文章。玩家会给你一份游戏原生导出的待选列表（Pending Selection）。" +
            "实体已译成「中文名(短ID)」的形式，短 ID 只用于区分同名实体。要求：" +
            "1) 只讨论给出的选项，不要发明新选项；" +
            "2) **必须严格按文末【输出格式】的字段逐行输出**，不写结论段、不写理由段、不做对比分析；" +
            "3) 数值只写能确认的，算不准就写「未知」，不要给估算；" +
            "4) 用玩家可以直接照着做的话，不要行话（如 兑现、边际、期望值 这类词）；" +
            "5) 〔〕里是该牌/该目标的规则说明，｜后面是抽出来的花费与条件；" +
            "6) 后文的规则与你的先验冲突时，以规则为准。";

        /// 组装后的系统提示（角色 + 基础规则 + 当前玩法 + 错误示例）。Init 时构建一次。
        private static string _system = BaseSystemPrompt;

        /// 机制索引：命中关键词时按需插入用户消息（不进系统提示，保持前缀稳定）。
        private sealed class Mechanic
        {
            public string[] Keys;
            public string Text;
        }
        private static readonly List<Mechanic> _mechanics = new List<Mechanic>();

        public static void Init(ManualLogSource log, string root)
        {
            _log = log;
            _outDir = Path.Combine(root, "dump", "llm");

            string keyPath = Path.Combine(root, "mods", "ai", "deepseek_key.txt");
            string quotaPath = Path.Combine(root, "mods", "ai", "coach.txt");

            if (!File.Exists(keyPath))
            {
                _log.LogInfo("[COACH] no deepseek_key.txt, disabled");
                return;
            }
            if (!File.Exists(quotaPath))
            {
                _log.LogInfo("[COACH] no coach.txt, disabled (写个数字进去即启用)");
                return;
            }

            try
            {
                _key = File.ReadAllText(keyPath).Trim();
                LoadKeys(root);
                string raw = File.ReadAllText(quotaPath).Trim();
                if (!int.TryParse(raw, out _max) || _max <= 0)
                {
                    _log.LogWarning("[COACH] coach.txt 内容无效: '" + raw + "'（应为正整数）");
                    return;
                }
                // 不在 HttpClient 上设全局 Authorization：四家同时请求时要按请求轮换 key
                _http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
                Directory.CreateDirectory(_outDir);
                LoadNameMap(root);
                LoadEffectMap(root);
                LoadKnowledge(root);
                Enabled = true;
                _log.LogWarning("[COACH] enabled, quota=" + _max + "/session, names=" + _names.Count
                                + ", effects=" + _effects.Count + ", system=" + _system.Length + "字"
                                + ", mechanics=" + _mechanics.Count
                                + ", keys=" + (_keys != null ? _keys.Length : 0) + ", out=" + _outDir);
            }
            catch (Exception e)
            {
                _log.LogWarning("[COACH] init failed: " + e.GetType().Name + ": " + e.Message);
            }
        }

        /// 多 key 支持：mods/ai/deepseek_keys.txt 每行一个 key（# 开头注释）。
        /// 四家同时挂教练时并发会撞单 key 限流，按请求轮询使用。
        private static void LoadKeys(string root)
        {
            var list = new List<string>();
            try
            {
                string multi = Path.Combine(root, "mods", "ai", "deepseek_keys.txt");
                if (File.Exists(multi))
                {
                    foreach (var line in File.ReadAllLines(multi, Encoding.UTF8))
                    {
                        string t = line.Trim();
                        if (t.Length == 0 || t.StartsWith("#", StringComparison.Ordinal)) continue;
                        if (!list.Contains(t)) list.Add(t);
                    }
                }
            }
            catch (Exception e) { _log.LogWarning("[COACH] keys: " + e.Message); }
            if (list.Count == 0 && !string.IsNullOrEmpty(_key)) list.Add(_key);
            _keys = list.ToArray();
        }

        private static string NextKey()
        {
            if (_keys == null || _keys.Length == 0) return _key;
            if (_keys.Length == 1) return _keys[0];
            int i = System.Threading.Interlocked.Increment(ref _keyCursor);
            return _keys[Math.Abs(i) % _keys.Length];
        }

        /// 加载知识层：基础规则 + 当前玩法 + 错误示例进系统提示，机制索引留待按需命中。
        /// 文件缺失时保持 BaseSystemPrompt 原样，不阻断观察器。
        private static void LoadKnowledge(string root)
        {
            string dir = Path.Combine(root, "mods", "ai", "knowledge");
            var sb = new StringBuilder(BaseSystemPrompt);

            string core = ReadTextFile(Path.Combine(dir, "core_rules.md"));
            if (!string.IsNullOrEmpty(core)) sb.Append("\n\n").Append(core.Trim());
            else _log.LogWarning("[COACH] 缺少 core_rules.md，按旧提示词运行");

            string mode = ReadTextFile(Path.Combine(dir, "current_mode.md"));
            if (!string.IsNullOrEmpty(mode)) sb.Append("\n\n").Append(mode.Trim());

            string ex = ReadTextFile(Path.Combine(dir, "examples.tsv"));
            if (!string.IsNullOrEmpty(ex))
            {
                var eb = new StringBuilder("\n\n## 已知错误与正确判断（必须遵守）\n");
                int n = 0;
                foreach (var line in ex.Split('\n'))
                {
                    string t = line.TrimEnd('\r');
                    if (t.Length == 0 || t.StartsWith("#", StringComparison.Ordinal)) continue;
                    var c = t.Split('\t');
                    if (c.Length < 4) continue;
                    eb.Append("· ").Append(c[1]).Append(" → 错误说法：").Append(c[2])
                      .Append("；正确：").Append(c[3]).Append('\n');
                    n++;
                }
                if (n > 0) sb.Append(eb.ToString());
            }

            // 输出格式放最后：指令离生成位置越近，格式约束越稳
            string fmt = ReadTextFile(Path.Combine(dir, "output_format.md"));
            if (!string.IsNullOrEmpty(fmt)) sb.Append("\n\n").Append(fmt.Trim());
            else _log.LogWarning("[COACH] 缺少 output_format.md，输出可能不是行动卡格式");

            _system = sb.ToString();

            // 机制索引：关键词至少 2 字，避免单字（水/牌）到处命中
            string mp = Path.Combine(dir, "mechanics.tsv");
            string mtxt = ReadTextFile(mp);
            if (!string.IsNullOrEmpty(mtxt))
            {
                foreach (var line in mtxt.Split('\n'))
                {
                    string t = line.TrimEnd('\r');
                    if (t.Length == 0 || t.StartsWith("#", StringComparison.Ordinal)) continue;
                    var c = t.Split('\t');
                    if (c.Length < 5) continue;
                    var keys = new List<string>();
                    foreach (var k in c[2].Split('|'))
                    {
                        string kk = k.Trim();
                        if (kk.Length >= 2) keys.Add(kk);
                    }
                    if (keys.Count == 0) continue;
                    // 一句话 + 细节（细节放括号里，太长就只留一句话）
                    string text = c[3];
                    if (!string.IsNullOrEmpty(c[4]) && c[4].Length <= 140)
                        text = text + "（" + c[4] + "）";
                    _mechanics.Add(new Mechanic { Keys = keys.ToArray(), Text = c[1] + "：" + text });
                }
            }
            _log.LogWarning("[COACH] knowledge: system=" + _system.Length + "字, mechanics="
                            + _mechanics.Count + " 条");
        }

        private static string ReadTextFile(string p)
        {
            try { return File.Exists(p) ? File.ReadAllText(p, Encoding.UTF8) : null; }
            catch (Exception e) { _log.LogWarning("[COACH] 读取 " + Path.GetFileName(p) + " 失败: " + e.Message); return null; }
        }

        /// 按本次选项/局势命中的机制，最多带 MaxMechanics 条。
        /// 命中的关键词越多越靠前；同样的局面每次命中顺序一致（便于比对日志）。
        private const int MaxMechanics = 3;

        private static string MatchMechanics(string haystack)
        {
            if (_mechanics.Count == 0 || string.IsNullOrEmpty(haystack)) return null;
            var hits = new List<Mechanic>();
            var scores = new List<int>();
            foreach (var m in _mechanics)
            {
                int score = 0;
                foreach (var k in m.Keys)
                {
                    if (haystack.IndexOf(k, StringComparison.Ordinal) >= 0) score++;
                }
                if (score == 0) continue;
                hits.Add(m);
                scores.Add(score);
            }
            if (hits.Count == 0) return null;
            // 稳定插入排序：分数高的在前，同分保持文件顺序
            for (int i = 1; i < hits.Count; i++)
            {
                var mh = hits[i];
                int sc = scores[i];
                int j = i - 1;
                while (j >= 0 && scores[j] < sc)
                {
                    hits[j + 1] = hits[j];
                    scores[j + 1] = scores[j];
                    j--;
                }
                hits[j + 1] = mh;
                scores[j + 1] = sc;
            }
            var sb = new StringBuilder();
            int n = Math.Min(MaxMechanics, hits.Count);
            for (int i = 0; i < n; i++) sb.Append("· ").Append(hits[i].Text).Append('\n');
            return sb.ToString();
        }

        private static void LoadNameMap(string root)
        {
            try
            {
                string p = Path.Combine(root, "mods", "ai", "name_map.tsv");
                if (!File.Exists(p)) { _log.LogWarning("[COACH] 没有 name_map.tsv，prompt 里会保留机器标识"); return; }
                foreach (var line in File.ReadAllLines(p, Encoding.UTF8))
                {
                    int tab = line.IndexOf('\t');
                    if (tab <= 0) continue;
                    _names[line.Substring(0, tab)] = line.Substring(tab + 1).Replace("\\n", " ");
                }
            }
            catch (Exception e) { _log.LogWarning("[COACH] name_map: " + e.Message); }
        }

        /// 名称表查询：脚本（Targets）与文本翻译共用。未收录返回 null。
        public static string NameOf(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            string n;
            return _names.TryGetValue(key, out n) ? n : null;
        }

        private static readonly Dictionary<string, string> _effects =
            new Dictionary<string, string>(StringComparer.Ordinal);

        // 效果来源与优先级（来自 effect_map_v2.tsv）。
        // 用于让模型知道某段文本是"游戏官方原文"还是"人工补充"，以及人工补充是否已核对。
        private static readonly Dictionary<string, string> _effectSources =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, int> _effectPriorities =
            new Dictionary<string, int>(StringComparer.Ordinal);

        // 同名多版本的歧义表（来自 ambiguous_names.tsv）。
        // 这些中文名对应多个 GUID 且规则不同 —— 按中文名兜底取效果必然串用别的扩展的规则，
        // 因此这些键禁止用于兜底查询。
        private static readonly HashSet<string> _ambiguousNames = new HashSet<string>(StringComparer.Ordinal);

        /// 效果摘要查询：先按 GUID，再按 "name:<中文名>"。
        ///
        /// 注意 name: 兜底只在**该中文名唯一对应一个原型**时才允许：
        /// 同名跨扩展的卡（如"厄拉奇恩之战" BaseSet/Uprising 各一张）规则完全不同，
        /// 按名兜底会把另一张牌的规则搬过来，模型据此判断必然出错。
        public static string EffectOf(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            string e;
            if (_effects.TryGetValue(key, out e))
            {
                // GUID 命中时：若同名 name: 条目优先级更高且该名非歧义，
                // 用更权威的那份（人工复核的完整文本 > 自动提取的残缺短文本）。
                if (!key.StartsWith("name:", StringComparison.Ordinal))
                {
                    string nm = NameOf(key);
                    if (!string.IsNullOrEmpty(nm) && !_ambiguousNames.Contains(nm))
                    {
                        string nkey = "name:" + nm;
                        string e2;
                        if (_effects.TryGetValue(nkey, out e2) &&
                            EffectPriorityOf(nkey) > EffectPriorityOf(key))
                            return e2;
                    }
                }
                return e;
            }
            // name: 兜底：排除歧义名
            if (key.StartsWith("name:", StringComparison.Ordinal)) return null;
            if (_ambiguousNames.Contains(key)) return null;
            if (_effects.TryGetValue("name:" + key, out e)) return e;
            return null;
        }

        /// 该中文名是否因多版本而歧义（禁止按名兜底）。
        public static bool IsAmbiguousName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            return _ambiguousNames.Contains(name);
        }

        /// 某键的效果来源标签（本地化自动提取 / 人工补充表…）；没有记录返回 null。
        public static string EffectSourceOf(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            string s;
            if (_effectSources.TryGetValue(key, out s)) return s;
            if (!key.StartsWith("name:", StringComparison.Ordinal) &&
                _effectSources.TryGetValue("name:" + key, out s)) return s;
            return null;
        }

        /// 某键的效果优先级（越大越权威：自动提取 10 < 人工补充 20/30 < 人工复核 40）。
        public static int EffectPriorityOf(string key)
        {
            if (string.IsNullOrEmpty(key)) return 0;
            int p;
            if (_effectPriorities.TryGetValue(key, out p)) return p;
            if (!key.StartsWith("name:", StringComparison.Ordinal) &&
                _effectPriorities.TryGetValue("name:" + key, out p)) return p;
            return 0;
        }

        /// 加载效果表。优先级：v3（带花费/条件字段）> v2（纯文本，兼容旧部署）> 旧两列格式。
        ///
        /// v3 与 v2 的差别只有两列：花费、条件。运行时把它们拼成
        /// 「正文｜花费 6香料｜条件 若与宇航公会同盟」，模型不必再从长句里找。
        private static void LoadEffectMap(string root)
        {
            string v3 = Path.Combine(root, "mods", "ai", "effect_map_v3.tsv");
            if (File.Exists(v3))
            {
                try
                {
                    foreach (var line in File.ReadAllLines(v3, Encoding.UTF8))
                    {
                        if (line.Length == 0) continue;
                        // 键 来源 优先级 扩展 歧义标记 正文 花费 条件
                        var cols = line.Split('\t');
                        if (cols.Length < 6) continue;
                        string key = cols[0];
                        if (key.Length == 0) continue;
                        string cost = cols.Length > 6 ? cols[6] : null;
                        string cond = cols.Length > 7 ? cols[7] : null;
                        _effects[key] = Compose(cols[5].Replace("\\n", " "), cost, cond);
                        _effectSources[key] = cols[1];
                        int prio;
                        if (int.TryParse(cols[2], out prio)) _effectPriorities[key] = prio;
                        if (!string.IsNullOrEmpty(cols[4])) _ambiguousNames.Add(cols[4]);
                    }
                    _log.LogWarning("[COACH] effect_map_v3: " + _effects.Count + " 条, 歧义 " +
                                    _ambiguousNames.Count + " 个");
                }
                catch (Exception e) { _log.LogWarning("[COACH] effect_map_v3: " + e.Message); }
            }

            string v2 = Path.Combine(root, "mods", "ai", "effect_map_v2.tsv");
            if (_effects.Count == 0 && File.Exists(v2))
            {
                try
                {
                    foreach (var line in File.ReadAllLines(v2, Encoding.UTF8))
                    {
                        if (line.Length == 0) continue;
                        // 键 来源 优先级 扩展 歧义标记 文本
                        var cols = line.Split('\t');
                        if (cols.Length < 6) continue;
                        string key = cols[0];
                        if (key.Length == 0) continue;
                        _effects[key] = cols[5].Replace("\\n", " ");
                        _effectSources[key] = cols[1];
                        int prio;
                        if (int.TryParse(cols[2], out prio)) _effectPriorities[key] = prio;
                        if (!string.IsNullOrEmpty(cols[4])) _ambiguousNames.Add(cols[4]);
                    }
                    _log.LogWarning("[COACH] effect_map_v2(回退): " + _effects.Count + " 条, 歧义 " +
                                    _ambiguousNames.Count + " 个");
                }
                catch (Exception e) { _log.LogWarning("[COACH] effect_map_v2: " + e.Message); }
            }

            // 歧义清单：覆盖 v2 标记之外的情况（人工填表按中文名填、无 GUID 可选）
            string amb = Path.Combine(root, "mods", "ai", "ambiguous_names.tsv");
            try
            {
                if (File.Exists(amb))
                {
                    foreach (var line in File.ReadAllLines(amb, Encoding.UTF8))
                    {
                        if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                        int tab = line.IndexOf('\t');
                        if (tab <= 0) continue;
                        _ambiguousNames.Add(line.Substring(0, tab));
                    }
                }
            }
            catch (Exception e) { _log.LogWarning("[COACH] ambiguous_names: " + e.Message); }

            // 回退：旧的两列格式
            if (_effects.Count == 0)
            {
                try
                {
                    string p = Path.Combine(root, "mods", "ai", "effect_map.tsv");
                    if (!File.Exists(p)) { _log.LogWarning("[COACH] 没有 effect_map.tsv，选项不带效果文本"); return; }
                    foreach (var line in File.ReadAllLines(p, Encoding.UTF8))
                    {
                        int tab = line.IndexOf('\t');
                        if (tab <= 0) continue;
                        _effects[line.Substring(0, tab)] = line.Substring(tab + 1).Replace("\\n", " ");
                    }
                    _log.LogWarning("[COACH] effect_map(旧格式回退): " + _effects.Count + " 条");
                }
                catch (Exception e) { _log.LogWarning("[COACH] effect_map: " + e.Message); }
            }
        }

        /// 把正文与结构化字段拼成一句：正文｜花费 6香料｜条件 若与宇航公会同盟。
        /// 用全角竖线分隔，避免和正文里的〔〕嵌套；字段为空则不出现。
        private static string Compose(string text, string cost, string cond)
        {
            var sb = new StringBuilder(text ?? "");
            if (!string.IsNullOrEmpty(cost)) sb.Append("｜花费 ").Append(cost);
            if (!string.IsNullOrEmpty(cond)) sb.Append("｜条件 ").Append(cond);
            return sb.ToString();
        }

        private static readonly Regex KeyRe = new Regex(
            @"\b(?:worm|card|button)\.[A-Za-z0-9_.]+", RegexOptions.Compiled);

        // 富文本残留：<sprite name=DrawCard> 之类。图标名本身有信息量（抽牌/军力），
        // 所以保留名字、去掉标签，而不是整段删掉。
        private static readonly Regex SpriteRe = new Regex(
            "<sprite\\s+name=\"?([^\">]+)\"?\\s*/?>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex TagRe = new Regex(
            "</?(?:b|i|u|size|color|alpha|line-height|voffset|nobr|space|indent)[^>]*>", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// 把机器标识翻成人话：先去富文本标签，再换本地化键（worm.space.* / 能力名），最后换裸 GUID。
        private static string Humanize(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            s = SpriteRe.Replace(s, "$1");
            s = TagRe.Replace(s, "");
            s = KeyRe.Replace(s, ReplaceKey);
            return GuidRe.Replace(s, ReplaceGuid);
        }

        private static readonly MatchEvaluator ReplaceGuid = OnGuidMatch;
        private static readonly MatchEvaluator ReplaceKey = OnKeyMatch;

        private static string OnGuidMatch(Match m)
        {
            if (_names.TryGetValue(m.Value, out string n)) return n;
            return m.Value.Substring(0, 8);
        }

        private static string OnKeyMatch(Match m)
        {
            if (_names.TryGetValue(m.Value, out string n)) return n;
            return m.Value;
        }

        /// FNV-1a，进程内稳定即可（不用 string.GetHashCode，它跨进程随机化）
        private static string StableHash(string s)
        {
            uint h = 2166136261u;
            foreach (char c in s)
            {
                h ^= c;
                h *= 16777619u;
            }
            return h.ToString("x8");
        }

        private static readonly Regex CounterRe = new Regex(@"Counter \d+", RegexOptions.Compiled);
        private static readonly Regex SourceRe = new Regex(@"SourceID [^,)>]+", RegexOptions.Compiled);
        private static readonly Regex ShortIdRe = new Regex(@"\(?[0-9a-f]{8}\)?", RegexOptions.Compiled);
        private static readonly Regex StampRe = new Regex(@" at \d+", RegexOptions.Compiled);
        private static readonly Regex WsRe = new Regex(@"\s+", RegexOptions.Compiled);

        /// 去重签名：把 counter / 实体实例短 ID / 时间戳都抹掉。
        /// 同一份待选列表在一局里会被问好几次（counter 与实例 ID 每次都不同），
        /// 只按原文哈希去重拦不住，实测 30 条里有 7 条是这种重复。
        private static string Signature(string pending)
        {
            string s = CounterRe.Replace(pending, "Counter #");
            s = SourceRe.Replace(s, "SourceID X");
            s = ShortIdRe.Replace(s, "");
            s = StampRe.Replace(s, "");
            s = WsRe.Replace(s, " ");
            return s;
        }

        /// 在游戏线程里调用：pending 必须是已经转成托管字符串的文本。
        public static void Observe(string playerName, int counter, string stateBlock, string pending,
            string memoryBlock = null, long playerKey = 0)
        {
            if (!Enabled || string.IsNullOrEmpty(pending)) return;

            string human = Humanize(pending);
            // 棋盘状态也算进去：同样的选项、不同的局势，答案可能不同
            string dedupeKey = (playerName ?? "?") + "|" + StableHash(Signature(human) + "|" + (stateBlock ?? ""));
            lock (_lock)
            {
                if (!_seen.Add(dedupeKey)) return;   // 同一组选项不重复问
                if (_sent >= _max) return;
                _sent++;
            }
            int seq = _sent;

            pending = human;

            // 按需组织：规则在数据层已完整保存，这里只决定"这一条 prompt 能放多少"。
            // 实测 prompt 平均 955 字、最长 2016 字，9000 上限从未触发过真正的信息丢失；
            // 真正的截断发生在生成侧与明细行数上限上。发送侧超限时按效果条目逐个裁剪，
            // 并显式标记省略了多少条 —— 不能让模型以为看到的就是全部。
            if (pending.Length > PromptBudget)
            {
                pending = TrimByEntries(pending, PromptBudget);
            }
            _log.LogInfo("[COACH] observe #" + seq + " len=" + pending.Length + " player=" + (playerName ?? "?"));
            string user = "AI 玩家：" + (playerName ?? "?") + "（第 " + counter + " 号选择）\n";
            if (!string.IsNullOrEmpty(stateBlock)) user += stateBlock + "\n";
            // 本局记忆：只含已确认发生的变化 + 明确标注的建议，紧贴局势，便于对照"刚发生了什么"。
            if (!string.IsNullOrEmpty(memoryBlock)) user += memoryBlock + "\n";
            // 按需机制：只带本次选项/局势涉及的机制，避免整本规则书重复发送。
            // 放在系统提示之外 —— 系统提示是稳定前缀，才能吃到上下文缓存。
            string mech = MatchMechanics(pending + "\n" + (stateBlock ?? ""));
            if (!string.IsNullOrEmpty(mech)) user += "【相关机制】\n" + mech;
            user += "待选列表：\n" + pending;

            Task.Run(() => Send(seq, playerName, user, pending, stateBlock, memoryBlock, counter, playerKey));
        }

        /// 单次 prompt 的字符预算。
        private const int PromptBudget = 9000;

        /// 超预算时按"效果条目"粒度裁剪：逐条缩短〔…〕里的效果文本，
        /// 保留全部选项与子目标的**存在性**，只压缩细节，并显式标记省略了多少。
        private static string TrimByEntries(string pending, int budget)
        {
            // 找出所有〔...〕块，逐个压缩到给定上限
            int perEntry = 120;
            for (int attempt = 0; attempt < 6 && pending.Length > budget; attempt++)
            {
                pending = ShortenEffectBlocks(pending, perEntry);
                if (pending.Length > budget) perEntry = Math.Max(20, perEntry / 2);
            }
            if (pending.Length > budget)
            {
                pending = pending.Substring(0, budget) + " …(内容超长，已截断)";
            }
            return pending;
        }

        /// 把 prompt 里所有〔…〕块的内容压缩到 maxLen 字。
        private static string ShortenEffectBlocks(string text, int maxLen)
        {
            if (string.IsNullOrEmpty(text)) return text;
            var sb = new System.Text.StringBuilder(text.Length);
            int i = 0;
            int trimmed = 0;
            while (i < text.Length)
            {
                int open = text.IndexOf('〔', i);
                if (open < 0) { sb.Append(text, i, text.Length - i); break; }
                int close = text.IndexOf('〕', open + 1);
                if (close < 0) { sb.Append(text, i, text.Length - i); break; }

                sb.Append(text, i, open - i);
                string body = text.Substring(open + 1, close - open - 1);
                if (body.Length > maxLen)
                {
                    // 保留条件前缀：条件对"能不能用"比效果数值更关键
                    string kept = body.Substring(0, maxLen);
                    int cut = kept.LastIndexOf('；');
                    if (cut > maxLen / 2) kept = kept.Substring(0, cut);
                    sb.Append("〔").Append(kept).Append("…（细节已省略）〕");
                    trimmed++;
                }
                else
                {
                    sb.Append(text, open, close - open + 1);
                }
                i = close + 1;
            }
            if (trimmed > 0)
            {
                sb.Append("\n〔另有 ").Append(trimmed).Append(" 处效果细节因长度限制被省略〕");
            }
            return sb.ToString();
        }

        /// 在后台线程同步完成一次请求（不能用 async lambda：本工程引用的 Il2Cppmscorlib 缺 NullableAttribute）。
        private static void Send(int seq, string playerName, string user, string pending, string state, string memory, int gameCounter, long playerKey)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                string body = "{\"model\":\"" + Model + "\",\"messages\":["
                    + "{\"role\":\"system\",\"content\":" + Json.Str(_system) + "},"
                    + "{\"role\":\"user\",\"content\":" + Json.Str(user) + "}],"
                    + "\"max_tokens\":400,\"temperature\":0.3}";

                using (var content = new StringContent(body, Encoding.UTF8, "application/json"))
                using (var req = new HttpRequestMessage(HttpMethod.Post, Endpoint))
                {
                    req.Headers.Add("Authorization", "Bearer " + NextKey());
                    req.Content = content;
                    using (var resp = _http.SendAsync(req).GetAwaiter().GetResult())
                    {
                    string text = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    sw.Stop();
                    string answer = Json.ExtractContent(text);
                    int code = (int)resp.StatusCode;
                    _log.LogInfo("[COACH] #" + seq + " " + (int)sw.ElapsedMilliseconds + "ms http=" + code
                                 + " -> " + Short(answer, 300));
                    Append(seq, playerName, pending, answer, (int)sw.ElapsedMilliseconds, code, text, state, memory, gameCounter, playerKey);
                    }
                }
            }
            catch (Exception e)
            {
                sw.Stop();
                _log.LogWarning("[COACH] #" + seq + " failed after " + (int)sw.ElapsedMilliseconds + "ms: "
                                + e.GetType().Name + ": " + e.Message);
                Append(seq, playerName, pending, null, (int)sw.ElapsedMilliseconds, -1, e.Message, state, memory, gameCounter, playerKey);
            }
        }

        private static void Append(int seq, string player, string pending, string answer, int ms, int http, string raw, string state = null, string memory = null, int gameCounter = 0, long playerKey = 0)
        {
            // 记进本局记忆时只当"建议"，绝不写成已执行 —— 我们并不知道玩家是否照做。
            if (!string.IsNullOrEmpty(answer)) Memory.NoteAdvice(playerKey, answer, _log);
            try
            {
                string line = "{\"seq\":" + seq
                    + ",\"ts\":\"" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\""
                    + ",\"player\":" + Json.Str(player)
                    + ",\"ms\":" + ms + ",\"http\":" + http
                    + ",\"answer\":" + Json.Str(answer)
                    + ",\"pending\":" + Json.Str(pending)
                    + ",\"state\":" + Json.Str(state ?? "")
                    + ",\"memory\":" + Json.Str(memory ?? "")
                    + ",\"actual\":" + Json.Str(Actual.ResultFor(gameCounter) ?? "")
                    + (http >= 200 && http < 300 ? "" : ",\"raw\":" + Json.Str(Short(raw, 500)))
                    + "}";
                // 多个回答线程可能同时落盘，File.AppendAllText 会撞 "being used by another process"。
                // 加锁 + 重试，宁可多等 100ms，也不要静默丢样本。
                string path = Path.Combine(_outDir, "coach_" + DateTime.Now.ToString("yyyyMMdd") + ".jsonl");
                lock (_fileLock)
                {
                    for (int attempt = 0; attempt < 4; attempt++)
                    {
                        try
                        {
                            File.AppendAllText(path, line + "\n", new UTF8Encoding(false));
                            break;
                        }
                        catch (IOException)
                        {
                            if (attempt == 3) throw;
                            System.Threading.Thread.Sleep(60);
                        }
                    }
                }
            }
            catch (Exception e) { _log.LogWarning("[COACH] append: " + e.Message); }
        }

        private static string Short(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r", " ").Replace("\n", " ");
            return s.Length <= n ? s : s.Substring(0, n) + "…";
        }
    }

    /// 只为这里的两个场景写的极简 JSON 工具：转义 & 取 content 字段。
    internal static class Json
    {
        public static string Str(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder(s.Length + 16);
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        /// 从 chat/completions 响应里取 choices[0].message.content
        public static string ExtractContent(string json)
        {
            if (string.IsNullOrEmpty(json)) return "";
            int i = json.IndexOf("\"content\"", StringComparison.Ordinal);
            if (i < 0) return json;
            i = json.IndexOf('"', i + 9);
            if (i < 0) return json;
            var sb = new StringBuilder();
            for (int p = i + 1; p < json.Length; p++)
            {
                char c = json[p];
                if (c == '"') break;
                if (c == '\\' && p + 1 < json.Length)
                {
                    p++;
                    char e = json[p];
                    switch (e)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (p + 4 < json.Length &&
                                int.TryParse(json.Substring(p + 1, 4),
                                    System.Globalization.NumberStyles.HexNumber, null, out int code))
                            {
                                sb.Append((char)code);
                                p += 4;
                            }
                            break;
                        default: sb.Append(e); break;
                    }
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
