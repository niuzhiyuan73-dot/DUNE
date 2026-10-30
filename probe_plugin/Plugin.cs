using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using canis.archetypes;
using worm.canis;
using worm.canis.entities;
using worm.canis.actions.phases;

namespace DuneProbe
{
    [BepInPlugin(GUID, "Dune Probe", "2.3.0")]
    public class Plugin : BasePlugin
    {
        public const string GUID = "local.duneprobe";

        internal static ManualLogSource Logger;
        private static WormMatch _match;
        private static bool _done;

        public override void Load()
        {
            Logger = Log;
            Logger.LogInfo("========== [PROBE v23] 枚举属性 + 领袖能力树 ==========");
            var h = new Harmony("local.duneprobe.harmony");
            Patch(h, nameof(OnSetupPlayer), typeof(SetupPhase), "SetupPlayer");
            Patch(h, nameof(OnDraw), typeof(RoundPhase), "DrawCards");
            Logger.LogInfo("========== [PROBE v23] 就绪 ==========");
        }

        private static void Patch(Harmony h, string handler, Type t, string method)
        {
            try
            {
                var mi = t.GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic |
                                             BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (mi == null) { Logger.LogWarning($"[PROBE] {method} 未找到"); return; }
                h.Patch(mi, postfix: new HarmonyMethod(typeof(Plugin).GetMethod(handler,
                    BindingFlags.Static | BindingFlags.NonPublic)));
                Logger.LogInfo($"[PROBE] 挂载 {method}");
            }
            catch (Exception e) { Logger.LogWarning($"[PROBE] {method} 失败: {e.GetType().Name}"); }
        }

        private static void OnSetupPlayer(SetupPhase __instance, object[] __args)
        {
            try { if (_match == null && __instance.wormMatch != null) _match = __instance.wormMatch; } catch { }
        }

        private static void OnDraw(object __instance)
        {
            if (_done) return;
            _done = true;
            try
            {
                var playmat = _match?.WormPlaymat;
                var player = Children(playmat).FirstOrDefault(x => x?.GetType().Name == "WormPlayer");
                if (player == null) { Logger.LogWarning("[PROBE] 无 Player"); return; }

                var deck = Children(player).FirstOrDefault(x => Name(x) == "Deck");
                var card = Children(deck).FirstOrDefault();
                if (card == null) { Logger.LogWarning("[PROBE] Deck 为空"); return; }

                Logger.LogInfo($"[PROBE] 卡牌实体类型: {card.GetType().FullName} | 名称: {Name(card)}");

                // ---- 0) 卡牌实体自身的关键成员（改卡/加能力的入口）----
                DumpCardMembers(card);

                object attrs = card.GetType().GetProperty("attributes")?.GetValue(card);
                Logger.LogInfo($"[PROBE] attributes 类型: {attrs?.GetType().FullName}");

                // ---- 1) 枚举属性（MutableAttributes : IEnumerable<IAttribute>）----
                var items = Iterate(attrs);
                Logger.LogInfo($"[PROBE] ==== 属性条目数: {items.Count} ====");
                int shown = 0;
                foreach (var it in items)
                {
                    if (shown++ >= 25) { Logger.LogInfo("[PROBE]   ...(截断)"); break; }
                    Logger.LogInfo($"[PROBE]   #{shown} {Describe(it)}");
                }

                // ---- 2) 直接读 data 字典 ----
                object data = null;
                try { data = attrs?.GetType().GetProperty("data")?.GetValue(attrs); } catch { }
                if (data != null)
                {
                    var kvs = Iterate(data);
                    Logger.LogInfo($"[PROBE] ==== data 字典条目数: {kvs.Count} (类型 {data.GetType().Name}) ====");
                    int k = 0;
                    foreach (var kv in kvs)
                    {
                        if (k++ >= 20) break;
                        Logger.LogInfo($"[PROBE]   KV {k}: {Describe(kv)}");
                    }
                }

                // ---- 3) 领袖能力树（深度 3）----
                var leader = Children(player).FirstOrDefault(x => Name(x) == "Leader");
                Logger.LogInfo($"[PROBE] ==== Leader 子树 ==== 子={Children(leader).Count}");
                DumpTree(leader, 1, 3);

                // ---- 4) 尝试从属性里解析 Archetype ----
                TryResolveArchetype(items);
            }
            catch (Exception e) { Logger.LogWarning("[PROBE] 异常: " + e.GetType().Name + ": " + e.Message); }
        }

        // ---------- 卡牌实体的关键成员 ----------
        private static void DumpCardMembers(object card)
        {
            var t = card.GetType();
            var chain = new List<string>();
            for (var c = t; c != null && c != typeof(object); c = c.BaseType) chain.Add(c.Name);
            Logger.LogInfo($"[PROBE] ==== 卡牌成员 ==== 继承链: {string.Join(" <- ", chain)}");

            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.GetIndexParameters().Length > 0) continue;
                var n = p.Name;
                if (n.IndexOf("Ability", StringComparison.OrdinalIgnoreCase) < 0 &&
                    n.IndexOf("Acquire", StringComparison.OrdinalIgnoreCase) < 0 &&
                    n.IndexOf("Cost", StringComparison.OrdinalIgnoreCase) < 0 &&
                    n.IndexOf("Value", StringComparison.OrdinalIgnoreCase) < 0 &&
                    n.IndexOf("Archetype", StringComparison.OrdinalIgnoreCase) < 0) continue;
                object v = null; string err = "";
                try { v = p.GetValue(card); } catch (Exception e) { err = "<" + e.GetType().Name + ">"; }
                var cnt = "";
                try
                {
                    if (v != null && v.GetType().Name.Contains("List") || (v != null && v.GetType().Name.Contains("Array")))
                        cnt = $" 数量={Iterate(v).Count}";
                }
                catch { }
                Logger.LogInfo($"[PROBE]   属性 {n} : {p.PropertyType.Name} = {(err != "" ? err : Short(v))}{cnt}");
            }

            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                var n = m.Name;
                if (n.IndexOf("Ability", StringComparison.OrdinalIgnoreCase) < 0 &&
                    n.IndexOf("Acquire", StringComparison.OrdinalIgnoreCase) < 0) continue;
                var ps = string.Join(", ", m.GetParameters().Select(x => $"{x.ParameterType.Name} {x.Name}"));
                Logger.LogInfo($"[PROBE]   方法 {m.ReturnType.Name} {n}({ps})");
            }

            // 试着读取收购成本
            try
            {
                var am = t.GetMethod("AcquireValue", BindingFlags.Public | BindingFlags.Instance);
                if (am != null) Logger.LogWarning($"[PROBE]   >>> AcquireValue() = {Short(am.Invoke(card, null))}");
            }
            catch (Exception e) { Logger.LogInfo("[PROBE]   AcquireValue 调用失败: " + (e.InnerException?.Message ?? e.Message)); }

            // 试着读取能力列表
            try
            {
                var ab = t.GetProperty("Abilities", BindingFlags.Public | BindingFlags.Instance)?.GetValue(card);
                if (ab != null)
                {
                    var list = Iterate(ab);
                    Logger.LogWarning($"[PROBE]   >>> Abilities 数量 = {list.Count} (类型 {ab.GetType().Name})");
                    int i = 0;
                    foreach (var a in list) { if (i++ >= 8) break; Logger.LogInfo($"[PROBE]      能力{i}: {Describe(a)}"); }
                }
            }
            catch (Exception e) { Logger.LogInfo("[PROBE]   Abilities 读取失败: " + (e.InnerException?.Message ?? e.Message)); }
        }

        // ---------- 属性条目描述 ----------
        private static string Describe(object it)
        {
            if (it == null) return "null";
            var t = it.GetType();
            var parts = new List<string> { t.Name };

            foreach (var pn in new[] { "Definition", "definition", "Id", "id", "Key", "key", "Value", "value" })
            {
                var p = t.GetProperty(pn, BindingFlags.Public | BindingFlags.Instance);
                if (p == null || p.GetIndexParameters().Length > 0) continue;
                try
                {
                    var v = p.GetValue(it);
                    parts.Add($"{pn}={Short(v)}");
                    // 定义对象再下一层
                    if (pn == "Definition" && v != null && v.GetType() != typeof(string))
                        parts.Add($"def.id={Short(Prop(v, "Id") ?? Prop(v, "id") ?? Prop(v, "AttributeID"))}");
                }
                catch { }
            }
            if (parts.Count <= 1)
            {
                // 退化：打印所有简单属性
                foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (p.GetIndexParameters().Length > 0) continue;
                    try
                    {
                        var v = p.GetValue(it);
                        if (v == null) continue;
                        var n = v.GetType().Name;
                        if (n is "String" or "Int32" or "Single" or "Boolean" or "Double" or "Int64")
                            parts.Add($"{p.Name}={Short(v)}");
                        else
                            parts.Add($"{p.Name}:{n}");
                    }
                    catch { }
                }
            }
            return string.Join(" | ", parts);
        }

        private static object Prop(object o, string name)
        {
            try { return o?.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(o); }
            catch { return null; }
        }

        // ---------- 通用枚举：Il2Cpp 集合不实现非泛型 IEnumerable 时退回反射 ----------
        private static List<object> Iterate(object o)
        {
            var r = new List<object>();
            if (o == null) return r;

            if (o is IEnumerable e)
            {
                try
                {
                    foreach (var x in e) { r.Add(x); if (r.Count > 500) break; }
                    if (r.Count > 0) return r;
                }
                catch { }
            }

            // 反射找 GetEnumerator（含 Il2Cpp 的显式实现命名）
            try
            {
                var t = o.GetType();
                var gm = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                          .FirstOrDefault(m => m.Name == "GetEnumerator" && m.GetParameters().Length == 0)
                       ?? t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                          .FirstOrDefault(m => m.Name.EndsWith("GetEnumerator") && m.GetParameters().Length == 0);
                if (gm != null)
                {
                    var en = gm.Invoke(o, null);
                    if (en is IEnumerator ie)
                        while (ie.MoveNext() && r.Count <= 500) r.Add(ie.Current);
                    else if (en != null)
                    {
                        var mv = en.GetType().GetMethod("MoveNext", BindingFlags.Public | BindingFlags.Instance)
                              ?? en.GetType().GetMethod("System_Collections_IEnumerator_MoveNext", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        var cur = en.GetType().GetProperty("Current", BindingFlags.Public | BindingFlags.Instance)
                               ?? en.GetType().GetProperty("System_Collections_IEnumerator_get_Current", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (mv != null && cur != null)
                        {
                            while (r.Count <= 500)
                            {
                                object more;
                                try { more = mv.Invoke(en, null); } catch { break; }
                                if (more is not bool b || !b) break;
                                try { r.Add(cur.GetValue(en)); } catch { break; }
                            }
                        }
                    }
                    if (r.Count > 0) return r;
                }
            }
            catch { }

            // Count + Item
            try
            {
                var cnt = o.GetType().GetProperty("Count");
                int n = cnt != null ? Convert.ToInt32(cnt.GetValue(o)) : 0;
                var item = o.GetType().GetProperty("Item");
                if (item != null && n > 0)
                    for (int i = 0; i < n && i < 500; i++)
                        try { r.Add(item.GetValue(o, new object[] { i })); } catch { }
            }
            catch { }
            return r;
        }

        private static void DumpTree(object e, int depth, int maxDepth)
        {
            if (e == null || depth > maxDepth) return;
            foreach (var c in Children(e))
            {
                Logger.LogInfo($"[PROBE]   {new string(' ', depth * 2)}{c?.GetType().Name} | {Name(c)} | 子={Children(c).Count}");
                if (depth < maxDepth) DumpTree(c, depth + 1, maxDepth);
            }
        }

        private static void TryResolveArchetype(List<object> items)
        {
            foreach (var it in items)
            {
                var v = Prop(it, "Value") ?? Prop(it, "value");
                if (v == null) continue;
                var s = Short(v);
                if (s.Length < 8 || s.Length > 40) continue;
                // 看起来像 GUID 的值，试试解析
                if (!s.Contains("-")) continue;
                Logger.LogInfo($"[PROBE] 候选 ArchetypeID 值: {s}");
                try
                {
                    var at = typeof(Archetype);
                    foreach (var m in at.GetMethods(BindingFlags.Public | BindingFlags.Static))
                    {
                        if (m.Name != "Get" || m.GetParameters().Length != 1) continue;
                        var pt = m.GetParameters()[0].ParameterType;
                        object arg = null;
                        try { arg = pt == v.GetType() ? v : Convert.ChangeType(v, pt); } catch { continue; }
                        var a = m.Invoke(null, new[] { arg });
                        Logger.LogWarning($"[PROBE]   => Archetype.Get({pt.Name}) -> {(a == null ? "null" : a.GetType().FullName)}");
                        if (a != null)
                        {
                            Logger.LogWarning($"[PROBE]   => 原型名: {Short(Prop(a, "EntityName"))}");
                            return;
                        }
                    }
                }
                catch (Exception e2) { Logger.LogInfo("[PROBE]   解析失败: " + (e2.InnerException?.Message ?? e2.Message)); }
            }
        }

        private static string Short(object v)
        {
            if (v == null) return "null";
            try { var s = v.ToString(); return s != null && s.Length > 70 ? s.Substring(0, 70) + "…" : s; }
            catch { return "<err>"; }
        }

        private static string Name(object e)
        {
            if (e == null) return "null";
            try
            {
                var v = e.GetType().GetProperty("EntityName")?.GetValue(e);
                if (v == null) return "null";
                var np = v.GetType().GetProperty("Name");
                return np != null ? (np.GetValue(v)?.ToString() ?? "null") : v.ToString();
            }
            catch { return "?"; }
        }

        private static List<object> Children(object e)
        {
            var r = new List<object>();
            if (e == null) return r;
            try
            {
                var list = e.GetType().GetProperty("Children")?.GetValue(e);
                if (list == null) return r;
                var cnt = list.GetType().GetProperty("Count");
                int n = cnt != null ? Convert.ToInt32(cnt.GetValue(list)) : 0;
                var item = list.GetType().GetProperty("Item");
                if (item == null) return r;
                for (int i = 0; i < n; i++) { try { r.Add(item.GetValue(list, new object[] { i })); } catch { } }
            }
            catch { }
            return r;
        }
    }
}
