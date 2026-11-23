using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace MomentFrames
{
    public class Building_PhotoSeat : Building { }

    // Show Me Your Tools deliberately keeps the previous work tool on the belt for
    // 2.5 real seconds (frozen while paused). Suppress only that optional visual while
    // a participant is posing/holding a baby. Do not change inventory, equipment,
    // carried babies or the other mod's animation state.
    [HarmonyPatch]
    internal static class PhotoPoseWorkTools
    {
        private static readonly Type animator = AccessTools.TypeByName("JobEffects.ToolAnimator");
        private static readonly MethodInfo target = animator == null ? null : AccessTools.Method(animator,
            "OnPawnRendered", new[] { typeof(Pawn), typeof(Vector3) });
        private static bool Prepare() => target != null;
        private static MethodBase TargetMethod() => target;
        internal static bool IsPhotoPose(JobDriver driver) => driver is JobDriver_MFPose || driver is JobDriver_MFCarryBaby;
        private static bool Prefix(Pawn pawn) => !IsPhotoPose(pawn?.jobs?.curDriver);
    }

    public partial class Building_PhotoStudio
    {
        private int backdropStyle;
        private Graphic styledGraphic;
        internal int BackdropStyle => backdropStyle;
        internal bool HasCanvas => def.defName == "MF_StudioBackdrop";
        internal static readonly string[] Backdrops = { "StudioBackdrop", "StudioGarden", "StudioBirthday" };
        public override Graphic Graphic => !HasCanvas || backdropStyle == 0 || !backgroundFile.NullOrEmpty() ? base.Graphic
            : styledGraphic ?? (styledGraphic = GraphicDatabase.Get<Graphic_Single>("Things/Building/MomentFrames/" + Backdrops[backdropStyle], ShaderDatabase.Cutout, def.graphicData.drawSize, Color.white));
        internal void SetBackdrop(int style)
        {
            backdropStyle = Mathf.Clamp(style, 0, 2); styledGraphic = null;
            SetPhotoBackground(null,1,.5f,.5f);
        }
        public override void ExposeData()
        {
            base.ExposeData(); Scribe_Values.Look(ref backdropStyle, "studioBackdropStyle");
            ExposeBackground();
            if (Scribe.mode == LoadSaveMode.PostLoadInit) { backdropStyle=Mathf.Clamp(backdropStyle,0,2);styledGraphic=null; }
        }
        internal List<IntVec3> SeatCells()
        {
            var cells = new List<IntVec3>();
            if (!Spawned) return cells;
            for (int row = 0; row < Rows; row++)
                foreach (int x in Enumerable.Range(-2, Columns).OrderBy(x => Math.Abs(x)).ThenBy(x => x))
                {
                    var c = Position + Rotate(new IntVec3(x, 0, -1 + row), Rotation);
                    if (c.InBounds(Map) && c.GetThingList(Map).OfType<Building_PhotoSeat>().Any(s => s.Rotation == Rotation)) cells.Add(c);
                }
            return cells;
        }
        internal IEnumerable<Building_PhotoStudioLamp> Lamps() => !Spawned ? Enumerable.Empty<Building_PhotoStudioLamp>()
            : Map.listerBuildings.AllBuildingsColonistOfClass<Building_PhotoStudioLamp>().Where(l => l.Studio == this);
        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (var gizmo in base.GetGizmos()) yield return gizmo;
            if (Faction != Faction.OfPlayer) yield break;
            yield return new Command_Action { defaultLabel = "MF_StudioDecorate".Translate(), defaultDesc = "MF_StudioDecorateHelp".Translate(),
                icon = ContentFinder<Texture2D>.Get("Things/Building/MomentFrames/StudioSofa"), action = () => Find.WindowStack.Add(new Dialog_StudioFurnishing(this)) };
            if(HasCanvas)yield return new Command_Action {defaultLabel="MF_Background".Translate(),defaultDesc="MF_BackgroundHelp".Translate(),icon=MFIcons.Camera,
                action=()=>Find.WindowStack.Add(new Dialog_StudioBackground(this))};
        }
        internal struct FurniturePlan
        {
            internal ThingDef def; internal IntVec3 cell;
            internal FurniturePlan(string name, IntVec3 cell) { def = ThingDef.Named(name); this.cell = cell; }
        }
        internal List<FurniturePlan> Furniture(int set)
        {
            var items = new List<FurniturePlan>();
            items.Add(new FurniturePlan(set == 1 ? "MF_StudioBench" : set == 2 ? "MF_StudioChair" : "MF_StudioSofa", Position + Rotate(new IntVec3(0,0,-1), Rotation)));
            foreach (int x in new[] { -6, 6 }) items.Add(new FurniturePlan("MF_StudioLamp", Position + Rotate(new IntVec3(x,0,-1), Rotation)));
            return items;
        }
        private bool Exists(FurniturePlan plan) => plan.cell.InBounds(Map) && plan.cell.GetThingList(Map).Any(t => t.Position == plan.cell && t.Rotation == Rotation && (t.def == plan.def || t.def.entityDefToBuild == plan.def));
        internal AcceptanceReport CanPlaceSet(int set)
        {
            if (!Spawned) return false;
            foreach (var item in Furniture(set).Where(p => !Exists(p)))
            {
                var report = GenConstruct.CanPlaceBlueprintAt(item.def, item.cell, Rotation, Map);
                if (!report.Accepted) return new AcceptanceReport(item.def.LabelCap + ": " + report.Reason);
            }
            return true;
        }
        internal bool PlaceSet(int set)
        {
            if (!CanPlaceSet(set).Accepted) return false;
            // Ordinary blueprints. Existing buildings are neither moved nor replaced.
            foreach (var item in Furniture(set).Where(p => !Exists(p))) GenConstruct.PlaceBlueprintForBuild(item.def, item.cell, Map, Rotation, Faction, null);
            return true;
        }
    }

    public class CompStudioFlickable : CompFlickable
    {
        public override IEnumerable<Gizmo> CompGetGizmosExtra() { yield break; }
    }
    public class Building_PhotoStudioLamp : Building
    {
        private int mode; // 0: automatic, 1: on, 2: off. Owners are reconstructed after load.
        private readonly HashSet<LordJob_MFGroupPhoto> owners = new HashSet<LordJob_MFGroupPhoto>();
        internal int Mode => mode;
        internal bool Lit => GetComp<CompFlickable>()?.SwitchIsOn == true && GetComp<CompPowerTrader>()?.PowerOn == true;
        public override Graphic Graphic => DefaultGraphic; // There is no separate switched-off texture.
        internal Building_PhotoStudio Studio => !Spawned ? null : Map.listerBuildings.AllBuildingsColonistOfClass<Building_PhotoStudio>()
            .Where(s => s.Position.DistanceToSquared(Position) <= 100 && s.Position.GetRoom(Map) == Position.GetRoom(Map))
            .OrderBy(s => s.Position.DistanceToSquared(Position)).ThenBy(s => s.thingIDNumber).FirstOrDefault();
        internal void Acquire(LordJob_MFGroupPhoto owner) { owners.Add(owner); Refresh(); }
        internal void Release(LordJob_MFGroupPhoto owner) { owners.Remove(owner); Refresh(); }
        internal void SetMode(int value) { mode = Mathf.Clamp(value, 0, 2); Refresh(); }
        private void Refresh()
        {
            owners.RemoveWhere(j => j.Finished || j.PhotoMap != Map);
            var flick = GetComp<CompFlickable>();
            if (flick != null) flick.SwitchIsOn = mode == 1 || (mode == 0 && owners.Count > 0);
        }
        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad); owners.Clear(); Refresh();
            LongEventHandler.ExecuteWhenFinished(() =>
            {
                if (!Spawned) return;
                var studio = Studio;
                var job = studio == null ? null : GroupPhoto.ActiveJob(Map, studio.Position);
                if (job != null) job.AttachStudioLamp(this);
            });
        }
        public override void ExposeData()
        {
            base.ExposeData(); Scribe_Values.Look(ref mode, "studioLightMode");
            if (Scribe.mode == LoadSaveMode.PostLoadInit) mode = Mathf.Clamp(mode,0,2);
        }
        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach(var g in base.GetGizmos()) yield return g;
            if (Faction != Faction.OfPlayer) yield break;
            yield return new Command_Action { defaultLabel = "MF_StudioLight".Translate() + ": " + ("MF_StudioLight" + mode).Translate(),
                defaultDesc = "MF_StudioLightHelp".Translate(), icon = ContentFinder<Texture2D>.Get("Things/Building/MomentFrames/StudioLamp"),
                action = () => Find.WindowStack.Add(new FloatMenu(Enumerable.Range(0,3).Select(i => new FloatMenuOption(("MF_StudioLight"+i).Translate(), () => SetMode(i))).ToList())) };
        }
        public override string GetInspectString() => base.GetInspectString() + "\n" + ("MF_StudioLight" + mode).Translate() + "\n" + "MF_StudioLightHelp".Translate();
    }

    public partial class LordJob_MFGroupPhoto
    {
        private readonly List<Building_PhotoStudioLamp> studioLamps = new List<Building_PhotoStudioLamp>();
        private bool studioLightingStarted;
        internal void AttachStudioLamp(Building_PhotoStudioLamp lamp)
        {
            if (Finished || lamp == null || studioLamps.Contains(lamp)) return;
            studioLamps.Add(lamp); lamp.Acquire(this);
        }
        private void StartStudioLights()
        {
            if (studioLightingStarted || Map == null) return;
            studioLightingStarted = true;
            var studio = Building_PhotoStudio.At(Map, spot);
            if (studio != null) foreach(var lamp in studio.Lamps()) AttachStudioLamp(lamp);
        }
        private void StopStudioLights()
        {
            foreach(var lamp in studioLamps) if (!lamp.Destroyed) lamp.Release(this);
            studioLamps.Clear(); studioLightingStarted = false;
        }
    }

    internal sealed class Dialog_StudioFurnishing : Window
    {
        private readonly Building_PhotoStudio studio;
        private int selected;
        private Vector2 scroll;
        protected override float Margin => 0;
        public override Vector2 InitialSize => new Vector2(Mathf.Min(800,UI.screenWidth-40),Mathf.Min(700,UI.screenHeight-40));
        internal Dialog_StudioFurnishing(Building_PhotoStudio studio)
        {
            this.studio=studio;selected=studio.BackdropStyle;
            forcePause=false;doWindowBackground=false;doCloseX=false;absorbInputAroundWindow=true;
        }
        public override void DoWindowContents(Rect rect)
        {
            if (!studio.Spawned) { Close(); return; }
            using(AlbumStyle.Begin())
            {
                AlbumStyle.DrawPaper(rect); var page=rect.ContractedBy(22);float row=SharedPhotoUI.ControlHeight;
                SharedPhotoUI.Heading(new Rect(page.x,page.y,page.width-48,row),"MF_StudioDecorate".Translate());
                if(SharedPhotoUI.Button(new Rect(page.xMax-42,page.y,42,row),"×"))Close();
                var body=new Rect(page.x,page.y+row+12,page.width,page.height-row-12);
                float w=body.width-20, cw=(w-16)/3;float y=0;
                string help="MF_StudioSetHelp".Translate();float h=AlbumStyle.TextHeight(help,w)+8;
                var report=studio.CanPlaceSet(selected);
                string status=report.Accepted?"MF_StudioSetCost".Translate():report.Reason;
                var content=new Rect(0,0,w,row+12+222+h+10+(studio.HasCanvas?2*(row+10):0)+row+10+AlbumStyle.TextHeight(status,w)+16);
                Widgets.BeginScrollView(body,ref scroll,content);
                try
                {
                    for(int i=0;i<3;i++)
                    {
                        var tab=new Rect(i*(cw+8),y,cw,row);
                        if(SharedPhotoUI.Button(tab,("MF_StudioSet"+i).Translate()))selected=i;
                        if(selected==i)Widgets.DrawBoxSolid(new Rect(tab.x,tab.yMax-3,tab.width,3),AlbumStyle.Theme.Accent);
                    }
                    y+=row+12;
                    GUI.DrawTexture(new Rect(0,y,w,210),ContentFinder<Texture2D>.Get("Things/Building/MomentFrames/"+(studio.HasCanvas?Building_PhotoStudio.Backdrops[selected]:"StudioArch")),ScaleMode.ScaleToFit);
                    string seat=selected==1?"StudioBench":selected==2?"StudioChair":"StudioSofa";
                    float seatWidth=selected==2?65:190;
                    GUI.DrawTexture(new Rect((w-seatWidth)/2,y+125,seatWidth,70),ContentFinder<Texture2D>.Get("Things/Building/MomentFrames/"+seat),ScaleMode.ScaleToFit);
                    foreach(float x in new[]{8f,w-70f})GUI.DrawTexture(new Rect(x,y+40,62,145),ContentFinder<Texture2D>.Get("Things/Building/MomentFrames/StudioLamp"),ScaleMode.ScaleToFit);
                    y+=222;
                    SharedPhotoUI.Label(new Rect(0,y,w,h),help,true);y+=h+10;
                    if(studio.HasCanvas)
                    {
                        if(SharedPhotoUI.Button(new Rect(0,y,w,row),"MF_StudioApplyBackdrop".Translate()))studio.SetBackdrop(selected);y+=row+10;
                        if(SharedPhotoUI.Button(new Rect(0,y,w,row),"MF_StudioOwnBackdrop".Translate()))Find.WindowStack.Add(new Dialog_StudioBackground(studio));y+=row+10;
                    }
                    if(SharedPhotoUI.Button(new Rect(0,y,w,row),"MF_StudioPlaceSet".Translate(),report.Accepted))studio.PlaceSet(selected);y+=row+10;
                    SharedPhotoUI.Label(new Rect(0,y,w,AlbumStyle.TextHeight(status,w)+12),status,true);
                }
                finally {Widgets.EndScrollView();}
            }
        }
    }
}
