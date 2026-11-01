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
            "4) 若某项信息确实缺失（例如只给了 ID 没有名字），明确说缺什么，不要臆测牌名。";

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

        /// 效果摘要查询（mods/ai/effect_map.tsv）：先按 GUID，再按 "name:<中文名>"。
        public static string EffectOf(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            string e;
            if (_effects.TryGetValue(key, out e)) return e;
            if (_effects.TryGetValue("name:" + key, out e)) return e;
            return null;
        }

        private static void LoadEffectMap(string root)
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
            }
            catch (Exception e) { _log.LogWarning("[COACH] effect_map: " + e.Message); }
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

            if (pending.Length > 9000) pending = pending.Substring(0, 9000) + " …(截断)";
            _log.LogInfo("[COACH] observe #" + seq + " len=" + pending.Length + " player=" + (playerName ?? "?"));
            string user = "AI 玩家：" + (playerName ?? "?") + "（第 " + counter + " 号选择）\n";
            if (!string.IsNullOrEmpty(stateBlock)) user += stateBlock + "\n";
            user += "待选列表：\n" + pending;

            Task.Run(() => Send(seq, playerName, user, pending));
        }

        /// 在后台线程同步完成一次请求（不能用 async lambda：本工程引用的 Il2Cppmscorlib 缺 NullableAttribute）。
        private static void Send(int seq, string playerName, string user, string pending)
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
                    Append(seq, playerName, pending, answer, (int)sw.ElapsedMilliseconds, code, text);
                }
            }
            catch (Exception e)
            {
                sw.Stop();
                _log.LogWarning("[COACH] #" + seq + " failed after " + (int)sw.ElapsedMilliseconds + "ms: "
                                + e.GetType().Name + ": " + e.Message);
                Append(seq, playerName, pending, null, (int)sw.ElapsedMilliseconds, -1, e.Message);
            }
        }

        private static void Append(int seq, string player, string pending, string answer, int ms, int http, string raw)
        {
            try
            {
                string line = "{\"seq\":" + seq
                    + ",\"ts\":\"" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\""
                    + ",\"player\":" + Json.Str(player)
                    + ",\"ms\":" + ms + ",\"http\":" + http
                    + ",\"answer\":" + Json.Str(answer)
                    + ",\"pending\":" + Json.Str(pending)
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
