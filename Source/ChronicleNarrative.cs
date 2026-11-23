using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    internal static class ChronicleNarrative
    {
        private static readonly HashSet<string> expanded=new HashSet<string>();
        internal static int Revision { get; private set; }
        internal static bool Expanded(PhotoRecord rec) => expanded.Contains(rec.id);
        internal static void Toggle(PhotoRecord rec) {if(!expanded.Remove(rec.id))expanded.Add(rec.id);Revision++;}
        internal static float Height(PhotoRecord rec,float width)
        {
            float all=AlbumStyle.TextHeight(rec.DisplayDesc??"",width);
            float shortHeight=AlbumStyle.TextHeight("Ag\nAg",width);
            return all>shortHeight ? (Expanded(rec)?all:shortHeight)+SharedPhotoUI.ControlHeight+6 : all;
        }
        internal static void Draw(Rect area,PhotoRecord rec)
        {
            string note=rec.DisplayDesc??"";float all=AlbumStyle.TextHeight(note,area.width);
            float shortHeight=AlbumStyle.TextHeight("Ag\nAg",area.width);
            bool longStory=all>shortHeight;float body=longStory&&!Expanded(rec)?shortHeight:all;
            // Clip to whole visible lines, never render under the expand button.
            var style=new GUIStyle(AlbumStyle.StyleFor(note,GameFont.Small)){wordWrap=true,clipping=TextClipping.Clip};
            var old=GUI.color;GUI.color=AlbumStyle.Theme.Ink;GUI.Label(new Rect(area.x,area.y,area.width,body),note,style);GUI.color=old;
            if(longStory && SharedPhotoUI.QuietButton(new Rect(area.x,area.y+body+4,area.width,SharedPhotoUI.ControlHeight),
                (Expanded(rec)?"MF_UX_StoryLess":"MF_UX_StoryMore").Translate()))Toggle(rec);
        }
        internal static float RowHeight(PhotoRecord rec,float width,float thumbWidth)
        {
            float textWidth=Mathf.Max(80,width-(rec.eventOnly?20:thumbWidth+26));
            return Mathf.Max(rec.eventOnly?88:126,AlbumStyle.TextHeight(rec.DisplayTitle,textWidth-32)+
                AlbumStyle.TextHeight(rec.dateLabel,textWidth)+Height(rec,textWidth)+38);
        }
        internal static void DrawRow(Rect row,PhotoRecord rec,PhotoStore store,float thumbWidth,string people)
        {
            float x=row.x+10,w=row.width-20;
            if(!rec.eventOnly)
            {
                var card=new Rect(row.x+4,row.y+8,thumbWidth,Mathf.Min(104,row.height-16));
                Widgets.DrawBoxSolid(card,AlbumStyle.Theme.Mat);
                var texture=PhotoTextureCache.ThumbDeferred(store.ThumbPath(rec),store.FilePath(rec));
                if(texture!=null)GUI.DrawTexture(card.ContractedBy(6),texture,ScaleMode.ScaleToFit);
                else
                {
                    bool loading = PhotoThumbnailLoader.Pending(store.ThumbPath(rec));
                    SharedPhotoUI.Label(card.ContractedBy(8),(loading ? "MF_BurstLoading" : "MF_UX_MissingShort").Translate(),true);
                    if (!loading) TooltipHandler.TipRegion(card,"MF_UX_MissingPhoto".Translate());
                }
                x=row.x+thumbWidth+20;w=row.xMax-x-6;
            }
            else Widgets.DrawBoxSolid(new Rect(row.x,row.y+10,2,row.height-20),AlbumStyle.Theme.MutedInk);
            float titleH=AlbumStyle.TextHeight(rec.DisplayTitle,w-28),y=row.y+6;
            PhotoEventIcons.DrawEvent(new Rect(x,y+2,20,20),rec,AlbumStyle.Theme.MutedInk);
            SharedPhotoUI.Label(new Rect(x+28,y,w-28,titleH),rec.DisplayTitle);y+=titleH+5;
            string meta=AlbumPresentation.Title(rec.dateLabel+(rec.eventOnly?" / "+"MF_RecordOnly".Translate():""));
            float metaH=AlbumStyle.TextHeight(meta,w);
            SharedPhotoUI.Label(new Rect(x,y,w,metaH),meta,true);y+=metaH+5;
            Draw(new Rect(x,y,w,Height(rec,w)),rec);
            TooltipHandler.TipRegion(row,rec.DisplayTitle+"\n"+rec.dateLabel+"\n"+people);
        }
    }
}
