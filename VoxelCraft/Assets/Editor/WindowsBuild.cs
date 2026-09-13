using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace VoxelCraft.Editor
{
    /// <summary>
    /// Headless Windows 64-bit player build for local play:
    ///   Unity.exe -batchmode -quit -projectPath &lt;proj&gt; `
    ///     -executeMethod VoxelCraft.Editor.WindowsBuild.Build -logFile &lt;log&gt;
    /// Logs "WINDOWS BUILD OK" on success. Output: Builds/Windows/VoxelCraft.exe
    /// (kept out of git - local体验 only, the shipped artifact is WebGL).
    /// </summary>
    public static class WindowsBuild
    {
        public static void Build()
        {
            string[] scenes = EditorBuildSettings.scenes
                .Where(s => s.enabled)
                .Select(s => s.path)
                .ToArray();
            if (scenes.Length == 0)
            {
                throw new Exception("[WindowsBuild] no enabled scenes in EditorBuildSettings");
            }

            string output = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Builds", "Windows", "VoxelCraft.exe"));
            if (File.Exists(output))
            {
                File.Delete(output);
            }
            string dir = Path.GetDirectoryName(output);
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }

            Debug.Log($"[WindowsBuild] building {scenes.Length} scene(s) -> {output}");
            BuildReport report = BuildPipeline.BuildPlayer(scenes, output, BuildTarget.StandaloneWindows64, BuildOptions.None);
            BuildSummary summary = report.summary;
            Debug.Log($"WINDOWS BUILD result={summary.result} errors={summary.totalErrors} size={summary.totalSize:N0}B");

            if (summary.result != BuildResult.Succeeded || summary.totalErrors > 0)
            {
                Debug.Log("WINDOWS BUILD FAIL");
                throw new Exception($"[WindowsBuild] failed: {summary.result}, errors={summary.totalErrors}");
            }
            Debug.Log("WINDOWS BUILD OK");
        }
    }
}
