using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    /// <summary>Display-only projection copies. At most eight 1024px images (~32 MiB RGB+mipmaps), rather than full 4096px originals.
    /// A ninth active projector uses its ordinary thumbnail until an entry is released. No photo file is rewritten.</summary>
    internal static class ProjectionTextureCache
    {
        internal const int Cap = 8, MaxSide = 1024;
        private sealed class Entry { internal Material Material; internal int Refs, LastUse; }
        private static readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>();
        private static PhotoStore store;
        internal static int Count => entries.Count;
        private static void EnsureStore()
        {
            if (ReferenceEquals(store, PhotoStore.Instance)) return;
            foreach (var entry in entries.Values) Destroy(entry);
            entries.Clear(); store = PhotoStore.Instance;
        }
        internal static bool Owns(string path, Material material)
        {
            EnsureStore();
            return path != null && entries.TryGetValue(path, out var entry) && ReferenceEquals(entry.Material, material);
        }
        internal static Material Acquire(string path)
        {
            EnsureStore();
            if (path.NullOrEmpty()) return null;
            if (entries.TryGetValue(path, out var held)) { held.Refs++; held.LastUse = Time.frameCount; return held.Material; }
            while (entries.Count >= Cap)
            {
                var victim = entries.Where(e => e.Value.Refs == 0).OrderBy(e => e.Value.LastUse).FirstOrDefault();
                if (victim.Key == null) return null;
                Destroy(victim.Value); entries.Remove(victim.Key);
            }
            Texture2D full = null, bounded = null; RenderTexture resized = null;
            RenderTexture previous = RenderTexture.active;
            try
            {
                if (!File.Exists(path)) return null;
                full = new Texture2D(2, 2, TextureFormat.RGB24, false);
                if (!full.LoadImage(File.ReadAllBytes(path), true)) return null;
                float scale = Mathf.Min(1f, (float)MaxSide / Mathf.Max(full.width, full.height));
                int width = Mathf.Max(1, Mathf.RoundToInt(full.width * scale)), height = Mathf.Max(1, Mathf.RoundToInt(full.height * scale));
                resized = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
                Graphics.Blit(full, resized); RenderTexture.active = resized;
                bounded = new Texture2D(width, height, TextureFormat.RGB24, true);
                bounded.ReadPixels(new Rect(0f, 0f, width, height), 0, 0, false); bounded.Apply(true, true);
                bounded.filterMode = FilterMode.Trilinear; bounded.wrapMode = TextureWrapMode.Clamp;
                var material = new Material(ShaderDatabase.Cutout) { mainTexture = bounded };
                entries[path] = new Entry { Material = material, Refs = 1, LastUse = Time.frameCount };
                bounded = null; return material;
            }
            catch (Exception e) { Log.WarningOnce("[MomentFrames] large projection unavailable; using thumbnail: " + e, 0x4D465052); return null; }
            finally
            {
                RenderTexture.active = previous;
                if (resized != null) RenderTexture.ReleaseTemporary(resized);
                if (full != null) UnityEngine.Object.Destroy(full);
                if (bounded != null) UnityEngine.Object.Destroy(bounded);
            }
        }
        internal static void Release(string path, Material material)
        {
            EnsureStore();
            if (path != null && entries.TryGetValue(path, out var entry) && ReferenceEquals(entry.Material, material))
            { entry.Refs = Mathf.Max(0, entry.Refs - 1); entry.LastUse = Time.frameCount; }
        }
        private static void Destroy(Entry entry)
        {
            if (entry.Material == null) return;
            if (entry.Material.mainTexture != null) UnityEngine.Object.Destroy(entry.Material.mainTexture);
            UnityEngine.Object.Destroy(entry.Material);
        }
    }

    internal sealed class ProjectionBinding
    {
        private string path;
        private Material material;
        private int retryFrame;
        internal Material Material => material;
        internal void Bind(PhotoRecord record)
        {
            string desired = record?.HasFile == true ? PhotoStore.Instance?.FilePath(record) : null;
            if (desired == path && (material != null && ProjectionTextureCache.Owns(path, material) || Time.frameCount < retryFrame)) return;
            Unbind(); path = desired;
            material = ProjectionTextureCache.Acquire(path);
            if (material == null) retryFrame = Time.frameCount + 120;
        }
        internal void Unbind()
        {
            ProjectionTextureCache.Release(path, material); path = null; material = null; retryFrame = 0;
        }
    }
}
