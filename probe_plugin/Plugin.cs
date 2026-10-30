using System;
using System.Collections;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using canis.archetypes;
using Canis;
using Canis.entities;
using worm.canis.archetypes;
using worm.canis.actions.phases;
using worm.canis.entities;

namespace DuneProbe
{
    [BepInPlugin(GUID, "Dune Probe", "0.4.0")]
    public class Plugin : BasePlugin
    {
        public const string GUID = "local.duneprobe";

        internal static ManualLogSource Logger;
        private static bool _injected;

        public override void Load()
        {
            Logger = Log;
            Logger.LogInfo("========== [PROBE v4] 注入测试开始 ==========");
            PatchDeckSetup();
            Logger.LogInfo("========== [PROBE v4] 就绪 ==========");
        }

        private static void PatchDeckSetup()
        {
            var harmony = new Harmony("local.duneprobe.harmony");
            var postfix = new HarmonyMethod(typeof(Plugin).GetMethod(nameof(OnDeckSetup),
                BindingFlags.Static | BindingFlags.NonPublic));

            var mi = typeof(DeckSetupPhase).GetMethod("CompleteDeckSetup",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (mi == null)
            {
                Logger.LogWarning("[PROBE] CompleteDeckSetup 未找到");
                return;
            }
            harmony.Patch(mi, postfix: postfix);
            Logger.LogInfo("[PROBE] CompleteDeckSetup 已挂 postfix");
        }

        private static Archetype FindArchetype(string typeNameFragment)
        {
            try
            {
                var all = WormArchetypes.Instance.AllArchetypes();
                if (all == null) return null;
                foreach (object o in (IEnumerable)all)
                {
                    var a = o as Archetype;
                    if (a == null) continue;
                    if (a.GetType().Name.IndexOf(typeNameFragment, StringComparison.OrdinalIgnoreCase) >= 0)
                        return a;
                }
            }
            catch (Exception e)
            {
                Logger.LogWarning("[PROBE] 枚举原型失败: " + e.GetType().Name + ": " + e.Message);
            }
            return null;
        }

        private static bool IsHuman(object player)
        {
            try
            {
                var p = player.GetType().GetProperty("IsHuman");
                if (p != null) return (bool)p.GetValue(player);
            }
            catch { }
            return false;
        }

        private static string Prop(object o, string name)
        {
            try
            {
                var p = o.GetType().GetProperty(name);
                if (p == null) return "<无此属性>";
                var v = p.GetValue(o);
                return v == null ? "null" : v.ToString();
            }
            catch (Exception e) { return "<" + e.GetType().Name + ">"; }
        }

        private static void OnDeckSetup(DeckSetupPhase __instance)
        {
            try
            {
                object player = __instance.player;
                object match = __instance.wormMatch;

                Logger.LogWarning($"[PROBE] >>> CompleteDeckSetup: player={Prop(player, "EntityName")} 类型={player?.GetType().Name} IsHuman={IsHuman(player)}");

                if (_injected) return;
                if (!IsHuman(player)) return;

                var arch = FindArchetype("Dagger");
                if (arch == null) { Logger.LogWarning("[PROBE] 未找到 Dagger 原型"); return; }
                Logger.LogInfo($"[PROBE] 找到原型: {arch.GetType().Name}");

                Entity created = null;
                try
                {
                    created = WormArchetypes.Instance.CreateEntity(arch, (Canis.Match)match, (PlayerEntity)player, null);
                }
                catch (Exception e)
                {
                    Logger.LogWarning("[PROBE] CreateEntity 失败: " + e.GetType().Name + ": " + e.Message);
                    return;
                }

                if (created == null) { Logger.LogWarning("[PROBE] CreateEntity 返回 null"); return; }
                Logger.LogInfo($"[PROBE] 已创建实体: {created.GetType().Name}");

                var deckProp = player.GetType().GetProperty("Deck");
                if (deckProp == null) { Logger.LogWarning("[PROBE] player 上没有 Deck 属性"); return; }
                object deck = deckProp.GetValue(player);
                if (deck == null) { Logger.LogWarning("[PROBE] Deck 为 null"); return; }

                string deckType = deck.GetType().Name;
                int before = CountOf(deck);
                Logger.LogInfo($"[PROBE] Deck 类型={deckType} 当前数量={before}");

                var add = deck.GetType().GetMethod("Add");
                if (add == null) { Logger.LogWarning("[PROBE] Deck 上没有 Add 方法"); return; }

                var paramType = add.GetParameters()[0].ParameterType;
                Logger.LogInfo($"[PROBE] Add 参数类型={paramType.Name}");

                object toAdd = created;
                if (!paramType.IsInstanceOfType(created))
                {
                    var tryCast = created.GetType().GetMethod("TryCast");
                    Logger.LogWarning("[PROBE] 实体类型不匹配，直接添加可能失败");
                }

                add.Invoke(deck, new[] { toAdd });
                int after = CountOf(deck);
                Logger.LogWarning($"[PROBE] >>> 注入完成: 牌组 {before} -> {after}");
                _injected = true;
            }
            catch (Exception e)
            {
                Logger.LogWarning("[PROBE] 注入流程异常: " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static int CountOf(object coll)
        {
            try
            {
                var c = coll.GetType().GetProperty("Count");
                if (c != null) return (int)c.GetValue(coll);
            }
            catch { }
            return -1;
        }
    }
}
