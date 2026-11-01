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
        private static string _key;
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

        private const string SystemPrompt =
            "你是《沙丘：帝国》(Dune: Imperium) 的资深教练。玩家会给你一份游戏原生导出的待选列表" +
            "（Pending Selection，含有 Kind 与若干 Buttons，按钮里带标题、费用等属性）。" +
            "实体已译成「中文名(短ID)」的形式，短 ID 只用于区分同名实体，回答时直接用中文名即可。" +
            "请判断哪一项对最终胜利最有利，并给出 2-3 句理由。要求：" +
            "1) 只讨论给出的选项，不要发明新选项；2) 优先考虑胜利点与后期兑现能力，" +
            "而不是短期资源堆叠；3) 用中文，先给结论（选项标题或序号），再给理由。" +
            "4) 若某项信息确实缺失（例如只给了 ID 没有名字），明确说缺什么，不要臆测牌名。" +
            "5) 〔〕里是该牌/该目标的规则说明；选项与子目标都带〔〕。请据此比较，" +
            "注意有些牌的费用、条件、次数限制会直接决定它此刻值不值得打。";

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
                string raw = File.ReadAllText(quotaPath).Trim();
                if (!int.TryParse(raw, out _max) || _max <= 0)
                {
                    _log.LogWarning("[COACH] coach.txt 内容无效: '" + raw + "'（应为正整数）");
                    return;
                }
                _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                _http.DefaultRequestHeaders.Add("Authorization", "Bearer " + _key);
                Directory.CreateDirectory(_outDir);
                LoadNameMap(root);
                LoadEffectMap(root);
                Enabled = true;
                _log.LogWarning("[COACH] enabled, quota=" + _max + "/session, names=" + _names.Count
                                + ", effects=" + _effects.Count + ", out=" + _outDir);
            }
            catch (Exception e)
            {
                _log.LogWarning("[COACH] init failed: " + e.GetType().Name + ": " + e.Message);
            }
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
            if (_effects.TryGetValue(key, out e)) return e;
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

        /// 加载效果表。优先读 effect_map_v2.tsv（含来源/优先级/歧义标记、长文本不截断）；
        /// v2 不存在时回退到旧的两列 effect_map.tsv。
        private static void LoadEffectMap(string root)
        {
            string v2 = Path.Combine(root, "mods", "ai", "effect_map_v2.tsv");
            if (File.Exists(v2))
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
                    _log.LogWarning("[COACH] effect_map_v2: " + _effects.Count + " 条, 歧义 " +
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
        public static void Observe(string playerName, int counter, string stateBlock, string pending)
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
            user += "待选列表：\n" + pending;

            Task.Run(() => Send(seq, playerName, user, pending, stateBlock));
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
        private static void Send(int seq, string playerName, string user, string pending, string state)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                string body = "{\"model\":\"" + Model + "\",\"messages\":["
                    + "{\"role\":\"system\",\"content\":" + Json.Str(SystemPrompt) + "},"
                    + "{\"role\":\"user\",\"content\":" + Json.Str(user) + "}],"
                    + "\"max_tokens\":400,\"temperature\":0.3}";

                using (var content = new StringContent(body, Encoding.UTF8, "application/json"))
                using (var resp = _http.PostAsync(Endpoint, content).GetAwaiter().GetResult())
                {
                    string text = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    sw.Stop();
                    string answer = Json.ExtractContent(text);
                    int code = (int)resp.StatusCode;
                    _log.LogInfo("[COACH] #" + seq + " " + (int)sw.ElapsedMilliseconds + "ms http=" + code
                                 + " -> " + Short(answer, 300));
                    Append(seq, playerName, pending, answer, (int)sw.ElapsedMilliseconds, code, text, state);
                }
            }
            catch (Exception e)
            {
                sw.Stop();
                _log.LogWarning("[COACH] #" + seq + " failed after " + (int)sw.ElapsedMilliseconds + "ms: "
                                + e.GetType().Name + ": " + e.Message);
                Append(seq, playerName, pending, null, (int)sw.ElapsedMilliseconds, -1, e.Message, state);
            }
        }

        private static void Append(int seq, string player, string pending, string answer, int ms, int http, string raw, string state = null)
        {
            try
            {
                string line = "{\"seq\":" + seq
                    + ",\"ts\":\"" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\""
                    + ",\"player\":" + Json.Str(player)
                    + ",\"ms\":" + ms + ",\"http\":" + http
                    + ",\"answer\":" + Json.Str(answer)
                    + ",\"pending\":" + Json.Str(pending)
                    + ",\"state\":" + Json.Str(state ?? "")
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
