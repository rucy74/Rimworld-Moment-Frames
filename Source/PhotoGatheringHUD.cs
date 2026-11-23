using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    /// <summary>Real-time UI cache only. No additional simulation tick or map-wide pawn scan.</summary>
    internal static class PhotoGatheringHUD
    {
        private static Map cachedMap;
        private static float refreshed=-1;
        private static readonly List<LordJob_MFGroupPhoto> jobs=new List<LordJob_MFGroupPhoto>();
        private static int index;
        private static bool expanded;
        private static string summary="",details="";
        internal static void Draw()
        {
            // GameComponentOnGUI also runs in the menu once a Game exists (scenario setup, world generation), before Find.World is set.
            if(Current.ProgramState!=ProgramState.Playing || PhotoCapture.HideGuides || Find.World.renderer.wantedMode!=WorldRenderMode.None || Find.CurrentMap==null) return;
            // Leave modals and an open main tab unobscured.
            if(Find.WindowStack.Windows.Any(w=>w.absorbInputAroundWindow))return;
            Map map=Find.CurrentMap;
            if(cachedMap!=map || Time.realtimeSinceStartup-refreshed>.5f)
            {
                cachedMap=map;refreshed=Time.realtimeSinceStartup;jobs.Clear();
                jobs.AddRange(map.lordManager.lords.Select(l=>l.LordJob).OfType<LordJob_MFGroupPhoto>().Where(j=>!j.Finished));
                index=Mathf.Clamp(index,0,Mathf.Max(0,jobs.Count-1));
                if(jobs.Count>0) {summary=PhotoGatheringProgress.Summary(jobs[index]);details=PhotoGatheringProgress.Details(jobs[index]);}
            }
            if(jobs.Count==0 || jobs[index].Finished)return;
            var job=jobs[index];float width=Mathf.Min(480,UI.screenWidth-48);
            using(AlbumStyle.Begin())
            {
                float c=SharedPhotoUI.ControlHeight,header=Mathf.Max(c,AlbumStyle.TextHeight(summary,width-66)+12);
                float detail=expanded?AlbumStyle.TextHeight(details,width-24)+c+26:0;
                var box=new Rect(Mathf.Max(12,(UI.screenWidth-width)/2),UI.screenHeight-72-header-detail,width,header+detail);
                AlbumStyle.DrawPaper(box);SharedPhotoUI.Label(new Rect(box.x+12,box.y+6,width-66,header-12),summary);
                if(SharedPhotoUI.Button(new Rect(box.xMax-46,box.y+6,34,header-12),expanded?"−":"+"))expanded=!expanded;
                if(!expanded)return;
                float y=box.y+header;
                SharedPhotoUI.Label(new Rect(box.x+12,y,width-24,detail-c-20),details,true);y=box.yMax-c-10;
                float cell=(width-40)/3;
                if(SharedPhotoUI.Button(new Rect(box.x+12,y,cell,c),"MF_UX_Locate".Translate()))CameraJumper.TryJump(new TargetInfo(job.spot,map));
                if(SharedPhotoUI.Button(new Rect(box.x+20+cell,y,cell,c),"MF_UX_GatheringNext".Translate(index+1,jobs.Count),jobs.Count>1))
                {index=(index+1)%jobs.Count;refreshed=-1;}
                if(SharedPhotoUI.Button(new Rect(box.x+28+cell*2,y,cell,c),"MF_UX_CancelGathering".Translate()))
                    Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("MF_UX_CancelGatheringConfirm".Translate(),()=>job.Cancel()));
            }
        }
    }
}
