using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    internal static class ReactionPreparationUI
    {
        internal static float HeightFor(float width) => SharedPhotoUI.ControlHeight+12f;
        internal static float GuestHeight => SharedPhotoUI.ControlHeight+10f+(PhotoFaces.Available?SharedPhotoUI.ControlHeight+4f:0f);
        internal static Rect GuestRect(Rect row) => new Rect(row.x+114f,row.yMax-GuestHeight+5f,row.width-126f,GuestHeight-10f);
        internal static void GuestButton(Rect row,Pawn pawn,bool selected,int mode,IDictionary<string,int> choices,System.Action enable=null,IDictionary<string,int> faces=null)
        {
            Rect button=GuestRect(row);int choice=ReactionChoices.Choice(choices,pawn.GetUniqueLoadID());
            button.height=SharedPhotoUI.ControlHeight;
            if(PhotoFaces.Available)PhotoFaces.Button(new Rect(button.x,button.yMax+4f,button.width,SharedPhotoUI.ControlHeight),pawn,selected,faces);
            string text="MF_Learn_EmoteChoice".Translate(choice==ReactionChoices.None?"MF_Learn_None".Translate():ReactionChoices.Label(choice));
            bool active=selected;
            if(SharedPhotoUI.Button(button,text,active))
                Find.WindowStack.Add(new Dialog_ExpressionPalette(pawn,choice,icon=>{choices[pawn.GetUniqueLoadID()]=icon;if(icon!=ReactionChoices.None)enable?.Invoke();}));
            if(choice>=0) ReactionArt.Draw(new Rect(button.x+6f,button.center.y-13f,26f,26f),choice,ReactionArt.ColorFor(choice));
            TooltipHandler.TipRegion(button,"MF_CaptureExpressionHelp".Translate());
        }
        internal static void PresetControls(Rect area,IDictionary<string,int> choices,IEnumerable<Pawn> roster,IEnumerable<Pawn> selected,IDictionary<string,int> faces=null)
        {
            var store=ReactionPresetsGameComponent.Instance;float half=(area.width-8f)/2f, control=SharedPhotoUI.ControlHeight;
            if(SharedPhotoUI.Button(new Rect(area.x,area.y,half,control),"MF_CapturePresetLoad".Translate(),store?.presets.Count>0))
            {
                var ids=roster.Select(p=>p.GetUniqueLoadID()).ToList();
                Find.WindowStack.Add(new FloatMenu(store.presets.OrderBy(p=>p.name).Select(p=>new FloatMenuOption(p.name,
                    ()=>{ReactionChoices.Apply(choices,ReactionChoices.Read(p.pawnIds,p.icons),ids);
                        if(faces!=null)foreach(var pair in PhotoFaces.Read(p.facePawnIds,p.faces).Where(k=>ids.Contains(k.Key)))faces[pair.Key]=pair.Value;})).ToList()));
            }
            if(SharedPhotoUI.Button(new Rect(area.x+half+8f,area.y,half,control),"MF_CapturePresetSave".Translate(),store!=null&&selected.Any()))
                Find.WindowStack.Add(new Dialog_MFReactionPresetName(store,choices,selected.Select(p=>p.GetUniqueLoadID()).ToList(),faces));
            TooltipHandler.TipRegion(area,"MF_CapturePresetHelp".Translate());
        }
    }

    internal sealed class Dialog_MFReactionPresetName : Window
    {
        private readonly ReactionPresetsGameComponent store;
        private readonly Dictionary<string,int> choices;
        private readonly List<string> ids;
        private readonly Dictionary<string,int> faces;
        private string presetName="";
        private Vector2 scroll;
        protected override float Margin => 0;
        private static float HeadingHeight(float width, float control) => Mathf.Max(control,
            AlbumStyle.TextHeight("MF_CapturePresetName".Translate(),width,GameFont.Medium)+8f);
        private static float BodyHeight(float width, float control) => HeadingHeight(width,control)+10f+control+8f
            +AlbumStyle.TextHeight("MF_CapturePresetNameHelp".Translate(),width)+8f;
        private static float FooterHeight(float width, float control)
        {
            float half=(width-8f)/2f;
            return Mathf.Max(control,new[]{"CancelButton","MF_CapturePresetSave","MF_CapturePresetReplace"}
                .Max(key=>AlbumStyle.TextHeight(key.Translate(),half-12f)+12f));
        }
        public override Vector2 InitialSize
        {
            get
            {
                using(AlbumStyle.Begin())
                {
                    float width=Mathf.Min(520f,UI.screenWidth-40f),control=SharedPhotoUI.ControlHeight;
                    float height=44f+BodyHeight(width-64f,control)+14f+FooterHeight(width-44f,control);
                    return new Vector2(width,Mathf.Min(UI.screenHeight-40f,height));
                }
            }
        }
        internal Dialog_MFReactionPresetName(ReactionPresetsGameComponent store,IDictionary<string,int> choices,List<string> ids,IDictionary<string,int> faces=null)
        {this.store=store;this.choices=new Dictionary<string,int>(choices);this.ids=ids;this.faces=faces==null?null:new Dictionary<string,int>(faces);doCloseX=false;doWindowBackground=false;forcePause=false;absorbInputAroundWindow=true;closeOnAccept=false;}
        public override void DoWindowContents(Rect r)
        {
            using(AlbumStyle.Begin())
            {
                AlbumStyle.DrawPaper(r);var page=r.ContractedBy(22f);float control=SharedPhotoUI.ControlHeight;
                float half=(page.width-8f)/2f,footer=FooterHeight(page.width,control),y=page.yMax-footer;
                var viewport=new Rect(page.x,page.y,page.width,Mathf.Max(1f,page.height-footer-14f));
                float width=viewport.width-20f,heading=HeadingHeight(width,control);
                Widgets.BeginScrollView(viewport,ref scroll,new Rect(0,0,width,Mathf.Max(viewport.height,BodyHeight(width,control))));
                try
                {
                    SharedPhotoUI.Heading(new Rect(0,0,width,heading),"MF_CapturePresetName".Translate());
                    var field=new Rect(0,heading+10f,width,control);
                    presetName=AlbumBridgeUI.TextField(field,presetName,"MFReactionPresetName");
                    float helpHeight=AlbumStyle.TextHeight("MF_CapturePresetNameHelp".Translate(),width)+8f;
                    SharedPhotoUI.Label(new Rect(0,field.yMax+8f,width,helpHeight),"MF_CapturePresetNameHelp".Translate(),true);
                }
                finally{Widgets.EndScrollView();}
                if(SharedPhotoUI.Button(new Rect(page.x,y,half,footer),"CancelButton".Translate()))Close();
                bool exists=store.presets.Any(p=>string.Equals(p.name,presetName.Trim(),System.StringComparison.OrdinalIgnoreCase));
                if(SharedPhotoUI.Button(new Rect(page.x+half+8f,y,half,footer),(exists?"MF_CapturePresetReplace":"MF_CapturePresetSave").Translate(),!string.IsNullOrWhiteSpace(presetName)))
                {store.Save(presetName,choices,ids,faces);Close();}
            }
        }
    }
}
