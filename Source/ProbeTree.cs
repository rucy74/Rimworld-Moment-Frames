using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    public static partial class Probe
    {
        private static bool treeMode, treeUIRan;
        private static Building_PhotoTree treeFixture;
        private static Dialog_PhotoTree treeDialog;
        private static int treeSerial;
        private static string[] treeImages, savedTreeSlots;
        private static string savedTreeId;
        private static void TreeCheck(bool ok, string message)
        {
            if (!ok) throw new InvalidOperationException("memory tree: " + message);
            checks.Add("PASS: memory tree — " + message);
        }
        private static string[] TreeFacts() => treeFixture.Selection.slots.Select(s => (s.photoId ?? "") + ":" + s.pinned).ToArray();
        private static PhotoRecord AddTreePhoto(string kind = "group")
        {
            var store = PhotoStore.Instance; int i = treeSerial++;
            var rec = new PhotoRecord { id = "tree-fixture-" + i.ToString("D3"), kind = kind, ticksAbs = testRec.ticksAbs + i + 1,
                title = "MF_Tree_Slot".Translate(i + 1), desc = "Disposable memory-tree fixture", dateLabel = testRec.dateLabel,
                file = "tree-fixture-" + i.ToString("D3") + ".png", aspect = PhotoCapture.DefaultAspect };
            rec.SetPeople(new[] { p1, p2 }); File.Copy(treeImages[i % treeImages.Length], store.FilePath(rec), true);
            Traverse.Create(store).Field("records").GetValue<List<PhotoRecord>>().Add(rec); store.NotifyChanged(); return rec;
        }
        private static void AddTreePhotos(int count) { for (int i = 0; i < count; i++) AddTreePhoto(i % 2 == 0 ? "group" : "wedding"); treeFixture.RefreshSelection(); }
        private static void TreeStep()
        {
            switch (phase)
            {
                case 0:
                    FxSet(false); Setup();
                    treeFixture = (Building_PhotoTree)ThingMaker.MakeThing(ThingDef.Named("MF_MemoryTree"), ThingDefOf.WoodLog);
                    treeFixture.SetFaction(Faction.OfPlayer); treeFixture.GetComp<CompQuality>().SetQuality(QualityCategory.Excellent, ArtGenerationContext.Colony);
                    GenSpawn.Spawn(treeFixture, center + new IntVec3(-6, 0, -3), map);
                    for (int x = -9; x <= -3; x++) for (int z = -6; z <= 0; z++) map.terrainGrid.SetTerrain(center + new IntVec3(x, 0, z), TerrainDef.Named("WoodPlankFloor"));
                    Find.CameraDriver.SetRootPosAndSize(treeFixture.DrawPos, 5f);
                    testRec = PhotoStore.Instance.Request(map, center, "group", "tree-real-capture", "Memory-tree live capture", "", new[] { p1, p2 });
                    TreeCheck(testRec != null, "new photo registered through real capture queue"); Wait(.5f); phase = 1; return;
                case 1:
                    if (!testRec.HasFile) { if (!PhotoStore.Instance.Records.Contains(testRec)) throw new InvalidOperationException("tree fixture capture was dropped"); return; }
                    treeImages = Directory.Exists(Path.Combine(dir,"fixtures")) ? Directory.GetFiles(Path.Combine(dir,"fixtures"),"*.png") : Array.Empty<string>();
                    if (treeImages.Length == 0) treeImages = new[] { PhotoStore.Instance.FilePath(testRec) };
                    AddTreePhotos(4); CloseLog(); Find.CameraDriver.SetRootPosAndSize(treeFixture.DrawPos,5f); Wait(.8f); phase = 2; return;
                case 2:
                    TreeCheck(treeFixture.drawnPhotoCount == 5 && treeFixture.drawnStringCount == 5 && treeFixture.drawnCardCount == 5, "actual DrawAt renders five photos, strings and cards");
                    CloseLog(); Shot("tree-5.png"); Wait(.5f); phase = 20; return;
                case 20:
                    Require("tree-5.png"); AddTreePhotos(10); Wait(.8f); phase = 3; return;
                case 3:
                    Require("tree-5.png"); TreeCheck(treeFixture.drawnPhotoCount == 15,"actual DrawAt renders fifteen photos");
                    CloseLog(); Shot("tree-15.png"); Wait(.5f); phase = 30; return;
                case 30:
                    Require("tree-15.png"); AddTreePhotos(20); Wait(.8f); phase = 4; return;
                case 4:
                    Require("tree-15.png"); TreeCheck(treeFixture.drawnPhotoCount == 35 && treeFixture.drawnStringCount == 35 && treeFixture.drawnCardCount == 35 && treeFixture.drawnPlaceholderCount == 0,
                        "all 35 actual map photos, strings and cards render without placeholders");
                    TreeScaleMetrics(); CloseLog(); Shot("tree-35.png"); Wait(.5f); phase = 40; return;
                case 40:
                    Require("tree-35.png"); TreeControls();
                    MomentFramesMod.Settings.albumTheme = "graphite"; MomentFramesMod.Settings.albumFont = "pencil"; MomentFramesMod.Settings.albumTextSize = 130;
                    treeFixture.GetGizmos().OfType<Command_Action>().First(g => g.defaultLabel == "MF_TreeManage".Translate().ToString()).action();
                    treeDialog = Find.WindowStack.Windows.OfType<Dialog_PhotoTree>().Single(); Wait(.8f); phase = 5; return;
                case 5:
                    Require("tree-35.png"); CloseLog(); Shot("tree-management.png"); Wait(.5f); phase = 50; return;
                case 50:
                    Require("tree-management.png");
                    TreeCheck(treeDialog.windowRect.xMin >= 0 && treeDialog.windowRect.yMin >= 0 && treeDialog.windowRect.xMax <= UI.screenWidth && treeDialog.windowRect.yMax <= UI.screenHeight,
                        "native management window fits screen at 130 percent font size");
                    treeDialog.SelectSlot(8); treeDialog.ChooseSelected(); var picker = Find.WindowStack.Windows.OfType<Dialog_ChoosePhoto>().Single();
                    Traverse.Create(picker).Field("onPick").GetValue<Action<string>>()(testRec.id); picker.Close();
                    TreeCheck(treeFixture.SlotId(8) == testRec.id && treeFixture.Selection.slots[8].pinned,"real picker callback places and pins the chosen photo");
                    treeDialog.ToggleSelectedPin(); TreeCheck(!treeFixture.Selection.slots[8].pinned,"management action unpins a photo"); treeDialog.ToggleSelectedPin();
                    int count = PhotoStore.Instance.Records.Count; treeDialog.RemoveSelected();
                    TreeCheck(treeFixture.SlotId(8) == "" && PhotoStore.Instance.Records.Count == count && File.Exists(PhotoStore.Instance.FilePath(testRec)),"take-down keeps album record and original file");
                    treeFixture.SetPhoto(8,testRec.id); treeDialog.OpenSelected(); TreeCheck(Find.MainTabsRoot.OpenTab?.defName == "MF_Album","view-larger opens real album");
                    Find.MainTabsRoot.EscapeCurrentTab(false); treeDialog.Close(); CloseLog(); Find.Selector.ClearSelection(); Find.Selector.Select(treeFixture,false);
                    Find.WindowStack.Add(new Window_TreeUIProbe()); Wait(.5f); phase = 6; return;
                case 6:
                    Require("tree-management.png"); TreeCheck(treeUIRan,"native engine OnGUI click probe executed");
                    Find.WindowStack.TryRemove(typeof(Window_TreeUIProbe),false); Find.MainTabsRoot.EscapeCurrentTab(false); Find.Selector.ClearSelection();
                    AddTreePhoto(); treeFixture.RefreshSelection(); TreeCacheAndSave(); Wait(.8f); phase = 7; return;
                case 7:
                    GameDataSaveLoader.LoadGame("mftree"); Wait(1.5f); phase = 8; return;
                case 8:
                    map = Find.CurrentMap; Find.TickManager.Pause(); treeFixture = map.listerThings.AllThings.OfType<Building_PhotoTree>().Single(t => t.GetUniqueLoadID() == savedTreeId);
                    treeFixture.RefreshSelection(); TreeCheck(TreeFacts().SequenceEqual(savedTreeSlots) && !treeFixture.Selection.autoFill && treeFixture.Selection.allowedKinds.SequenceEqual(new[] { "group","wedding" }),
                        "real game save/load preserves exact slots, pins, kind filter and automatic-off setting");
                    testRec = PhotoStore.Instance.Records.First(r => r.id == testRec.id); var next = AddTreePhoto(); treeFixture.RefreshSelection();
                    TreeCheck(TreeFacts().SequenceEqual(savedTreeSlots),"new photos preserve loaded slots while automatic filling is off");
                    treeFixture.Configure(true,new[] { "group","wedding" });
                    TreeCheck(treeFixture.Selection.slots.Any(s => s.photoId == next.id) && treeFixture.SlotId(8) == testRec.id && treeFixture.Selection.slots[8].pinned,
                        "resuming catches up while preserving manually pinned branches");
                    GameDataSaveLoader.LoadGame("mftree-legacy"); Wait(1.5f); phase = 9; return;
                case 9:
                    map = Find.CurrentMap; Find.TickManager.Pause(); treeFixture = map.listerThings.AllThings.OfType<Building_PhotoTree>().Single(); treeFixture.RefreshSelection();
                    TreeCheck(treeFixture.Selection.slots.Count == 35 && treeFixture.Selection.autoFill && treeFixture.Selection.allowedKinds.Count == 0,"game XML without tree fields initializes usable defaults");
                    File.WriteAllText(Path.Combine(dir,"tree-result.json"),"{\"ok\":true,\"checks\":[" + string.Join(",",checks.Select(Json))
                        + "],\"limits\":\"Core/Harmony disposable fixtures; 35 records include copied probe photographs. Synthetic native clicks, not physical user input. Natural long-term play, player saves and full mod packs untested.\"}");
                    active = false; Application.Quit(); return;
            }
        }
        private static void TreeControls()
        {
            var store = PhotoStore.Instance;
            TreeCheck(treeFixture.FixedDisplay && treeFixture.Displayed.Count() == 35 && Reminisce.DisplayedFrames(map).Contains(treeFixture),"existing reminiscence discovers all tree photos through IPhotoDisplay");
            int cells = Mathf.RoundToInt(MemoryTreeLayout.StructureCells);
            TreeCheck(treeFixture.IsFine && treeFixture.def.size == new IntVec2(cells,cells) && !treeFixture.def.rotatable
                && treeFixture.def.graphicData.drawSize == new Vector2(cells,cells),cells + " by " + cells + " furniture matches renderer scale, quality and fixed orientation");
            var before = treeFixture.Selection.slots.Select(s => s.photoId).ToArray(); treeFixture.SetPinned(0,true);
            var oldest = store.Records.Where(r => before.Contains(r.id) && r.id != before[0]).OrderBy(r => r.ticksAbs).First(); int oldSlot = Array.IndexOf(before,oldest.id);
            var newer = AddTreePhoto(); treeFixture.RefreshSelection();
            TreeCheck(treeFixture.SlotId(0) == before[0] && treeFixture.SlotId(oldSlot) == newer.id && Enumerable.Range(0,35).Where(i => i != oldSlot).All(i => treeFixture.SlotId(i) == before[i]),
                "36th photo replaces only oldest unpinned slot and preserves other 34 positions");
            treeFixture.Configure(true,new[] { "wedding" }); TreeCheck(treeFixture.SlotId(0) == before[0] && treeFixture.Displayed.All(r => r.id == before[0] || r.kind == "wedding"),"kind filtering preserves out-of-filter pinned photo");
            treeFixture.Configure(true,Array.Empty<string>()); treeFixture.RefreshSelection();
            TreeCheck(treeFixture.BoundPhotoCount == 35 && Enumerable.Range(0,35).All(i => treeFixture.BoundPathAt(i) == store.ThumbPath(treeFixture.RecordAt(i)) && treeFixture.BoundTextureAt(i).height <= 256),
                "35 map bindings use bounded thumbnails rather than full photographs");
            int syncs = treeFixture.SelectionSyncCount; for(int i=0;i<250;i++)treeFixture.EnsureSelection(); TreeCheck(treeFixture.SelectionSyncCount == syncs,"unchanged UI checks do not rescan album");
        }
        private static void TreeScaleMetrics()
        {
            var cards = Enumerable.Range(0,MemoryTreeLayout.Count).Select(i => treeFixture.CardUICorners(i)).ToArray();
            TreeCheck(cards.Select((c,i) => treeFixture.PhotoAtUI(c.Aggregate(Vector2.zero,(sum,p) => sum+p)/4f) == i).All(ok => ok),
                "all 35 enlarged card centers select their own branch without overlapping another card");
            TreeCheck(cards.SelectMany(c => c).All(p => p.x >= 0 && p.y >= 0 && p.x <= UI.screenWidth && p.y <= UI.screenHeight),
                "all 35 enlarged cards fit the actual native viewport");
            var widths = cards.Select(c => Vector2.Distance(c[0],c[3])).ToArray();
            File.WriteAllText(Path.Combine(dir,"tree-scale.json"),"{\"structureCells\":" + Mathf.RoundToInt(MemoryTreeLayout.StructureCells)
                + ",\"screenWidth\":" + UI.screenWidth + ",\"screenHeight\":" + UI.screenHeight
                + ",\"cardWidthMinPx\":" + Mathf.RoundToInt(widths.Min()) + ",\"cardWidthMaxPx\":" + Mathf.RoundToInt(widths.Max())
                + ",\"photoCount\":35,\"cameraRequest\":5,\"source\":\"actual rotated CardUICorners projection in native game\"}");
        }
        private static void TreeCacheAndSave()
        {
            var store = PhotoStore.Instance; var ids = TreeFacts(); var holder = new PhotoBinding(); holder.Bind(treeFixture.RecordAt(8));
            PhotoTextureCache.Clear(); treeFixture.RefreshSelection(); holder.Bind(treeFixture.RecordAt(8)); holder.Unbind();
            TreeCheck(treeFixture.BoundPhotoCount == 35 && TreeFacts().SequenceEqual(ids),"cache clear rebinds thumbnails without changing choices");
            TreeCheck(PhotoTextureCache.IsCached(treeFixture.BoundPathAt(8)),"stale shared holder cannot release tree's new cache entry");
            var missing = treeFixture.RecordAt(8); string path = store.FilePath(missing); byte[] bytes = File.ReadAllBytes(path); string thumbPath = store.ThumbPath(missing); byte[] thumbBytes = File.Exists(thumbPath) ? File.ReadAllBytes(thumbPath) : null; File.Delete(path); File.Delete(thumbPath); PhotoTextureCache.Clear(); treeFixture.RefreshSelection();
            TreeCheck(treeFixture.SlotId(8) == missing.id && treeFixture.Selection.slots[8].pinned,"missing image preserves branch and pin");
            File.WriteAllBytes(path,bytes); if (thumbBytes != null) File.WriteAllBytes(thumbPath,thumbBytes); treeFixture.RefreshSelection(); TreeCheck(treeFixture.BoundTextureAt(8) != null,"restored image rebinds same saved slot");
            var at = treeFixture.Position; var mini = treeFixture.MakeMinified(); TreeCheck(!treeFixture.Spawned && treeFixture.BoundPhotoCount == 0 && mini.InnerThing == treeFixture,"uninstall releases bindings and preserves same inner tree");
            mini.InnerThing = null; mini.Destroy(); GenSpawn.Spawn(treeFixture,at,map); treeFixture.RefreshSelection(); TreeCheck(treeFixture.SlotId(8) == missing.id && treeFixture.Selection.slots[8].pinned && treeFixture.BoundPhotoCount == 35,"reinstall preserves pins and rebinds photos");
            treeFixture.SetPinned(0,true); treeFixture.SetPinned(26,true); treeFixture.Configure(false,new[] { "group","wedding" }); savedTreeSlots = TreeFacts(); savedTreeId = treeFixture.GetUniqueLoadID();
            GameDataSaveLoader.SaveGame("mftree"); var xml = new XmlDocument(); xml.Load(GenFilePaths.FilePathForSavedGame("mftree")); var nodes = xml.SelectNodes("//mfTreeSelection");
            TreeCheck(nodes.Count == 1 && nodes[0].SelectNodes("slots/li").Count == 35 && nodes[0].SelectNodes("seen/li").Count > 0,"actual game XML saves 35 slots and consumed-photo history");
            nodes[0].ParentNode.RemoveChild(nodes[0]); xml.Save(GenFilePaths.FilePathForSavedGame("mftree-legacy"));
        }
        private sealed class Window_TreeUIProbe : Window
        {
            internal Window_TreeUIProbe() { doWindowBackground = false; closeOnAccept = false; closeOnCancel = false; layer = WindowLayer.GameUI; absorbInputAroundWindow = false; }
            public override Vector2 InitialSize => new Vector2(1,1);
            protected override void SetInitialSizeAndPosition() { windowRect = new Rect(0,0,1,1); }
            public override void DoWindowContents(Rect rect) { }
            // WindowStack calls this before GUI.Window groups: synthetic map coordinates must not be clipped to our 1px fixture window.
            public override void ExtraOnGUI()
            {
                if (treeUIRan || !active || Event.current.type != EventType.Repaint) return;
                try {
                    int chosen = 8; var corners = treeFixture.CardUICorners(chosen); var click = corners.Aggregate(Vector2.zero,(sum,p)=>sum+p)/4f;
                    TreeCheck(treeFixture.PhotoAtUI(click) == chosen && treeFixture.PhotoAtUI(new Vector2(-500,-500)) == -1,"projected rotated card resolves its own slot and rejects outside points");
                    var previous = Event.current;
                    try {
                        Event.current = new Event { type=EventType.MouseDown, button=0, mousePosition=click };
                        int dispatched = treeFixture.mapInputDispatchCount, clicked = treeFixture.mapClickCount;
                        ((UIRoot_Play)Find.UIRoot).mapUI.HandleMapClicks();
                        TreeCheck(treeFixture.mapInputDispatchCount > dispatched && treeFixture.mapClickCount == clicked+1 && treeFixture.lastClickedSlot == chosen && Event.current.type == EventType.Used && Find.MainTabsRoot.OpenTab?.defName == "MF_Album",
                            "synthetic MouseDown through HandleMapClicks: dispatch " + dispatched + "→" + treeFixture.mapInputDispatchCount + ", clicks " + clicked + "→" + treeFixture.mapClickCount
                            + ", slot " + treeFixture.lastClickedSlot + ", event " + Event.current.type + ", album " + Find.MainTabsRoot.OpenTab?.defName
                            + ", selected " + (Find.Selector.SingleSelectedThing == treeFixture) + ", designator " + Find.DesignatorManager.SelectedDesignator + ", targeting " + Find.Targeter.IsTargeting
                            + ", windows " + string.Join(";",Find.WindowStack.Windows.Select(w=>w.GetType().Name+":"+w.absorbInputAroundWindow+":"+w.layer+":"+w.windowRect.Contains(click))));
                    } finally { Event.current = previous; }
                    treeUIRan = true;
                } catch(Exception e) { Fail(e); }
            }
        }
    }
}
