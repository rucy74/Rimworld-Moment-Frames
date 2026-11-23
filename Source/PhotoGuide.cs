using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    internal static class PhotoGuide
    {
        internal static readonly string[] Topics={"family","birthday","display","background","export"};
        internal static readonly string[][] Images={
            new[]{"place-controls","setup","finish-controls"},new[]{"birthday-place","birthday-setup","birthday"},
            new[]{"display","projector-controls","projector-size"},new[]{"furnishing","background-ui","background-preview"},
            new[]{"album","export-controls","export"}};
        internal static string EmptyKey => SettingsCapturePolicy.Automatic(MomentFramesMod.Settings)?"MF_Learn_EmptyAuto":"MF_Learn_EmptyManual";
        internal static void Open(string topic=null, Window context=null) => Find.WindowStack.Add(new Dialog_PhotoGuide(topic,context));
        internal static void HelpButton(Rect rect,string topic=null,Window context=null)
        {
            if(SharedPhotoUI.Button(rect,"?"))Open(topic,context);
            TooltipHandler.TipRegion(rect,"MF_Learn_Title".Translate());
        }
        /// <summary>Pictures showing the game's buttons exist once per language; a language without its own set shows the English one.</summary>
        internal static string PathFor(string image)
        {
            string root=Path.Combine(LoadedModManager.GetMod<MomentFramesMod>().Content.RootDir,"GuidePhotos");
            string language=(LanguageDatabase.activeLanguage?.folderName??"").Split(' ','(')[0];
            foreach(string folder in new[]{language,"English",""})   // only English and Korean have their own pictures; photos without words sit at the root
                foreach(string extension in new[]{".jpg",".png"})
                {
                    string path=Path.Combine(root,folder,image+extension);
                    if(File.Exists(path))return path;
                }
            return Path.Combine(root,image+".png");
        }
        internal static void Album()
        {
            var def=DefDatabase<MainButtonDef>.GetNamed("MF_Album",false);
            if(def!=null && Current.Game!=null)Find.MainTabsRoot.SetCurrentTab(def);
        }
        internal static void Manual()
        {
            if(Find.CurrentMap==null)return;
            Find.MainTabsRoot.SetCurrentTab(null);
            Find.DesignatorManager.Select(new Designator_TakePhoto());
        }
        internal static List<Building> Places(string topic)
        {
            var map=Find.CurrentMap;
            if(map==null)return new List<Building>();
            return map.listerBuildings.allBuildingsColonist.Where(b=>b.Spawned && !b.Destroyed &&
                (topic=="display"?b is IPhotoDisplay:topic=="background"?b is Building_PhotoStudio s && s.def.defName=="MF_StudioBackdrop":b is Building_PhotoSpot))
                .OrderBy(b=>b.thingIDNumber).ToList();
        }
        internal static void ChoosePlace(string topic,Action<Building> select)
        {
            var places=Places(topic);
            if(places.Count==0){Messages.Message("MF_Learn_NoPlace".Translate(),MessageTypeDefOf.RejectInput,false);return;}
            Find.WindowStack.Add(new FloatMenu(places.Select(b=>new FloatMenuOption("MF_PreparationSelectedSpot".Translate(b.LabelShort,b.Position.x,b.Position.z),()=>select(b))).ToList()));
        }
        internal static void ActivatePlace(string topic,Building b)
        {
            if(b==null || !b.Spawned || b.Destroyed || b.Map!=Find.CurrentMap)return;
            Find.MainTabsRoot.SetCurrentTab(null);CameraJumper.TryJump(b);Find.Selector.ClearSelection();Find.Selector.Select(b);
            if(topic=="family")
            {
                var active=GroupPhoto.ActiveJob(b.Map,b.Position);
                if(active?.IsBirthday==true)return;
                Find.WindowStack.Add(new Dialog_GroupPhotoSetup((Building_PhotoSpot)b,active));
            }
            else if(topic=="background")Find.WindowStack.Add(new Dialog_StudioBackground((Building_PhotoStudio)b));
        }
    }

    // Bundled real photographs, loaded only while this optional guide is open. No update/tick component.
    internal sealed class Dialog_PhotoGuide : Window
    {
        private readonly Window context;
        private readonly Dictionary<string,Texture2D> images=new Dictionary<string,Texture2D>();
        private readonly HashSet<string> missing=new HashSet<string>();
        internal string Topic {get; private set;}
        internal int Step {get; private set;}
        internal bool Enlarged {get; private set;}
        private Rect normalWindow;
        internal int LoadedImages => images.Count;
        internal Rect ContentRect;
        internal Rect ActionMessageRect;
        internal readonly Dictionary<string,Rect> Buttons=new Dictionary<string,Rect>();
        internal readonly List<string> LayoutIssues=new List<string>();
        internal float VisibleBodyHeight;
        private Vector2 scroll;
        private string actionMessage;
        protected override float Margin=>0;
        public override Vector2 InitialSize=>new Vector2(Mathf.Min(1040,UI.screenWidth-32),Mathf.Min(790,UI.screenHeight-32));
        internal Dialog_PhotoGuide(string topic=null,Window context=null)
        {
            this.context=context;Topic=PhotoGuide.Topics.Contains(topic)?topic:null;
            // From a preparation window the guide opens at the step about that window, and comes back to where it was while that window stays open.
            if(context!=null&&Topic!=null)Step=ReferenceEquals(rememberedContext,context)&&rememberedTopic==Topic?rememberedStep:1;
            doCloseX=false;doWindowBackground=false;forcePause=false;absorbInputAroundWindow=true;closeOnAccept=false;
        }
        internal void Select(string topic,int step=0)
        {
            SetEnlarged(false);Topic=PhotoGuide.Topics.Contains(topic)?topic:null;Step=Mathf.Clamp(step,0,2);scroll=Vector2.zero;actionMessage=null;ReleaseImages();
        }
        internal void SetEnlarged(bool value)
        {
            if(value==Enlarged)return;
            if(value){normalWindow=windowRect;windowRect=new Rect(12,12,UI.screenWidth-24,UI.screenHeight-24);}
            else windowRect=normalWindow;
            Enlarged=value;
        }
        private void ReleaseImages(){foreach(var t in images.Values)UnityEngine.Object.Destroy(t);images.Clear();missing.Clear();}
        private static Window rememberedContext;
        private static string rememberedTopic;
        private static int rememberedStep;
        public override void PostClose()
        {
            ReleaseImages();
            if(context!=null&&Topic!=null){rememberedContext=context;rememberedTopic=Topic;rememberedStep=Step;}
            base.PostClose();
        }
        internal Texture2D Picture(string name)
        {
            if(images.TryGetValue(name,out var t))return t;
            if(missing.Contains(name))return null;
            try
            {
                t=new Texture2D(2,2,TextureFormat.RGB24,false);
                if(!t.LoadImage(File.ReadAllBytes(PhotoGuide.PathFor(name))))throw new IOException("Image decode failed");
                images[name]=t;return t;
            }
            catch(Exception e)
            {
                if(t!=null)UnityEngine.Object.Destroy(t);missing.Add(name);
                Log.Warning("[MomentFrames] guide photograph unavailable ("+name+"): "+e.Message);return null;
            }
        }
        private bool Button(string id,Rect r,string label,bool active=true)
        {
            Buttons[id]=r;
            if(!LabelFits(r,label))LayoutIssues.Add(id+": label height exceeds button");
            return SharedPhotoUI.Button(r,label,active);
        }
        internal static bool LabelFits(Rect r,string label)=>AlbumStyle.TextHeight(label,r.width-12)+12<=r.height+1;
        private void Paragraph(ref float y,float width,string text,bool muted=false)
        {
            float h=AlbumStyle.TextHeight(text,width)+8;SharedPhotoUI.Label(new Rect(0,y,width,h),text,muted);y+=h+8;
        }
        // Shipped screenshots are lossless crops; these coordinates refer to those crops.
        internal static Rect PhotoRegion(string name)
        {
            if(name=="background-ui")return new Rect(.03f,.025f,.94f,.14f);
            return new Rect(0,0,1,1);
        }
        private void Picture(Rect r,string name,bool clickable)
        {
            var t=Picture(name);
            if(t!=null)
            {
                if(name=="setup" && !Enlarged && r.height>=220)
                {
                    DrawRegion(new Rect(r.x,r.y,r.width,r.height-66),t,new Rect(.03f,.438f,.915f,.314f));
                    DrawRegion(new Rect(r.x,r.yMax-52,r.width,52),t,new Rect(.03f,.912f,.94f,.06f));
                }
                else if(name=="place-controls" || name=="finish-controls" || name=="birthday-place" || name=="projector-controls")
                {
                    var strip=r;
                    if((name=="finish-controls" || name=="projector-controls") && !Enlarged && r.height>=160)
                    {
                        var result=Picture(name=="finish-controls"?"family":"projector");
                        if(result!=null)DrawRegion(new Rect(r.x,r.y,r.width,r.height-90),result,new Rect(0,0,1,1));
                        strip=new Rect(r.x,r.yMax-82,r.width,82);
                    }
                    Rect region=Enlarged?new Rect(0,0,1,1):PhotoRegion(name);
                    Rect fitted=DrawRegion(strip,t,region);
                    Rect target=name=="projector-controls"?new Rect(.006f,.065f,.12f,.88f):
                        new Rect(name=="birthday-place"?.013f:name=="place-controls"?.21f:.408f,.065f,.188f,.88f);
                    var mark=new Rect(fitted.x+(target.x-region.x)/region.width*fitted.width,
                        fitted.y+(target.y-region.y)/region.height*fitted.height,
                        target.width/region.width*fitted.width,target.height/region.height*fitted.height);
                    var color=new Color(1f,.79f,.28f);
                    Widgets.DrawBoxSolid(new Rect(mark.x-3,mark.y-3,mark.width+6,3),color);
                    Widgets.DrawBoxSolid(new Rect(mark.x-3,mark.yMax,mark.width+6,3),color);
                    Widgets.DrawBoxSolid(new Rect(mark.x-3,mark.y,3,mark.height),color);
                    Widgets.DrawBoxSolid(new Rect(mark.xMax,mark.y,3,mark.height),color);
                }
                else DrawRegion(r,t,Enlarged?new Rect(0,0,1,1):PhotoRegion(name));
            }
            else SharedPhotoUI.Label(r.ContractedBy(10),"MF_Learn_ImageMissing".Translate(),true);
            if(clickable)
            {
                TooltipHandler.TipRegion(r,"MF_Learn_Enlarge".Translate());
                if(Widgets.ButtonInvisible(r))SetEnlarged(true);
            }
        }
        private static Rect DrawRegion(Rect r,Texture2D t,Rect region)
        {
            float aspect=t.width*region.width/(t.height*region.height);
            float w=Mathf.Min(r.width,r.height*aspect),h=w/aspect;
            var fitted=new Rect(r.center.x-w/2,r.center.y-h/2,w,h);
            GUI.DrawTextureWithTexCoords(fitted,t,new Rect(region.x,1-region.yMax,region.width,region.height));
            return fitted;
        }
        public override void DoWindowContents(Rect r)
        {
            using(AlbumStyle.Begin(true))
            {
                Buttons.Clear();LayoutIssues.Clear();ActionMessageRect=Rect.zero;AlbumStyle.DrawPaper(r);var p=r.ContractedBy(22);float h=SharedPhotoUI.ControlHeight;
                h=Mathf.Max(h,AlbumStyle.TextHeight("MF_Learn_ChooseStudio".Translate(),(p.width-10)/2-12)+12,
                    context==null?0:AlbumStyle.TextHeight("MF_Learn_Return".Translate(),p.width-12)+12);
                SharedPhotoUI.Heading(new Rect(p.x,p.y,p.width-h-12,h),"MF_Learn_Title".Translate());
                if(Button("close",new Rect(p.xMax-h,p.y,h,h),"×")){Close();return;}
                if(Enlarged && Topic!=null)
                {
                    ContentRect=new Rect(p.x,p.y+h+10,p.width,p.height-h*2-20);
                    Picture(ContentRect,PhotoGuide.Images[Array.IndexOf(PhotoGuide.Topics,Topic)][Step],false);
                    if(Button("image-back",new Rect(p.x,p.yMax-h,p.width,h),"MF_Learn_Back".Translate()))SetEnlarged(false);
                    return;
                }
                float top=p.y+h+14,nav=p.width>=760?190:0;
                var body=new Rect(p.x+nav+(nav>0?14:0),top,p.width-nav-(nav>0?14:0),p.yMax-top-h-14);
                if(nav>0)
                {
                    float y=top;
                    foreach(string item in new[]{"home"}.Concat(PhotoGuide.Topics))
                    {
                        string label=("MF_Learn_"+item).Translate();float bh=Mathf.Max(h,AlbumStyle.TextHeight(label,nav-16)+18);
                        if(Button("topic-"+item,new Rect(p.x,y,nav,bh),label)){Select(item);return;}
                        if((Topic??"home")==item)Widgets.DrawBoxSolid(new Rect(p.x,y+bh-3,nav,3),AlbumStyle.Theme.Accent);
                        y+=bh+8;
                    }
                }
                else
                {
                    if(Button("topics",new Rect(body.x,body.y,body.width,h),("MF_Learn_"+(Topic??"home")).Translate()))
                        Find.WindowStack.Add(new FloatMenu(new[]{"home"}.Concat(PhotoGuide.Topics).Select(t=>new FloatMenuOption(("MF_Learn_"+t).Translate(),()=>Select(t))).ToList()));
                    body.yMin+=h+10;
                }
                // A failed action must explain itself without asking the player to scroll
                // past the example image. Reserve visible space above the instructions.
                if(actionMessage!=null)
                {
                    float messageH=AlbumStyle.TextHeight(actionMessage,body.width-24)+24;
                    ActionMessageRect=new Rect(body.x,body.y,body.width,messageH);
                    var tint=AlbumStyle.Theme.Accent;tint.a=.12f;
                    Widgets.DrawBoxSolid(ActionMessageRect,tint);
                    SharedPhotoUI.Label(ActionMessageRect.ContractedBy(12),actionMessage);
                    body.yMin+=messageH+10;
                }
                ContentRect=body;
                if(Topic==null)Home(body);else Detail(body);
                var footer=new Rect(p.x,p.yMax-h,p.width,h);float half=(footer.width-10)/2;
                if(context!=null)
                {
                    if(Button("return",footer,"MF_Learn_Return".Translate()))Close();
                }
                else if(Topic==null)
                {
                    if(Button("manual",new Rect(footer.x,footer.y,half,h),"MF_Learn_Manual".Translate(),Find.CurrentMap!=null)){Close();PhotoGuide.Manual();}
                    if(Button("album",new Rect(footer.x+half+10,footer.y,half,h),"MF_Learn_Album".Translate(),Current.Game!=null)){Close();PhotoGuide.Album();}
                }
                else
                {
                    string key=Topic=="export"?"MF_Export":Topic=="family"?"MF_Learn_ChoosePlace":Topic=="background"?"MF_Learn_ChooseStudio":"MF_Learn_SelectObject";
                    if(Button("action",new Rect(footer.x,footer.y,half,h),key.Translate(),Current.Game!=null))RunAction();
                    if(Button("album",new Rect(footer.x+half+10,footer.y,half,h),"MF_Learn_Album".Translate(),Current.Game!=null)){Close();PhotoGuide.Album();}
                }
            }
        }
        private void Home(Rect area)
        {
            float width=area.width-20,control=SharedPhotoUI.ControlHeight;
            bool wide=width>=620;float photoW=wide?width*.43f:width;
            float textW=wide?width-photoW-20:width;
            string title="MF_Learn_FirstTitle".Translate(),body="MF_Learn_FirstBody".Translate();
            float copyH=AlbumStyle.TextHeight(title,textW)+AlbumStyle.TextHeight(body,textW)+38+control;
            float heroH=wide?Mathf.Max(220,copyH):150+copyH;
            int cols=width>=530?2:1;float w=(width-(cols-1)*12)/cols;
            float rowH=Mathf.Max(66,PhotoGuide.Topics.Skip(1).Max(t=>AlbumStyle.TextHeight(("MF_Learn_"+t).Translate(),w-24))+20);
            string more="MF_Learn_More".Translate(),hint="MF_Learn_ManualHint".Translate();
            float total=heroH+34+AlbumStyle.TextHeight(more,width)+Mathf.Ceil(4f/cols)*(rowH+10)+AlbumStyle.TextHeight(hint,width)+24;
            Widgets.BeginScrollView(area,ref scroll,new Rect(0,0,width,total));
            Picture(new Rect(0,0,photoW,wide?heroH:142),"family",false);
            float x=wide?photoW+20:0,y=wide?0:150;
            float titleH=AlbumStyle.TextHeight(title,textW);
            SharedPhotoUI.Label(new Rect(x,y,textW,titleH+8),title);y+=titleH+14;
            float bodyH=AlbumStyle.TextHeight(body,textW);
            SharedPhotoUI.Label(new Rect(x,y,textW,bodyH+8),body);y+=bodyH+18;
            bool start=SharedPhotoUI.Button(new Rect(x,y,textW,control),"MF_Learn_FirstAction".Translate());
            y=heroH+20;Paragraph(ref y,width,more,true);
            string chosenTopic=start?"family":null;
            for(int i=1;i<PhotoGuide.Topics.Length;i++)
            {
                string topic=PhotoGuide.Topics[i];int j=i-1;
                if(SharedPhotoUI.Button(new Rect((j%cols)*(w+12),y+(j/cols)*(rowH+10),w,rowH),("MF_Learn_"+topic).Translate()))chosenTopic=topic;
            }
            y+=Mathf.Ceil(4f/cols)*(rowH+10)+8;Paragraph(ref y,width,hint,true);
            Widgets.EndScrollView();
            if(chosenTopic!=null)Select(chosenTopic);
        }
        private void Detail(Rect area)
        {
            int at=Array.IndexOf(PhotoGuide.Topics,Topic);float control=SharedPhotoUI.ControlHeight;
            string title=("MF_Learn_"+Topic+"_"+Step+"Title").Translate();
            string body=("MF_Learn_"+Topic+"_"+Step).Translate();
            float half=(area.width-10)/2;
            if(Button("prev",new Rect(area.x,area.yMax-control,half,control),"MF_Learn_Prev".Translate(),Step>0)){Select(Topic,Step-1);return;}
            if(Button("next",new Rect(area.x+half+10,area.yMax-control,half,control),"MF_Learn_Next".Translate(),Step<2)){Select(Topic,Step+1);return;}
            var scrollArea=new Rect(area.x,area.y,area.width,area.height-control-10);float width=scrollArea.width-20;
            string step="MF_Learn_StepTitle".Translate(Step+1,title);
            float titleH=AlbumStyle.TextHeight(step,width),bodyH=AlbumStyle.TextHeight(body,width);
            float captionH=AlbumStyle.TextHeight("MF_Learn_PhotoCaption".Translate(),width);
            float imageH=Mathf.Clamp(scrollArea.height-titleH-bodyH-captionH-64,72,300);
            if(PhotoGuide.Images[at][Step]=="background-ui")imageH=Mathf.Min(imageH,110);
            float total=titleH+bodyH+captionH+imageH+64;
            Widgets.BeginScrollView(scrollArea,ref scroll,new Rect(0,0,width,total));float y=0;
            Paragraph(ref y,width,step);
            Paragraph(ref y,width,body);
            VisibleBodyHeight=Mathf.Min(bodyH,Mathf.Max(0,scrollArea.height-titleH-24));
            Paragraph(ref y,width,"MF_Learn_PhotoCaption".Translate(),true);
            Picture(new Rect(0,y,width,imageH),PhotoGuide.Images[at][Step],true);y+=imageH+6;
            Widgets.EndScrollView();
        }
        internal void RunAction()
        {
            if(Current.Game==null)return;
            if(Topic=="export")
            {
                if(PhotoStore.Instance==null || !PhotoStore.Instance.Records.Any(r=>r.HasFile)){actionMessage="MF_Learn_ExportEmpty".Translate();return;}
                AlbumExport.Open(PhotoStore.Instance);Close();return;
            }
            if(PhotoGuide.Places(Topic).Count==0){actionMessage=("MF_Learn_Missing_"+Topic).Translate();return;}
            PhotoGuide.ChoosePlace(Topic,b=>{Close();PhotoGuide.ActivatePlace(Topic,b);});
        }
    }
}
