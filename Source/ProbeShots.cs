using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    /// <summary>Workshop screenshots (probe profile, extra command line -momentFramesShots=&lt;photo folder&gt;): no checks. A quick-test colony whose
    /// album holds the author's own photos, copied from that folder and titled with the mod's own templates in the profile's language; then the
    /// album, the chronicle, the gallery, a colonist's chronicle tab, a gathering at the photo spot, the settings and the "photo taken" card,
    /// one screenshot each. Time stays paused except for the ticks this code runs itself.</summary>
    public static partial class Probe
    {
        private static string shotsSource;
        private static SettingsWindow ShotsSettingsPage() => HarmonyLib.Traverse.Create(LoadedModManager.GetMod<MomentFramesMod>()).Field("settingsWindow").GetValue<SettingsWindow>();
        private static int shotsPhase;
        private static LordJob_MFGroupPhoto shotsJob;
        private static PhotoRecord shotsGroup, shotsGroupSmall, shotsParty;
        private static string ShotsColony => Ko("Choco");

        // A Korean run names the cast and writes the titles' own words in Korean; every other language keeps the English ones.
        private static readonly Dictionary<string, string> ShotKorean = new Dictionary<string, string>
        {
            { "Choco", "초코" }, { "Rowe", "로우" },
            { "Victoria", "빅토리아" }, { "Celeste", "셀레스테" }, { "Mitsuya", "미츠야" }, { "Moray", "모레이" }, { "Evie", "이비" }, { "Elna", "엘나" },
            { "Julian", "줄리안" }, { "Mushinto", "무신토" }, { "Karl", "칼" }, { "Kelo", "켈로" }, { "Lily", "릴리" }, { "Biscuit", "비스킷" },
            { "Moral guide", "종교 지도자" }, { "moral guide", "종교 지도자" }, { "Research specialist", "연구 전문가" }, { "research specialist", "연구 전문가" },
            { "Feast of unity", "단결의 잔치" }, { "Unity festival", "단결의 축제" }, { "Diabolus", "디아볼루스" }, { "Toar (relic)", "토아르 (유물)" },
            { "heat wave", "폭염" }, { "mushroom jungle", "버섯 정글" }, { "eye forest", "눈알 숲" },
        };

        private static string Ko(string en) => en != null && LanguageDatabase.activeLanguage?.folderName?.StartsWith("Korean") == true && ShotKorean.TryGetValue(en, out var ko) ? ko : en;

        private class ShotPhoto
        {
            public string prefix, kind, reshootOf, detail;
            public Func<string> title;
            public string[] people;   // the first one is the sentence's subject
        }

        private static readonly string[] ShotWomen = { "Victoria", "Celeste", "Mitsuya", "Moray", "Evie", "Elna" };
        private static readonly string[] ShotMen = { "Julian", "Mushinto", "Karl", "Kelo" };

        private static readonly ShotPhoto[] ShotPhotos =
        {
            new ShotPhoto { prefix = "0001_", kind = "role", detail = "moral guide", title = () => "MF_Role".Translate(Ko("Victoria"), Ko("Moral guide")), people = new[] { "Victoria", "Julian", "Celeste" } },
            new ShotPhoto { prefix = "0002_", kind = "role", detail = "research specialist", title = () => "MF_Role".Translate(Ko("Celeste"), Ko("Research specialist")), people = new[] { "Celeste", "Julian", "Victoria" } },
            new ShotPhoto { prefix = "0003_", kind = "group", title = () => "MF_GroupPhoto".Translate(8), people = new[] { "Karl", "Evie", "Elna", "Kelo", "Celeste", "Mitsuya", "Julian", "Victoria" } },
            new ShotPhoto { prefix = "0004_", kind = "recruit", title = () => "MF_Recruit".Translate(Ko("Moray")), people = new[] { "Moray", "Kelo" } },
            new ShotPhoto { prefix = "0006_", kind = "party", title = () => "MF_Celebration".Translate(Ko("Feast of unity"), Ko("Julian")), people = new[] { "Julian", "Celeste", "Victoria", "Mitsuya", "Moray" } },
            new ShotPhoto { prefix = "0007_", kind = "mechboss", title = () => "MF_MechBoss".Translate(Ko("Diabolus")), people = new[] { "Mushinto", "Julian" } },
            new ShotPhoto { prefix = "0008_", kind = "reliquary", title = () => "MF_RelicInstalled".Translate(Ko("Toar (relic)")), people = new[] { "Mitsuya" } },
            new ShotPhoto { prefix = "0009_5500-4-02_group", kind = "group", title = () => "MF_GroupPhoto".Translate(4), people = new[] { "Celeste", "Julian", "Mushinto", "Moray" } },
            new ShotPhoto { prefix = "0009_5500-4-02_relicfound", kind = "relicfound", title = () => "MF_RelicFound".Translate(Ko("Toar (relic)")), people = new[] { "Mushinto", "Moray" } },
            new ShotPhoto { prefix = "0010_", kind = "reshoot", reshootOf = "0001_", detail = "moral guide", title = null, people = new[] { "Victoria" } },
            new ShotPhoto { prefix = "0011_", kind = "reshoot", reshootOf = "0003_", title = null, people = new[] { "Karl", "Evie", "Elna", "Kelo", "Celeste", "Mitsuya", "Julian", "Victoria" } },
        };

        private static Pawn ShotPawn(string name) => map.mapPawns.FreeColonistsSpawned.FirstOrDefault(p => p.Name is NameTriple n && n.First == Ko(name));

        /// <summary>The kind's first sentence template with the right subject, season and colony (Pick takes the first listed person and today's season).</summary>
        private static string ShotDesc(string kind, List<Pawn> pawns, int ticks, Vector2 ll, string detail)
        {
            string key = "MF_Desc_" + kind + "_1";
            if (!key.CanTranslate()) return "";
            string names = string.Join(", ", pawns.Select(p => p.LabelShort));
            string first = pawns.Count > 0 ? pawns[0].LabelShort : "MF_Desc_Someone".Translate().ToString();
            return key.Translate(names, first, GenDate.Season(ticks, ll).LabelCap(), ShotsColony, Ko(detail) ?? "").ToString();
        }

        /// <summary>Ten colonists named after the author's (women's names on women), the colony called by its name, then each listed photo (and its
        /// thumbnail) copied into this game's folder as a record of its original kind, dated as in its file name, with those colonists as its people.</summary>
        private static void ShotsImport()
        {
            var store = PhotoStore.Instance;
            Faction.OfPlayer.Name = ShotsColony;
            if (map.Parent is Settlement st) st.Name = ShotsColony;
            if (!SteamCast())   // the Steam media run brings its own mixed cast when the race mods are loaded
            {
                var women = new Queue<string>(ShotWomen);
                var men = new Queue<string>(ShotMen);
                foreach (var p in map.mapPawns.FreeColonistsSpawned.ToList())
                {
                    var q = p.gender == Gender.Female ? women : men;
                    if (q.Count == 0) continue;
                    string n = q.Dequeue();
                    p.Name = new NameTriple(Ko(n), Ko(n), Ko("Rowe"));
                }
                while (women.Count > 0 || men.Count > 0)
                {
                    bool woman = women.Count > 0;
                    string n = (woman ? women : men).Dequeue();
                    var p = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDefOf.Colonist, Faction.OfPlayer, forceGenerateNewPawn: true,
                        fixedGender: woman ? Gender.Female : Gender.Male, developmentalStages: DevelopmentalStage.Adult));
                    p.Name = new NameTriple(Ko(n), Ko(n), Ko("Rowe"));
                    GenSpawn.Spawn(p, CellFinder.RandomClosewalkCellNear(center, map, 6), map);
                }
            }
            foreach (var r in store.Records.ToList()) store.Remove(r);
            var records = (List<PhotoRecord>)HarmonyLib.AccessTools.Field(typeof(PhotoStore), "records").GetValue(store);
            var files = Directory.GetFiles(shotsSource, "*.png").Select(Path.GetFileName).Where(f => !f.EndsWith(".thumb.png")).OrderBy(f => f).ToList();
            var ll = Find.WorldGrid.LongLatOf(map.Tile);
            int now = Find.TickManager.TicksAbs;
            int y0 = GenDate.Year(now, ll.x), q0 = (int)GenDate.Quadrum(now, ll.x), d0 = GenDate.DayOfQuadrum(now, ll.x);
            var byPrefix = new Dictionary<string, PhotoRecord>();
            Directory.CreateDirectory(store.FolderPath);
            int index = 1;
            foreach (var sp in ShotPhotos)
            {
                string file = files.FirstOrDefault(f => f.StartsWith(sp.prefix));
                if (file == null) throw new InvalidOperationException("no photo starting " + sp.prefix + " in " + shotsSource);
                var slug = file.Split('_')[1].Split('-').Select(int.Parse).ToArray();   // year-quadrum-day
                int ticks = now + (((slug[0] - y0) * 4 + (slug[1] - 1 - q0)) * 15 + (slug[2] - 1 - d0)) * GenDate.TicksPerDay + GenDate.TicksPerHour * 6;
                byte[] png = File.ReadAllBytes(Path.Combine(shotsSource, file));
                string thumbSrc = Path.Combine(shotsSource, Path.GetFileNameWithoutExtension(file) + ".thumb.png");
                byte[] thumb = File.Exists(thumbSrc) ? File.ReadAllBytes(thumbSrc) : null;
                float aspect = PhotoCapture.MakeThumb(png, ref thumb);
                var pawns = sp.people.Select(ShotPawn).Where(p => p != null).ToList();
                var original = sp.reshootOf != null ? byPrefix[sp.reshootOf] : null;
                var rec = new PhotoRecord
                {
                    id = Guid.NewGuid().ToString("N"),
                    kind = sp.kind,
                    reshootOf = original?.id,
                    eventKey = "shots:" + sp.prefix,
                    title = sp.title != null ? sp.title() : "MF_ReshootTitle".Translate(original.title).ToString(),
                    desc = ShotDesc(original?.kind ?? sp.kind, pawns, ticks, ll, sp.detail),
                    ticksAbs = ticks,
                    capturedTicksAbs = ticks,
                    dateLabel = GenDate.DateFullStringAt(ticks, ll),
                    dateSlug = file.Split('_')[1],
                    year = slug[0],
                    spanCells = 19f,
                    aspect = PhotoCapture.ClampAspect(aspect),
                    mapId = map.uniqueID,
                    center = map.Center.ToVector3Shifted()
                };
                rec.SetPeople(pawns);
                rec.file = $"{index++:0000}_{rec.dateSlug}_{rec.kind}_{rec.id.Substring(0, 8)}.png";
                File.WriteAllBytes(Path.Combine(store.FolderPath, rec.file), png);
                if (thumb != null) File.WriteAllBytes(store.ThumbPath(rec), thumb);
                records.Add(rec);
                byPrefix[sp.prefix] = rec;
            }
            store.NotifyChanged();
            shotsGroup = byPrefix["0003_"];
            shotsGroupSmall = byPrefix["0009_5500-4-02_group"];
            shotsParty = byPrefix["0006_"];
            checks.Add("INFO: shots — " + records.Count + " of the author's photos imported; colonists " + string.Join(", ", map.mapPawns.FreeColonistsSpawned.Select(p => p.LabelShort + "(" + p.gender + ")")));
        }

        /// <summary>Only the imported photos (a quick-test "first day" or a gathering that got taken would otherwise slip in) and no message lines.</summary>
        private static void ShotsTidy()
        {
            var store = PhotoStore.Instance;
            foreach (var r in store.Records.Where(r => r.eventKey == null || !r.eventKey.StartsWith("shots:")).ToList()) store.Remove(r);
            Messages.Clear();
        }

        private static void ShotsStep()
        {
            if (Find.TickManager.CurTimeSpeed != TimeSpeed.Paused) Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            if (shotsPhase >= 9000) { if (SteamStep()) Finish(); return; }   // Steam Workshop media (ProbeSteamMedia.cs)
            switch (shotsPhase)
            {
                case 0:
                    {
                        LanguageFilesParse();
                        FxSet(false);
                        var s = MomentFramesMod.Settings;
                        s.msColonyStart = false;
                        Prefs.AdaptiveTrainingEnabled = false;
                        map = Find.CurrentMap;
                        center = map.Center;
                        CloseLog();
                        Find.Storyteller.storytellerComps.Clear();
                        Find.Storyteller.incidentQueue.Clear();
                        map.fogGrid.ClearAllFog();
                        for (int x = center.x - 20; x <= center.x + 20; x++)
                            for (int z = center.z - 12; z <= center.z + 12; z++)
                            {
                                var c = new IntVec3(x, 0, z);
                                if (!c.InBounds(map)) continue;
                                foreach (var t in c.GetThingList(map).ToList())
                                    if (!(t is Pawn) && t.def.destroyable) t.Destroy(DestroyMode.Vanish);
                                map.roofGrid.SetRoof(c, null);
                            }
                        ShotsImport();
                        if (steamMediaMode) SteamImportExtras();
                        Wait(1f);
                        shotsPhase = steamMediaMode ? 9000 : 1;
                        return;
                    }
                case 1:
                    ShotsTidy();
                    AlbumOpen(false, false);
                    Wait(1.5f);
                    shotsPhase = 2;
                    return;
                case 2:
                    CloseLog();
                    Shot("en-album.png");
                    Wait(1f);
                    shotsPhase = 3;
                    return;
                case 3:
                    ShotsTidy();
                    AlbumOpen(true, true);
                    Wait(1.5f);
                    shotsPhase = 4;
                    return;
                case 4:
                    CloseLog();
                    Shot("en-chronicle.png");
                    Wait(1f);
                    shotsPhase = 5;
                    return;
                case 5:
                    ShotsTidy();
                    MainTabWindow_Album.Open(shotsGroup);
                    Wait(1.5f);
                    shotsPhase = 6;
                    return;
                case 6:
                    CloseLog();
                    Shot("en-gallery.png");
                    Wait(1f);
                    shotsPhase = 7;
                    return;
                case 7:
                    {
                        ShotsTidy();
                        Find.MainTabsRoot.EscapeCurrentTab(false);
                        var celeste = ShotPawn("Celeste") ?? map.mapPawns.FreeColonistsSpawned.First();
                        Find.Selector.ClearSelection();
                        Find.Selector.Select(celeste, false);
                        InspectPaneUtility.OpenTab(typeof(ITab_Pawn_Chronicle));
                        Find.CameraDriver.SetRootPosAndSize(celeste.DrawPos, 14f);
                        Wait(1.5f);
                        shotsPhase = 8;
                        return;
                    }
                case 8:
                    CloseLog();
                    Shot("en-pawntab.png");
                    Wait(1f);
                    shotsPhase = 9;
                    return;
                case 9:
                    {
                        Find.MainTabsRoot.EscapeCurrentTab(false);
                        Find.Selector.ClearSelection();
                        foreach (var l in map.lordManager.lords.ToList()) map.lordManager.RemoveLord(l);
                        foreach (var p in map.mapPawns.FreeColonistsSpawned)
                        {
                            if (p.needs?.rest != null) p.needs.rest.CurLevel = 1f;
                            if (p.needs?.food != null) p.needs.food.CurLevel = 1f;
                            if (!p.Awake()) RestUtility.WakeUp(p);
                            if (p.Drafted) p.drafter.Drafted = false;
                        }
                        var cell = new IntVec3(center.x, 0, center.z - 5);
                        photoSpot = (Building_PhotoSpot)ThingMaker.MakeThing(ThingDef.Named("MF_PhotoSpot"));
                        photoSpot.SetFaction(Faction.OfPlayer);
                        GenSpawn.Spawn(photoSpot, cell, map);
                        GroupPhoto.ForgetEligible();
                        var report = GroupPhoto.CanStart(map, cell, out var pawns);
                        if (!report.Accepted) throw new InvalidOperationException("shots: group photo cannot start: " + report.Reason);
                        var lord = GroupPhoto.Start(map, cell, pawns);
                        if (lord == null) throw new InvalidOperationException("shots: group photo did not start");
                        shotsJob = (LordJob_MFGroupPhoto)lord.LordJob;
                        Find.CameraDriver.SetRootPosAndSize(cell.ToVector3Shifted() + new Vector3(0f, 0f, 3f), 16f);
                        shotsPhase = 10;
                        return;
                    }
                case 10:
                    {
                        // Some already in place, the last few still walking (the inspect pane then names who is coming).
                        var job = GroupPhoto.ActiveJob(map, photoSpot.Position);
                        if (job == null) throw new InvalidOperationException("shots: the line-up ended: " + shotsJob?.Result + ", danger " + map.dangerWatcher.DangerRating + ", hostiles " + string.Join(",", map.mapPawns.AllPawnsSpawned.Where(p => p.HostileTo(Faction.OfPlayer) && !p.Downed).Select(p => p.def.defName).Take(6)));
                        int owned = job.lord?.ownedPawns.Count ?? 0;
                        if (job.InPlace < Math.Max(2, owned - 3))
                        {
                            for (int i = 0; i < 10; i++) Find.TickManager.DoSingleTick();
                            SettleDraw();
                            return;
                        }
                        ShotsTidy();
                        Find.Selector.ClearSelection();
                        Find.Selector.Select(photoSpot, false);
                        Find.CameraDriver.SetRootPosAndSize(photoSpot.Position.ToVector3Shifted() + new Vector3(0f, 0f, 2.5f), 9f);   // close enough to read the line-up
                        Wait(1f);
                        shotsPhase = 11;
                        return;
                    }
                case 11:
                    CloseLog();
                    Shot("en-spot.png");
                    Wait(1f);
                    shotsPhase = 12;
                    return;
                case 12:
                    GroupPhoto.ActiveJob(map, photoSpot.Position)?.Cancel();
                    Find.Selector.ClearSelection();
                    ShotsTidy();
                    {
                        var s = MomentFramesMod.Settings;   // the page as a new player sees it: effects at their defaults (phase 0 turned them off)
                        s.fxFlash = s.fxShutter = s.fxPopup = s.fxFreeze = s.fxCamera = true;
                    }
                    Find.WindowStack.Add(new Dialog_ModSettings(LoadedModManager.GetMod<MomentFramesMod>()));
                    Wait(1.5f);
                    shotsPhase = 13;
                    return;
                case 13:
                    CloseLog();
                    Shot("en-settings.png");
                    Wait(1f);
                    shotsPhase = 130;
                    return;
                // Shot() grabs the frame at its end, so each page change waits for a later step than the shot before it.
                case 130:
                    // the disk-usage card under the photo folder, and the size line under the resolution (2026-11-01)
                    MomentFramesMod.Settings.pxPerCell = PhotoCapture.DefaultPxPerCell;
                    ShotsSettingsPage().SetExpanded(true);
                    ShotsSettingsPage().SelectCategory("storage");
                    ShotsSettingsPage().SetScroll(150f);   // the whole usage card, buttons included
                    Wait(3f);   // the folder count runs on a worker
                    shotsPhase = 131;
                    return;
                case 131:
                    CloseLog();
                    Shot("settings-storage.png");
                    Wait(1f);
                    shotsPhase = 1311;
                    return;
                case 1311:
                    ShotsSettingsPage().SelectCategory("photo");
                    ShotsSettingsPage().SetScroll(200f);   // the resolution card below the framing span
                    Wait(1.5f);
                    shotsPhase = 132;
                    return;
                case 132:
                    CloseLog();
                    Shot("settings-resolution.png");
                    Wait(1f);
                    shotsPhase = 14;
                    return;
                case 14:
                    {
                        Find.WindowStack.TryRemove(typeof(Dialog_ModSettings));
                        ShotsTidy();
                        var s = MomentFramesMod.Settings;
                        s.fxPopup = true;
                        s.fxFlash = s.fxShutter = s.fxFreeze = s.fxCamera = false;
                        PhotoFx.Play(shotsGroupSmall);
                        Wait(0.6f);
                        shotsPhase = 15;
                        return;
                    }
                case 15:
                    CloseLog();
                    Shot("en-card.png");
                    Wait(1.5f);
                    shotsPhase = 16;
                    return;
                case 16:
                    // Photo editing: the editor on the party photo, cropped and in sepia, as the player would leave it before saving.
                    Find.WindowStack.TryRemove(typeof(Window_PhotoPop), false);
                    ShotsTidy();
                    Find.WindowStack.Add(new Dialog_PhotoEdit(shotsParty));
                    Wait(1f);
                    shotsPhase = 17;
                    return;
                case 17:
                    {
                        var editor = Find.WindowStack.WindowOfType<Dialog_PhotoEdit>();
                        if (editor == null) throw new InvalidOperationException("shots: the editor did not open");
                        var t = HarmonyLib.Traverse.Create(editor);
                        t.Field("crop").SetValue(new Rect(0.12f, 0.1f, 0.72f, 0.78f));
                        t.Method("SetFilter", PhotoFilters.Sepia).GetValue();
                        Wait(1f);
                        shotsPhase = 18;
                        return;
                    }
                case 18:
                    CloseLog();
                    Shot("en-editor.png");
                    Wait(1f);
                    shotsPhase = 19;
                    return;
                case 19:
                    {
                        // Two saved edits of that photo; the gallery on the first shows the strip Original / Edit 1 / Edit 2.
                        Find.WindowStack.TryRemove(typeof(Dialog_PhotoEdit), false);
                        var store = PhotoStore.Instance;
                        var sepia = store.SaveEdit(shotsParty, new Rect(0.12f, 0.1f, 0.72f, 0.78f), PhotoFilters.Sepia);
                        store.SaveEdit(shotsParty, new Rect(0f, 0f, 1f, 1f), PhotoFilters.Mono);
                        if (sepia == null) throw new InvalidOperationException("shots: the edit was not saved");
                        store.SetShown(shotsParty, sepia.id);
                        ShotsTidy();
                        MainTabWindow_Album.ShowVersion(shotsParty, sepia.id);
                        Wait(1.5f);
                        shotsPhase = 20;
                        return;
                    }
                case 20:
                    CloseLog();
                    Shot("en-edit-gallery.png");
                    Wait(1f);
                    shotsPhase = 21;
                    return;
                case 21:
                    if (!ShowcaseStep()) return;
                    foreach (var n in new[] { "en-album.png", "en-chronicle.png", "en-gallery.png", "en-pawntab.png", "en-spot.png", "en-settings.png", "en-card.png", "en-editor.png", "en-edit-gallery.png" }) Require(n);
                    Finish();
                    return;
            }
        }
    }
}
