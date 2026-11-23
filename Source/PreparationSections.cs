using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    // Only remembers disclosure state for this game session. Folding never changes the photograph's choices.
    internal static class PreparationSections
    {
        internal static bool GroupLayout, GroupExpressions, BirthdayLayout, BirthdayExpressions;
        internal static float FoldHeight(string text, float width) => Mathf.Max(SharedPhotoUI.ControlHeight, AlbumStyle.TextHeight(text,width-42)+16);
        internal static bool Fold(float width, ref float y, string text, bool expanded)
        {
            float h=FoldHeight(text,width);var rect=new Rect(0,y,width,h);
            if(SharedPhotoUI.Button(rect,(expanded?"− ":"+ ")+text))expanded=!expanded;
            y+=h+8;return expanded;
        }
        internal static string LayoutSummary(int positions,bool saved,bool focus,bool studio=false) => "MF_Learn_Layout".Translate(
            (positions>0?"MF_Learn_ManualPositions":"MF_Learn_AutoPositions").Translate(),
            (saved?"MF_UX_SavedFrame":studio?"MF_UX_AutoFrame":focus?"MF_PreparationPeopleFocus":"MF_PreparationScenery").Translate());
        internal static string ExpressionSummary(IEnumerable<Pawn> chosen, IDictionary<string,int> icons, IDictionary<string,int> faces, int mode)
        {
            var ids=chosen.Select(p=>p.GetUniqueLoadID()).ToList();
            int ic=mode==PhotoReactions.ModeOff?0:ids.Count(id=>ReactionChoices.Choice(icons,id)!=ReactionChoices.None);
            int fc=ids.Count(id=>PhotoFaces.Choice(faces,id)!=0);
            return ic==0&&fc==0?"MF_Learn_ExpressionsDefault".Translate():"MF_Learn_ExpressionsCount".Translate(fc,ic);
        }
        internal static float ExpressionHeight(float width,int count) =>
            AlbumStyle.TextHeight("MF_Learn_ExpressionsHelp".Translate(),width)+12+SharedPhotoUI.ReactionHeight
            +ReactionPreparationUI.HeightFor(width)+count*(ExpressionRowHeight+6);
        internal static float ExpressionRowHeight => Mathf.Max(80,ReactionPreparationUI.GuestHeight+38);
        internal static void Expressions(float width,ref float y,IList<Pawn> chosen,IEnumerable<Pawn> roster,
            IDictionary<string,int> icons,IDictionary<string,int> faces,ref int mode,ref bool remember,PhotoFacePreview preview, Action enable)
        {
            string help="MF_Learn_ExpressionsHelp".Translate();float h=AlbumStyle.TextHeight(help,width);
            SharedPhotoUI.Label(new Rect(0,y,width,h),help);y+=h+12;
            SharedPhotoUI.ReactionControls(new Rect(0,y,width,SharedPhotoUI.ReactionHeight),ref mode,ref remember);y+=SharedPhotoUI.ReactionHeight;
            ReactionPreparationUI.PresetControls(new Rect(0,y,width,ReactionPreparationUI.HeightFor(width)),icons,roster,chosen,faces);y+=ReactionPreparationUI.HeightFor(width);
            foreach(var p in chosen)
            {
                var row=new Rect(0,y,width,ExpressionRowHeight);
                Widgets.DrawBoxSolid(row,Color.Lerp(AlbumStyle.Theme.Paper,AlbumStyle.Theme.Accent,.09f));
                preview.Draw(p,new Rect(16,y+(row.height-64)/2,64,64),PhotoFaces.Choice(faces,p.GetUniqueLoadID()));
                SharedPhotoUI.Label(new Rect(114,y+3,width-126,30),p.LabelShort);
                ReactionPreparationUI.GuestButton(row,p,true,mode,icons,enable,faces);
                y+=row.height+6;
            }
        }
    }
}
