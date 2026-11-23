using System;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    internal sealed class Dialog_PhotoLocation : Window
    {
        private readonly Map map;
        private readonly Building_PhotoSpot selected;
        private readonly Action<Building_PhotoSpot,bool> choose;
        private Vector2 scroll;
        public override Vector2 InitialSize => new Vector2(Mathf.Min(740f,UI.screenWidth-40f),Mathf.Min(660f,UI.screenHeight-40f));
        internal Dialog_PhotoLocation(Map map,Building_PhotoSpot selected,Action<Building_PhotoSpot,bool> choose)
        {
            this.map=map;this.selected=selected;this.choose=choose;
            doCloseX=true;doWindowBackground=false;forcePause=false;absorbInputAroundWindow=true;closeOnAccept=false;
        }
        private void Select(Building_PhotoSpot spot,bool preview)
        {
            if(!spot.Spawned || spot.Destroyed || spot.Map!=map)return;
            Close();choose(spot,preview);
        }
        public override void DoWindowContents(Rect rect)
        {
            using(AlbumStyle.Begin(true))
            {
                AlbumStyle.DrawPaper(rect);var page=rect.ContractedBy(18f);
                SharedPhotoUI.Heading(new Rect(page.x,page.y,page.width-30f,42f),"MF_LocationChoose".Translate());
                string help="MF_LocationHelp".Translate();float helpHeight=AlbumStyle.TextHeight(help,page.width)+8f;
                SharedPhotoUI.Label(new Rect(page.x,page.y+48f,page.width,helpHeight),help);
                var area=new Rect(page.x,page.y+56f+helpHeight,page.width,page.height-helpHeight-62f);
                var spots=map.listerBuildings.AllBuildingsColonistOfClass<Building_PhotoSpot>()
                    .Where(s=>s.Spawned&&!s.Destroyed).OrderBy(s=>s.thingIDNumber).ToList();
                if(spots.Count==0){SharedPhotoUI.Label(area,"MF_BirthdayNoSpot".Translate());return;}
                var locations=map.GetComponent<PhotoLocationMapComponent>();
                float width=area.width-20f,third=(width-16f)/3f;
                float buttonHeight=Mathf.Max(SharedPhotoUI.ControlHeight,new[]{"MF_LocationSelect","MF_LocationSelectPreview","MF_LocationRename"}
                    .Max(key=>AlbumStyle.TextHeight(key.Translate(),third-12f)+12f));
                Func<Building_PhotoSpot,string> contextFor=spot=>PhotoLocationMapComponent.Context(spot)
                    +(locations.BirthdaySpot==spot?" · "+"MF_BirthdayDefaultSpot".Translate():"");
                float nameHeight=Mathf.Max(28f,spots.Max(s=>AlbumStyle.TextHeight(PhotoLocationMapComponent.DisplayName(s),width-96f)));
                float contextHeight=Mathf.Max(28f,spots.Max(s=>AlbumStyle.TextHeight(contextFor(s),width-96f)));
                float headerHeight=Mathf.Max(76f,nameHeight+contextHeight+18f);
                float rowHeight=headerHeight+buttonHeight+18f;
                Widgets.BeginScrollView(area,ref scroll,new Rect(0,0,width,spots.Count*rowHeight));
                for(int i=0;i<spots.Count;i++)
                {
                    var spot=spots[i];float y=i*rowHeight;
                    var nameRect=new Rect(82f,y+7f,width-90f,nameHeight);
                    if(spot==selected)Widgets.DrawBoxSolid(new Rect(0,y,width,rowHeight-8f),new Color(.55f,.43f,.3f,.12f));
                    Widgets.ThingIcon(new Rect(8f,y+6f,62f,62f),spot);
                    SharedPhotoUI.Label(nameRect,PhotoLocationMapComponent.DisplayName(spot));
                    SharedPhotoUI.Label(new Rect(82f,y+11f+nameHeight,width-90f,contextHeight),contextFor(spot),true);
                    TooltipHandler.TipRegion(new Rect(0,y,width,headerHeight),PhotoLocationMapComponent.DisplayName(spot)+"\n"
                        +spot.LabelShort+"\n"+PhotoLocationMapComponent.CoordinateHint(spot.Position));
                    if(SharedPhotoUI.Button(new Rect(0,y+headerHeight,third,buttonHeight),"MF_LocationSelect".Translate())){Select(spot,false);break;}
                    if(SharedPhotoUI.Button(new Rect(third+8f,y+headerHeight,third,buttonHeight),"MF_LocationSelectPreview".Translate())){Select(spot,true);break;}
                    if(SharedPhotoUI.Button(new Rect(2f*(third+8f),y+headerHeight,third,buttonHeight),"MF_LocationRename".Translate()))
                        Find.WindowStack.Add(new Dialog_PhotoLocationName(spot));
                }
                Widgets.EndScrollView();
            }
        }
    }

    internal sealed class Dialog_PhotoLocationName : Window
    {
        private readonly Building_PhotoSpot spot;
        private readonly PhotoLocationMapComponent locations;
        private string name;
        public override Vector2 InitialSize => new Vector2(Mathf.Min(520f,UI.screenWidth-40f),290f);
        internal Dialog_PhotoLocationName(Building_PhotoSpot spot)
        {
            this.spot=spot;locations=spot.Map.GetComponent<PhotoLocationMapComponent>();name=locations.CustomName(spot);
            doCloseX=true;doWindowBackground=false;forcePause=false;absorbInputAroundWindow=true;closeOnAccept=false;
        }
        public override void DoWindowContents(Rect rect)
        {
            using(AlbumStyle.Begin(true))
            {
                AlbumStyle.DrawPaper(rect);var page=rect.ContractedBy(18f);
                SharedPhotoUI.Heading(new Rect(page.x,page.y,page.width-30f,40f),"MF_LocationRename".Translate());
                name=AlbumBridgeUI.TextField(new Rect(page.x,page.y+48f,page.width,38f),name,"MF_PhotoLocationName");
                if(name.Length>48)name=name.Substring(0,48);
                SharedPhotoUI.Label(new Rect(page.x,page.y+96f,page.width,70f),"MF_LocationRenameHelp".Translate());
                bool valid=spot.Spawned&&!spot.Destroyed;
                if(SharedPhotoUI.Button(new Rect(page.x,page.yMax-42f,page.width,42f),"MF_LocationSaveName".Translate(),valid))
                {locations.Rename(spot,name);Close();}
            }
        }
    }
}
