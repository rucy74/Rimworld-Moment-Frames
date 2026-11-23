using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    /// <summary>A decoration on an album's paper margin, stored with the record and never written into its image.</summary>
    public sealed class PhotoSticker : IExposable
    {
        public PhotoIcon icon = PhotoIcon.Heart;
        public float x = .12f, y = .12f; // normalized center in the outer paper mount, independent of display resolution

        public PhotoSticker() { }
        public PhotoSticker(PhotoIcon icon, float x, float y) { this.icon = icon; this.x = x; this.y = y; }

        public void ExposeData()
        {
            Scribe_Values.Look(ref icon, "icon", PhotoIcon.Heart);
            Scribe_Values.Look(ref x, "x", .12f);
            Scribe_Values.Look(ref y, "y", .12f);
        }
    }

    public static class AlbumStickers
    {
        internal const int MaxCount = 6;
        internal static readonly PhotoIcon[] Palette = {
            PhotoIcon.Heart, PhotoIcon.Star, PhotoIcon.Smile, PhotoIcon.Music, PhotoIcon.Exclamation, PhotoIcon.Wave, PhotoIcon.Paw
        };
        private const float Gap = 3f;
        internal static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        internal static bool Allowed(PhotoIcon icon) => Array.IndexOf(Palette, icon) >= 0;

        internal static void Normalize(List<PhotoSticker> stickers)
        {
            if (stickers == null) return;
            stickers.RemoveAll(s => s == null || !Allowed(s.icon) || !Finite(s.x) || !Finite(s.y));
            if (stickers.Count > MaxCount) stickers.RemoveRange(MaxCount, stickers.Count - MaxCount);
            foreach (var s in stickers) { s.x = Mathf.Clamp01(s.x); s.y = Mathf.Clamp01(s.y); }
        }

        internal static List<PhotoSticker> Copy(IEnumerable<PhotoSticker> source)
        {
            var result = new List<PhotoSticker>();
            if (source != null)
                foreach (var s in source)
                    if (s != null) result.Add(new PhotoSticker(s.icon, s.x, s.y));
            Normalize(result);
            return result;
        }

        internal static bool Add(List<PhotoSticker> draft, PhotoIcon icon, Vector2 normalizedCenter)
        {
            if (draft == null || draft.Count >= MaxCount || !Allowed(icon)
                || !Finite(normalizedCenter.x) || !Finite(normalizedCenter.y)) return false;
            draft.Add(new PhotoSticker(icon, Mathf.Clamp01(normalizedCenter.x), Mathf.Clamp01(normalizedCenter.y)));
            return true;
        }

        internal static bool Move(List<PhotoSticker> draft, int index, Vector2 normalizedCenter)
        {
            if (draft == null || index < 0 || index >= draft.Count || draft[index] == null
                || !Finite(normalizedCenter.x) || !Finite(normalizedCenter.y)) return false;
            float x = Mathf.Clamp01(normalizedCenter.x), y = Mathf.Clamp01(normalizedCenter.y);
            if (Mathf.Approximately(draft[index].x, x) && Mathf.Approximately(draft[index].y, y)) return false;
            draft[index].x = x; draft[index].y = y;
            return true;
        }

        internal static bool Remove(List<PhotoSticker> draft, int index)
        {
            if (draft == null || index < 0 || index >= draft.Count) return false;
            draft.RemoveAt(index);
            return true;
        }

        private static bool Same(IList<PhotoSticker> a, IList<PhotoSticker> b)
        {
            if ((a?.Count ?? 0) != (b?.Count ?? 0)) return false;
            if (a == null || a.Count == 0) return true;
            for (int i = 0; i < a.Count; i++)
                if (a[i] == null || b[i] == null || a[i].icon != b[i].icon || a[i].x != b[i].x || a[i].y != b[i].y) return false;
            return true;
        }

        internal static bool Commit(PhotoRecord record, IEnumerable<PhotoSticker> draft, PhotoStore store = null)
        {
            if (record == null) return false;
            var next = Copy(draft);
            if (Same(record.stickers, next)) return false;
            record.stickers = next;
            (store ?? PhotoStore.Instance)?.NotifyChanged();
            return true;
        }

        internal static float StickerSize(Rect frame) => Mathf.Min(36f, Mathf.Max(18f, Mathf.Min(frame.width, frame.height) * .08f));

        /// <summary>Reserves enough paper for a full decoration before fitting the image. Use the same rect for image rendering and Draw.</summary>
        internal static Rect ImageRect(Rect outerFrame, float aspect)
        {
            if (!Finite(aspect) || aspect <= .01f) aspect = PhotoCapture.DefaultAspect;
            float inset = Mathf.Min(StickerSize(outerFrame) + Gap + 5f, Mathf.Max(0f, Mathf.Min(outerFrame.width, outerFrame.height) * .24f));
            Rect mount = outerFrame.ContractedBy(inset);
            float width = Mathf.Max(1f, Mathf.Min(mount.width, mount.height * aspect));
            float height = width / aspect;
            return new Rect(mount.center.x - width / 2f, mount.center.y - height / 2f, width, height);
        }

        internal static bool WithinMargin(Rect outerFrame, Rect actualImageRect, Rect stickerRect)
        {
            if (stickerRect.width <= 0f || stickerRect.height <= 0f || !Finite(stickerRect.x) || !Finite(stickerRect.y)) return false;
            const float tolerance = .01f;
            return stickerRect.xMin >= outerFrame.xMin - tolerance && stickerRect.xMax <= outerFrame.xMax + tolerance
                && stickerRect.yMin >= outerFrame.yMin - tolerance && stickerRect.yMax <= outerFrame.yMax + tolerance
                && !stickerRect.Overlaps(actualImageRect.ExpandedBy(Gap));
        }

        /// <summary>When the image ratio or window changes, move the decoration to its nearest safe margin rather than across a face.</summary>
        internal static bool TryPlacedRect(PhotoSticker sticker, Rect outerFrame, Rect actualImageRect, out Rect result)
        {
            result = default;
            if (sticker == null || !Allowed(sticker.icon) || !Finite(sticker.x) || !Finite(sticker.y)
                || outerFrame.width < 18f || outerFrame.height < 18f) return false;
            float size = StickerSize(outerFrame), half = size / 2f;
            var desired = new Vector2(outerFrame.x + Mathf.Clamp01(sticker.x) * outerFrame.width,
                outerFrame.y + Mathf.Clamp01(sticker.y) * outerFrame.height);
            float left = outerFrame.xMin + half, right = outerFrame.xMax - half;
            float top = outerFrame.yMin + half, bottom = outerFrame.yMax - half;
            var bounded = new Vector2(Mathf.Clamp(desired.x, left, right), Mathf.Clamp(desired.y, top, bottom));
            Rect At(Vector2 center) => new Rect(center.x - half, center.y - half, size, size);
            result = At(bounded);
            if (WithinMargin(outerFrame, actualImageRect, result)) return true;
            // Each candidate moves along just one axis. Invalid (too narrow) strips are omitted.
            Vector2[] candidates = {
                new Vector2(bounded.x, actualImageRect.yMin - half - Gap - .02f),
                new Vector2(bounded.x, actualImageRect.yMax + half + Gap + .02f),
                new Vector2(actualImageRect.xMin - half - Gap - .02f, bounded.y),
                new Vector2(actualImageRect.xMax + half + Gap + .02f, bounded.y)
            };
            float best = float.MaxValue; bool found = false;
            foreach (var candidate in candidates)
            {
                Rect possible = At(candidate);
                if (!WithinMargin(outerFrame, actualImageRect, possible)) continue;
                float distance = (candidate - desired).sqrMagnitude;
                if (distance < best) { best = distance; result = possible; found = true; }
            }
            return found;
        }

        internal static Color ColorFor(PhotoIcon icon)
        {
            bool dark = AlbumStyle.Theme.Dark;
            if (icon == PhotoIcon.Heart) return dark ? new Color(.91f, .66f, .58f) : new Color(.64f, .37f, .30f);
            if (icon == PhotoIcon.Star) return dark ? new Color(.87f, .76f, .49f) : new Color(.59f, .46f, .23f);
            if (icon == PhotoIcon.Music || icon == PhotoIcon.Wave) return dark ? new Color(.70f, .82f, .68f) : new Color(.34f, .46f, .36f);
            return AlbumStyle.Theme.Ink;
        }

        internal static void Draw(IReadOnlyList<PhotoSticker> stickers, Rect outerFrame, Rect actualImageRect)
        {
            if (stickers == null) return;
            for (int i = 0; i < Mathf.Min(stickers.Count, MaxCount); i++)
                if (TryPlacedRect(stickers[i], outerFrame, actualImageRect, out Rect rect))
                    PhotoIconArt.Draw(rect, stickers[i].icon, ColorFor(stickers[i].icon));
        }

        public static void Draw(PhotoRecord record, Rect outerFrame, Rect actualImageRect) => Draw(record?.stickers, outerFrame, actualImageRect);
    }
}
