using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Haiven.Demo.BossRoom.Editor
{
    public static class HaivenBossRoomEditorStartup
    {
        private const string StartupScene = "Assets/Scenes/Startup.unity";

        public static void Open()
        {
            if (!File.Exists(StartupScene))
            {
                throw new FileNotFoundException("Boss Room startup scene was not found", StartupScene);
            }

            EditorSceneManager.OpenScene(StartupScene, OpenSceneMode.Single);
            Debug.Log($"[Haiven Boss Room] Opened {StartupScene}");
        }
    }
}
