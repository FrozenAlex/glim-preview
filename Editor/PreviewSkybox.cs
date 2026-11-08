using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace FrozenAlex.GlimPreview
{
    internal sealed class PreviewSkybox : IDisposable
    {
        readonly Scene scene;
        readonly List<int> textureProperties = new();
        readonly List<GameObject> roots = new();
        readonly List<Light> lights = new();
        int submittedHash, pendingHash, resolution;
        float submittedIntensity;
        double nextScan;
        RenderTexture capture;
        AsyncGPUReadbackRequest request;
        Color[] pendingPixels;
        bool finished, failed, disposed;
        internal int CaptureCount { get; private set; }

        public PreviewSkybox(Scene scene, float intensity)
        {
            this.scene = scene;
            submittedIntensity = intensity;
            submittedHash = SkyboxHash();
        }

        static int SunHash(Light sun)
        {
            if (!sun) return 0;
            unchecked
            {
                int hash = sun.GetInstanceID();
                hash = hash * 31 + sun.transform.forward.GetHashCode();
                hash = hash * 31 + sun.color.GetHashCode();
                hash = hash * 31 + sun.intensity.GetHashCode();
                hash = hash * 31 + sun.colorTemperature.GetHashCode();
                hash = hash * 31 + sun.useColorTemperature.GetHashCode();
                return hash * 31 + sun.isActiveAndEnabled.GetHashCode();
            }
        }

        int SkyboxHash()
        {
            unchecked
            {
                int hash = RenderSettings.defaultReflectionResolution;
                var material = RenderSettings.skybox;
                if (!material) return hash;
                hash = hash * 31 + material.GetInstanceID();
                hash = hash * 31 + material.ComputeCRC();
                hash = hash * 31 + EditorUtility.GetDirtyCount(material);
                if (material.shader) hash = hash * 31 + EditorUtility.GetDirtyCount(material.shader);
                textureProperties.Clear();
                material.GetTexturePropertyNameIDs(textureProperties);
                foreach (int property in textureProperties)
                {
                    var texture = material.GetTexture(property);
                    if (!texture) continue;
                    hash = hash * 31 + texture.GetInstanceID();
                    hash = hash * 31 + (int)texture.updateCount;
                    hash = hash * 31 + EditorUtility.GetDirtyCount(texture);
                    hash = hash * 31 + texture.imageContentsHash.GetHashCode();
                }
                // Unity's procedural sky also depends on its sun, not just material
                // properties. With automatic sun selection, observe directional lights.
                if (material.shader && material.shader.name == "Skybox/Procedural")
                {
                    hash = hash * 31 + SunHash(RenderSettings.sun);
                    if (!RenderSettings.sun)
                    {
                        scene.GetRootGameObjects(roots);
                        foreach (var root in roots)
                        {
                            root.GetComponentsInChildren(false, lights);
                            foreach (var light in lights)
                                if (light.type == LightType.Directional) hash = hash * 31 + SunHash(light);
                        }
                    }
                }
                return hash;
            }
        }

        public void Update()
        {
            if (disposed || !scene.IsValid() || !scene.isLoaded) return;
            if (capture)
            {
                if (!finished) return;
                if (failed)
                {
                    Debug.LogWarning("Glim: skybox readback failed; it will be retried.");
                    FinishCapture();
                    nextScan = EditorApplication.timeSinceStartup + 2;
                    return;
                }
                if (!Bindings.app_update_preview_skybox(pendingPixels, (uint)pendingPixels.Length * 4, (uint)resolution)) return;
                submittedHash = pendingHash;
                FinishCapture();
            }
            // RenderSettings belongs to the active scene. Never replace this
            // preview's environment with another scene's settings after a switch.
            if (SceneManager.GetActiveScene() != scene || EditorApplication.timeSinceStartup < nextScan) return;
            nextScan = EditorApplication.timeSinceStartup + 0.35;
            float intensity = RenderSettings.ambientIntensity;
            if (!intensity.Equals(submittedIntensity) && Bindings.app_update_preview_skybox_intensity(intensity))
                submittedIntensity = intensity;
            int hash = SkyboxHash();
            if (hash == submittedHash) return;
            BeginCapture(hash);
        }

        void BeginCapture(int hash)
        {
            resolution = Mathf.Clamp(RenderSettings.defaultReflectionResolution, 1, 2048);
            capture = new RenderTexture(new RenderTextureDescriptor(resolution, resolution)
            {
                dimension = TextureDimension.Cube,
                graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat,
                depthBufferBits = 24,
                msaaSamples = 1
            }) { name = "Glim preview skybox capture", hideFlags = HideFlags.HideAndDontSave };
            capture.Create();
            var previousTarget = RenderTexture.active;
            var cameraObject = new GameObject("Glim preview skybox capture") { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                SceneManager.MoveGameObjectToScene(cameraObject, scene);
                var camera = cameraObject.AddComponent<Camera>();
                camera.enabled = false;
                camera.clearFlags = CameraClearFlags.Skybox;
                camera.cullingMask = 0;
                camera.backgroundColor = Color.black;
                camera.allowHDR = true;
                if (!camera.RenderToCubemap(capture)) throw new InvalidOperationException("Skybox cubemap capture failed.");
                pendingHash = hash;
                finished = failed = false;
                request = AsyncGPUReadback.Request(capture, 0, GraphicsFormat.R32G32B32A32_SFloat, CompleteCapture);
                CaptureCount++;
            }
            catch { finished = true; FinishCapture(); throw; }
            finally
            {
                RenderTexture.active = previousTarget;
                UnityEngine.Object.DestroyImmediate(cameraObject);
            }
        }

        void CompleteCapture(AsyncGPUReadbackRequest completed)
        {
            if (disposed) return;
            try
            {
                failed = completed.hasError || completed.layerCount != 6;
                if (failed) return;
                int facePixels = resolution * resolution;
                pendingPixels = new Color[facePixels * 6];
                for (int face = 0; face < 6; face++)
                {
                    var data = completed.GetData<Color>(face);
                    if (data.Length != facePixels) { failed = true; return; }
                    for (int i = 0; i < facePixels; i++)
                    {
                        var pixel = data[i];
                        for (int channel = 0; channel < 4; channel++)
                            if (float.IsNaN(pixel[channel]) || float.IsInfinity(pixel[channel]))
                            {
                                // Retry the latest settings instead of endlessly
                                // submitting an invalid HDR snapshot to native code.
                                failed = true;
                                return;
                            }
                        pendingPixels[face * facePixels + i] = pixel;
                    }
                }
            }
            catch (Exception e)
            {
                failed = true;
                Debug.LogWarning("Glim: could not read skybox pixels: " + e.Message);
            }
            finally { finished = true; }
        }

        void FinishCapture()
        {
            if (capture) UnityEngine.Object.DestroyImmediate(capture);
            capture = null;
            pendingPixels = null;
        }

        public void Dispose()
        {
            disposed = true;
            if (capture && !finished) request.WaitForCompletion();
            FinishCapture();
        }
    }
}
