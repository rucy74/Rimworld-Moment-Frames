using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    /// <summary>Quarter photos and actual same-area survey history. All source files stay intact.</summary>
    internal static class SettlementRecordsTab
    {
        private static PhotoStore owner;
        private static int mapId = -1, stamp = -1, version = -1, presentStamp = -1, historyRegion;
        private static Vector2 scroll;
        private static bool quarters = true;
        private static bool historyMode;
        private static bool allPlaces;
        private static readonly Dictionary<string, SettlementPhotoBinding> images = new Dictionary<string, SettlementPhotoBinding>();
        private static readonly HashSet<string> visibleImages = new HashSet<string>();
        private static Dictionary<string, PhotoRecord> photoIndex = new Dictionary<string, PhotoRecord>();
        private static List<SettlementHistory.Entry> history = new List<SettlementHistory.Entry>();
        private static List<PhotoRecord> events = new List<PhotoRecord>();
        private static List<PhotoRecord> dated = new List<PhotoRecord>();
        private static readonly Dictionary<int, string> mapLabels = new Dictionary<int, string>();
        private static List<int> periods = new List<int>();
        private static SettlementSnapshot snapshot;
        internal static void Reset()
        {
            foreach (var binding in images.Values) binding.Unbind(); images.Clear();
            owner = null; mapId = stamp = version = presentStamp = -1; scroll = Vector2.zero;
            historyMode = false; historyRegion = 0; history.Clear(); photoIndex.Clear(); visibleImages.Clear();
            allPlaces = false; dated.Clear(); mapLabels.Clear();
        }
        private static void Select(int selectedMap, int selectedStamp)
        {
            foreach (var binding in images.Values) binding.Unbind(); images.Clear();
            mapId = selectedMap; stamp = selectedStamp; version = -1; scroll = Vector2.zero;
        }
        private static string PeriodLabel(int value)
        { SettlementLayout.Period(value, out int year, out int quarter); return "MF_Phase2_Period".Translate(year, quarter); }
        private static string MapLabel(int id) => mapLabels.TryGetValue(id, out var label) ? label : PhotoLocations.MapLabel(id, owner);
        private static string RegionLabel(int region) => region == 0 ? "MF_Phase2_HistoryWhole".Translate().ToString() : ("MF_Phase2_Quarter" + (region - 1)).Translate().ToString();
        private static void Mode(bool showHistory)
        {
            if (historyMode == showHistory) return;
            historyMode = showHistory; scroll = Vector2.zero; ReleaseAll();
        }
        private static void ReleaseAll()
        { foreach (var binding in images.Values) binding.Unbind(); images.Clear(); }
        private static void ReleaseInvisible()
        {
            foreach (string id in images.Keys.Where(id => !visibleImages.Contains(id)).ToList())
            { images[id].Unbind(); images.Remove(id); }
        }
        private static void Selected(Rect rect, bool selected)
        { if (selected) Widgets.DrawBoxSolid(new Rect(rect.x, rect.yMax - 2f, rect.width, 2f), AlbumStyle.Theme.Accent); }
        private static void RefreshHistory(PhotoStore store)
        {
            history = SettlementHistory.ForMap(store.Settlement.Snapshots.Select(s =>
            {
                string id = s.photoIds != null && s.photoIds.Count > historyRegion ? s.photoIds[historyRegion] : null;
                bool available = id != null && photoIndex.TryGetValue(id, out var record) && File.Exists(store.FilePath(record));
                return new SettlementHistory.Entry { MapId = s.mapId, Stamp = s.stamp, CapturedAbs = s.finishedAbs,
                    Complete = s.complete && !s.failed, Available = available, PhotoId = id };
            }), mapId, presentStamp);
        }
        internal static void Draw(Rect rect, PhotoStore store)
        {
            if (store == null) return;
            if (!ReferenceEquals(store, owner)) { Reset(); owner = store; }
            var maps = Find.Maps.Where(m => m.IsPlayerHome && !m.IsPocketMap).ToList();
            if (mapId < 0) mapId = maps.FirstOrDefault(m => m == Find.CurrentMap)?.uniqueID ?? maps.FirstOrDefault()?.uniqueID ?? store.Records.FirstOrDefault()?.mapId ?? -1;
            Map selectedMap = Find.Maps.FirstOrDefault(m => m.uniqueID == mapId);
            int now = selectedMap != null ? SettlementChronicle.Stamp(selectedMap, Find.TickManager.TicksAbs)
                : version == store.Version && presentStamp >= 0 ? presentStamp
                : store.Records.Where(r => r.mapId == mapId).Select(SettlementChronicle.Stamp)
                    .Concat(store.Settlement.Snapshots.Where(s => s.mapId == mapId).Select(s => s.stamp))
                    .DefaultIfEmpty(SettlementLayout.Stamp(5500, 1)).Max();
            if (version != store.Version || now != presentStamp)
            {
                foreach (var binding in images.Values) binding.Unbind(); images.Clear();
                events = store.Records.Where(r => RecordEventPolicy.IncludePlace(mapId, allPlaces, r.mapId,
                    SettlementChronicle.IsSurvey(r), r.HasFile && File.Exists(store.FilePath(r)))).OrderBy(r => r.ticksAbs).ToList();
                mapLabels.Clear();
                foreach (int id in maps.Select(m => m.uniqueID).Concat(store.Records.Select(r => r.mapId)).Where(id => id >= 0).Distinct())
                    mapLabels[id] = PhotoLocations.MapLabel(id, store);
                periods = events.Select(SettlementChronicle.Stamp).Concat(store.Settlement.Snapshots.Where(s => s.mapId == mapId).Select(s => s.stamp)).Distinct().OrderBy(p => p).ToList();
                presentStamp = now;
                if (!periods.Contains(now)) periods.Add(now);
                if (stamp < 0) stamp = now;
                if (!periods.Contains(stamp)) periods.Add(stamp);
                periods.Sort();
                dated = events.Where(r => SettlementChronicle.Stamp(r) == stamp).ToList();
                photoIndex = store.Records.Where(r => r.HasFile && r.mapId == mapId && SettlementChronicle.IsSurvey(r))
                    .GroupBy(r => r.id).ToDictionary(g => g.Key, g => g.First());
                RefreshHistory(store);
                snapshot = store.Settlement.Snapshots.LastOrDefault(s => s.mapId == mapId && s.stamp == stamp && s.complete)
                    ?? store.Settlement.Snapshots.LastOrDefault(s => s.mapId == mapId && s.stamp == stamp);
                version = store.Version;
            }
            float row = Mathf.Max(40f, MainTabWindow_Album.HeaderRowHeight);
            if (AlbumStyle.Button(new Rect(rect.x, rect.y, 170f, row), MapLabel(mapId)))
            {
                var ids = maps.Select(m => m.uniqueID).Concat(store.Records.Select(r => r.mapId)).Where(id => id >= 0).Distinct().OrderBy(id => id).ToList();
                Find.WindowStack.Add(new FloatMenu(ids.Select(id => new FloatMenuOption(MapLabel(id), () => Select(id, -1))).ToList()));
            }
            float modeWidth = Mathf.Min(170f, (rect.width - 188f) / 2f);
            Rect periodMode = new Rect(rect.xMax - modeWidth * 2f - 8f, rect.y, modeWidth, row);
            Rect changeMode = new Rect(rect.xMax - modeWidth, rect.y, modeWidth, row);
            if (AlbumStyle.Button(periodMode, "MF_Phase2_PeriodView".Translate())) Mode(false);
            if (AlbumStyle.Button(changeMode, "MF_Phase2_HistoryView".Translate())) Mode(true);
            Selected(periodMode, !historyMode); Selected(changeMode, historyMode);
            float yTop = rect.y + row + 8f;
            if (historyMode)
            {
                float regionWidth = (rect.width - 32f) / 5f;
                for (int i = 0; i < 5; i++)
                {
                    Rect regionButton = new Rect(rect.x + i * (regionWidth + 8f), yTop, regionWidth, row);
                    if (AlbumStyle.Button(regionButton, RegionLabel(i)) && historyRegion != i)
                    { historyRegion = i; scroll = Vector2.zero; ReleaseAll(); RefreshHistory(store); }
                    Selected(regionButton, historyRegion == i);
                }
                string summary = history.Count > 0 ? "MF_Phase2_HistorySummary".Translate(RegionLabel(historyRegion), PeriodLabel(history.First().Stamp),
                    PeriodLabel(history.Last().Stamp), history.Count).ToString() : RegionLabel(historyRegion);
                float playWidth = Mathf.Min(220f, rect.width * .3f);
                GUI.color = AlbumStyle.Theme.MutedInk;
                AlbumStyle.Label(new Rect(rect.x + 8f, yTop + row + 6f, rect.width - playWidth - 24f, row), summary);
                GUI.color = Color.white;
                if (AlbumStyle.Button(new Rect(rect.xMax - playWidth, yTop + row + 6f, playWidth, row), "MF_FavoriteSequence".Translate(), history.Count > 0))
                    Find.WindowStack.Add(new Dialog_SettlementSequence(store, history, MapLabel(mapId) + " · " + RegionLabel(historyRegion)));
                DrawHistory(new Rect(rect.x, yTop + row * 2f + 12f, rect.width, rect.yMax - yTop - row * 2f - 12f), store);
                return;
            }
            Map currentMap = Find.CurrentMap;
            bool canCapture = currentMap?.uniqueID == mapId && currentMap.IsPlayerHome && !store.Settlement.Snapshots.Any(s => s.mapId == mapId && !s.complete && !s.failed);
            float captureWidth = Mathf.Min(220f, rect.width * .28f), navigationWidth = rect.width - captureWidth - 16f;
            float yearWidth = (navigationWidth - 96f) * .58f, quarterWidth = navigationWidth - 96f - yearWidth;
            int index = periods.IndexOf(stamp);
            if (AlbumStyle.Button(new Rect(rect.x, yTop, 36f, row), "<", index > 0)) Select(mapId, periods[index - 1]);
            if (AlbumStyle.Button(new Rect(rect.x + 44f, yTop, yearWidth, row), "MF_Phase2_SelectYear".Translate(stamp / 4)))
                Find.WindowStack.Add(new FloatMenu(SettlementHistory.Years(periods, presentStamp).AsEnumerable().Reverse().Select(year =>
                    new FloatMenuOption("MF_Phase2_YearOption".Translate(year), () => Select(mapId, SettlementHistory.SelectYear(year, stamp, presentStamp)))).ToList()));
            if (AlbumStyle.Button(new Rect(rect.x + 52f + yearWidth, yTop, quarterWidth, row), "MF_Phase2_SelectQuarter".Translate(stamp % 4 + 1)))
            {
                int count = stamp / 4 == presentStamp / 4 ? presentStamp % 4 + 1 : 4;
                Find.WindowStack.Add(new FloatMenu(Enumerable.Range(1, count).Select(quarter => new FloatMenuOption("MF_Phase2_QuarterOption".Translate(quarter),
                    () => Select(mapId, SettlementLayout.Stamp(stamp / 4, quarter)))).ToList()));
            }
            if (AlbumStyle.Button(new Rect(rect.x + navigationWidth - 36f, yTop, 36f, row), ">", index + 1 < periods.Count)) Select(mapId, periods[index + 1]);
            if (AlbumStyle.Button(new Rect(rect.xMax - captureWidth, yTop, captureWidth, row), "MF_Phase2_SurveyNow".Translate(), canCapture))
            { int current = SettlementChronicle.Stamp(currentMap, Find.TickManager.TicksAbs); store.Settlement.Request(store, currentMap, current, false); Select(mapId, current); }
            // A second survey of this period may be queued while an earlier completed survey is displayed.
            var pending=store.Settlement.Snapshots.LastOrDefault(s=>s.mapId==mapId && !s.complete && !s.failed);
            string progress=store.Settlement.Progress(pending??snapshot);
            if(!progress.NullOrEmpty())
            {
                float progressH=AlbumStyle.TextHeight(progress,rect.width-16)+12;
                SharedPhotoUI.Label(new Rect(rect.x+8,yTop+row+6,rect.width-16,progressH),progress,true);
                yTop+=progressH+6;
            }
            float filterWidth = Mathf.Min(190f, rect.width * .3f);
            Rect filter = new Rect(rect.xMax - filterWidth, yTop + row + 6f, filterWidth, row);
            if (AlbumStyle.Button(filter, (allPlaces ? "MF_Records_AllPlaces" : "MF_Records_ThisPlace").Translate()))
            { allPlaces = !allPlaces; version = -1; scroll = Vector2.zero; }
            TooltipHandler.TipRegion(filter, "MF_Records_PlaceFilterHint".Translate());
            GUI.color = AlbumStyle.Theme.MutedInk;
            AlbumStyle.Label(new Rect(rect.x + 8f, yTop + row + 6f, rect.width - filterWidth - 20f, row),
                "MF_Records_PeriodScope".Translate(allPlaces ? "MF_Records_AllPlaces".Translate().ToString() : MapLabel(mapId), PeriodLabel(stamp), dated.Count));
            GUI.color = Color.white;
            Rect outer = new Rect(rect.x, yTop + row * 2f + 12f, rect.width, rect.yMax - yTop - row * 2f - 12f);
            float width = outer.width - 20f, cardW = (width - 18f) / 2f;
            float overviewH = Mathf.Clamp(width * .65f, 420f, 760f);
            float quadrantH = cardW / 1.5f + 72f, eventH = cardW / 1.5f + 128f;
            float height = overviewH + 108f + (quarters && snapshot?.complete == true ? quadrantH * 2f + 16f : 0f) + 50f + Mathf.Ceil(dated.Count / 2f) * (eventH + 16f);
            Rect view = new Rect(0f, 0f, width, Mathf.Max(outer.height - 1f, height));
            Widgets.BeginScrollView(outer, ref scroll, view);
            visibleImages.Clear();
            try
            {
                float y = 0f;
                PhotoRecord overview = Record(store, 0);
                if (overview != null)
                { Rect print = new Rect(0f, y, width, overviewH); if (Visible(print, outer.height)) DrawPrint(print, overview, store, true); }
                else
                {
                    AlbumStyle.DrawPaper(new Rect(0f, y, width, overviewH)); Text.Anchor = TextAnchor.MiddleCenter;
                    GUI.color = AlbumStyle.Theme.Ink;
                    string key = snapshot?.complete == true ? "MF_Phase2_SurveyMissing" : snapshot?.failed == true ? "MF_Phase2_SurveyFailed" : snapshot != null ? "MF_Phase2_SurveyQueued" : "MF_Phase2_SurveyEmpty";
                    AlbumStyle.HeaderLabel(new Rect(20f, y + overviewH / 2f - 40f, width - 40f, 80f), key.Translate());
                    Text.Anchor = TextAnchor.UpperLeft; GUI.color = Color.white;
                }
                y += overviewH + 8f;
                GUI.color = AlbumStyle.Theme.MutedInk;
                string timing = snapshot?.complete == true ? "MF_Phase2_SurveyCaptured".Translate(PhotoStore.DateLabel(maps.FirstOrDefault(m => m.uniqueID == mapId), snapshot.finishedAbs)).ToString() : "MF_Phase2_SurveySchedule".Translate().ToString();
                AlbumStyle.Label(new Rect(8f, y, width - 16f, 36f), timing); GUI.color = Color.white; y += 40f;
                if (snapshot?.complete == true)
                {
                    if (AlbumStyle.Button(new Rect(0f, y, width, 40f), (quarters ? "MF_Phase2_QuartersClose" : "MF_Phase2_QuartersOpen").Translate())) quarters = !quarters;
                    y += 48f;
                    if (quarters)
                    {
                        for (int i = 0; i < 4; i++)
                        { Rect print = new Rect((i % 2) * (cardW + 18f), y + (i / 2) * (quadrantH + 8f), cardW, quadrantH); if (Visible(print, outer.height)) DrawPrint(print, Record(store, i + 1), store, true); }
                        y += 2f * (quadrantH + 8f);
                    }
                }
                GUI.color = AlbumStyle.Theme.Ink;
                AlbumStyle.HeaderLabel(new Rect(8f, y, width - 16f, 42f), "MF_Phase2_PeriodPhotos".Translate(dated.Count)); y += 50f;
                for (int i = 0; i < dated.Count; i++)
                {
                    Rect card = new Rect((i % 2) * (cardW + 18f), y + (i / 2) * (eventH + 16f), cardW, eventH);
                    if (card.yMax >= scroll.y && card.y <= scroll.y + outer.height) DrawPrint(card, dated[i], store, false);
                }
                GUI.color = Color.white;
            }
            finally { Widgets.EndScrollView(); ReleaseInvisible(); Text.Anchor = TextAnchor.UpperLeft; GUI.color = Color.white; }
        }
        private static bool Visible(Rect rect, float viewportHeight) => rect.yMax >= scroll.y && rect.y <= scroll.y + viewportHeight;
        private static void DrawHistory(Rect outer, PhotoStore store)
        {
            float width = outer.width - 20f, printHeight = Mathf.Clamp(width * .65f, 420f, 760f), step = printHeight + 92f;
            float noteHeight = 72f;
            Rect view = new Rect(0f, 0f, width, Mathf.Max(outer.height - 1f, history.Count * step + noteHeight));
            Widgets.BeginScrollView(outer, ref scroll, view); visibleImages.Clear();
            try
            {
                GUI.color = AlbumStyle.Theme.MutedInk;
                AlbumStyle.Label(new Rect(8f, 0f, width - 16f, noteHeight), (history.Count == 1 ? "MF_Phase2_HistoryOne" : "MF_Phase2_HistoryHint").Translate());
                GUI.color = Color.white;
                if (history.Count == 0)
                {
                    Rect empty = new Rect(0f, noteHeight, width, Mathf.Min(420f, Mathf.Max(120f, outer.height - noteHeight)));
                    AlbumStyle.DrawPaper(empty); Text.Anchor = TextAnchor.MiddleCenter;
                    AlbumStyle.HeaderLabel(empty.ContractedBy(20f), "MF_Phase2_HistoryEmpty".Translate()); Text.Anchor = TextAnchor.UpperLeft;
                }
                for (int i = 0; i < history.Count; i++)
                {
                    float y = noteHeight + i * step;
                    if (!Visible(new Rect(0f, y, width, step), outer.height)) continue;
                    var entry = history[i]; PhotoRecord record = photoIndex[entry.PhotoId];
                    GUI.color = AlbumStyle.Theme.Ink;
                    AlbumStyle.HeaderLabel(new Rect(8f, y, width - 16f, 40f), PeriodLabel(entry.Stamp));
                    GUI.color = AlbumStyle.Theme.MutedInk;
                    AlbumStyle.Label(new Rect(8f, y + 40f, width - 16f, 32f), "MF_Phase2_HistoryCaptureDate".Translate(record.dateLabel)); GUI.color = Color.white;
                    Rect print = new Rect(0f, y + 76f, width, printHeight);
                    if (Visible(print, outer.height)) DrawPrint(print, record, store, true);
                }
            }
            finally { Widgets.EndScrollView(); ReleaseInvisible(); Text.Anchor = TextAnchor.UpperLeft; GUI.color = Color.white; }
        }
        private static PhotoRecord Record(PhotoStore store, int index)
        { string id = snapshot?.complete == true && snapshot.photoIds.Count > index ? snapshot.photoIds[index] : null; return id == null ? null : store.Records.FirstOrDefault(r => r.id == id); }
        private static void DrawPrint(Rect rect, PhotoRecord record, PhotoStore store, bool large)
        {
            if (record == null)
            {
                AlbumStyle.DrawPaper(rect); GUI.color = AlbumStyle.Theme.MutedInk;
                AlbumStyle.HeaderLabel(rect.ContractedBy(16f), "MF_Phase2_SurveyMissing".Translate()); GUI.color = Color.white; return;
            }
            AlbumStyle.DrawPaper(rect); Rect image = new Rect(rect.x + 12f, rect.y + 12f, rect.width - 24f, rect.height - (large ? 78f : 134f));
            visibleImages.Add(record.id);
            if (!images.TryGetValue(record.id, out var binding)) images[record.id] = binding = new SettlementPhotoBinding();
            binding.Bind(record); Texture texture = binding.Material?.mainTexture;
            texture ??= PhotoTextureCache.Thumb(store.ThumbPath(record), store.FilePath(record));
            if (texture != null) GUI.DrawTexture(image, texture, ScaleMode.ScaleToFit);
            else { GUI.color = AlbumStyle.Theme.MutedInk; AlbumStyle.HeaderLabel(image.ContractedBy(12f), "MF_Phase2_SurveyMissing".Translate()); GUI.color = Color.white; }
            GUI.color = AlbumStyle.Theme.Ink;
            AlbumStyle.HeaderLabel(new Rect(rect.x + 12f, image.yMax + 8f, rect.width - 24f, 54f), record.DisplayTitle);
            if (!large) AlbumStyle.Label(new Rect(rect.x + 12f, rect.yMax - 60f, rect.width - 24f, 48f), record.dateLabel
                + (allPlaces ? " · " + MapLabel(record.mapId) : "") + "\n" + record.DisplayDesc);
            GUI.color = Color.white;
            if (Widgets.ButtonInvisible(rect)) Find.WindowStack.Add(new Dialog_MFPhotoZoom(record));
        }
    }
}
