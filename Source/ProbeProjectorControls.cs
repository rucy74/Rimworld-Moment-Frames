using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    public static partial class Probe
    {
        private static bool projectorControlsMode;
        private static Building_PhotoProjector controlsProjector;
        private static Dialog_ProjectionSize controlsDialog;
        private static Vector3 controlsSample;
        private static Color controlsBehind;
        private static string controlsOriginalHash;
        private const string ControlsSave = "mf-projector-controls", ControlsLegacy = "mf-projector-legacy";
        private static void ControlsAssert(bool value, string label)
        {
            if (!value) throw new InvalidOperationException("projector controls: " + label);
            checks.Add("PASS: " + label);
        }
        private static void ProjectorControlsStep()
        {
            var store = PhotoStore.Instance;
            File.WriteAllText(Path.Combine(dir, "phase.txt"), phase.ToString());
            switch (phase)
            {
                case 0:
                    deadline = Time.realtimeSinceStartup + 180f;
                    map = Find.CurrentMap; center = map.Center; Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    Find.Storyteller.storytellerComps.Clear(); Find.Storyteller.incidentQueue.Clear(); ClearHostiles(map); CloseLog();
                    Prefs.DevMode = false; Prefs.AdaptiveTrainingEnabled = false; Find.PlaySettings.showLearningHelper = false;
                    var settings = MomentFramesMod.Settings;
                    settings.autoCaptureEnabled = settings.buildingPhotos = settings.memoryNight = settings.yearLetter = false;
                    settings.settlementPeriod = 0; settings.albumTextSize = 115; settings.albumOpacity = 1;
                    foreach (var c in CellRect.CenteredOn(center, 20))
                    {
                        foreach (var t in c.GetThingList(map).ToList()) if (!(t is Pawn) && t.def.destroyable) t.Destroy();
                        map.fogGrid.Unfog(c); map.roofGrid.SetRoof(c, null);
                        map.terrainGrid.SetTerrain(c, DefDatabase<TerrainDef>.GetNamed("WoodPlankFloor"));
                    }
                    controlsProjector = (Building_PhotoProjector)ThingMaker.MakeThing(ThingDef.Named("MF_Projector"), ThingDefOf.WoodLog);
                    controlsProjector.SetFaction(Faction.OfPlayer); GenSpawn.Spawn(controlsProjector, center + new IntVec3(0, 0, -8), map, Rot4.North);
                    ControlsAssert(controlsProjector.ProjectionDepth == ProjectionGeometry.BehindDepth, "new projector defaults behind objects");
                    var texture = new Texture2D(128, 128, TextureFormat.RGB24, false);
                    var color = new Color32(22, 178, 220, 255); texture.SetPixels32(Enumerable.Repeat(color, 128 * 128).ToArray()); texture.Apply(false);
                    Directory.CreateDirectory(store.FolderPath); string file = "projector-control.png";
                    File.WriteAllBytes(Path.Combine(store.FolderPath, file), texture.EncodeToPNG()); UnityEngine.Object.Destroy(texture);
                    var record = new PhotoRecord { id = "projector-controls-card", file = file, kind = "manual", title = "Projection test card", aspect = 1.5f,
                        mapId = map.uniqueID, ticksAbs = Find.TickManager.TicksAbs, capturedTicksAbs = Find.TickManager.TicksAbs, year = 5500, dateSlug = "5500-1-01", dateLabel = "Fixture" };
                    Traverse.Create(store).Field("records").GetValue<List<PhotoRecord>>().Add(record); store.NotifyChanged();
                    controlsOriginalHash = FollowupHash(store.FilePath(record)); controlsProjector.SetSelectedFilm(new[] { record.id });
                    CheckProjectorFilmLabels(record);
                    ControlsAssert(controlsProjector.SetCustomProjectionSize(12.5f, 8f), "custom dimensions accepted inside the map");
                    controlsProjector.ImageLayout(out var off, out float width, out float height);
                    ControlsAssert(Math.Abs(width - 12f) < .001f && Math.Abs(height - 8f) < .001f, "3:2 photo fits 12.5x8 box without stretching");
                    controlsSample = controlsProjector.DrawPos + off;
                    IntVec3 obstacle = IntVec3.FromVector3(controlsSample);
                    controlsSample = obstacle.ToVector3Shifted();
                    var wall = ThingMaker.MakeThing(ThingDefOf.Wall, ThingDefOf.WoodLog); wall.SetFaction(Faction.OfPlayer); GenSpawn.Spawn(wall, obstacle, map);
                    var item = ThingMaker.MakeThing(ThingDefOf.Gold); item.stackCount = 40; GenSpawn.Spawn(item, obstacle + IntVec3.East * 2, map);
                    foreach (var pawn in map.mapPawns.FreeColonistsSpawned.ToList())
                    { FixReady(pawn); pawn.jobs?.StopAll(); pawn.Position = center + new IntVec3(-10, 0, -9); pawn.Notify_Teleported(); }
                    map.mapDrawer.RegenerateEverythingNow(); Find.CameraDriver.SetRootPosAndSize(controlsSample, 12f);
                    Find.Selector.Select(controlsProjector); Wait(1.5f); phase = 1; return;
                case 1:
                    CloseLog(); Wait(.3f); phase = 11; return;
                case 11:
                    File.WriteAllText(Path.Combine(dir, "runtime-log.txt"), string.Join("\n", Log.Messages.Select(message => message.text)));
                    Shot("projection-behind.png");
                    FilmCommand().action(); Wait(.6f); phase = 15; return;
                case 15:
                    Shot("film-menu.png");
                    Find.WindowStack.WindowOfType<FloatMenu>()?.Close(); Wait(.4f); phase = 2; return;
                case 2:
                    controlsBehind = ControlsPixel("projection-behind.png");
                    var depth = controlsProjector.GetGizmos().OfType<Command_Action>().Single(c => c.defaultLabel == "MF_ProjectionDepth".Translate(controlsProjector.DepthLabel));
                    depth.action(); var depthMenu = Find.WindowStack.WindowOfType<FloatMenu>();
                    var frontChoice = Traverse.Create(depthMenu).Field("options").GetValue<List<FloatMenuOption>>().Single(o => o.Label == "MF_ProjectionFront".Translate());
                    depthMenu.Close(); frontChoice.action();
                    ControlsAssert(controlsProjector.ProjectionDepth == ProjectionGeometry.FrontDepth, "actual draw-order gizmo menu selects front");
                    Wait(.6f); phase = 3; return;
                case 3:
                    CloseLog(); Wait(.3f); phase = 12; return;
                case 12:
                    File.WriteAllText(Path.Combine(dir, "runtime-log.txt"), string.Join("\n", Log.Messages.Select(message => message.text)));
                    Shot("projection-front.png"); Wait(.6f); phase = 4; return;
                case 4:
                    var front = ControlsPixel("projection-front.png");
                    checks.Add("INFO: obstacle sample behind=" + controlsBehind + "; front=" + front);
                    ControlsAssert(front.b > front.r * 2f && front.g > front.r * 2f, "front photo visibly covers the real wooden wall with the cyan test card");
                    ControlsAssert(controlsBehind.r > controlsBehind.b && Vector3.Distance(new Vector3(front.r, front.g, front.b), new Vector3(controlsBehind.r, controlsBehind.g, controlsBehind.b)) > .15f,
                        "behind photo leaves the actual wooden wall visible at the same screen location");
                    var size = controlsProjector.GetGizmos().OfType<Command_Action>().Single(c => c.defaultLabel == "MF_ProjectionSize".Translate(controlsProjector.ProjectionSizeLabel));
                    size.action(); var menu = Find.WindowStack.WindowOfType<FloatMenu>();
                    var custom = Traverse.Create(menu).Field("options").GetValue<List<FloatMenuOption>>().Single(o => o.Label == "MF_ProjectionCustomInput".Translate());
                    menu.Close(); custom.action(); controlsDialog = Find.WindowStack.WindowOfType<Dialog_ProjectionSize>();
                    ControlsAssert(controlsDialog != null && !controlsDialog.forcePause, "actual gizmo menu action opens non-pausing numeric dialog");
                    Wait(.6f); phase = 5; return;
                case 5:
                    CloseLog();
                    Shot("projection-size-dialog.png"); Wait(.6f); phase = 13; return;
                case 13:
                    MomentFramesMod.Settings.albumTextSize = 130; Wait(.4f); phase = 14; return;
                case 14:
                    Shot("projection-size-dialog-130.png"); Wait(.4f); phase = 6; return;
                case 6:
                    foreach (var invalid in new[] { "NaN", "Infinity", "-1", "0", "41", "1,5", "" })
                    {
                        Traverse.Create(controlsDialog).Field("widthText").SetValue(invalid);
                        ControlsAssert(!controlsDialog.TryApply() && controlsProjector.CustomProjectionWidth == 12.5f, "invalid input rejected without altering projector: " + invalid);
                    }
                    Traverse.Create(controlsDialog).Field("widthText").SetValue("14.25"); Traverse.Create(controlsDialog).Field("heightText").SetValue("7.5");
                    ControlsAssert(controlsDialog.TryApply() && controlsProjector.CustomProjectionWidth == 14.25f && controlsProjector.CustomProjectionHeight == 7.5f, "decimal input applies through the real dialog");
                    ControlsAssert(MemoryNight.ScreenRect(controlsProjector) == controlsProjector.ProjectionRect, "memory-night audience geometry uses the resized projection rectangle");
                    foreach (var rotation in new[] { Rot4.North, Rot4.East, Rot4.South, Rot4.West })
                    {
                        var rect = Building_PhotoProjector.ProjectionRectAt(controlsProjector.Position, rotation, 1.5f, 0, 14.25f, 7.5f);
                        var cells = Building_PhotoProjector.BeamCells(controlsProjector.Position, rotation, map, 0, 1.5f, 14.25f, 7.5f).ToList();
                        ControlsAssert(cells.Count == rect.Area && cells.All(rect.Contains), "preview and actual rectangle agree for rotation " + rotation);
                    }
                    var edge = (Building_PhotoProjector)ThingMaker.MakeThing(ThingDef.Named("MF_Projector"), ThingDefOf.WoodLog);
                    edge.SetFaction(Faction.OfPlayer); GenSpawn.Spawn(edge, new IntVec3(1, 0, 1), map, Rot4.South);
                    ControlsAssert(!edge.SetCustomProjectionSize(40f, 40f) && !edge.HasCustomProjection, "off-map custom input rejected without committing"); edge.Destroy();
                    Find.WindowStack.TryRemove(typeof(FloatMenu), false); Find.Selector.ClearSelection();
                    GameDataSaveLoader.SaveGame(ControlsSave); Wait(.5f); phase = 7; return;
                case 7:
                    GameDataSaveLoader.LoadGame(ControlsSave); phase = 8; Wait(.5f); return;
                case 8:
                    map = Find.CurrentMap; controlsProjector = map.listerBuildings.AllBuildingsColonistOfClass<Building_PhotoProjector>().Single();
                    ControlsAssert(controlsProjector.CustomProjectionWidth == 14.25f && controlsProjector.CustomProjectionHeight == 7.5f && controlsProjector.ProjectionDepth == ProjectionGeometry.FrontDepth,
                        "actual save/load preserves custom width, height and front order");
                    ControlsAssert(FollowupHash(store.FilePath(store.Get("projector-controls-card"))) == controlsOriginalHash, "resizing and draw order never alter the original PNG");
                    controlsProjector.SetProjectionSize(2); ControlsAssert(!controlsProjector.HasCustomProjection && controlsProjector.ProjectionSize == 2, "existing preset clears custom override");
                    GameDataSaveLoader.SaveGame(ControlsLegacy); phase = 9; Wait(.5f); return;
                case 9:
                    string legacyPath = GenFilePaths.FilePathForSavedGame(ControlsLegacy);
                    var document = XDocument.Load(legacyPath);
                    var saved = document.Descendants("thing").Single(t => t.Element("def")?.Value == "MF_Projector");
                    foreach (string key in new[] { "mfProjectionWidth", "mfProjectionHeight", "mfProjectionDepth" }) saved.Element(key)?.Remove();
                    document.Save(legacyPath); GameDataSaveLoader.LoadGame(ControlsLegacy); phase = 10; Wait(.5f); return;
                case 10:
                    map = Find.CurrentMap; controlsProjector = map.listerBuildings.AllBuildingsColonistOfClass<Building_PhotoProjector>().Single();
                    ControlsAssert(!controlsProjector.HasCustomProjection && controlsProjector.ProjectionSize == 2 && controlsProjector.ProjectionDepth == ProjectionGeometry.LegacyDepth,
                        "save without new keys retains the old preset and legacy drawing order");
                    File.WriteAllText(Path.Combine(dir, "runtime-log.txt"), string.Join("\n", Log.Messages.Select(message => message.text)));
                    File.WriteAllText(Path.Combine(dir, "controls-result.json"), "{\"ok\":true,\"runtime\":" + Json(VersionControl.CurrentVersionString) + ",\"checks\":[" + string.Join(",", checks.Select(Json))
                        + "],\"limits\":\"Disposable Core+Harmony+MF fixture; cyan photo and real wall/item. Gizmo actions and fields invoked by probe, not physical input. No user save, installation, full-modpack or natural-event claim.\"}");
                    active = false; Application.Quit(); return;
            }
        }
        private static Command_Action FilmCommand() => controlsProjector.GetGizmos().OfType<Command_Action>()
            .Single(c => c.defaultLabel == "MF_ProjectorFilmButton".Translate(controlsProjector.DisplayedFilmLabel));

        private static void CheckProjectorFilmLabels(PhotoRecord record)
        {
            ControlsAssert(FilmCommand().defaultLabel.Contains("MF_FavoriteSelectedFilm".Translate()), "hand-picked film appears on the gizmo");
            controlsProjector.SetFilm("", "", "");
            var opts = controlsProjector.FilmOptions();
            ControlsAssert(FilmCommand().defaultLabel.Contains("MF_FilmAll".Translate()) &&
                opts.Any(o => o.Label == "MF_ProjectorFilmChosen".Translate("MF_FilmAll".Translate())), "all photos visible on gizmo and marked in menu");
            opts.Single(o => o.Label == "MF_FilmYear".Translate(5500)).action();
            ControlsAssert(FilmCommand().defaultLabel.Contains("5500") &&
                controlsProjector.FilmOptions().Any(o => o.Label == "MF_ProjectorFilmChosen".Translate("MF_FilmYear".Translate(5500))), "year selection action updates gizmo and selected option");
            controlsProjector.SetFilm("manual", "", "");
            ControlsAssert(FilmCommand().defaultLabel.Contains(MainTabWindow_Album.KindLabel("manual")), "photo kind appears on gizmo");
            controlsProjector.SetFilm("", "film-person", "Alexandra");
            ControlsAssert(FilmCommand().defaultDesc.Contains("Alexandra"), "person film retains its full name in tooltip");
            Traverse.Create(controlsProjector).Field("fixedPhotoId").SetValue(record.id);
            ControlsAssert(controlsProjector.DisplayedFilmLabel == "MF_ProjectionSingle".Translate() && FilmCommand().defaultDesc.Contains(record.title),
                "fixed photo shows single-photo mode and full title instead of inactive film");
            controlsProjector.SetFilm("", "", "");
            ControlsAssert(controlsProjector.GetGizmos().OfType<Command_Action>().Any(c => c.defaultLabel == "MF_ProjectorOpenAlbum".Translate()),
                "projector's album action has an explicit label");
        }
        private static Color ControlsPixel(string name)
        {
            Require(name); var image = new Texture2D(2, 2, TextureFormat.RGB24, false);
            try
            {
                image.LoadImage(File.ReadAllBytes(Path.Combine(dir, name)));
                Vector3 screen = Find.Camera.WorldToScreenPoint(controlsSample);
                int x = Mathf.Clamp(Mathf.RoundToInt(screen.x), 1, image.width - 2), y = Mathf.Clamp(Mathf.RoundToInt(screen.y), 1, image.height - 2);
                checks.Add("INFO: screenshot sample " + x + "," + y + " in " + image.width + "x" + image.height);
                Color sum = Color.clear;
                for (int ix = -1; ix <= 1; ix++) for (int iy = -1; iy <= 1; iy++) sum += image.GetPixel(x + ix, y + iy);
                return sum / 9f;
            }
            finally { UnityEngine.Object.Destroy(image); }
        }
    }
}
