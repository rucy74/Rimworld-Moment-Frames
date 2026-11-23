using System;
using System.Linq;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    internal sealed class Dialog_ExpressionPalette : Window
    {
        private readonly Pawn pawn;
        private readonly int selected;
        private readonly Action<int> choose;
        private Vector2 scroll;
        protected override float Margin => 0;
        public override Vector2 InitialSize => new Vector2(Mathf.Min(580f,UI.screenWidth-32f),Mathf.Min(600f,UI.screenHeight-32f));
        internal Dialog_ExpressionPalette(Pawn pawn,int selected,Action<int> choose)
        { this.pawn=pawn;this.selected=selected;this.choose=choose;forcePause=false;doCloseX=false;doWindowBackground=false;absorbInputAroundWindow=true; }
        internal void Choose(int icon) { choose(icon);Close(); }
        public override void DoWindowContents(Rect r)
        {
            using(AlbumStyle.Begin())
            {
                AlbumStyle.DrawPaper(r);var page=r.ContractedBy(22f);float c=SharedPhotoUI.ControlHeight;
                SharedPhotoUI.Heading(new Rect(page.x,page.y,page.width-c-8,c),"MF_UX_PawnExpression".Translate(pawn.LabelShort));
                if(SharedPhotoUI.Button(new Rect(page.xMax-c,page.y,c,c),"×"))Close();
                var area=new Rect(page.x,page.y+c+12,page.width,page.height-c-12);
                float width=area.width-20,half=(width-8)/2,cell=(width-16)/3;
                float row=Mathf.Max(110,72+AlbumStyle.TextHeight("MF_CaptureExpressionAuto".Translate(),cell-12));
                string help="MF_UX_ExpressionHelp".Translate();float helpH=AlbumStyle.TextHeight(help,width)+10;
                Widgets.BeginScrollView(area,ref scroll,new Rect(0,0,width,c+14+3*(row+8)+helpH));
                var options=new[]{ReactionChoices.None,ReactionChoices.Automatic};
                for(int i=0;i<options.Length;i++)
                {
                    int icon=options[i];var rect=new Rect(i*(half+8),0,half,c);
                    if(SharedPhotoUI.Button(rect,ReactionChoices.Label(icon)))Choose(icon);
                    Mark(rect,icon);
                }
                var icons=ReactionChoices.Options.Where(i=>i>=0).ToArray();
                for(int i=0;i<icons.Length;i++)
                {
                    int icon=icons[i];var rect=new Rect((i%3)*(cell+8),c+14+(i/3)*(row+8),cell,row);
                    if(SharedPhotoUI.Button(rect,""))Choose(icon);
                    ReactionArt.Draw(new Rect(rect.center.x-32,rect.y+4,64,64),icon,ReactionArt.ColorFor(icon));
                    SharedPhotoUI.Label(new Rect(rect.x+8,rect.y+72,rect.width-16,rect.height-76),ReactionChoices.Label(icon));
                    Mark(rect,icon);
                }
                SharedPhotoUI.Label(new Rect(0,c+14+3*(row+8),width,helpH),help,true);
                Widgets.EndScrollView();
            }
        }
        private void Mark(Rect rect,int icon)
        { if(selected==icon)Widgets.DrawBoxSolid(new Rect(rect.x+2,rect.yMax-4,rect.width-4,3),AlbumStyle.Theme.Accent); }
    }
}
