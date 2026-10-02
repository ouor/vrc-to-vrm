using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using Debug = UnityEngine.Debug;

namespace VrcToVrm
{
    /// <summary>
    /// Converts prefabs with the default (auto-guessed) settings and writes a report.
    /// Runs from the menu (selected prefabs/folders), from -executeMethod (Convert), or when Temp/VrcToVrm.run
    /// appears (one prefab asset path per line; an empty file does nothing).
    /// </summary>
    [InitializeOnLoad]
    public static class VrcToVrmBatch
    {
        const string TriggerFile = "Temp/VrcToVrm.run";
        const string ReportFile = "Logs/VrcToVrm_report.txt";

        static double _nextPoll;

        // Poll instead of checking once at load: the trigger may be written after the last script reload.
        static VrcToVrmBatch() => EditorApplication.update += Poll;

        static void Poll()
        {
            if (EditorApplication.timeSinceStartup < _nextPoll) return;
            _nextPoll = EditorApplication.timeSinceStartup + 2;
            if (!File.Exists(TriggerFile)) return;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode) return;
            string[] paths;
            try
            {
                paths = File.ReadAllLines(TriggerFile).Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
                File.Delete(TriggerFile);
            }
            catch (IOException)
            {
                return; // still being written or locked by the writer; try again on the next poll
            }
            if (paths.Length > 0) Convert(paths);
        }

        /// <summary>Converts every selected prefab (or prefabs inside selected folders) that contains a VRChat avatar.</summary>
        [MenuItem("Tools/VRC → VRM/Batch Convert Selected", false, 1)]
        public static void ConvertSelected()
        {
            var selected = Selection.assetGUIDs.Select(AssetDatabase.GUIDToAssetPath).ToArray();
            var folders = selected.Where(AssetDatabase.IsValidFolder).ToArray();
            var paths = selected.Where(p => !AssetDatabase.IsValidFolder(p) && IsAvatarPrefab(p))
                .Concat(folders.Length > 0 ? FindAvatars(folders) : new string[0])
                .Distinct().OrderBy(p => p).ToArray();
            if (paths.Length == 0)
            {
                EditorUtility.DisplayDialog("VRC → VRM", L.Tr("batch.001"), "OK");
                return;
            }
            Convert(paths);
            EditorUtility.RevealInFinder(ReportFile);
        }

        [MenuItem("Tools/VRC → VRM/Batch Convert Selected", true)]
        static bool CanConvertSelected() => Selection.assetGUIDs.Length > 0;

        static bool IsAvatarPrefab(string path) =>
            path.EndsWith(".prefab") && AssetDatabase.LoadAssetAtPath<GameObject>(path)?.GetComponentInChildren<VRCAvatarDescriptor>(true) != null;

        public static string[] FindAvatars(params string[] folders) =>
            AssetDatabase.FindAssets("t:Prefab", folders).Select(AssetDatabase.GUIDToAssetPath)
                .Where(IsAvatarPrefab)
                .OrderBy(p => p).ToArray();

        public static void Convert(string[] paths)
        {
            var sb = new StringBuilder();
            var summary = new List<string>();
            var usedNames = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            try
            {
                for (int i = 0; i < paths.Length; i++)
                {
                    var path = paths[i];
                    var src = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                    if (src == null) { summary.Add($"MISSING  {path}"); continue; }
                    EditorUtility.DisplayProgressBar("VRC → VRM batch", $"{src.name} ({i + 1}/{paths.Length})", i / (float)paths.Length);
                    var sw = Stopwatch.StartNew();
                    sb.AppendLine($"===== {path}");
                    ConversionResult r;
                    try
                    {
                        var a = VrcAvatarAnalysis.Analyze(src);
                        // two prefabs with the same avatar name must not share (and overwrite) one output folder
                        var baseName = LilToonToMToon.Sanitize(a.Root.name); // the converter uses the sanitized name for the folder
                        var outName = baseName;
                        for (int n = 2; !usedNames.Add(outName); n++) outName = $"{baseName}_{n}";
                        var o = new ConversionOptions { Title = src.name, OutputName = outName };
                        o.Blink = a.BlinkShape; o.BlinkLeft = a.BlinkLeftShape; o.BlinkRight = a.BlinkRightShape;
                        o.LookUp = a.LookUpShape; o.LookDown = a.LookDownShape;
                        o.Emotions = ShapeNameHeuristics.GuessEmotions(a.Candidates);
                        o.Customs = new HashSet<ExpressionCandidate>(a.Candidates.Where(c => !o.Emotions.ContainsValue(c)));

                        foreach (var w in a.Warnings) sb.AppendLine("  [Analyze] " + w);
                        sb.AppendLine($"  lipsync={a.LipSyncSource}: {string.Join(", ", a.Vowels.Select(kv => $"{kv.Key}={string.Join("+", kv.Value.Select(v => v.ShapeName))}"))}");
                        sb.AppendLine($"  blink={a.BlinkShape} L={a.BlinkLeftShape} R={a.BlinkRightShape} up={a.LookUpShape} down={a.LookDownShape} eyes={a.EyeUpDeg:0}/{a.EyeDownDeg:0}/{a.EyeOuterDeg:0}/{a.EyeInnerDeg:0}");
                        foreach (var kv in o.Emotions) sb.AppendLine($"  emotion {kv.Key} <- {kv.Value.ShortLabel}");
                        foreach (var c in a.Candidates) sb.AppendLine($"  candidate {c.ShortLabel} shapes={c.Shapes.Count}");

                        r = VrcToVrmConverter.Convert(src, a, o);
                        var emo = string.Join(",", o.Emotions.Keys.Select(k => k.ToString()));
                        var kb = r.Success && File.Exists(r.VrmPath) ? new FileInfo(r.VrmPath).Length / 1024 : 0;
                        var errors = r.Log.Count(l => l.StartsWith("[Error]") || (l.Contains("Validate:Error") && !l.Contains("Author")));
                        summary.Add($"{(r.Success ? "OK  " : "FAIL")} {src.name,-28} {kb / 1024,4}MB {sw.Elapsed.TotalSeconds,5:0}s  blink={(a.BlinkShape != null ? "y" : "n")} LR={(a.BlinkLeftShape != null ? "y" : "n")} lip={a.Vowels.Count} emo=[{emo}] cand={a.Candidates.Count} pb={a.PhysBoneCount} err={errors}");
                    }
                    catch (System.Exception e)
                    {
                        r = new ConversionResult();
                        r.Log.Add("[Error] " + e);
                        summary.Add($"EXC  {src.name,-28} {e.GetType().Name}: {e.Message}");
                    }
                    sb.AppendLine($"  success={r.Success} vrm={r.VrmPath}");
                    foreach (var l in r.Log) sb.AppendLine("  " + l);
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
            Directory.CreateDirectory(Path.GetDirectoryName(ReportFile));
            File.WriteAllText(ReportFile, "SUMMARY\n" + string.Join("\n", summary) + "\n\n" + sb);
            Debug.Log($"[VrcToVrm] batch done: {paths.Length} prefabs, report: {ReportFile}");
        }
    }
}
