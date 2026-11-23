using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    internal sealed class Dialog_PhotoPositions : Window
    {
        private readonly Map map; private readonly IntVec3 origin; private readonly Rot4 direction;
        private readonly List<Pawn> pawns; private readonly Dictionary<string,IntVec3> draft;
        private readonly List<IntVec3> slots; private readonly Action<Dictionary<string,IntVec3>> apply;
        private readonly IDictionary<string,int> faces; private readonly PhotoFacePreview portraits=new PhotoFacePreview();
        private string picked,dragging; private Vector2 scroll;
        public override Vector2 InitialSize=>new Vector2(Mathf.Min(850,UI.screenWidth-40),Mathf.Min(750,UI.screenHeight-40));
        internal Dialog_PhotoPositions(Map map,IntVec3 origin,Rot4 direction,IList<Pawn> pawns,IList<IntVec3> cells,
            IDictionary<string,int> faces,Action<Dictionary<string,IntVec3>> apply)
        {
            this.map=map;this.origin=origin;this.direction=direction;this.pawns=pawns.ToList();this.faces=faces;this.apply=apply;
            draft=PhotoPositions.Capture(pawns,cells,origin,direction);
            slots=PhotoPositions.Slots(map,origin,direction,pawns.Count).Select(c=>PhotoPositions.Local(c,origin,direction)).Distinct().OrderByDescending(c=>c.z).ThenBy(c=>c.x).ToList();
            doCloseX=true;forcePause=false;absorbInputAroundWindow=true;closeOnAccept=false;
        }
        public override void PostClose(){portraits.Dispose();base.PostClose();}
        private void Move(string id,IntVec3 target)
        {
            if(id==null||!draft.ContainsKey(id))return;
            var occupant=draft.FirstOrDefault(k=>k.Value==target);
            var old=draft[id];draft[id]=target;if(occupant.Key!=null&&occupant.Key!=id)draft[occupant.Key]=old;
        }
        public override void DoWindowContents(Rect r)
        {
            using(AlbumStyle.Begin(true))
            {
                AlbumStyle.DrawPaper(r);var p=r.ContractedBy(16);
                SharedPhotoUI.Heading(new Rect(p.x,p.y,p.width-30,40),"MF_Positions".Translate());
                string help="MF_PositionsHelp".Translate();float helpHeight=AlbumStyle.TextHeight(help,p.width)+8;
                SharedPhotoUI.Label(new Rect(p.x,p.y+48,p.width,helpHeight),help);
                var area=new Rect(p.x,p.y+56+helpHeight,p.width,Mathf.Max(60,p.height-114-helpHeight));
                int minX=slots.Count==0?0:slots.Min(c=>c.x),maxX=slots.Count==0?0:slots.Max(c=>c.x),maxZ=slots.Count==0?0:slots.Max(c=>c.z);
                float size=Mathf.Min(112,(area.width-20)/Math.Max(1,maxX-minX+1));
                float left=Mathf.Max(0,(area.width-20-(maxX-minX+1)*size)/2);
                float height=Mathf.Max(area.height,slots.Count==0?0:(maxZ-slots.Min(c=>c.z)+1)*116f+35);
                portraits.Begin(area,scroll);Widgets.BeginScrollView(area,ref scroll,new Rect(0,0,area.width-20,height));
                SharedPhotoUI.Label(new Rect(0,0,area.width-20,28),"MF_PositionsBack".Translate());
                foreach(var slot in slots)
                {
                    var rect=new Rect(left+(slot.x-minX)*size,32+(maxZ-slot.z)*116,size-5,110);
                    var entry=draft.FirstOrDefault(k=>k.Value==slot);
                    var pawn=pawns.FirstOrDefault(x=>x.GetUniqueLoadID()==entry.Key);
                    bool valid=GroupFormation.Usable(origin+Building_PhotoStudio.Rotate(slot,direction),origin.GetRoom(map),map);
                    Widgets.DrawBoxSolid(rect,valid?new Color(.55f,.43f,.3f,picked!=null&&picked==entry.Key?.32f:.13f):new Color(.7f,.2f,.2f,.25f));
                    if(pawn!=null)
                    {
                        portraits.Draw(pawn,new Rect(rect.x+(rect.width-65)/2,rect.y+4,65,65),PhotoFaces.Choice(faces,entry.Key));
                        SharedPhotoUI.Label(new Rect(rect.x+4,rect.y+70,rect.width-8,36),pawn.LabelShort);
                    }
                    if(Mouse.IsOver(rect)&&Event.current.type==EventType.MouseDown&&Event.current.button==0)
                    {
                        if(picked!=null && picked!=entry.Key){Move(picked,slot);picked=null;dragging=null;}
                        else {picked=entry.Key;dragging=entry.Key;}
                        Event.current.Use();
                    }
                    if(Mouse.IsOver(rect)&&Event.current.type==EventType.MouseUp&&Event.current.button==0&&dragging!=null)
                    {Move(dragging,slot);dragging=null;Event.current.Use();}
                }
                if(Event.current.type==EventType.MouseUp)dragging=null;
                Widgets.EndScrollView();
                float half=(p.width-8)/2;
                if(SharedPhotoUI.Button(new Rect(p.x,p.yMax-42,half,42),"MF_PositionsAuto".Translate())){apply(new Dictionary<string,IntVec3>());Close();}
                if(SharedPhotoUI.Button(new Rect(p.x+half+8,p.yMax-42,half,42),"MF_PositionsApply".Translate(),draft.Count==pawns.Count))
                {apply(new Dictionary<string,IntVec3>(draft));Close();}
            }
        }
    }
}
