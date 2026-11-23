using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    /// <summary>Read-only scan and preview first. Only the user's checked items can commit new album records/edits.</summary>
    internal sealed class Dialog_ExternalPhotos : Window
    {
        private readonly PhotoStore store;
        private readonly bool otherDaysOnly;
        private List<ExternalPhotoCandidate> candidates=new List<ExternalPhotoCandidate>();
        private readonly HashSet<string> selected=new HashSet<string>();
        private Vector2 scroll;
        private int previewIndex=-1;
        private Texture2D preview;
        private string status="";
        private Task<List<ExternalPhotoCandidate>> scan;
        private CancellationTokenSource cancel;
        private int scanned;
        private bool closed;
        internal bool Busy => scan != null;
        public override Vector2 InitialSize=>new Vector2(Mathf.Min(1240f,UI.screenWidth-40f),Mathf.Min(850f,UI.screenHeight-40f));
        internal Dialog_ExternalPhotos(bool otherDaysOnly)
        {store=PhotoStore.Instance;this.otherDaysOnly=otherDaysOnly;doCloseX=true;absorbInputAroundWindow=true;forcePause=false;closeOnAccept=false;}
        public override void PreOpen(){base.PreOpen();Refresh();}
        public override void PostClose()
        {
            closed=true;cancel?.Cancel();cancel?.Dispose();
            scan?.ContinueWith(t=>{var ignored=t.Exception;},TaskContinuationOptions.OnlyOnFaulted);
            Release();base.PostClose();
        }
        private void Release(){if(preview!=null)UnityEngine.Object.Destroy(preview);preview=null;previewIndex=-1;}
        private void Refresh()
        {
            if(Busy||closed||store==null||!ReferenceEquals(store,PhotoStore.Instance))return;
            Release();selected.Clear();candidates.Clear();status="";scanned=0;
            try
            {
                cancel?.Dispose();cancel=new CancellationTokenSource();var token=cancel.Token;
                string folder=store.FolderPath;var entries=ExternalPhotoImport.ScanReferences(store).ToArray();
                scan=Task.Run(()=>ExternalPhotoFiles.Scan(folder,entries,ExternalPhotoFiles.History(folder),token,n=>Interlocked.Exchange(ref scanned,n)),token);
            }
            catch(Exception e){candidates.Clear();status=e.Message;}
        }
        public override void WindowUpdate()
        {
            base.WindowUpdate();if(closed)return;
            if(!ReferenceEquals(store,PhotoStore.Instance)){Close();return;}
            if(scan?.IsCompleted!=true)return;
            var task=scan;scan=null;
            if(task.Status==TaskStatus.RanToCompletion)
            {
                try
                {
                    candidates=task.Result;ExternalPhotoImport.CompleteScan(candidates);
                    if(otherDaysOnly)candidates=candidates.Where(c=>c.kind==ExternalPhotoKind.OtherDay).ToList();
                }
                catch(Exception e){candidates.Clear();status=e.Message;}
            }
            else status=task.IsCanceled?"MF_StorageCancelled".Translate().ToString():task.Exception?.GetBaseException().Message;
        }
        private void SelectPreview(int index)
        {
            if(index==previewIndex)return;Release();previewIndex=index;
            var candidate=candidates[index];if(candidate.path.NullOrEmpty()||!File.Exists(candidate.path))return;
            Texture2D full=null;RenderTexture resized=null;var previous=RenderTexture.active;
            try
            {
                if(new FileInfo(candidate.path).Length>ExternalPhotoFiles.MaximumBytes){status="MF_Extras_ImageTooLarge".Translate();return;}
                byte[] bytes=File.ReadAllBytes(candidate.path);
                if(!ExternalPhotoFiles.Png(bytes,out _,out bool tooLarge)){status=(tooLarge?"MF_Extras_ImageTooLarge":"MF_Extras_InvalidPng").Translate();return;}
                full=new Texture2D(2,2,TextureFormat.RGB24,false);if(!full.LoadImage(bytes,false))return;
                float scale=Mathf.Min(1f,1024f/Mathf.Max(full.width,full.height));int width=Mathf.Max(1,Mathf.RoundToInt(full.width*scale)),height=Mathf.Max(1,Mathf.RoundToInt(full.height*scale));
                resized=RenderTexture.GetTemporary(width,height,0,RenderTextureFormat.ARGB32);Graphics.Blit(full,resized);RenderTexture.active=resized;
                preview=new Texture2D(width,height,TextureFormat.RGB24,false);preview.ReadPixels(new Rect(0,0,width,height),0,0);preview.Apply(false,false);
            }
            catch(Exception e){status=e.Message;}
            finally{RenderTexture.active=previous;if(resized!=null)RenderTexture.ReleaseTemporary(resized);if(full!=null)UnityEngine.Object.Destroy(full);}
        }
        private static string KindLabel(ExternalPhotoCandidate c)=>c.duplicate?"MF_Extras_Duplicate".Translate().ToString():("MF_Extras_"+c.kind).Translate().ToString();
        public override void DoWindowContents(Rect rect)
        {
            using(AlbumStyle.Begin())
            {
                AlbumStyle.DrawPaper(rect);GUI.color=AlbumStyle.Theme.Ink;
                Text.Font=GameFont.Medium;AlbumStyle.Label(new Rect(18f,12f,rect.width-64f,44f),(otherDaysOnly?"MF_Extras_OtherDays":"MF_Extras_RefreshFolder").Translate());Text.Font=GameFont.Small;
                AlbumStyle.Label(new Rect(18f,60f,rect.width-36f,92f),"MF_Extras_ScanHint".Translate());
                float leftWidth=Mathf.Min(460f,rect.width*.42f);Rect left=new Rect(18f,160f,leftWidth,rect.height-236f);
                Rect view=new Rect(0,0,left.width-20f,Mathf.Max(left.height,candidates.Count*88f));Widgets.BeginScrollView(left,ref scroll,view);
                try
                {
                    for(int i=0;i<candidates.Count;i++)
                    {
                        var c=candidates[i];Rect row=new Rect(0,i*88f,view.width,82f);
                        GUI.color=Color.white;Widgets.DrawBoxSolid(row,new Color(.60f,.46f,.32f,i==previewIndex ? .19f : .08f));
                        bool can=c.kind!=ExternalPhotoKind.MissingImage&&!c.duplicate,check=selected.Contains(c.Key);
                        if(can){bool before=check;Widgets.Checkbox(new Vector2(8f,row.y+27f),ref check);if(check!=before){if(check)selected.Add(c.Key);else selected.Remove(c.Key);}}
                        GUI.color=AlbumStyle.Theme.Ink;AlbumStyle.Label(new Rect(44f,row.y+5f,row.width-48f,40f),c.history?.title.NullOrEmpty()==false?c.history.title:c.file);
                        GUI.color=AlbumStyle.Theme.MutedInk;AlbumStyle.Label(new Rect(44f,row.y+45f,row.width-48f,30f),KindLabel(c));GUI.color=Color.white;
                        if(Widgets.ButtonInvisible(new Rect(42f,row.y,row.width-42f,row.height)))SelectPreview(i);
                        TooltipHandler.TipRegion(row,c.file+"\n"+KindLabel(c));
                    }
                }
                finally{Widgets.EndScrollView();}
                Rect right=new Rect(left.xMax+20f,160f,rect.width-left.xMax-38f,rect.height-236f);
                if(preview!=null)
                {
                    GUI.color=Color.white;Rect image=new Rect(right.x,right.y,right.width,Mathf.Max(40f,right.height-94f));GUI.DrawTexture(image,preview,ScaleMode.ScaleToFit);
                    GUI.color=AlbumStyle.Theme.Ink;var current=candidates[previewIndex];
                    AlbumStyle.Label(new Rect(right.x,image.yMax+8f,right.width,84f),current.history?.Exact==true?"MF_Extras_KnownHistory".Translate():"MF_Extras_UncertainHistory".Translate());
                }
                else{GUI.color=AlbumStyle.Theme.MutedInk;AlbumStyle.Label(right,Busy?"MF_Extras_Scanning".Translate(Volatile.Read(ref scanned)):(candidates.Count==0?"MF_Extras_NoCandidates":"MF_Extras_SelectPreview").Translate());}
                GUI.color=Color.white;
                if(AlbumStyle.Button(new Rect(18f,rect.height-66f,160f,42f),"MF_Extras_Rescan".Translate(),!Busy))Refresh();
                if(AlbumStyle.Button(new Rect(rect.width-300f,rect.height-66f,282f,42f),"MF_Extras_ImportSelected".Translate(selected.Count),!Busy&&selected.Count>0&&ReferenceEquals(store,PhotoStore.Instance)))
                {
                    int imported=0,failed=0;string reason="";
                    foreach(var c in candidates.Where(c=>selected.Contains(c.Key)).ToList())
                        if(ExternalPhotoImport.Import(store,c,out string error))imported++;else{failed++;reason=error;}
                    Refresh();status="MF_Extras_ImportResult".Translate(imported,failed).ToString()+(reason.NullOrEmpty()?"":" · "+reason);
                }
                if(!status.NullOrEmpty()){GUI.color=AlbumStyle.Theme.Ink;TooltipHandler.TipRegion(new Rect(184f,rect.height-66f,Mathf.Max(1,rect.width-494f),42f),status);AlbumStyle.Label(new Rect(184f,rect.height-66f,Mathf.Max(1,rect.width-494f),42f),status);}GUI.color=Color.white;
            }
        }
    }
}
