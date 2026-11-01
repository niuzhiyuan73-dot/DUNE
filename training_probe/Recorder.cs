using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using BepInEx.Logging;
using Canis;
using Canis.entities;
using Networking.selection.messages;

namespace DuneTrainingRecorder;

internal static class Recorder
{
    internal static string Output;
    private static string Root;
    private static ManualLogSource Log;
    private static readonly string RunId = Guid.NewGuid().ToString("N");
    private static readonly BlockingCollection<(string path, string line)> Queue = new();
    private static int Writing;
    private static readonly Dictionary<string, Session> Sessions = new();
    private sealed class Session
    {
        internal string GameId, Folder;
        internal readonly Dictionary<string, Decision> Pending = new();
        internal readonly List<Submission> Awaiting = new();
        internal readonly Dictionary<string, string> Action = new();
        internal long Sequence;
    }
    private sealed class Decision
    { internal string Id, Actor, Fingerprint, Action; internal JsonObject State; internal int Counter; }
    internal sealed class Submission
    {
        internal Match Match; internal PlayerEntity Player;
        internal string Game, DecisionId, AttemptId;
        internal JsonObject Response, Before;
    }
    internal static void Init(string root, ManualLogSource log)
    {
        Root = root; Log = log; Output = Path.Combine(root, "dump", "training");
        Directory.CreateDirectory(Output);
        Capture.Init(root);
        new Thread(Writer) { IsBackground = true, Name = "Dune human training writer" }.Start();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Flush();
    }
    internal static void Warn(string operation, Exception e)
    { Log?.LogWarning("[TRAINING] " + operation + ": " + e.GetType().Name + " " + e.Message); }
    private static void Writer()
    {
        foreach (var job in Queue.GetConsumingEnumerable())
        {
            Interlocked.Increment(ref Writing);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(job.path));
                using var stream = new FileStream(job.path, FileMode.Append, FileAccess.Write, FileShare.Read);
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                writer.WriteLine(job.line); writer.Flush(); stream.Flush(true);
            }
            catch (Exception e)
            {
                Log.LogError("[TRAINING] RECORD WRITE FAILED: " + e.Message);
                // Keep evidence on a separate recovery spool rather than silently lose the record.
                try { File.AppendAllText(Path.Combine(Output, "recovery.jsonl"),
                    new JsonObject { ["intended_path"] = job.path, ["event"] = JsonNode.Parse(job.line) }.ToJsonString() + "\n"); }
                catch (Exception recovery) { Log.LogError("[TRAINING] recovery also failed: " + recovery.Message); }
            }
            finally { Interlocked.Decrement(ref Writing); }
        }
    }
    internal static void Flush()
    { var end = DateTime.UtcNow.AddSeconds(3); while ((Queue.Count > 0 || Writing > 0) && DateTime.UtcNow < end) Thread.Sleep(10); }
    private static void Emit(Session s, string type, JsonObject data)
    {
        data["schema_version"] = 1; data["event_id"] = Guid.NewGuid().ToString("N");
        data["event_type"] = type; data["game_id"] = s.GameId; data["capture_run_id"] = RunId;
        data["timestamp"] = DateTimeOffset.UtcNow.ToString("O");
        data["sequence_in_run"] = ++s.Sequence;
        Queue.Add((Path.Combine(s.Folder, "events.jsonl"), data.ToJsonString()));
    }
    private static Session SessionFor(Match m)
    {
        string game = m.GameID?.ToString();
        if (string.IsNullOrEmpty(game)) throw new InvalidOperationException("No stable GameID; refusing to mix games");
        if (Sessions.TryGetValue(game, out var s)) return s;
        // Use a filesystem-safe stable hash even if a future game version changes GameID format.
        string directory = Guid.TryParse(game, out var gid) ? gid.ToString() : Hash(game);
        s = new Session { GameId = game, Folder = Path.Combine(Output, directory) }; Sessions[game] = s;
        var versions = new JsonObject();
        foreach (var file in new[] { "mods/ai/effect_map_v3.tsv", "mods/ai/name_map.tsv", "mods/ai/knowledge/core_rules.md",
            "mods/ai/knowledge/current_mode.md" })
        {
            string path = Path.Combine(Root, file);
            if (!File.Exists(path)) continue;
            byte[] bytes = File.ReadAllBytes(path); string sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            versions[file] = sha;
            // Freeze source versions alongside this session so later table edits don't rewrite history.
            var dest = Path.Combine(s.Folder, "sources", sha + Path.GetExtension(file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest));
            if (!File.Exists(dest)) File.WriteAllBytes(dest, bytes);
        }
        Emit(s, "session_started", new JsonObject { ["mode"] = Capture.Mode(m), ["source_hashes"] = versions,
            ["recorder_version"] = "1.0.0", ["human_only"] = true,
            ["coverage_note"] = "records begin at first observed human prompt; existing saves may have earlier unrecorded play" });
        return s;
    }
    internal static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
    private static string Key(PlayerEntity p, int counter) => p.AccountID + ":" + counter;
    internal static void Offer(Match m, PlayerEntity player, SelectionMessage selection, string source)
    {
        try
        {
            if (m == null || player == null || selection == null) return;
            // AI turns are boundaries only, never training samples.
            Boundary(m, player.IsAI ? "before_ai_prompt" : "before_human_prompt");
            if (player.IsAI) return;
            var s = SessionFor(m); string key = Key(player, selection.Counter);
            var choices = Capture.Selection(m, selection);
            string fingerprint = Hash(choices.ToJsonString());
            if (s.Pending.TryGetValue(key, out var existing) && existing.Fingerprint == fingerprint) return;
            var state = Capture.Snapshot(m, player);
            string actor = player.AccountID.ToString(); string kind = choices["kind"]?.ToString();
            string id = RunId + ":" + selection.Counter + ":" + Guid.NewGuid().ToString("N");
            if (!s.Action.ContainsKey(actor) || kind == "PlayerTurn" || kind == "AssignLeader" || kind == "Combat") s.Action[actor] = id;
            var d = new Decision { Id = id, Actor = actor, Counter = selection.Counter,
                Fingerprint = fingerprint, State = state, Action = s.Action[actor] };
            s.Pending[key] = d;
            Emit(s, "decision_offered", new JsonObject { ["decision_id"] = id, ["action_group_id"] = d.Action,
                ["supersedes_decision_id"] = existing?.Id, ["actor_id"] = actor, ["actor_name"] = player.PlayerName,
                ["actor_is_ai"] = false, ["capture_source"] = source, ["before_state"] = state,
                ["selection"] = choices, ["label"] = new JsonObject { ["score"] = null, ["source"] = "unrated",
                    ["scale_min"] = 1, ["scale_max"] = 10, ["pass_score"] = 6 } });
        }
        catch (Exception e) { Warn("offer", e); }
    }
    internal static Submission BeforeSubmit(Match m, PlayerEntity p, int counter, Il2CppSystem.Object input)
    {
        if (p.IsAI) return null;
        var s = SessionFor(m);
        if (!s.Pending.TryGetValue(Key(p, counter), out var d))
        {
            Emit(s, "capture_gap", new JsonObject { ["actor_id"] = p.AccountID.ToString(), ["native_counter"] = counter,
                ["reason"] = "human_response_without_captured_prompt", ["response"] = Capture.Response(input) });
            return null;
        }
        var sub = new Submission { Match = m, Player = p, Game = s.GameId, DecisionId = d.Id,
            AttemptId = Guid.NewGuid().ToString("N"), Response = Capture.Response(input), Before = Capture.Snapshot(m, p) };
        Emit(s, "response_submitted", new JsonObject { ["decision_id"] = d.Id, ["attempt_id"] = sub.AttemptId,
            ["actor_id"] = d.Actor, ["native_counter"] = counter, ["response"] = Native.Copy(sub.Response),
            ["submission_state"] = Native.Copy(sub.Before) });
        return sub;
    }
    internal static void AfterSubmit(Submission sub, bool accepted)
    {
        try
        {
            var s = Sessions[sub.Game];
            Emit(s, "response_result", new JsonObject { ["decision_id"] = sub.DecisionId,
                ["attempt_id"] = sub.AttemptId, ["accepted"] = accepted });
            if (accepted)
            {
                s.Awaiting.Add(sub);
                // Native counters can be reused after an undo; a later offer becomes a new decision.
                string found = null;
                foreach (var kv in s.Pending) if (kv.Value.Id == sub.DecisionId) { found = kv.Key; break; }
                if (found != null) s.Pending.Remove(found);
            }
        }
        catch (Exception e) { Warn("response_result", e); }
    }
    internal static void Boundary(Match m, string source)
    {
        try
        {
            if (m == null || !Sessions.TryGetValue(m.GameID.ToString(), out var s) || s.Awaiting.Count == 0) return;
            var waiting = s.Awaiting.ToArray(); s.Awaiting.Clear();
            foreach (var sub in waiting)
            {
                var after = Capture.Snapshot(m, sub.Player);
                Emit(s, "resolution_observed", new JsonObject { ["decision_id"] = sub.DecisionId,
                    ["attempt_id"] = sub.AttemptId, ["boundary"] = source,
                    ["after_state"] = after, ["attribution"] = "observed_interval_not_guaranteed_single_action_reward",
                    ["note"] = "May include triggered effects or intermediate sub-decisions; net changes are not gross rewards/costs." });
            }
        }
        catch (Exception e) { Warn("boundary", e); }
    }
}
