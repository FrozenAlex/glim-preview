using global::Glim;
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace FrozenAlex.GlimPreview
{
    // Captures the shader's META emission output, rather than guessing shader-specific
    // property names. All Unity and GPU readback operations stay on the editor thread.
    internal sealed class PreviewMaterials : IDisposable
    {
        readonly List<BakeContextGroup> groups;
        readonly Dictionary<Material, int> materialHashes = new();
        readonly List<Material> materials = new();
        readonly List<int> textureProperties = new();
        readonly int[] submittedHashes;
        readonly MaterialPropertyBlock properties = new();
        MetaTexture capture;
        AsyncGPUReadbackRequest request;
        int pendingGroup = -1, pendingHash, nextGroup;
        byte[] pendingPixels;
        bool readbackFinished, readbackFailed, disposed;
        double nextScan;

        public PreviewMaterials(List<BakeContextGroup> groups)
        {
            this.groups = groups;
            submittedHashes = new int[groups.Count];
            for (int i = 0; i < groups.Count; i++) submittedHashes[i] = GroupHash(groups[i]);
        }

        internal static List<MeshRenderer> CloneRenderers(List<MeshRenderer> sources, List<MeshRenderer> owned, List<int> copyIds)
        {
            var result = new List<MeshRenderer>(sources.Count);
            foreach (var source in sources)
            {
                var go = new GameObject("Glim preview: " + source.name) { hideFlags = HideFlags.HideAndDontSave };
                copyIds.Add(PreviewRendererCopy.Register(go));
                // Parenting with an identity transform preserves shear and nonuniform scale.
                go.transform.SetParent(source.transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = source.GetComponent<MeshFilter>().sharedMesh;
                var clone = go.AddComponent<MeshRenderer>();
                owned.Add(clone);
                EditorUtility.CopySerialized(source, clone);
                var block = new MaterialPropertyBlock();
                source.GetPropertyBlock(block);
                clone.SetPropertyBlock(block);
                clone.enabled = false; // Exported explicitly, never drawn by the Scene camera.
                result.Add(clone);
            }
            return result;
        }

        int MaterialHash(Material material)
        {
            if (!material) return 0;
            if (materialHashes.TryGetValue(material, out int cached)) return cached;
            unchecked
            {
                int hash = material.ComputeCRC();
                hash = hash * 31 + (int)material.globalIlluminationFlags;
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
                materialHashes[material] = hash;
                return hash;
            }
        }

        int GroupHash(BakeContextGroup group)
        {
            if (group.sourceRenderers == null) return 0; // Terrain has no emission META atlas.
            unchecked
            {
                int hash = 17;
                foreach (var renderer in group.sourceRenderers)
                {
                    if (!renderer) { hash *= 31; continue; }
                    renderer.GetSharedMaterials(materials);
                    hash = hash * 31 + materials.Count;
                    foreach (var material in materials)
                    {
                        hash = hash * 31 + (material ? material.GetInstanceID() : 0);
                        hash = hash * 31 + MaterialHash(material);
                    }
                }
                return hash;
            }
        }

        void SyncMaterials(BakeContextGroup group)
        {
            for (int i = 0; i < group.sourceRenderers.Length; i++)
            {
                var source = group.sourceRenderers[i];
                var clone = group.previewRenderers[i];
                if (!source || !clone) continue;
                source.GetSharedMaterials(materials);
                clone.sharedMaterials = materials.ToArray();
                source.GetPropertyBlock(properties);
                clone.SetPropertyBlock(properties);
            }
        }

        static bool CanCapture(BakeContextGroup group)
        {
            if (group.sourceRenderers == null) return false;
            // The preview keeps its original geometry until it is reopened.
            // A missing copy or replaced mesh cannot reproduce that atlas; keep
            // its last pixels instead of clearing part of the snapshot.
            for (int i = 0; i < group.sourceRenderers.Length; i++)
            {
                var source = group.sourceRenderers[i];
                var clone = group.previewRenderers[i];
                if (!source || !clone) return false;
                var filter = source.GetComponent<MeshFilter>();
                if (!filter || filter.sharedMesh != clone.GetComponent<MeshFilter>().sharedMesh) return false;
            }
            return true;
        }

        public void Update()
        {
            if (pendingGroup >= 0)
            {
                if (!CanCapture(groups[pendingGroup]))
                {
                    // Let an already-issued readback finish without blocking
                    // the editor, then discard it if its export is obsolete.
                    if (!readbackFinished) return;
                    FinishCapture();
                    return;
                }
                if (pendingPixels == null)
                {
                    if (!readbackFinished) return;
                    if (readbackFailed)
                    {
                        Debug.LogWarning("Glim: emission atlas readback failed; it will be retried.");
                        FinishCapture();
                        nextScan = EditorApplication.timeSinceStartup + 2;
                        return;
                    }
                }
                if (capture != null) { capture.Dispose(); capture = null; }
                if (!Bindings.app_update_preview_emission((uint)pendingGroup, pendingPixels, (uint)pendingPixels.Length))
                    return; // Native scene export may still be uploading its initial textures.
                submittedHashes[pendingGroup] = pendingHash;
                FinishCapture();
            }
            if (EditorApplication.timeSinceStartup < nextScan) return;
            nextScan = EditorApplication.timeSinceStartup + 0.35;
            materialHashes.Clear();
            for (int n = 0; n < groups.Count; n++)
            {
                int i = (nextGroup + n) % groups.Count;
                var group = groups[i];
                if (!CanCapture(group)) continue;
                int hash = GroupHash(group);
                if (hash == submittedHashes[i] || group.sourceRenderers == null) continue;
                SyncMaterials(group);
                capture = new MetaTexture((int)group.settings.width, MetaTexture.AtlasType.Emission);
                pendingGroup = i;
                pendingHash = hash;
                readbackFinished = readbackFailed = false;
                request = capture.CreateAtlas(group.previewRenderers, MetaTexture.AtlasType.Emission, completed =>
                {
                    if (disposed) return;
                    // Unity retains completed readback data for only one frame.
                    // Copy in the completion callback, not in the throttled poll.
                    readbackFailed = completed.hasError;
                    if (!readbackFailed) pendingPixels = completed.GetData<byte>().ToArray();
                    readbackFinished = true;
                });
                nextGroup = (i + 1) % groups.Count;
                break; // At most one atlas readback in flight; coalesce slider changes.
            }
        }

        void FinishCapture()
        {
            capture?.Dispose();
            capture = null;
            pendingPixels = null;
            pendingGroup = -1;
        }

        public void Dispose()
        {
            disposed = true;
            if (capture != null && !readbackFinished) request.WaitForCompletion();
            FinishCapture();
        }
    }
}
