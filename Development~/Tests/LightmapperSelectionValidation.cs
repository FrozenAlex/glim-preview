using System;
using System.IO;
using System.Reflection;
using Glim;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FrozenAlex.GlimPreview
{
    public static class LightmapperSelectionValidation
    {
        const string SourcePath = "Assets/LightmapperSelection.unity";
        const string TempPath = "Assets/TemporaryBakeScene.unity";
        static readonly FieldInfo SelectionField = typeof(LivePreviewWindow).GetField("lightmapper", BindingFlags.NonPublic | BindingFlags.Instance);
        static LivePreviewWindow window;
        static GlimLightmapper oldComponent;
        static string expectedId;
        static int stage, cycles;
        static double readyAt, deadline;
        static GlimLightmapper Selected => (GlimLightmapper)SelectionField.GetValue(window);

        public static void Run()
        {
            try
            {
                var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                new GameObject("First mapper, not selected").AddComponent<GlimLightmapper>();
                var chosen = new GameObject("Chosen mapper").AddComponent<GlimLightmapper>();
                chosen.gameObject.SetActive(false); // A deliberate choice need not be the first active component.
                window = EditorWindow.GetWindow<LivePreviewWindow>();
                window.SetLightmapper(chosen); // Select before saving, as on a first bake.
                EditorSceneManager.SaveScene(scene, SourcePath);
                expectedId = GlobalObjectId.GetGlobalObjectIdSlow(chosen).ToString();
                deadline = EditorApplication.timeSinceStartup + 30;
                EditorApplication.update += Tick;
                BeginBakeReload();
            }
            catch (Exception e) { Finish(e); }
        }

        static void BeginBakeReload()
        {
            var source = SceneManager.GetSceneByPath(SourcePath);
            oldComponent = Selected;
            var temporary = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            var wrong = new GameObject("Temporary wrong mapper").AddComponent<GlimLightmapper>();
            SceneManager.MoveGameObjectToScene(wrong.gameObject, temporary);
            EditorSceneManager.SaveScene(temporary, TempPath);
            EditorSceneManager.CloseScene(source, true);
            EditorSceneManager.SetActiveScene(temporary);
            stage = 0;
            readyAt = EditorApplication.timeSinceStartup + 0.8;
        }

        static void Tick()
        {
            try
            {
                double now = EditorApplication.timeSinceStartup;
                if (now > deadline) throw new Exception("Lightmapper restoration timed out at stage " + stage);
                if (now < readyAt) return;
                if (stage == 0)
                {
                    Check(!oldComponent, "Closing the source scene did not destroy its component instance");
                    Check(!Selected, "Selection switched to a Lightmapper in the temporary bake scene");
                    EditorSceneManager.OpenScene(SourcePath, OpenSceneMode.Single);
                    stage = 1;
                    readyAt = now + 0.8;
                }
                else if (stage == 1)
                {
                    if (!Selected) return;
                    Check(Selected.name == "Chosen mapper", "Restored the first mapper instead of the chosen mapper");
                    Check(!Selected.gameObject.activeSelf, "Lost the explicitly chosen inactive Lightmapper");
                    Check(GlobalObjectId.GetGlobalObjectIdSlow(Selected).ToString() == expectedId, "Restored a different persistent component");
                    if (++cycles < 3) { BeginBakeReload(); return; }
                    window.SetLightmapper(null);
                    EditorSceneManager.OpenScene(SourcePath, OpenSceneMode.Single);
                    stage = 2;
                    readyAt = now + 0.8;
                }
                else
                {
                    Check(!Selected, "An explicitly cleared field was automatically repopulated");
                    Finish(null);
                }
            }
            catch (Exception e) { Finish(e); }
        }

        static void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        static void Finish(Exception error)
        {
            EditorApplication.update -= Tick;
            if (window) window.Close();
            Directory.CreateDirectory("Results");
            string result = error == null
                ? "PASS: chosen Lightmapper survived three bake-style scene replacements; ignored the temporary scene's mapper; preserved inactive selection and explicit clearing."
                : error.ToString();
            File.WriteAllText("Results/result.txt", result);
            Debug.Log(result);
            EditorApplication.Exit(error == null ? 0 : 1);
        }
    }
}
