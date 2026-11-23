using System;
using System.IO;
using System.Linq;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    internal sealed class Dialog_StudioBackground : Window
    {
        private readonly Building_PhotoStudio studio;
        private Texture2D picture;private float zoom=1,x=.5f,y=.5f;private string status="";
        public override Vector2 InitialSize=>new Vector2(Mathf.Min(900,UI.screenWidth-40),Mathf.Min(730,UI.screenHeight-40));
        internal Dialog_StudioBackground(Building_PhotoStudio studio)
        {
            this.studio=studio;doCloseX=true;forcePause=false;absorbInputAroundWindow=true;closeOnAccept=false;
            if(!studio.BackgroundFile.NullOrEmpty()){Load(StudioBackgrounds.PathFor(studio.BackgroundFile));zoom=studio.BackgroundZoom;x=studio.BackgroundX;y=studio.BackgroundY;if(picture!=null)status="MF_BackgroundCurrent".Translate();}
        }
        public override void PostClose(){if(picture!=null)UnityEngine.Object.Destroy(picture);base.PostClose();}
        internal void Load(string path) => Load(path, Path.GetFileName(path));
        private void Load(string path, string label)
        {
            Texture2D loaded=null;
            try{loaded=StudioBackgrounds.Load(path);if(picture!=null)UnityEngine.Object.Destroy(picture);picture=loaded;loaded=null;zoom=1;x=y=.5f;status=label;}
            catch(Exception e){status=e.Message;}
            finally{if(loaded!=null)UnityEngine.Object.Destroy(loaded);}
        }
        public override void DoWindowContents(Rect r)
        {
            if(studio.Destroyed||!studio.Spawned){Close();return;}
            using(AlbumStyle.Begin(true))
            {
                AlbumStyle.DrawPaper(r);var p=r.ContractedBy(16);float third=(p.width-16)/3;
                SharedPhotoUI.Heading(new Rect(p.x,p.y,p.width-100,38),"MF_Background".Translate());
                PhotoGuide.HelpButton(new Rect(p.xMax-88,p.y,38,38),"background",this);
                if(SharedPhotoUI.Button(new Rect(p.x,p.y+46,third,40),"MF_BackgroundAlbum".Translate()))
                    Find.WindowStack.Add(new Dialog_ChoosePhoto("MF_Background".Translate(),null,id=>{var store=PhotoStore.Instance;var record=store?.Records.FirstOrDefault(a=>a.id==id);if(record!=null)Load(store.FilePath(record),record.title.NullOrEmpty()?record.dateLabel:record.title);}){forcePause=false});
                if(SharedPhotoUI.Button(new Rect(p.x+third+8,p.y+46,third,40),"MF_BackgroundFile".Translate()))Find.WindowStack.Add(new Dialog_StudioImageFile(Load));
                if(SharedPhotoUI.Button(new Rect(p.x+2*(third+8),p.y+46,third,40),"MF_BackgroundReset".Translate())){zoom=1;x=y=.5f;}
                string help="MF_BackgroundHelp".Translate();float helpHeight=AlbumStyle.TextHeight(help,p.width)+8;
                float height=Mathf.Max(90,p.height-240-helpHeight);var area=new Rect(p.x,p.y+96,p.width,height);
                var source=ContentFinder<Texture2D>.Get("Things/Building/MomentFrames/StudioBackdrop");
                float fit=Mathf.Min(area.width/source.width,area.height/source.height);
                var cloth=new Rect(area.center.x-source.width*fit/2,area.center.y-source.height*fit/2,source.width*fit,source.height*fit);
                GUI.DrawTexture(cloth,source);
                var panel=StudioBackgrounds.Panel;
                var target=new Rect(cloth.x+panel.x*cloth.width,cloth.y+(1-panel.yMax)*cloth.height,panel.width*cloth.width,panel.height*cloth.height);
                if(picture!=null)
                {
                    GUI.DrawTextureWithTexCoords(target,picture,StudioBackgrounds.Crop(picture.width,picture.height,zoom,x,y));
                    if(Mouse.IsOver(target)&&Event.current.type==EventType.MouseDrag&&Event.current.button==0)
                    {
                        var crop=StudioBackgrounds.Crop(picture.width,picture.height,zoom,x,y);
                        x=Mathf.Clamp01(x-Event.current.delta.x/target.width*crop.width/Mathf.Max(.001f,1-crop.width));
                        y=Mathf.Clamp01(y+Event.current.delta.y/target.height*crop.height/Mathf.Max(.001f,1-crop.height));Event.current.Use();
                    }
                }
                float sy=area.yMax+8;
                SharedPhotoUI.Label(new Rect(p.x,sy,190,32),"MF_BackgroundZoom".Translate(zoom.ToString("0.0")));
                zoom=Widgets.HorizontalSlider(new Rect(p.x+198,sy+4,p.width-198,24),zoom,1,4);
                SharedPhotoUI.Label(new Rect(p.x,sy+38,p.width,helpHeight),help);
                SharedPhotoUI.Label(new Rect(p.x,p.yMax-76,p.width,28),status.Length>65?status.Substring(0,62)+"…":status,true);
                TooltipHandler.TipRegion(new Rect(p.x,p.yMax-76,p.width,28),status);
                float half=(p.width-8)/2;
                if(SharedPhotoUI.Button(new Rect(p.x,p.yMax-42,half,42),"MF_BackgroundClear".Translate(),!studio.BackgroundFile.NullOrEmpty()))
                    Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("MF_BackgroundClearConfirm".Translate(),()=>{studio.SetPhotoBackground(null,1,.5f,.5f);Close();},true));
                if(SharedPhotoUI.Button(new Rect(p.x+half+8,p.yMax-42,half,42),"MF_PositionsApply".Translate(),picture!=null))
                {
                    try{string file=StudioBackgrounds.Import(picture);studio.SetPhotoBackground(file,zoom,x,y);Close();}
                    catch(Exception e){status=e.Message;}
                }
            }
        }
    }

    // Portable in-game file chooser: no Windows-only dependency, no album registration or source-file writes.
    internal sealed class Dialog_StudioImageFile : Window
    {
        private string path,loadedPath,error="";private string[] directories=Array.Empty<string>(),files=Array.Empty<string>();
        private readonly Action<string> pick;private readonly bool pngOnly;private Vector2 scroll;
        public override Vector2 InitialSize=>new Vector2(Mathf.Min(820,UI.screenWidth-40),Mathf.Min(650,UI.screenHeight-40));
        internal Dialog_StudioImageFile(Action<string> pick, bool pngOnly = false)
        {this.pick=pick;this.pngOnly=pngOnly;path=Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);if(path.NullOrEmpty()||!Directory.Exists(path))path=GenFilePaths.SaveDataFolderPath;doCloseX=true;forcePause=false;absorbInputAroundWindow=true;closeOnAccept=false;Refresh();}
        private void Refresh()
        {
            try
            {
                path=Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')));
                if(File.Exists(path)){pick(path);Close();return;}
                directories=Directory.GetDirectories(path).OrderBy(s=>s).ToArray();
                files=Directory.GetFiles(path).Where(s=>(pngOnly ? new[]{".png"} : new[]{".png",".jpg",".jpeg"}).Contains(Path.GetExtension(s).ToLowerInvariant())).OrderBy(s=>s).ToArray();
                loadedPath=path;error="";scroll=Vector2.zero;
            }
            catch(Exception e){error=e.Message;}
        }
        public override void DoWindowContents(Rect r)
        {
            Widgets.Label(new Rect(0,0,r.width-30,32),(pngOnly?"MF_Imports_AddFile":"MF_BackgroundFile").Translate());
            path=Widgets.TextField(new Rect(0,40,r.width-100,34),path);
            if(Widgets.ButtonText(new Rect(r.width-92,40,92,34),"MF_BackgroundOpen".Translate()))Refresh();
            if(Widgets.ButtonText(new Rect(0,82,140,32),"MF_BackgroundParent".Translate())){var parent=Directory.GetParent(loadedPath??path);if(parent!=null){path=parent.FullName;Refresh();}}
            Widgets.Label(new Rect(150,82,r.width-150,36),(pngOnly?"PNG · ":"PNG / JPG · ")+error);
            var area=new Rect(0,126,r.width,r.height-126);
            Widgets.BeginScrollView(area,ref scroll,new Rect(0,0,area.width-20,(directories.Length+files.Length)*38));float y=0;
            foreach(var dir in directories)
            {if(Widgets.ButtonText(new Rect(0,y,area.width-20,34),"[ "+Path.GetFileName(dir)+" ]")){path=dir;Refresh();break;}y+=38;}
            foreach(var file in files)
            {if(Widgets.ButtonText(new Rect(0,y,area.width-20,34),Path.GetFileName(file))){pick(file);Close();}y+=38;}
            Widgets.EndScrollView();
        }
    }
}

