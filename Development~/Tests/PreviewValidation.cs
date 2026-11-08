using global::Glim;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.PostProcessing;

namespace FrozenAlex.GlimPreview
{
    public static class PreviewValidation
    {
        static Material emitter;
        static ColorGrading grading;
        static MeshRenderer renderer;
        static SceneView view;
        static RenderTexture target;
        static Vector4 originalST;
        static int stage, ticks, originalLitPixels;
        static Transform movingParent;
        static GameObject addedObject;
        static BakeContext snapshotContext;
        static GlimLightmapper baker;
        static Bindings.GlimConfig config;
        static ulong previousSceneRevision;
        static double deadline, nextTick, changedAt;
        static FieldInfo textureField = typeof(PreviewViewport).GetField("frame", BindingFlags.NonPublic | BindingFlags.Static);
        static string resultDir;

        public static void Run()
        {
            resultDir = Path.GetFullPath("Results");
            Directory.CreateDirectory(resultDir);
            try
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                RenderSettings.skybox = null;
                RenderSettings.ambientIntensity = 0;
                RenderSettings.defaultReflectionResolution = 16;
                CheckHdrDisplayShader();
                var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
                quad.name = "Emission test";
                quad.transform.localScale = Vector3.one * 6;
                GameObjectUtility.SetStaticEditorFlags(quad, StaticEditorFlags.ContributeGI);
                renderer = quad.GetComponent<MeshRenderer>();
                originalST = new Vector4(0.5f, 0.5f, 0.25f, 0.25f);
                renderer.lightmapScaleOffset = originalST;
                emitter = new Material(Shader.Find("Standard"));
                emitter.color = Color.gray;
                emitter.EnableKeyword("_EMISSION");
                emitter.SetColor("_EmissionColor", Color.black);
                emitter.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
                AssetDatabase.CreateAsset(emitter, "Assets/Emitter.mat");
                renderer.sharedMaterial = emitter;
                var group = ScriptableObject.CreateInstance<GlimLightmapGroup>();
                group.resolution = global::Glim.Resolution._64;
                group.packingType = UVPackingType.ScaleOffset;
                AssetDatabase.CreateAsset(group, "Assets/Group.asset");
                baker = new GameObject("Glim").AddComponent<GlimLightmapper>();
                baker.group = group;
                baker.previewWidth = 128;
                baker.previewHeight = 128;
                EditorSceneManager.SaveScene(UnityEngine.SceneManagement.SceneManager.GetActiveScene(), "Assets/PreviewTest.unity");
                var main = new GameObject("Main Camera"); main.tag = "MainCamera";
                main.AddComponent<Camera>().allowHDR = true;
                var layer = main.AddComponent<PostProcessLayer>();
                layer.Init(AssetDatabase.LoadAssetAtPath<PostProcessResources>("Packages/com.unity.postprocessing/PostProcessing/PostProcessResources.asset"));
                layer.volumeLayer = ~0;
                var volume = new GameObject("Color correction").AddComponent<PostProcessVolume>();
                volume.isGlobal = true;
                volume.sharedProfile = ScriptableObject.CreateInstance<PostProcessProfile>();
                grading = volume.sharedProfile.AddSettings<ColorGrading>();
                grading.enabled.Override(true);
                grading.saturation.Override(0);
                view = EditorWindow.GetWindow<SceneView>();
                view.LookAtDirect(Vector3.zero, Quaternion.identity, 3);
                view.orthographic = true;
                var camera = view.camera;
                target = new RenderTexture(128, 128, 24, RenderTextureFormat.ARGBFloat);
                target.Create();

                camera.transform.SetPositionAndRotation(new Vector3(0, 0, -3), Quaternion.identity);
                camera.orthographic = true;
                camera.orthographicSize = 1;
                config = new Bindings.GlimConfig(Bindings.CoordinateSystem.Unity, 8, 8, 8, 0,
                    true, true, 1, new Bindings.LightmapSettings(128, 128, false, false, false, false),
                    camera.transform.position, camera.transform.forward, 1, baker.lightFalloff, false,
                    baker.lightmapMode, 0, 1, 0, 1, 0, 1, 4, 0.05f, true, true);
                Bake.Start(baker, config, view);
                if (renderer.lightmapScaleOffset != originalST) throw new Exception("Preview changed original lightmap UVs");
                deadline = EditorApplication.timeSinceStartup + 150;
                EditorApplication.update += Tick;
            }
            catch (Exception e) { Finish(e); }
        }

        static void Tick()
        {
            try
            {
                double now = EditorApplication.timeSinceStartup;
                if (now > deadline) throw new Exception("Unity preview test timed out at stage " + stage + "; running=" + Bake.IsPreviewRunning + "; active=" + PreviewViewport.Active + "; hasView=" + typeof(PreviewViewport).GetField("hasView", BindingFlags.NonPublic|BindingFlags.Static).GetValue(null) + "; samples=" + PreviewViewport.Samples);
                if (now < nextTick) return;
                nextTick = now + 0.1;
                // Exercise the exact Scene camera command-buffer path, even in batch mode.
                view.Repaint();
                if (++ticks % 10 == 0)
                    Debug.Log("PREVIEW TEST: stage=" + stage + " scene=" + Bindings.app_preview_scene_revision() + " active=" + PreviewViewport.Active + " running=" + Bake.IsPreviewRunning + " samples=" + PreviewViewport.Samples + " revision=" + typeof(PreviewViewport).GetField("revision", BindingFlags.NonPublic|BindingFlags.Static).GetValue(null) + " message=" + Bake.LastError);
                if (stage == 4 && !Bake.IsPreviewRunning) { Finish(null); return; }
                if (stage == 13)
                {
                    if (Bake.IsPreviewRunning || ReadContext() != null) return;
                    Bake.Start(baker, config, view);
                    stage = 14; changedAt = now;
                    return;
                }
                var frame = textureField.GetValue(null) as Texture2D;
                if (!frame || PreviewViewport.Samples < 8) return;
                var values = frame.GetPixels();
                var center = values[(frame.height / 2) * frame.width + frame.width / 2];
                if (stage == 0)
                {
                    if (Mathf.Max(center.r, center.g, center.b) > 0.01f) throw new Exception("Initial emission was not black: " + center);
                    emitter.globalIlluminationFlags = MaterialGlobalIlluminationFlags.BakedEmissive;
                    emitter.SetColor("_EmissionColor", new Color(2, 0, 0));
                    EditorUtility.SetDirty(emitter);
                    stage = 1; changedAt = now;
                }
                else if (stage == 1 && now > changedAt + 0.5 && center.r > 1)
                {
                    File.WriteAllBytes(Path.Combine(resultDir, "emission-red.exr"), frame.EncodeToEXR(Texture2D.EXRFlags.OutputAsFloat));
                    CaptureComposite("viewport-red.png");
                    // Undo/Redo-style value change without an asset save or dirty flag.
                    emitter.SetColor("_EmissionColor", new Color(0, 3, 0));
                    stage = 2; changedAt = now;
                }
                else if (stage == 2 && now > changedAt + 0.5 && center.g > 2 && center.r < 0.01f)
                {
                    CaptureComposite("viewport-green.png");
                    originalLitPixels = values.Count(c => c.g > 1);
                    grading.saturation.Override(-100);
                    snapshotContext = ReadContext();
                    previousSceneRevision = Bindings.app_preview_scene_revision();
                    PreviewViewport.Denoise = true;
                    PreviewViewport.DisplayMode = PreviewDisplayMode.FalseColor;
                    PreviewViewport.Opacity = 0.25f; // Diagnostics must remain fully opaque.
                    PreviewViewport.Repaint();
                    stage = 90; changedAt = now;
                }
                else if (stage == 90 && now > changedAt + 0.5)
                {
                    AssertNoRebuild();
                    CaptureComposite("viewport-false-color.png");
                    PreviewViewport.DisplayMode = PreviewDisplayMode.Highlights;
                    PreviewViewport.Repaint();
                    stage = 91; changedAt = now;
                }
                else if (stage == 91 && now > changedAt + 0.5)
                {
                    AssertNoRebuild();
                    CaptureComposite("viewport-highlights.png");
                    PreviewViewport.DisplayMode = PreviewDisplayMode.Normal;
                    PreviewViewport.ExposureEV = -2;
                    PreviewViewport.Opacity = 1;
                    PreviewViewport.UseSceneEffects = false;
                    PreviewViewport.Repaint();
                    stage = 92; changedAt = now;
                }
                else if (stage == 92 && now > changedAt + 0.5 && PreviewViewport.DenoiseStatus == 3)
                {
                    AssertNoRebuild();
                    if (center.g < 2.9f || frame.format != TextureFormat.RGBAFloat)
                        throw new Exception("Display controls modified or clipped the original HDR frame");
                    var denoised = typeof(PreviewViewport).GetField("denoisedFrame", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null) as Texture2D;
                    if (!denoised) return;
                    if (denoised.format != TextureFormat.RGBAFloat ||
                        Mathf.Abs(denoised.GetPixel(denoised.width/2, denoised.height/2).g - center.g) > 0.01f)
                        throw new Exception("Surface denoiser altered primary emission or clipped HDR");
                    CaptureComposite("viewport-denoised-exposure.png");
                    PreviewViewport.ExposureEV = 0;
                    PreviewViewport.Denoise = false;
                    PreviewViewport.UseSceneEffects = true;
                    PreviewViewport.Repaint();
                    stage = 5; changedAt = now;
                }
                else if (stage == 5 && now > changedAt + 1)
                {
                    CaptureComposite("viewport-graded.png");
                    PreviewViewport.UseSceneEffects = false;
                    PreviewViewport.Repaint();
                    stage = 6; changedAt = now;
                }
                else if (stage == 6 && now > changedAt + 0.5)
                {
                    CaptureComposite("viewport-ungraded-green.png");
                    movingParent = new GameObject("Moved parent").transform;
                    renderer.transform.SetParent(movingParent, true);
                    movingParent.position = new Vector3(100, 0, 0);
                    stage = 7; changedAt = now;
                }
                else if (stage == 7 && now > changedAt + 0.5 && center.g < 0.01f)
                {
                    movingParent.position = Vector3.zero;
                    Undo.IncrementCurrentGroup();
                    Undo.RecordObject(renderer.transform, "Preview mirrored scale");
                    renderer.transform.localScale = new Vector3(-2, 6, 1);
                    Undo.FlushUndoRecordObjects();
                    stage = 8; changedAt = now;
                }
                else if (stage == 8 && now > changedAt + 0.5 && center.g > 2 && values.Count(c => c.g > 1) < originalLitPixels * 0.7f)
                {
                    CaptureComposite("viewport-mirrored-green.png");
                    Undo.PerformUndo();
                    stage = 9; changedAt = now;
                }
                else if (stage == 9 && now > changedAt + 0.5 && values.Count(c => c.g > 1) >= originalLitPixels * 0.9f)
                {
                    snapshotContext = ReadContext();
                    previousSceneRevision = Bindings.app_preview_scene_revision();
                    addedObject = GameObject.CreatePrimitive(PrimitiveType.Quad);
                    addedObject.name = "Object awaiting manual restart";
                    addedObject.transform.position = new Vector3(0, 0, -1);
                    addedObject.transform.localScale = Vector3.one * 2;
                    GameObjectUtility.SetStaticEditorFlags(addedObject, StaticEditorFlags.ContributeGI);
                    var blue = new Material(emitter);
                    blue.SetColor("_EmissionColor", new Color(0, 0, 4));
                    AssetDatabase.CreateAsset(blue, "Assets/Blue.mat");
                    addedObject.GetComponent<MeshRenderer>().sharedMaterial = blue;
                    stage = 10; changedAt = now;
                }
                else if (stage == 10 && now > changedAt + 1.5)
                {
                    AssertNoRebuild();
                    if (center.g < 2 || center.b > 0.01f || addedObject.GetComponentsInChildren<MeshRenderer>(true).Length != 1)
                        throw new Exception("Adding an object changed the preview without a restart");
                    emitter.SetColor("_EmissionColor", new Color(2, 0, 0));
                    stage = 11; changedAt = now;
                }
                else if (stage == 11 && now > changedAt + 0.5 && center.r > 1.9 && center.g < 0.01f)
                {
                    AssertNoRebuild();
                    if (renderer.lightmapScaleOffset != originalST) throw new Exception("Live updates changed baked UVs");
                    Undo.IncrementCurrentGroup();
                    Undo.DestroyObjectImmediate(renderer.gameObject);
                    stage = 12; changedAt = now;
                }
                else if (stage == 12 && now > changedAt + 1.5)
                {
                    AssertNoRebuild();
                    if (center.r < 1.9 || center.b > 0.01f)
                        throw new Exception("Deleting an exported object changed the snapshot or recaptured its incomplete atlas");
                    Bake.StopPreview();
                    stage = 13; changedAt = now;
                }
                else if (stage == 14 && now > changedAt + 0.5 && center.b > 3 && center.r < 0.01f)
                {
                    CaptureComposite("viewport-restarted-blue.png");
                    if (ReferenceEquals(snapshotContext, ReadContext())) throw new Exception("Restart did not export a new scene");
                    renderer = addedObject.GetComponent<MeshRenderer>();
                    snapshotContext = ReadContext();
                    previousSceneRevision = Bindings.app_preview_scene_revision();
                    renderer.enabled = false;
                    stage = 15; changedAt = now;
                }
                else if (stage == 15 && now > changedAt + 1.5)
                {
                    AssertNoRebuild();
                    if (center.b < 3) throw new Exception("Disabling a renderer changed the geometry snapshot");
                    renderer.enabled = true;
                    Bake.StopPreview();
                    stage = 4;
                }
                else if (stage == 4 && !Bake.IsPreviewRunning) Finish(null);
                if (stage == 4 && !Bake.IsPreviewRunning) Finish(null);
            }
            catch (Exception e) { Finish(e); }
        }

        static BakeContext ReadContext() =>
            (BakeContext)typeof(Bake).GetField("_context", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);

        static void AssertNoRebuild()
        {
            if (!ReferenceEquals(snapshotContext, ReadContext()) || Bindings.app_preview_scene_revision() != previousSceneRevision)
                throw new Exception("Object edits automatically rebuilt the preview");
        }

        static void CheckHdrDisplayShader()
        {
            var shader = AssetDatabase.LoadAssetAtPath<Shader>("Packages/com.frozenalex.glim-preview/Editor/PreviewComposite.shader");
            if (!shader || !shader.isSupported) throw new Exception("Preview display shader is unavailable");
            var material = new Material(shader);
            var input = new Texture2D(64, 16, TextureFormat.RGBAFloat, false, true)
                { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            var output = new RenderTexture(64, 16, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            var readback = new Texture2D(64, 16, TextureFormat.RGBAFloat, false, true);
            var old = RenderTexture.active;
            try
            {
                output.Create();
                Color[] Render(int pass)
                {
                    Graphics.Blit(input, output, material, pass);
                    RenderTexture.active = output;
                    readback.ReadPixels(new Rect(0, 0, 64, 16), 0, 0); readback.Apply();
                    return readback.GetPixels();
                }
                input.SetPixels(Enumerable.Repeat(new Color(8,8,8,1), 64 * 16).ToArray()); input.Apply();
                material.SetFloat("_Opacity", 1);
                material.SetFloat("_DisplayMode", 0);
                material.SetFloat("_ExposureEV", 0);
                if (Render(0)[100].r <= 1) throw new Exception("Normal display clipped HDR before exposure");
                material.SetFloat("_ExposureEV", -3);
                if (Mathf.Abs(Render(0)[100].r - 1) > 0.02f) throw new Exception("HDR exposure did not map 8 to 1 at -3 EV");

                material.SetFloat("_ExposureEV", 0); material.SetFloat("_DisplayMode", 1);
                material.SetFloat("_ReferenceGray", 0.18f);
                if (Render(0)[100].r < 0.9f) throw new Exception("High HDR value did not map to a bright exposure band");
                input.SetPixels(Enumerable.Repeat(new Color(0.18f,0.18f,0.18f,1), 64 * 16).ToArray()); input.Apply();
                var gray = Render(0)[100];
                float expected = QualitySettings.activeColorSpace == ColorSpace.Linear ? Mathf.GammaToLinearSpace(0.5f) : 0.5f;
                if (Mathf.Abs(gray.r - expected) > 0.02f || Mathf.Abs(gray.r-gray.g) > 0.01f)
                    throw new Exception("Reference gray exposure band is wrong: " + gray);
                material.SetFloat("_DisplayMode", 2); material.SetFloat("_HighlightLimit", 0.1f);
                var warning = Render(0)[100];
                if (warning.r < warning.g + 0.1f) throw new Exception("HDR highlight threshold did not mark the pixel");
            }
            finally
            {
                RenderTexture.active = old; output.Release();
                UnityEngine.Object.DestroyImmediate(output); UnityEngine.Object.DestroyImmediate(readback);
                UnityEngine.Object.DestroyImmediate(input); UnityEngine.Object.DestroyImmediate(material);
            }
        }

        static void CaptureComposite(string filename)
        {
            view.camera.Render();
            int previewBuffers = view.camera.GetCommandBuffers(CameraEvent.BeforeImageEffects)
                .Concat(view.camera.GetCommandBuffers(CameraEvent.AfterImageEffects))
                .Count(b => b.name == "Glim Scene View Preview");
            if (previewBuffers != 1) throw new Exception("Expected exactly one Glim composite buffer; found " + previewBuffers);
            Debug.Log("COMPOSITE: buffers=" + string.Join(",", view.camera.GetCommandBuffers(CameraEvent.BeforeImageEffects).Select(b => b.name + ":" + b.sizeInBytes)) + " state=" + PreviewViewport.Samples + " saturation=" + view.camera.GetComponent<PostProcessLayer>().GetSettings<ColorGrading>().saturation.value + " source=" + grading.saturation.value);
            target = typeof(SceneView).GetField("m_SceneTargetTexture", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(view) as RenderTexture;
            if (!target) throw new Exception("Scene view target is missing");
            var old = RenderTexture.active;
            RenderTexture.active = target;
            var image = new Texture2D(target.width, target.height, TextureFormat.RGBAFloat, false, true);
            image.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
            image.Apply();
            var pixel = image.GetPixel(target.width / 2, target.height / 2);
            Debug.Log("COMPOSITE pixel " + pixel + " target " + target.width + "x" + target.height);
            if (filename == "viewport-red.png" && (pixel.r < 0.5f || pixel.g > 0.1f)) throw new Exception("Red viewport composite failed: " + pixel);
            if (filename.Contains("blue") && (pixel.b < 0.5f || pixel.g > 0.1f)) throw new Exception("Blue viewport composite failed: " + pixel);
            if (filename.Contains("green") && (pixel.g < 0.5f || pixel.r > 0.1f)) throw new Exception("Green viewport composite failed: " + pixel);
            if ((filename == "viewport-false-color.png" || filename == "viewport-highlights.png") &&
                (pixel.r < pixel.g + 0.1f || pixel.r < pixel.b + 0.1f))
                throw new Exception("Diagnostic colors were altered by grading, denoising or opacity: " + pixel);
            if (filename == "viewport-denoised-exposure.png")
            {
                // Standard's META emission differs between Gamma and Linear
                // projects; compare to the actual raw HDR sample in this run.
                var raw = (Texture2D)textureField.GetValue(null);
                float expected = raw.GetPixel(raw.width/2, raw.height/2).g * Mathf.Pow(2, PreviewViewport.ExposureEV);
                if (QualitySettings.activeColorSpace == ColorSpace.Gamma) expected = Mathf.LinearToGammaSpace(expected);
                if (Mathf.Abs(pixel.g - expected) > Mathf.Max(0.03f, expected * 0.01f) || pixel.r > 0.02f)
                    throw new Exception($"Denoised HDR exposure was not composed correctly: {pixel}; expected green {expected}");
            }
            var png = new Texture2D(target.width, target.height, TextureFormat.RGBA32, false, true);
            png.SetPixels(image.GetPixels()); png.Apply();
            if (filename == "viewport-graded.png" && (Mathf.Abs(pixel.r - pixel.g) > 0.04f || Mathf.Abs(pixel.g - pixel.b) > 0.04f || pixel.r < 0.1f))
                throw new Exception("Scene color grading did not affect preview: " + pixel);
            File.WriteAllBytes(Path.Combine(resultDir, filename), png.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(png);
            UnityEngine.Object.DestroyImmediate(image);
            RenderTexture.active = old;
        }

        static void Finish(Exception error)
        {
            EditorApplication.update -= Tick;
            Bake.StopPreview();
            if (error == null && renderer && renderer.GetComponentsInChildren<MeshRenderer>(true).Length != 1)
                error = new Exception("Preview renderer copies were not cleaned up");
            string result = error == null ? "PASS: HDR exposure and diagnostic bands, OIDN surface denoising with exact primary emission and HDR preservation, diagnostics bypass grading/opacity/denoising, live emission and color grading, movement/Undo, no automatic mesh rebuild, manual restart, baked UV preservation and copy cleanup." : error.ToString();
            File.WriteAllText(Path.Combine(resultDir, "result.txt"), result);
            Debug.Log(result);
            EditorApplication.Exit(error == null ? 0 : 1);
        }
    }
}


