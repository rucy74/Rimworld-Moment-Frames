using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    internal static class ChronicleExtras
    {
        /// <summary>Only actual registered pictures. Death/ending flow is never intercepted to open or delay a remembrance.</summary>
        internal static List<PhotoRecord> Select(IEnumerable<PhotoRecord> source,string personId,string personName,int count=5)
        {
            var candidates=(source??Enumerable.Empty<PhotoRecord>()).Where(r=>r!=null&&r.HasFile&&!Kinds.IsSurvey(r.kind)
                &&r.kind!=Kinds.Relic&&r.kind!=Kinds.OtherDay&&r.kind!=Kinds.External
                &&(personId==null? !Kinds.PersonalOnly(r.kind):MainTabWindow_Album.Matches(r,personId,personName)))
                .OrderBy(r=>r.ticksAbs).ThenBy(r=>r.id,StringComparer.Ordinal).ToList();
            var chosen=new List<PhotoRecord>();var kinds=new HashSet<string>();
            if(candidates.Count>0){chosen.Add(candidates[0]);candidates.RemoveAt(0);kinds.Add(chosen[0].kind??"");}
            while(chosen.Count<Mathf.Clamp(count,3,5)&&candidates.Count>0)
            {
                var next=candidates.OrderByDescending(r=>!kinds.Contains(r.kind??""))
                    .ThenByDescending(r=>chosen.Min(p=>Math.Abs((long)r.ticksAbs-p.ticksAbs)))
                    .ThenByDescending(r=>Kinds.IsMilestone(r.kind)).ThenBy(r=>r.id,StringComparer.Ordinal).First();
                chosen.Add(next);kinds.Add(next.kind??"");candidates.Remove(next);
            }
            return chosen.OrderBy(r=>r.ticksAbs).ThenBy(r=>r.id,StringComparer.Ordinal).ToList();
        }
    }
    internal sealed class Dialog_MFRemembrance : Window
    {
        private readonly PhotoStore store;
        private string personId,personName;
        private List<PhotoRecord> photos=new List<PhotoRecord>();
        private int index;
        private Texture2D picture;
        private string heldPath;
        private bool playing;
        private float elapsed;
        public override Vector2 InitialSize=>new Vector2(Mathf.Min(1260f,UI.screenWidth-40f),Mathf.Min(850f,UI.screenHeight-40f));
        internal Dialog_MFRemembrance(string personId=null,string personName=null)
        {store=PhotoStore.Instance;this.personId=personId;this.personName=personName;doCloseX=true;absorbInputAroundWindow=true;forcePause=false;closeOnAccept=false;}
        public override void PreOpen(){base.PreOpen();Refresh();}
        public override void PostClose(){Release();base.PostClose();}
        private void Release(){if(heldPath!=null)PhotoTextureCache.Release(heldPath);heldPath=null;picture=null;}
        private void Refresh(){Release();index=0;elapsed=0;playing=false;photos=ChronicleExtras.Select(store?.Records,personId,personName);}
        private void Move(int next){Release();index=Mathf.Clamp(next,0,Mathf.Max(0,photos.Count-1));elapsed=0;}
        private void PeopleMenu()
        {
            var options=new List<FloatMenuOption>{new FloatMenuOption("MF_Extras_ColonyRemembrance".Translate(),()=>{personId=personName=null;Refresh();})};
            foreach(var pair in MainTabWindow_Album.PeopleIndex(store).OrderBy(p=>p.Value))
            {string id=pair.Key,name=pair.Value;options.Add(new FloatMenuOption(name,()=>{personId=id;personName=name;Refresh();}));}
            Find.WindowStack.Add(new FloatMenu(options));
        }
        public override void DoWindowContents(Rect rect)
        {
            using(AlbumStyle.Begin())
            {
                AlbumStyle.DrawPaper(rect);GUI.color=AlbumStyle.Theme.Ink;Text.Font=GameFont.Medium;
                AlbumStyle.Label(new Rect(20f,14f,rect.width-64f,46f),personId==null?"MF_Extras_ColonyRemembrance".Translate():"MF_Extras_PersonRemembrance".Translate(personName));Text.Font=GameFont.Small;
                if(AlbumStyle.Button(new Rect(20f,65f,260f,40f),"MF_Extras_ChooseRemembrance".Translate()))PeopleMenu();
                GUI.color=AlbumStyle.Theme.MutedInk;AlbumStyle.Label(new Rect(298f,65f,rect.width-322f,50f),"MF_Extras_RemembranceHint".Translate());GUI.color=Color.white;
                if(photos.Count==0){GUI.color=AlbumStyle.Theme.Ink;AlbumStyle.Label(new Rect(30f,130f,rect.width-60f,100f),"MF_Extras_NoRemembrance".Translate());GUI.color=Color.white;return;}
                if(playing&&Event.current.type==EventType.Repaint)
                {elapsed+=Time.unscaledDeltaTime;if(elapsed>=12f){if(index+1<photos.Count)Move(index+1);else{playing=false;elapsed=0;}}}
                var current=photos[index];string path=store.FilePath(current);
                if(path!=heldPath){Release();heldPath=path;picture=PhotoTextureCache.AcquireTexture(path);}
                Rect image=new Rect(20f,126f,rect.width-40f,Mathf.Max(80f,rect.height-294f));
                if(picture!=null)GUI.DrawTexture(image,picture,ScaleMode.ScaleToFit);
                else{GUI.color=AlbumStyle.Theme.MutedInk;AlbumStyle.Label(image,"MF_MissingFile".Translate());GUI.color=Color.white;}
                GUI.color=AlbumStyle.Theme.Ink;AlbumPresentation.CardTitle(new Rect(26f,image.yMax+8f,rect.width-52f,48f),current.DisplayTitle);
                AlbumPresentation.CardDate(new Rect(26f,image.yMax+58f,rect.width-52f,26f),current.dateLabel);GUI.color=Color.white;
                float y=rect.height-62f;
                if(AlbumStyle.Button(new Rect(20f,y,70f,40f),"<",index>0))Move(index-1);
                if(AlbumStyle.Button(new Rect(98f,y,150f,40f),(playing?"MF_Extras_StopSlides":"MF_Extras_PlaySlides").Translate())){playing=!playing;elapsed=0;}
                GUI.color=AlbumStyle.Theme.Ink;AlbumStyle.Label(new Rect(262f,y,110f,40f),(index+1)+" / "+photos.Count);GUI.color=Color.white;
                if(AlbumStyle.Button(new Rect(380f,y,70f,40f),">",index+1<photos.Count))Move(index+1);
                if(AlbumStyle.Button(new Rect(rect.width-232f,y,212f,40f),"MF_Extras_OpenInAlbum".Translate()))MainTabWindow_Album.Open(current);
            }
        }
    }
}
