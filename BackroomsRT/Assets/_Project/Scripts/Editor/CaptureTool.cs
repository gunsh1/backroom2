using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Backrooms.Capture;

namespace Backrooms.EditorTools
{
    /// <summary>
    /// エディタ（GUI モード）でシーンを再生し、決められた視点のスチルと fps を採取する。
    /// DXR はグラフィックスデバイスを要するため、ここだけ -batchmode を使わない。
    /// </summary>
    public static class CaptureTool
    {
        const string ScenePath = "Assets/_Project/Scenes/Level0.unity";

        [MenuItem("Backrooms/4. Capture Stills")]
        public static void PlayAndCapture()
        {
            try
            {
                Directory.CreateDirectory("../artifacts/shots");

                var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
                if (!scene.IsValid()) { Fail("シーンを開けない: " + ScenePath); return; }

                if (UnityEngine.Object.FindFirstObjectByType<FlatCaptureRunner>() == null)
                {
                    var go = new GameObject("~CaptureRunner");
                    go.AddComponent<FlatCaptureRunner>();
                }

                EditorSceneManager.MarkSceneDirty(scene);
                EditorApplication.EnterPlaymode();
            }
            catch (Exception e) { Fail(e.ToString()); }
        }

        static void Fail(string message)
        {
            Debug.LogError("[CaptureTool] " + message);
            try
            {
                Directory.CreateDirectory("../artifacts");
                File.WriteAllText("../artifacts/capture_report.txt", "FATAL: " + message);
            }
            catch { }
            EditorApplication.Exit(1);
        }
    }
}
