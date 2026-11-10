using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>
/// Batchmode player builds for Linux, Windows, and macOS.
/// Invoked with -executeMethod HaivenCrossPlatformPlayerBuild.BuildAll.
/// </summary>
public static class HaivenCrossPlatformPlayerBuild
{
    public static void BuildAll()
    {
        // -projectPath does not set the process working directory. Builds/ is
        // gitignored only at the Unity project root, the parent of Assets.
        var projectRoot = Path.GetDirectoryName(Application.dataPath);
        if (string.IsNullOrEmpty(projectRoot))
        {
            Debug.LogError("Haiven player build could not resolve the Unity project from Application.dataPath.");
            EditorApplication.Exit(1);
            return;
        }

        var root = Path.Combine(projectRoot, "Builds", "players");
        var targets = new[]
        {
            (BuildTarget.StandaloneLinux64, Path.Combine(root, "linux-x64", "HaivenBossRoom.x86_64")),
            (BuildTarget.StandaloneWindows64, Path.Combine(root, "windows-x64", "HaivenBossRoom.exe")),
            (BuildTarget.StandaloneOSX, Path.Combine(root, "macos", "HaivenBossRoom.app")),
        };

        Directory.CreateDirectory(root);
        var resultPath = Path.Combine(root, "build-result.txt");
        var failed = 0;
        using (var result = new StreamWriter(resultPath, append: false))
        {
            foreach (var (target, path) in targets)
            {
                try
                {
                    var bytes = BuildOne(target, path);
                    result.WriteLine($"ok {target} bytes={bytes} path={path}");
                    result.Flush();
                }
                catch (Exception exception)
                {
                    failed++;
                    result.WriteLine($"fail {target} path={path}");
                    result.WriteLine(exception);
                    result.Flush();
                    Debug.LogError($"Haiven player build failed for {target}: {exception}");
                }
            }

            result.WriteLine(failed == 0 ? "all-ok" : $"failed={failed}");
        }

        EditorApplication.Exit(failed == 0 ? 0 : 1);
    }

    static ulong BuildOne(BuildTarget target, string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var scenes = EditorBuildSettings.scenes.Where(scene => scene.enabled).Select(scene => scene.path).ToArray();
        if (scenes.Length == 0)
            throw new InvalidOperationException("No enabled scenes in EditorBuildSettings.");

        Debug.Log($"Haiven player build starting {target} -> {path} scenes={scenes.Length}");
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = path,
            target = target,
            options = BuildOptions.StrictMode,
        });
        var summary = report.summary;
        if (summary.result != BuildResult.Succeeded)
            throw new InvalidOperationException($"{target} {summary.result} errors={summary.totalErrors}");

        Debug.Log($"Haiven player build succeeded {target} bytes={summary.totalSize} path={summary.outputPath}");
        return summary.totalSize;
    }
}
