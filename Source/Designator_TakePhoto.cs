using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    /// <summary>Misc-tab tool: drag a rectangle (or click) to photograph that area. The shot keeps the dragged proportions (within the portrait/panorama limits); a click uses the default 3:2 span.</summary>
    public class Designator_TakePhoto : Designator
    {
        public Designator_TakePhoto()
        {
            defaultLabel = "MF_TakePhoto".Translate();
            defaultDesc = "MF_TakePhotoDesc".Translate() + "\n\n" + "MF_DragHint".Translate();
            icon = MFIcons.Camera;
            useMouseIcon = true;
            soundDragSustain = SoundDefOf.Designate_DragStandard;
            soundDragChanged = SoundDefOf.Designate_DragStandard_Changed;
            soundSucceeded = SoundDefOf.Designate_ZoneAdd;
        }

        public override DrawStyleCategoryDef DrawStyleCategory => DrawStyleCategoryDefOf.FilledRectangle;

        public override void DrawMouseAttachments()
        {
            base.DrawMouseAttachments();
            if (PhotoCapture.HideGuides) return;
            bool dragging = Find.DesignatorManager.Dragger?.Dragging == true;
            string hint = (dragging ? "MF_Phase2_DragRelease" : "MF_Phase2_DragCapture").Translate();
            var oldFont = Text.Font; var oldAnchor = Text.Anchor; var oldColor = GUI.color;
            try
            {
                Text.Font = GameFont.Small; Text.Anchor = TextAnchor.MiddleLeft;
                float width = Mathf.Min(420f, UI.screenWidth - 20f), height = Mathf.Max(38f, Text.CalcHeight(hint, width - 18f) + 12f);
                var mouse = Event.current.mousePosition;
                var box = new Rect(Mathf.Clamp(mouse.x + 24f, 8f, UI.screenWidth - width - 8f), Mathf.Clamp(mouse.y + 34f, 8f, UI.screenHeight - height - 8f), width, height);
                Widgets.DrawBoxSolid(box, new Color(.12f, .13f, .11f, .92f));
                GUI.color = Color.white; Widgets.Label(box.ContractedBy(7f), hint);
            }
            finally { Text.Font = oldFont; Text.Anchor = oldAnchor; GUI.color = oldColor; }
        }

        public override AcceptanceReport CanDesignateCell(IntVec3 loc)
        {
            return loc.InBounds(Map);
        }

        /// <summary>Capture (centre in world units, vertical span in cells, width/height) that contains the whole rectangle at its own proportions.
        /// Beyond the portrait/panorama limits, or below the minimum span, the area grows around the rectangle instead of distorting it.</summary>
        public static void FitRect(CellRect rect, out Vector3 center, out float span, out float aspect)
        {
            center = new Vector3((rect.minX + rect.maxX + 1) / 2f, 0f, (rect.minZ + rect.maxZ + 1) / 2f);
            aspect = PhotoCapture.ClampAspect(rect.Width / (float)rect.Height);
            span = Mathf.Max(rect.Height, rect.Width / aspect);
            span = Mathf.Clamp(span, PhotoCapture.MinSpan, PhotoCapture.MaxSpan);
        }

        public override void DesignateSingleCell(IntVec3 c)
        {
            Shoot(c.ToVector3Shifted(), MomentFramesMod.Settings.spanCells, PhotoCapture.DefaultAspect);
        }

        public override void DesignateMultiCell(IEnumerable<IntVec3> cells)
        {
            var list = cells.Where(c => c.InBounds(Map)).ToList();
            if (list.Count == 0) return;
            if (list.Count == 1)
            {
                DesignateSingleCell(list[0]);
                Finalize(true);
                return;
            }
            var rect = CellRect.FromLimits(list.Min(c => c.x), list.Min(c => c.z), list.Max(c => c.x), list.Max(c => c.z));
            FitRect(rect, out Vector3 center, out float span, out float aspect);
            Shoot(center, span, aspect);
            Finalize(true);
        }

        private void Shoot(Vector3 center, float span, float aspect)
        {
            var store = PhotoStore.Instance;
            if (store == null) return;
            IntVec3 cell = center.ToIntVec3();
            float radius = Mathf.Max(span, span * aspect) / 1.5f;   // same reach as the old 3:2 rule (radius = span for a 3:2 shot)
            var people = Map.mapPawns.FreeColonistsSpawned.Where(p => p.Position.InHorDistOf(cell, radius)).ToList();
            var rec = store.Request(Map, center, "manual", "", "", "", people, span, aspect);
            if (rec != null) rec.title = "MF_ManualTitle".Translate(rec.dateLabel);
        }

        public override void SelectedUpdate()
        {
            GenUI.RenderMouseoverBracket();
            CellRect rect;
            var dragger = Find.DesignatorManager.Dragger;
            if (dragger != null && dragger.Dragging && dragger.DragCells.Count > 0)
            {
                var cells = dragger.DragCells;
                rect = CellRect.FromLimits(cells.Min(c => c.x), cells.Min(c => c.z), cells.Max(c => c.x), cells.Max(c => c.z));
                FitRect(rect, out Vector3 center, out float span, out float aspect);
                rect = PhotoCapture.CoveredRect(center, span, Map, aspect);
            }
            else
            {
                IntVec3 c = UI.MouseCell();
                if (!c.InBounds(Map)) return;
                rect = PhotoCapture.CoveredRect(c.ToVector3Shifted(), MomentFramesMod.Settings.spanCells, Map);
            }
            GenDraw.DrawFieldEdges(rect.Cells.ToList());
        }
    }
}
