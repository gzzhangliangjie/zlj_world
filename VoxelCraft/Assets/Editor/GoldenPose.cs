using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEditor;
using VoxelCraft.Creatures;

namespace VoxelCraft.Editor
{
    /// <summary>
    /// Golden-baseline pose capture + regression gate (SPEC.md tooling).
    ///
    /// CAPTURE mode (default): drives each species through the SAME
    /// deterministic harness as AnimVerify.VerifyGait (walking=true,
    /// moving=true, forward-creeping transform, Tick(1/60)) and records
    /// EVERY bone's local position+rotation each frame. Writes
    /// _logs/golden/<species>.txt (one line per bone per frame).
    ///
    /// CHECK mode (-goldenCheck): replays the same harness against the
    /// CURRENT code and diffs every frame/bone against the baseline.
    /// Any |delta| beyond tolerance => FAIL with the worst offenders.
    ///
    /// Purpose: when a mechanism change (controllers state machine, molang,
    /// additive sampling...) replaces a special-case config, this gate
    /// proves behavioral equivalence on all 26 species automatically -
    /// the "already-correct animals" become the framework's test suite.
    /// </summary>
    public static class GoldenPose
    {
        const int Frames = 180;
        const float Dt = 1f / 60f;
        const float RotTolDeg = 0.5f;
        const float PosTolM = 0.005f;

        static string[] Species = { "pig", "cow", "sheep", "chicken", "wolf", "fox", "mooshroom", "goat", "ocelot", "creeper", "horse", "donkey", "rabbit", "panda", "armadillo", "llama", "steve", "bee", "bat", "zombie", "skeleton", "villager", "spider", "parrot", "hoglin", "polar_bear", "salmon", "pufferfish", "axolotl", "croc" };
        static string LogDir => Path.Combine(Directory.GetParent(Application.dataPath).FullName, "_logs", "golden");

        public static void Run()
        {
            bool check = System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-goldenCheck") >= 0;
            Directory.CreateDirectory(LogDir);
            int totalSp = 0, passSp = 0;
            var report = new StringBuilder();
            foreach (var sp in Species)
            {
                string file = Path.Combine(LogDir, sp + ".txt");
                // capture always regenerates in-memory then compares/writes
                var frames = Capture(sp);
                if (!check)
                {
                    WriteFrames(file, frames);
                    report.AppendLine($"[GOLDEN] capture {sp}: {frames.Count} frames, {BoneCount(frames)} bone-samples -> {file}");
                    totalSp++; passSp++;
                    continue;
                }
                if (!File.Exists(file))
                {
                    report.AppendLine($"[GOLDEN] {sp}: MISSING baseline (run capture first)");
                    totalSp++; continue;
                }
                var diffs = Diff(file, frames);
                if (diffs.Count == 0)
                {
                    report.AppendLine($"[GOLDEN] {sp}: OK (0 diffs)");
                    totalSp++; passSp++;
                }
                else
                {
                    report.AppendLine($"[GOLDEN] {sp}: FAIL {diffs.Count} bone-frame diffs; worst:");
                    foreach (var d in diffs.GetRange(0, System.Math.Min(5, diffs.Count)))
                        report.AppendLine($"    {d}");
                    totalSp++;
                }
            }
            UnityEngine.Debug.Log(report.ToString());
            if (check)
                UnityEngine.Debug.Log($"[GOLDEN] RESULT: {(passSp == totalSp ? "PASS" : "FAIL")} species {passSp}/{totalSp}");
            else
                UnityEngine.Debug.Log($"[GOLDEN] RESULT: captured {passSp}/{totalSp}");
        }

        // Deterministic harness == AnimVerify.VerifyGait loop body.
        static List<string> Capture(string sp)
        {
            var frames = new List<string>();
            var go = new GameObject("Golden_" + sp);
            try
            {
                var ani = go.AddComponent<Creatures.BlockyAnimal>();
                ani.species = sp;
                ani.BuildModel();
                go.transform.rotation = Quaternion.identity;
                var player = go.GetComponent<Creatures.BedrockAnimationPlayer>();
                ani.walking = true;
                var sb = new StringBuilder();
                for (int f = 0; f < Frames; f++)
                {
                    if (player != null)
                    {
                        // hopper wing_flap injection (AnimVerify harness parity)
                        var reg = CreatureRegistry.Get(sp);
                        if (reg != null && reg.archetype == "hopper")
                            player.variables["wing_flap"] = (Mathf.Sin(f * Dt * 20f) + 1f) * 0.5f;
                        player.moving = true;
                        go.transform.position += go.transform.forward * (ani.walkSpeed * Dt);
                        player.Tick(Dt);
                        // B-plan species: advance controllers like AnimVerify does
                        ani.TickControllers(Dt, moving: true);
                    }
                    else ani.ApplyLegacyGait(ani.walkSpeed, Dt, f * Dt * (4f + ani.walkSpeed * 3f));
                    if (f < 30) continue; // skip warm-up blend, same window as gait checks
                    sb.Length = 0;
                    foreach (var t in go.GetComponentsInChildren<Transform>())
                    {
                        if (t == go.transform) continue;
                        var e = t.localEulerAngles; var p = t.localPosition;
                        sb.Append(t.name).Append('|')
                          .Append(e.x.ToString("F3")).Append(',').Append(e.y.ToString("F3")).Append(',').Append(e.z.ToString("F3")).Append(';')
                          .Append(p.x.ToString("F4")).Append(',').Append(p.y.ToString("F4")).Append(',').Append(p.z.ToString("F4")).Append('\n');
                    }
                    frames.Add(sb.ToString());
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
            return frames;
        }

        static int BoneCount(List<string> frames) => frames.Count > 0 ? frames[0].Split('\n').Length - 1 : 0;

        static void WriteFrames(string file, List<string> frames)
        {
            var sb = new StringBuilder();
            foreach (var fr in frames) sb.Append(fr);
            File.WriteAllText(file, sb.ToString());
        }

        static List<string> Diff(string file, List<string> now)
        {
            var lines = File.ReadAllLines(file);
            var bad = new List<string>();
            // baseline flattened: frames* bones lines; now: one string per frame
            int li = 0;
            for (int f = 0; f < now.Count && li < lines.Length; f++)
            {
                var cur = now[f].Split('\n');
                for (int b = 0; b < cur.Length && li < lines.Length; b++)
                {
                    if (cur[b].Length == 0) continue;
                    var old = lines[li++];
                    if (old.Length == 0) continue;
                    if (!ParseCompare(old, cur[b], out string worst))
                        bad.Add($"frame{f} {worst}");
                }
            }
            return bad;
        }

        static bool ParseCompare(string oldLine, string newLine, out string worst)
        {
            worst = null;
            var o = oldLine.Split('|'); var n = newLine.Split('|');
            if (o.Length != 2 || n.Length != 2 || o[0] != n[0]) { worst = $"bone-name mismatch '{o[0]}' vs '{n[0]}'"; return false; }
            var oe = o[1].Split(';'); var ne = n[1].Split(';');
            float dMax = 0f; string kind = "";
            for (int i = 0; i < 2; i++)
            {
                var a = oe[i].Split(','); var b2 = ne[i].Split(',');
                if (a.Length != 3 || b2.Length != 3) { worst = $"{o[0]} parse"; return false; }
                for (int c = 0; c < 3; c++)
                {
                    if (!float.TryParse(a[c], out var av) || !float.TryParse(b2[c], out var bv))
                    { worst = $"{o[0]} num"; return false; }
                    float d = Mathf.Abs(av - bv);
                    // euler wrap: 359.9 vs 0.1 is a 0.2 diff
                    if (i == 0) d = Mathf.Min(d, Mathf.Abs(360f - d));
                    float tol = i == 0 ? RotTolDeg : PosTolM;
                    if (d > dMax) { dMax = d; kind = i == 0 ? "rot" : "pos"; }
                }
            }
            if (dMax > (kind == "rot" ? RotTolDeg : PosTolM))
            { worst = $"{o[0]} {kind} delta {dMax:F3} [{oldLine}]->[{newLine}]"; return false; }
            return true;
        }
    }
}
