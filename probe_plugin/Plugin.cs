using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace DuneProbe
{
    [BepInPlugin(GUID, "Dune Probe", "0.6.0")]
    public class Plugin : BasePlugin
    {
        public const string GUID = "local.duneprobe";

        internal static ManualLogSource Logger;
        private static bool _injected;

        public override void Load()
        {
            Logger = Log;
            Logger.LogInfo("========== [PROBE v6] 注入尝试 ==========");

            var harmony = new Harmony("local.duneprobe.harmony");
            var postfix = new HarmonyMethod(typeof(Plugin).GetMethod(nameof(OnCreateDecks),
                BindingFlags.Static | BindingFlags.NonPublic));

            var t = FindType("worm.canis.actions.phases.SetupPhase");
            var mi = t?.GetMethod("CreateDecks", BindingFlags.Public | BindingFlags.NonPublic |
                                                BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (mi == null) { Logger.LogWarning("[PROBE] CreateDecks 未找到"); return; }

            harmony.Patch(mi, postfix: postfix);
            Logger.LogInfo("[PROBE] CreateDecks 已挂 postfix");
            Logger.LogInfo("========== [PROBE v6] 就绪 ==========");
        }

        private static void OnCreateDecks(object __instance)
        {
            if (_injected) return;
            try
            {
                object match = GetProp(__instance, "wormMatch");
                if (match == null) { Logger.LogWarning("[PROBE] wormMatch 为 null"); return; }

                object order = GetProp(match, "PlayerTurnOrder");
                var players = ToList(order);
                Logger.LogInfo($"[PROBE] 玩家数: {players.Count}");

                foreach (var p in players)
                {
                    if (p == null) continue;
                    bool human = ToBool(GetProp(p, "IsHuman"));
                    var deck = GetProp(p, "Deck");
                    Logger.LogInfo($"[PROBE] 玩家 {Str(GetProp(p, "EntityName"))} 人类={human} 牌组={Count(deck)}");
                    if (human && !_injected)
                    {
                        DoInject(match, p, deck);
                        _injected = true;
                    }
                }
            }
            catch (Exception e)
            {
                Logger.LogWarning("[PROBE] 异常: " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static void DoInject(object match, object player, object deck)
        {
            if (deck == null) { Logger.LogWarning("[PROBE] Deck 为 null"); return; }

            var archType = FindType("worm.canis.archetypes.WormArchetypes");
            var archetypeType = FindType("canis.archetypes.Archetype");
            if (archType == null || archetypeType == null) { Logger.LogWarning("[PROBE] 类型缺失"); return; }

            object inst = GetProp(archType, "Instance") ?? archType.GetProperty("Instance")?.GetValue(null);
            if (inst == null) { Logger.LogWarning("[PROBE] 单例为 null"); return; }

            object all = archType.GetMethod("AllArchetypes").Invoke(inst, null);
            object dagger = null;
            foreach (var a in ToList(all))
            {
                if (a != null && a.GetType().Name == "Dagger") { dagger = a; break; }
            }
            if (dagger == null) { Logger.LogWarning("[PROBE] 未找到 Dagger"); return; }
            Logger.LogInfo($"[PROBE] 原型: {dagger.GetType().FullName}");

            MethodInfo create = null;
            foreach (var m in archType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (m.Name != "CreateEntity") continue;
                var ps = m.GetParameters();
                if (ps.Length == 4 && ps[0].ParameterType == archetypeType) { create = m; break; }
            }
            if (create == null) { Logger.LogWarning("[PROBE] 未找到 CreateEntity(Archetype,...)"); return; }

            int before = Count(deck);
            object entity = null;
            try
            {
                var ps = create.GetParameters();
                entity = create.Invoke(inst, new object[] { dagger, match, player, null });
            }
            catch (Exception e)
            {
                Logger.LogWarning("[PROBE] CreateEntity 失败: " + e.GetType().Name + ": " +
                                  (e.InnerException?.Message ?? e.Message));
                return;
            }
            if (entity == null) { Logger.LogWarning("[PROBE] 实体为 null"); return; }
            Logger.LogInfo($"[PROBE] 实体类型: {entity.GetType().Name}");

            var add = deck.GetType().GetMethod("Add", new[] { entity.GetType() }) ??
                      deck.GetType().GetMethod("Add");
            if (add == null) { Logger.LogWarning("[PROBE] Deck 无 Add 方法"); return; }

            try
            {
                var pt = add.GetParameters()[0].ParameterType;
                Logger.LogInfo($"[PROBE] Add 参数类型={pt.Name} 实体是否匹配={pt.IsInstanceOfType(entity)}");
                add.Invoke(deck, new[] { entity });
                Logger.LogWarning($"[PROBE] >>> 注入完成: 牌组 {before} -> {Count(deck)}");
            }
            catch (Exception e)
            {
                Logger.LogWarning("[PROBE] Add 失败: " + e.GetType().Name + ": " + (e.InnerException?.Message ?? e.Message));
            }
        }

        private static List<object> ToList(object coll)
        {
            var result = new List<object>();
            if (coll == null) return result;

            if (coll is IEnumerable e)
            {
                foreach (var x in e) result.Add(x);
                return result;
            }

            int n = Count(coll);
            if (n <= 0) return result;

            var item = coll.GetType().GetProperty("Item");
            if (item == null) return result;
            for (int i = 0; i < n; i++)
            {
                try { result.Add(item.GetValue(coll, new object[] { i })); }
                catch { }
            }
            return result;
        }

        private static Type FindType(string full)
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = a.GetType(full);
                if (t != null) return t;
            }
            return null;
        }

        private static object GetProp(object o, string name)
        {
            if (o == null) return null;
            try
            {
                var p = o.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                return p?.GetValue(o);
            }
            catch { return null; }
        }

        private static bool ToBool(object o)
        {
            try { return o != null && Convert.ToBoolean(o); } catch { return false; }
        }

        private static string Str(object o)
        {
            if (o == null) return "null";
            try { return o.ToString(); } catch { return "<err>"; }
        }

        private static int Count(object coll)
        {
            if (coll == null) return -1;
            try
            {
                var c = coll.GetType().GetProperty("Count");
                return c != null ? Convert.ToInt32(c.GetValue(coll)) : -1;
            }
            catch { return -1; }
        }
    }
}
