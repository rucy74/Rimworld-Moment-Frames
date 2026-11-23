using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    internal sealed class PhotoPickerFilters
    {
        internal string Search="";
        internal bool FavoritesOnly,NewestFirst=true;
        internal int Year;
        internal string Key => Search+"\n"+FavoritesOnly+"/"+Year+"/"+NewestFirst;
        private string cacheKey;
        private List<PhotoRecord> source,visible;
        internal List<PhotoRecord> Apply(List<PhotoRecord> records)
        {
            if(ReferenceEquals(source,records) && cacheKey==Key)return visible;
            source=records;cacheKey=Key;
            var q=records.Where(r=>(!FavoritesOnly||r.favorite)&&(Year==0||r.year==Year)&&Dialog_ChoosePhoto.MatchesSearch(r,Search));
            visible=(NewestFirst?q.OrderByDescending(r=>r.ticksAbs):q.OrderBy(r=>r.ticksAbs)).ThenBy(r=>r.id).ToList();
            return visible;
        }
        internal float Height => SharedPhotoUI.ControlHeight*2+48;
        internal bool Draw(Rect area,List<PhotoRecord> records,IEnumerable<string> selected)
        {
            string before=Key;float c=SharedPhotoUI.ControlHeight;
            Search=AlbumBridgeUI.TextField(new Rect(area.x,area.y,area.width-c-8,c),Search,"MF_UX_PickerSearch");
            if(Search.Length>100)Search=Search.Substring(0,100);
            if(Search.NullOrEmpty())AlbumBridgeUI.InputHint(new Rect(area.x+8,area.y+6,area.width-c-24,c-12),"MF_DesignNav_SearchTip".Translate());
            if(SharedPhotoUI.Button(new Rect(area.xMax-c,area.y,c,c),"×"))Search="";
            float third=(area.width-16)/3,y=area.y+c+8;
            if(SharedPhotoUI.Button(new Rect(area.x,y,third,c),(FavoritesOnly?"MF_FavoritesOnly":"MF_FavoriteAllPhotos").Translate()))FavoritesOnly=!FavoritesOnly;
            if(SharedPhotoUI.Button(new Rect(area.x+third+8,y,third,c),"MF_Filter_Year".Translate()+": "+(Year==0?"MF_All".Translate().ToString():"MF_YearLabel".Translate(Year).ToString())))
            {
                var options=new List<FloatMenuOption>{new FloatMenuOption("MF_All".Translate(),()=>Year=0)};
                options.AddRange(records.Select(r=>r.year).Distinct().OrderByDescending(v=>v).Select(v=>new FloatMenuOption("MF_YearLabel".Translate(v),()=>Year=v)));
                Find.WindowStack.Add(new FloatMenu(options));
            }
            if(SharedPhotoUI.Button(new Rect(area.x+2*(third+8),y,third,c),(NewestFirst?"MF_UX_Newest":"MF_UX_Oldest").Translate()))NewestFirst=!NewestFirst;
            var shown=Apply(records);var ids=new HashSet<string>(selected??Enumerable.Empty<string>());
            int valid=records.Count(r=>ids.Contains(r.id)),hidden=valid-shown.Count(r=>ids.Contains(r.id));
            SharedPhotoUI.Label(new Rect(area.x,y+c+6,area.width,32),"MF_UX_PickerCount".Translate(shown.Count,records.Count,valid,hidden),true);
            return before!=Key;
        }
    }
}
