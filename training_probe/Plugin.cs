using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Diagnostics;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Canis;
using Canis.entities;
using Canis.hasExecutionStack;
using Networking.selection.messages;
using worm.canis.entities;

namespace DuneTrainingRecorder;

[BepInPlugin("local.dune.human-training", "Dune Human Training Recorder", "1.0.0")]
public sealed class Plugin : BasePlugin
{
    internal static ManualLogSource Logger;
    internal static string Root;
    public override void Load()
    {
        Logger = Log;
        Root = Path.GetFullPath(Path.Combine(Paths.GameRootPath, ".."));
        Recorder.Init(Root, Log);
        var h = new Harmony("local.dune.human-training.hooks");
        Hook(h, typeof(PlayerEntity), "SelectFrom", nameof(Select), null);
        Hook(h, typeof(WormPlayer), "SelectFrom", nameof(Select), null);
        // Active-selection registration is an additional observation point on resume.
        Hook(h, typeof(Match), "SetActiveSelectionOfPlayer", null, nameof(Active));
        Hook(h, typeof(HasExecutionStack), "ConsumeCounter", nameof(ConsumeBefore), nameof(ConsumeAfter));
        Hook(h, typeof(HasExecutionStack), "Run", null, nameof(Paused));
        Hook(h, typeof(Match), "SaveOnPause", null, nameof(Saved));
        Hook(h, typeof(Match), "Dispose", nameof(Closing), null);
        StartDocuments();
        Log.LogInfo("[TRAINING] human-only recorder loaded; records: " + Recorder.Output);
    }

    private static void Hook(Harmony h, Type type, string name, string pre, string post)
    {
        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.Name == name).ToArray();
        if (methods.Length == 0) Logger.LogWarning("[TRAINING] hook unavailable: " + type.Name + "." + name);
        foreach (var m in methods)
            try
            {
                h.Patch(m, pre == null ? null : new HarmonyMethod(typeof(Plugin).GetMethod(pre, BindingFlags.NonPublic | BindingFlags.Static)),
                    post == null ? null : new HarmonyMethod(typeof(Plugin).GetMethod(post, BindingFlags.NonPublic | BindingFlags.Static)));
                Logger.LogInfo("[TRAINING] hooked " + type.Name + "." + name);
            }
            catch (Exception e) { Logger.LogError("[TRAINING] hook " + name + ": " + e.Message); }
    }

    private static void Select(PlayerEntity __instance, SelectionMessage __0, Match __1)
    { Recorder.Offer(__1, __instance, __0, "select_from"); }

    private static void Active(Match __instance, Canis.utils.ids.AccountID __0, SelectionMessage __1)
    {
        try
        {
            foreach (var p in __instance.players)
                if (p.AccountID.ToString() == __0.ToString()) Recorder.Offer(__instance, p, __1, "active_selection");
        }
        catch (Exception e) { Recorder.Warn("active_selection", e); }
    }

    private static void ConsumeBefore(HasExecutionStack __instance, int __0, Il2CppSystem.Object __1,
        PlayerEntity __2, out Recorder.Submission __state)
    {
        __state = null;
        try
        {
            if (__2 == null || __2.IsAI) return;
            var pending = __instance.GetPlayerPendingResponse(__2, __0);
            if (pending != null) Recorder.Offer(__instance.Match, __2, pending.Selection, "consume_fallback");
            __state = Recorder.BeforeSubmit(__instance.Match, __2, __0, __1);
        }
        catch (Exception e) { Recorder.Warn("consume_before", e); }
    }
    private static void ConsumeAfter(bool __result, Recorder.Submission __state)
    { if (__state != null) Recorder.AfterSubmit(__state, __result); }
    private static void Paused(HasExecutionStack __instance)
    { try { Recorder.Boundary(__instance.Match, "execution_pause"); } catch (Exception e) { Recorder.Warn("pause", e); } }
    private static void Saved(Match __instance)
    { Recorder.Boundary(__instance, "save_pause"); }
    private static void Closing(Match __instance)
    { Recorder.Boundary(__instance, "match_dispose"); Recorder.Flush(); }

    private static void StartDocuments()
    {
        try
        {
            var cfg = Path.Combine(Root, "mods", "training", "python_path.txt");
            string python = File.Exists(cfg) ? File.ReadAllText(cfg).Trim() : "python";
            var start = new ProcessStartInfo(python) { UseShellExecute = false, CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Root };
            start.ArgumentList.Add("-B");
            start.ArgumentList.Add(Path.Combine(Root, "mods", "tools", "training_documents.py"));
            start.ArgumentList.Add("--root"); start.ArgumentList.Add(Root);
            Process.Start(start);
            Logger.LogInfo("[TRAINING] documents: http://127.0.0.1:18761 (separate local process)");
        }
        catch (Exception e) { Logger.LogError("[TRAINING] document service failed (raw recording remains active): " + e.Message); }
    }
}
