using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Backrooms.Game;

namespace Backrooms.EditorTools
{
    /// <summary>
    /// シーンを再生して、1 回のプレイのループが閉じているかを確かめる。
    /// DXR がグラフィックスデバイスを要るので、キャプチャと同じく
    /// -batchmode を使わない。
    /// </summary>
    public static class GameTestTool
    {
        const string ScenePath = "Assets/_Project/Scenes/Level0.unity";

        [MenuItem("Backrooms/5. Test Game Loop")]
        public static void PlayAndTest()
        {
            try
            {
                Directory.CreateDirectory("../artifacts");

                var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
                if (!scene.IsValid()) { Fail("シーンを開けない: " + ScenePath); return; }

                if (UnityEngine.Object.FindFirstObjectByType<GameSelfTest>() == null)
                {
                    var go = new GameObject("~GameSelfTest");
                    go.AddComponent<GameSelfTest>();
                }

                EditorSceneManager.MarkSceneDirty(scene);
                EditorApplication.EnterPlaymode();
            }
            catch (Exception e) { Fail(e.ToString()); }
        }

        static void Fail(string message)
        {
            Debug.LogError("[GameTest] " + message);
            try { File.WriteAllText("../artifacts/game_report.txt", "FATAL: " + message); }
            catch { }
            EditorApplication.Exit(1);
        }
    }
}
