using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace VoxelCraft.Editor
{
    /// <summary>
    /// Headless WebGL build entry point:
    ///   Unity.exe -batchmode -quit -projectPath &lt;proj&gt; `
    ///     -executeMethod VoxelCraft.Editor.WebGLBuild.Build -logFile &lt;log&gt;
    /// Logs "WEBGL BUILD OK" plus an artifact list, or throws (non-zero exit).
    /// Player settings are applied via reflection so this compiles on any editor minor.
    /// </summary>
    public static class WebGLBuild
    {
        public static void Build()
        {
            // Compression: gzip when available, plus decompression fallback so the
            // build runs on ANY static host (no Content-Encoding headers required).
            TrySetBool("UnityEditor.EditorUserBuildSettings", "webGLCompressionGzip", true);
            TrySetBool("UnityEditor.EditorUserBuildSettings", "webGLCompressionBrotli", false);
            TrySetEnum("UnityEditor.EditorUserBuildSettings", "webGLCompression", "Gzip");
            TrySetEnum("UnityEditor.PlayerSettings+WebGL", "compressionFormat", "Gzip");
            TrySetBool("UnityEditor.EditorUserBuildSettings", "webGLDecompressionFallback", true);
            TrySetBool("UnityEditor.PlayerSettings+WebGL", "decompressionFallback", true);
            TrySetEnum("UnityEditor.PlayerSettings+WebGL", "memoryGrowthMode", "Geometric");

            string[] scenes = EditorBuildSettings.scenes
                .Where(s => s.enabled)
                .Select(s => s.path)
                .ToArray();
            if (scenes.Length == 0)
            {
                throw new Exception("[WebGLBuild] no enabled scenes in EditorBuildSettings");
            }

            string output = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Builds", "WebGL"));
            if (Directory.Exists(output))
            {
                Directory.Delete(output, true);
            }

            Debug.Log($"[WebGLBuild] building {scenes.Length} scene(s) -> {output}");
            BuildReport report = BuildPipeline.BuildPlayer(scenes, output, BuildTarget.WebGL, BuildOptions.None);
            BuildSummary summary = report.summary;
            Debug.Log($"WEBGL BUILD result={summary.result} errors={summary.totalErrors} size={summary.totalSize:N0}B");

            if (summary.result != BuildResult.Succeeded || summary.totalErrors > 0)
            {
                Debug.Log("WEBGL BUILD FAIL");
                throw new Exception($"[WebGLBuild] failed: {summary.result}, errors={summary.totalErrors}");
            }

            foreach (string file in Directory.GetFiles(output, "*", SearchOption.AllDirectories))
            {
                Debug.Log($"WEBGL ARTIFACT {file} {new FileInfo(file).Length}");
            }
            Debug.Log("WEBGL BUILD OK");
        }

        private static void TrySetBool(string typeName, string property, bool value)
        {
            PropertyInfo p = FindProperty(typeName, property);
            if (p != null && p.PropertyType == typeof(bool))
            {
                p.SetValue(null, value);
                Debug.Log($"[WebGLBuild] {typeName}.{property} = {value}");
            }
            else
            {
                Debug.Log($"[WebGLBuild] (skipped) {typeName}.{property}");
            }
        }

        private static void TrySetEnum(string typeName, string property, string enumValueName)
        {
            PropertyInfo p = FindProperty(typeName, property);
            if (p != null && p.PropertyType.IsEnum)
            {
                object value = Enum.Parse(p.PropertyType, enumValueName, ignoreCase: true);
                p.SetValue(null, value);
                Debug.Log($"[WebGLBuild] {typeName}.{property} = {enumValueName}");
            }
            else
            {
                Debug.Log($"[WebGLBuild] (skipped) {typeName}.{property}");
            }
        }

        private static PropertyInfo FindProperty(string typeName, string property)
        {
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = asm.GetType(typeName);
                if (t != null)
                {
                    return t.GetProperty(property, BindingFlags.Public | BindingFlags.Static);
                }
            }
            return null;
        }
    }
}
