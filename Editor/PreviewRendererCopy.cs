using global::Glim;
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace FrozenAlex.GlimPreview
{
    // Undo/duplication may restore hidden children after their export was disposed.
    // A generated name and DontSave flags identify only our temporary objects;
    // the live registry determines whether the restored instance still has an owner.
    [InitializeOnLoad]
    internal static class PreviewRendererCopy
    {
        const string Prefix = "__GlimAddonPreviewCopy_";
        static readonly HashSet<int> Owned = new();
        static bool queued;

        static PreviewRendererCopy()
        {
            Undo.undoRedoPerformed += QueueCleanup;
            QueueCleanup();
        }

        internal static int Register(GameObject copy)
        {
            copy.name = Prefix + Guid.NewGuid().ToString("N");
            int id = copy.GetInstanceID();
            if (Owned.Count == 0) EditorApplication.hierarchyChanged += QueueCleanup;
            Owned.Add(id);
            return id;
        }

        internal static void Release(int id)
        {
            Owned.Remove(id);
            if (Owned.Count == 0) EditorApplication.hierarchyChanged -= QueueCleanup;
            QueueCleanup();
        }

        static void QueueCleanup()
        {
            if (queued) return;
            queued = true;
            EditorApplication.delayCall += Cleanup;
        }

        static void Cleanup()
        {
            queued = false;
            foreach (var renderer in Resources.FindObjectsOfTypeAll<MeshRenderer>())
            {
                if (!renderer) continue;
                var go = renderer.gameObject;
                if (!go.scene.IsValid() || go.hideFlags != HideFlags.HideAndDontSave ||
                    !go.name.StartsWith(Prefix, StringComparison.Ordinal) ||
                    !Guid.TryParseExact(go.name.Substring(Prefix.Length), "N", out _) ||
                    Owned.Contains(go.GetInstanceID())) continue;
                UnityEngine.Object.DestroyImmediate(go);
            }
        }
    }
}
