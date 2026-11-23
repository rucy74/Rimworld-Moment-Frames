using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    /// <summary>The 2026-11-03 UI fixes: everything that can be checked without a physical mouse, and a picture of each changed window.</summary>
    public static partial class Probe
    {
        private static bool uiFixesMode;
        private static Building_PhotoStudio uiStudio;
        private static Building_PhotoSpot uiSpot;
        private static PhotoRecord uiEdited, uiReunion;
        private static Dialog_GroupPhotoSetup uiGroup;
        private static Dialog_BirthdaySetup uiBirthday;
        private static Dialog_StudioBackground uiBackground;
        private static Dialog_PhotoGuide uiGuide;
        private static List<Pawn> uiPeople;

        private static void UiCheck(bool ok, string label)
        { if (!ok) throw new InvalidOperationException("ui fix: " + label); checks.Add("PASS: " + label); }

        private static MainTabWindow_Album UiAlbum => (MainTabWindow_Album)AlbumDef.TabWindow;
        private static List<PhotoRecord> UiList() => (List<PhotoRecord>)AccessTools.Method(typeof(MainTabWindow_Album), "Filtered").Invoke(UiAlbum, null);
        private static int UiCount(string prefix) => UiList().Count(r => r.id.StartsWith(prefix));

        private static PhotoRecord UiRecord(PhotoStore store, string id, string kind, string file, int offset)
        {
            var rec = new PhotoRecord { id = id, file = file, title = "UI " + id, kind = kind, aspect = 1.5f, year = 5500, ticksAbs = Find.TickManager.TicksAbs + offset,
                capturedTicksAbs = Find.TickManager.TicksAbs + offset, mapId = map.uniqueID, dateLabel = "5500", center = center.ToVector3Shifted(), spanCells = 20 };
            Traverse.Create(store).Field("records").GetValue<List<PhotoRecord>>().Add(rec);
            return rec;
        }

        private static void UiGallery(MainTabWindow_Album album, PhotoRecord rec) =>
            AccessTools.Method(typeof(MainTabWindow_Album), "ShowGallery", new[] { typeof(PhotoRecord), typeof(bool) }).Invoke(album, new object[] { rec, false });

        private static void UiFilterChecks(PhotoStore store)
        {
            var album = UiAlbum;
            var field = Traverse.Create(album);
            album.ShowChronicle(false, false); album.ClearFilters(); UiList();
            UiCheck(UiCount("ui-fix-manual") == 3 && UiCount("ui-fix-birthday") == 0, "default photo tab leaves birthdays out");
            UiCheck(UiCount("ui-fix-overview") == 1 && UiCount("ui-fix-quarter") == 0, "photo tab keeps the whole-map picture and leaves the four regions out");
            UiCheck(field.Field("hiddenBirthdays").GetValue<int>() == 2, "the hidden birthdays are counted");
            UiCheck(album.FilterSummary().Contains("MF_UI12_BirthdaysHidden".Translate(2).ToString()), "filter line says how many birthdays are hidden: " + album.FilterSummary());
            album.ShowAllPhotos(); UiList();
            UiCheck(UiCount("ui-fix-birthday") == 2 && UiCount("ui-fix-quarter") == 0, "show all photos includes birthdays, still without the regions");
            UiCheck(field.Field("hiddenBirthdays").GetValue<int>() == 0, "nothing is counted as hidden once birthdays are included");
            album.ClearFilters(); UiList();
            UiCheck(UiCount("ui-fix-birthday") == 0 && field.Field("hiddenBirthdays").GetValue<int>() == 2, "clear all conditions returns to the default without birthdays");
            field.Field("filterKind").SetValue(Kinds.SettlementQuarter); field.Field("cacheValid").SetValue(false);
            UiCheck(UiCount("ui-fix-quarter") == 4, "choosing the region kind still shows the four regions");
            album.ClearFilters();
            field.Field("search").SetValue("ui-fix-quarter-1"); field.Field("cacheValid").SetValue(false);
            UiCheck(UiCount("ui-fix-quarter") == 1, "searching still finds a region");
            album.ClearFilters();
            album.ShowChronicle(true, false); UiList();
            UiCheck(UiCount("ui-fix-quarter") == 4, "the chronicle keeps the regions");
            album.ShowChronicle(false, false);
            UiGallery(album, uiEdited); UiList();
            UiCheck(UiCount("ui-fix-quarter") == 4, "the gallery keeps the regions, so frames can still open them");
            album.ShowChronicle(false, false); album.ClearFilters();

            // Only birthdays taken: the page is empty and its one button must lead to them.
            var others = store.Records.Where(r => r.HasFile && !Kinds.PersonalOnly(r.kind)).Select(r => new KeyValuePair<PhotoRecord, string>(r, r.file)).ToList();
            foreach (var other in others) other.Key.file = null;
            store.NotifyChanged(); UiList();
            UiCheck(UiList().Count == 0 && album.EmptyMessageKey == "MF_DesignAlbum_NoMatches", "an album of only birthdays opens on the no-match page");
            album.ShowAllPhotos();
            UiCheck(UiCount("ui-fix-birthday") == 2, "the empty page's clear action (show all) reaches the birthdays");
            foreach (var other in others) other.Key.file = other.Value;
            store.NotifyChanged(); album.ClearFilters();

            // Closing the album brings the default back, also after a person's album.
            MainTabWindow_Album.OpenFor(uiPeople[0]);
            field.Field("includeBirthdays").SetValue(true);
            Find.MainTabsRoot.SetCurrentTab(null, false);
            UiCheck(!field.Field("includeBirthdays").GetValue<bool>(), "closing a person's album restores the birthday default");
            album.ShowChronicle(false, false); album.ClearFilters(); UiList();
            UiCheck(UiCount("ui-fix-birthday") == 0, "the next ordinary album opens without birthdays");
        }

        private static void UiGeometryChecks()
        {
            foreach (var width in new[] { 500f, 700f, 900f })
                foreach (var height in new[] { 150f, 360f, 700f })
                {
                    Rect area = new Rect(10f, 20f, width, height);
                    Rect zoom = MainTabWindow_Album.GalleryZoomRect(area);
                    Rect strip = new Rect(area.xMax - 52f, area.y, 52f, height - 20f);
                    UiCheck(strip.Overlaps(zoom), "control: the whole next-photo strip reaches under the zoom button at " + width + "x" + height);
                    UiCheck(!MainTabWindow_Album.GalleryNextHit(strip, zoom).Overlaps(zoom), "next-photo click area stays clear of the zoom button at " + width + "x" + height);
                }
        }

        private static void UiHintChecks()
        {
            string reach = "MF_PreparationReachShort".Translate();
            Pawn a = uiPeople[0], b = uiPeople[1];
            var one = new Dictionary<Pawn, string> { { a, "why" } };
            var brief = new Dictionary<Pawn, string> { { a, reach }, { b, reach } };
            UiCheck(PhotoPreparationChoices.RemoveHint("why", one, brief, p => true) != "", "one unreachable person who can be left out gets the hint");
            UiCheck(PhotoPreparationChoices.RemoveHint("why", one, brief, p => false) == "", "a required person does not get the hint");
            UiCheck(PhotoPreparationChoices.RemoveHint("other", one, brief, p => true) == "", "no hint while another reason comes first");
            UiCheck(PhotoPreparationChoices.RemoveHint("why", new Dictionary<Pawn, string> { { a, "why" }, { b, "why" } }, brief, p => true) == "", "two blocked people do not get the hint");
            UiCheck(PhotoPreparationChoices.RemoveHint("why", one, new Dictionary<Pawn, string> { { a, "tired" } }, p => true) == "", "a state problem keeps its own advice");
        }

        private static void UiFixesStep()
        {
            CloseLog();
            var store = PhotoStore.Instance;
            var album = UiAlbum;
            var field = Traverse.Create(album);
            switch (phase)
            {
                case 0:
                    deadline = Time.realtimeSinceStartup + 240f; map = Find.CurrentMap; center = map.Center;
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    ClearHostiles(map); Find.Storyteller.storytellerComps.Clear(); Find.Storyteller.incidentQueue.Clear();
                    var settings = MomentFramesMod.Settings;
                    settings.autoCaptureEnabled = settings.memoryNight = settings.yearLetter = settings.buildingPhotos = false;
                    settings.settlementPeriod = 0; settings.albumTextSize = 100;
                    Prefs.DevMode = false; Find.PlaySettings.showLearningHelper = false;
                    uiPeople = map.mapPawns.FreeColonistsSpawned.Take(3).ToList();
                    UiCheck(uiPeople.Count >= 2, "fresh minimal game has colonists");
                    foreach (var cell in CellRect.CenteredOn(center, 16)) { map.fogGrid.Unfog(cell); map.roofGrid.SetRoof(cell, null); }
                    foreach (var pawn in uiPeople) { FixReady(pawn); pawn.Position = center + IntVec3.West * (6 + uiPeople.IndexOf(pawn)); pawn.Notify_Teleported(); }
                    uiStudio = (Building_PhotoStudio)ThingMaker.MakeThing(ThingDef.Named("MF_StudioBackdrop"));
                    uiStudio.SetFaction(Faction.OfPlayer); GenSpawn.Spawn(uiStudio, center + IntVec3.North * 4, map, Rot4.North);
                    uiSpot = (Building_PhotoSpot)ThingMaker.MakeThing(ThingDef.Named("MF_PhotoSpot"));
                    uiSpot.SetFaction(Faction.OfPlayer); GenSpawn.Spawn(uiSpot, center + IntVec3.South * 8, map, Rot4.North);
                    Directory.CreateDirectory(store.FolderPath);
                    var tex = new Texture2D(600, 400, TextureFormat.RGB24, false);
                    tex.SetPixels32(Enumerable.Repeat(new Color32(70, 120, 90, 255), 600 * 400).ToArray()); tex.Apply(false);
                    byte[] png = tex.EncodeToPNG(); UnityEngine.Object.Destroy(tex);
                    File.WriteAllBytes(Path.Combine(store.FolderPath, "ui-fix.png"), png);
                    File.WriteAllBytes(Path.Combine(store.FolderPath, "ui-fix-edit.png"), png);
                    for (int i = 0; i < 3; i++) UiRecord(store, "ui-fix-manual-" + i, Kinds.Manual, "ui-fix.png", i);
                    for (int i = 0; i < 2; i++) UiRecord(store, "ui-fix-birthday-" + i, Kinds.Birthday, "ui-fix.png", 10 + i);
                    UiRecord(store, "ui-fix-overview", Kinds.SettlementOverview, "ui-fix.png", 20);
                    for (int i = 0; i < 4; i++) UiRecord(store, "ui-fix-quarter-" + i, Kinds.SettlementQuarter, "ui-fix.png", 21 + i).title = "ui-fix-quarter-" + i;
                    uiEdited = UiRecord(store, "ui-fix-edited", Kinds.Manual, "ui-fix.png", 30);
                    uiEdited.edits.Add(new PhotoEdit { id = "ui-fix-edit", file = "ui-fix-edit.png", filter = PhotoFilters.None });
                    uiReunion = UiRecord(store, "ui-fix-reunion", GroupPhoto.Kind, "ui-fix.png", 31);
                    uiReunion.SetPeople(uiPeople);
                    store.NotifyChanged();
                    UiCheck(!new Dialog_EditDescription(uiEdited).closeOnClickedOutside && !new Dialog_EditDescription(uiEdited).closeOnAccept, "a click outside or Enter does not close the note window");
                    UiCheck(!new Dialog_RenamePhoto(uiEdited).closeOnClickedOutside, "a click outside does not close the title window");
                    UiGeometryChecks();
                    UiHintChecks();
                    UiFilterChecks(store);
                    UiCheck(!OptionalPhotoBridges.RimGramInstalled && !OptionalPhotoBridges.RimTalkInstalled
                        && !Traverse.Create(typeof(MainTabWindow_Album)).Property("HasIntegrations").GetValue<bool>(), "without those mods the integrations button is gone");
                    AlbumOpen(false, false);
                    phase = 1; Wait(1f); return;
                case 1:
                    Shot("album-photos.png"); phase = 101; Wait(.5f); return;
                case 101:
                    UiGallery(album, uiEdited);
                    phase = 2; Wait(1f); return;
                case 2:
                    Shot("gallery-folded.png"); phase = 102; Wait(.5f); return;
                case 102:
                    UiCheck(!field.Method("VersionsOpen", new[] { typeof(PhotoRecord) }).GetValue<bool>(uiEdited), "an edited photo opens with its versions folded");
                    UiCheck(Mathf.Approximately(field.Method("VersionHeight", new[] { typeof(PhotoRecord) }).GetValue<float>(uiEdited), SharedPhotoUI.ControlHeight), "the folded bar takes one row");
                    field.Field("galleryVersion").SetValue("ui-fix-edit");
                    phase = 3; Wait(1f); return;
                case 3:
                    Shot("gallery-open.png"); phase = 103; Wait(.5f); return;
                case 103:
                    UiCheck(field.Method("VersionsOpen", new[] { typeof(PhotoRecord) }).GetValue<bool>(uiEdited), "viewing a version other than the one in use opens the band");
                    field.Field("galleryVersion").SetValue(null); field.Field("versionsOpen").SetValue(false);
                    UiCheck(!field.Method("VersionsOpen", new[] { typeof(PhotoRecord) }).GetValue<bool>(uiEdited), "folding returns to the version in use");
                    field.Field("reunionRequest").SetValue(uiReunion);
                    phase = 4; Wait(1f); return;
                case 4:
                    var reunion = Find.WindowStack.WindowOfType<Dialog_Reunion>();
                    UiCheck(reunion != null && Find.MainTabsRoot.OpenTab != AlbumDef, "the reunion window opens and the album closes so the map shows");
                    UiCheck(!reunion.absorbInputAroundWindow && !reunion.preventCameraMotion, "the reunion window leaves the map and camera usable");
                    Shot("reunion-over-map.png"); phase = 104; Wait(.5f); return;
                case 104:
                    Find.WindowStack.WindowOfType<Dialog_Reunion>().Close(false);
                    uiGroup = new Dialog_GroupPhotoSetup(uiStudio) { ExpandedSettings = true, ExpandedExpressions = false };
                    Find.WindowStack.Add(uiGroup);
                    phase = 5; Wait(1f); return;
                case 5:
                    UiCheck(uiGroup.LastContentEnd <= uiGroup.LastContentHeight + 1f, "studio layout section fits its scroll height");
                    Shot("studio-group-auto.png"); phase = 105; Wait(.5f); return;
                case 105:
                    uiGroup.Close(false);
                    Traverse.Create(uiStudio).Field("framed").SetValue(true);
                    uiGroup = new Dialog_GroupPhotoSetup(uiStudio) { ExpandedSettings = true, ExpandedExpressions = false };
                    Find.WindowStack.Add(uiGroup);
                    phase = 6; Wait(1f); return;
                case 6:
                    UiCheck(uiGroup.LastContentEnd <= uiGroup.LastContentHeight + 1f, "studio with a saved framing fits its scroll height");
                    Shot("studio-group-saved.png"); phase = 106; Wait(.5f); return;
                case 106:
                    uiGroup.Close(false);
                    Traverse.Create(uiStudio).Field("framed").SetValue(false);
                    uiGroup = new Dialog_GroupPhotoSetup(uiSpot) { ExpandedSettings = true, ExpandedExpressions = false };
                    Find.WindowStack.Add(uiGroup);
                    phase = 7; Wait(1f); return;
                case 7:
                    UiCheck(uiGroup.LastContentEnd <= uiGroup.LastContentHeight + 1f, "ordinary spot layout section fits its scroll height");
                    Shot("spot-group-normal.png"); phase = 107; Wait(.5f); return;
                case 107:
                    uiGroup.Close(false); uiGroup.ExpandedSettings = false;
                    uiBirthday = new Dialog_BirthdaySetup(uiPeople[0], uiPeople[0].ageTracker.AgeBiologicalYears, null, uiStudio.Position) { ExpandedSettings = false, ExpandedExpressions = false };
                    Find.WindowStack.Add(uiBirthday);
                    phase = 8; Wait(1f); return;
                case 8:
                    Shot("birthday-default.png"); phase = 108; Wait(.5f); return;
                case 108:
                    uiBirthday.ExpandedSettings = true;
                    phase = 81; Wait(1f); return;
                case 81:
                    UiCheck(uiBirthday.LastContentEnd <= uiBirthday.LastContentHeight + 1f, "birthday layout section at a studio fits its scroll height");
                    Shot("birthday-studio.png"); phase = 181; Wait(.5f); return;
                case 181:
                    uiBirthday.Close(false); uiBirthday.ExpandedSettings = false;
                    uiBackground = new Dialog_StudioBackground(uiStudio);
                    Find.WindowStack.Add(uiBackground);
                    AccessTools.Method(typeof(Dialog_StudioBackground), "Load", new[] { typeof(string), typeof(string) })
                        .Invoke(uiBackground, new object[] { Path.Combine(store.FolderPath, "ui-fix.png"), "A title, not a file name" });
                    phase = 9; Wait(1f); return;
                case 9:
                    UiCheck(Traverse.Create(uiBackground).Field("status").GetValue<string>() == "A title, not a file name", "the backdrop window names the album photo by its title");
                    Shot("background-title.png"); phase = 109; Wait(.5f); return;
                case 109:
                    uiBackground.Close(false);
                    uiGroup = new Dialog_GroupPhotoSetup(uiStudio);
                    uiGuide = new Dialog_PhotoGuide("family", uiGroup);
                    UiCheck(uiGuide.Step == 1, "a preparation window's help opens at the step about that window");
                    Find.WindowStack.Add(uiGuide);
                    phase = 10; Wait(1f); return;
                case 10:
                    Shot("guide-context.png"); phase = 110; Wait(.5f); return;
                case 110:
                    uiGuide.Select("family", 2); uiGuide.Close(false);
                    UiCheck(new Dialog_PhotoGuide("family", uiGroup).Step == 2, "reopened from the same window it returns to the step it was on");
                    UiCheck(new Dialog_PhotoGuide("family", new Dialog_GroupPhotoSetup(uiStudio)).Step == 1, "another window starts at its own step");
                    UiCheck(new Dialog_PhotoGuide("family").Step == 0, "the guide opened on its own still starts at the first step");
                    UiCheck(new Dialog_PhotoGuide(null, uiGroup).Step == 0, "no topic means the first page");
                    File.WriteAllText(Path.Combine(dir, "ui-fixes-result.json"), "{\"ok\":true,\"runtime\":" + Json(VersionControl.CurrentVersionString) + ",\"checks\":[" + string.Join(",", checks.Select(Json)) + "]}");
                    active = false; Application.Quit(); return;
            }
        }
    }
}
