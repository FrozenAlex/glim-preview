using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Glim;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FrozenAlex.GlimPreview
{
    public static class SkyboxValidation
    {
        static readonly BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        static readonly FieldInfo FrameField = typeof(PreviewViewport).GetField("frame", BindingFlags.Static | BindingFlags.NonPublic);
        static PreviewSkybox probe;
        static Color[] expected;
        static Material material;
        static Cubemap cube;
        static Light sun;
        static Scene scene;
        static SceneView view;
        static BakeContext context;
        static ulong sceneRevision;
        static int stage, captures, ticks;
        static float baseline, exposed;
        static double changedAt, deadline, nextTick;

        public static void Run()
        {
            try
            {
                scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                RenderSettings.defaultReflectionResolution = 16;
                RenderSettings.ambientIntensity = 1;
                cube = new Cubemap(16, TextureFormat.RGBAHalf, false);
                for (int face = 0; face < 6; face++)
                {
                    var pixels = new Color[256];
                    for (int y = 0; y < 16; y++)
                        for (int x = 0; x < 16; x++) pixels[y * 16 + x] = new Color(x / 16f, y / 16f, face / 6f, 1);
                    cube.SetPixels(pixels, (CubemapFace)face);
                }
                cube.Apply();
                Check(cube.GetPixel(CubemapFace.PositiveX, 15, 15).r > 0.5f, "Gradient test cubemap was not populated");
                material = new Material(Shader.Find("Skybox/Cubemap"));
                material.SetTexture("_Tex", cube);
                RenderSettings.skybox = material;
                probe = new PreviewSkybox(scene, 1);
                material.SetFloat("_Rotation", 45);
                expected = SkyboxCapture.Capture(scene);
                probe.Update();
                deadline = EditorApplication.timeSinceStartup + 150;
                EditorApplication.update += Tick;
            }
            catch (Exception e) { Finish(e); }
        }

        static void Fill(Cubemap texture, Color color)
        {
            var pixels = Enumerable.Repeat(color, texture.width * texture.width).ToArray();
            for (int face = 0; face < 6; face++) texture.SetPixels(pixels, (CubemapFace)face);
            texture.Apply();
        }

        static BakeContext ReadContext() => (BakeContext)typeof(Bake).GetField("_context", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        static PreviewSkybox Tracker => (PreviewSkybox)typeof(BakeContext).GetField("previewSkybox", PrivateInstance).GetValue(context);

        static void StartPreview()
        {
            probe.Dispose(); probe = null;
            Fill(cube, Color.red);
            material.SetFloat("_Rotation", 0);
            material.SetFloat("_Exposure", 1);
            AssetDatabase.CreateAsset(cube, "Assets/Sky.asset");
            AssetDatabase.CreateAsset(material, "Assets/Sky.mat");
            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.transform.position = new Vector3(100, 0, 0); // A valid scene export, outside the camera.
            GameObjectUtility.SetStaticEditorFlags(quad, StaticEditorFlags.ContributeGI);
            var surface = new Material(Shader.Find("Standard"));
            AssetDatabase.CreateAsset(surface, "Assets/Surface.mat");
            quad.GetComponent<MeshRenderer>().sharedMaterial = surface;
            var group = ScriptableObject.CreateInstance<GlimLightmapGroup>();
            group.resolution = global::Glim.Resolution._64;
            group.packingType = UVPackingType.ScaleOffset;
            AssetDatabase.CreateAsset(group, "Assets/Group.asset");
            var baker = new GameObject("Glim").AddComponent<GlimLightmapper>();
            baker.group = group;
            baker.previewWidth = baker.previewHeight = 64;
            baker.previewSamples = 8;
            baker.previewBounces = 0;
            baker.previewThrottle = 1;
            EditorSceneManager.SaveScene(scene, "Assets/SkyboxTest.unity");
            view = EditorWindow.GetWindow<SceneView>();
            view.LookAtDirect(Vector3.zero, Quaternion.identity, 3);
            view.orthographic = true;
            Bake.Start(baker, LivePreviewWindow.CreateConfig(baker, view.camera), view);
            context = ReadContext();
            stage = 1;
        }

        static void Tick()
        {
            try
            {
                double now = EditorApplication.timeSinceStartup;
                if (now > deadline) throw new Exception("Skybox validation timed out at stage " + stage + ": " + Bake.LastError);
                if (now < nextTick) return;
                nextTick = now + 0.1;
                if (++ticks % 10 == 0) Debug.Log("SKYBOX TEST: stage=" + stage + "; error=" + Bake.LastError);
                if (stage == 0)
                {
                    probe.Update(); // Native renderer is inactive, so the completed pixels stay pending.
                    var pixels = (Color[])typeof(PreviewSkybox).GetField("pendingPixels", PrivateInstance).GetValue(probe);
                    if (pixels == null) return;
                    Check(pixels.Length == expected.Length, "Async skybox capture has the wrong face count");
                    float error = 0;
                    for (int i = 0; i < pixels.Length; i++)
                        for (int channel = 0; channel < 4; channel++) error = Mathf.Max(error, Mathf.Abs(pixels[i][channel] - expected[i][channel]));
                    Check(error < 0.005f, "Async cubemap face order/orientation differs from initial export: " + error);
                    Debug.Log("SKYBOX TEST: all six async faces match the initial export; max error=" + error);
                    StartPreview();
                    return;
                }
                if (stage == 11)
                {
                    if (Bake.IsPreviewRunning || ReadContext() != null) return;
                    Check(!Resources.FindObjectsOfTypeAll<RenderTexture>().Any(rt => rt.name == "Glim preview skybox capture"), "Skybox capture texture leaked after shutdown");
                    Finish(null);
                    return;
                }
                view.Repaint();
                var frame = FrameField.GetValue(null) as Texture2D;
                if (!frame || PreviewViewport.Samples < 8) return;
                Color color = frame.GetPixel(frame.width / 2, frame.height / 2);
                if (ticks % 10 == 0) Debug.Log("SKYBOX TEST: pixel=" + color + "; captures=" + Tracker.CaptureCount);
                if (stage > 1)
                {
                    Check(ReferenceEquals(context, ReadContext()), "Skybox edit re-exported the scene");
                    Check(Bindings.app_preview_scene_revision() == sceneRevision, "Skybox edit rebuilt geometry");
                }
                if (stage == 1 && color.r > 0.1f && color.g < 0.01f && color.b < 0.01f)
                {
                    baseline = color.r;
                    sceneRevision = Bindings.app_preview_scene_revision();
                    captures = Tracker.CaptureCount;
                    stage = 2; changedAt = now;
                }
                else if (stage == 2 && now > changedAt + 1.1)
                {
                    Check(Tracker.CaptureCount == captures, "Idle preview keeps capturing its skybox");
                    Undo.IncrementCurrentGroup();
                    Undo.RecordObject(material, "Skybox exposure");
                    material.SetFloat("_Exposure", 0.25f);
                    Undo.FlushUndoRecordObjects();
                    exposed = SkyboxCapture.Capture(scene)[0].r;
                    stage = 3; changedAt = now;
                }
                else if (stage == 3 && now > changedAt + 0.5 && Mathf.Abs(color.r - exposed) < 0.005f)
                {
                    Check(Tracker.CaptureCount > captures, "Exposure edit did not recapture the skybox");
                    Undo.PerformUndo();
                    stage = 4; changedAt = now;
                }
                else if (stage == 4 && now > changedAt + 0.5 && Mathf.Abs(color.r - baseline) < 0.01f)
                {
                    captures = Tracker.CaptureCount;
                    RenderSettings.ambientIntensity = 0.5f;
                    stage = 5; changedAt = now;
                }
                else if (stage == 5 && now > changedAt + 0.5 && Mathf.Abs(color.r - baseline * 0.5f) < 0.01f)
                {
                    Check(Tracker.CaptureCount == captures, "Intensity-only edit recaptured the cubemap");
                    Fill(cube, Color.blue);
                    stage = 6; changedAt = now;
                }
                else if (stage == 6 && now > changedAt + 0.5 && color.b > 0.1f && color.r < 0.01f)
                {
                    var green = new Cubemap(16, TextureFormat.RGBAHalf, false);
                    Fill(green, Color.green);
                    material = new Material(material);
                    material.SetTexture("_Tex", green);
                    RenderSettings.skybox = material;
                    RenderSettings.defaultReflectionResolution = 32;
                    stage = 7; changedAt = now;
                }
                else if (stage == 7 && now > changedAt + 0.5 && color.g > 0.1f && color.b < 0.01f)
                {
                    RenderSettings.skybox = null;
                    stage = 8; changedAt = now;
                }
                else if (stage == 8 && now > changedAt + 0.5 && Mathf.Max(color.r, color.g, color.b) < 0.01f)
                {
                    material = new Material(Shader.Find("Skybox/Procedural"));
                    RenderSettings.skybox = material;
                    sun = new GameObject("Sun").AddComponent<Light>();
                    sun.type = LightType.Directional;
                    sun.transform.rotation = Quaternion.Euler(40, 0, 0);
                    RenderSettings.sun = sun;
                    captures = Tracker.CaptureCount;
                    stage = 9; changedAt = now;
                }
                else if (stage == 9 && now > changedAt + 0.8 && Tracker.CaptureCount > captures && Mathf.Max(color.r, color.g, color.b) > 0.01f)
                {
                    captures = Tracker.CaptureCount;
                    sun.transform.rotation = Quaternion.Euler(70, 25, 0);
                    stage = 10; changedAt = now;
                }
                else if (stage == 10 && now > changedAt + 0.8 && Tracker.CaptureCount > captures)
                {
                    // Stop a second tracker while its readback is still pending.
                    probe = new PreviewSkybox(scene, RenderSettings.ambientIntensity);
                    material.SetFloat("_Exposure", 0.8f);
                    probe.Update();
                    probe.Dispose(); probe = null;
                    Bake.StopPreview();
                    stage = 11;
                }
            }
            catch (Exception e) { Finish(e); }
        }

        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

        static void Finish(Exception error)
        {
            EditorApplication.update -= Tick;
            probe?.Dispose();
            Bake.StopPreview();
            Directory.CreateDirectory("Results");
            string result = error == null
                ? "PASS: async cubemap orientation, skybox exposure/Undo, texture edits, material swap, resolution change, removal, procedural sun edits, idle/intensity capture exclusion, geometry preservation, and readback cleanup."
                : error.ToString();
            File.WriteAllText("Results/result.txt", result);
            Debug.Log(result);
            EditorApplication.Exit(error == null ? 0 : 1);
        }
    }
}
