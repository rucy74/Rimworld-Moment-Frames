using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    public sealed class PhotoLocationMapComponent : MapComponent
    {
        private Building_PhotoSpot birthday;
        private bool chosen;
        private Dictionary<int,string> names = new Dictionary<int,string>();
        public PhotoLocationMapComponent(Map map) : base(map) { }
        internal bool IsDefault(Building_PhotoSpot spot) => chosen && birthday == spot;
        internal void Select(Building_PhotoSpot spot) { birthday = spot; chosen = spot != null; }
        internal string CustomName(Building_PhotoSpot spot) => spot != null && names.TryGetValue(spot.thingIDNumber,out var name) ? name : "";
        internal void Rename(Building_PhotoSpot spot,string value)
        {
            if(spot==null || spot.Map!=map)return;
            string name=(value??"").Replace('\r',' ').Replace('\n',' ').Trim();
            if(name.Length>48)name=name.Substring(0,48);
            if(name.NullOrEmpty())names.Remove(spot.thingIDNumber);else names[spot.thingIDNumber]=name;
        }
        internal static string DisplayName(Building_PhotoSpot spot)
        {
            if(spot==null)return "MF_LocationFree".Translate();
            string name=spot.Map?.GetComponent<PhotoLocationMapComponent>()?.CustomName(spot);
            if(!name.NullOrEmpty())return name;
            var peers=spot.Map?.listerBuildings.AllBuildingsColonistOfClass<Building_PhotoSpot>()
                .Where(s=>s.def==spot.def).OrderBy(s=>s.thingIDNumber).ToList();
            return peers!=null && peers.Count>1 ? "MF_LocationNumbered".Translate(spot.LabelShort,peers.IndexOf(spot)+1).ToString() : spot.LabelShort;
        }
        internal static string Context(Building_PhotoSpot spot)
        {
            var room=spot?.Map==null ? null : spot.Position.GetRoom(spot.Map);
            return room==null || room.PsychologicallyOutdoors ? "MF_LocationOutdoors".Translate().ToString() : room.GetRoomRoleLabel();
        }
        internal static string CoordinateHint(IntVec3 position) => "MF_LocationCoordinates".Translate(position.x,position.z);
        internal Building_PhotoSpot BirthdaySpot => chosen
            ? birthday != null && birthday.Spawned && !birthday.Destroyed && birthday.Map == map && birthday.Faction == Faction.OfPlayer ? birthday : null
            : map.listerBuildings.AllBuildingsColonistOfClass<Building_PhotoSpot>().OrderBy(s => s.thingIDNumber).FirstOrDefault();
        public override void ExposeData()
        {
            Scribe_References.Look(ref birthday, "mfBirthdaySpot"); Scribe_Values.Look(ref chosen, "mfBirthdaySpotChosen");
            Scribe_Collections.Look(ref names,"mfPhotoLocationNames",LookMode.Value,LookMode.Value);
            if(Scribe.mode==LoadSaveMode.PostLoadInit)names=names??new Dictionary<int,string>();
        }
    }

    // Slots are local to the shooting direction, so a rotated studio preserves left/right and front/back.
    internal static class PhotoPositions
    {
        internal static IntVec3 Local(IntVec3 cell, IntVec3 origin, Rot4 direction) => Building_PhotoStudio.Rotate(cell - origin, new Rot4((4-direction.AsInt)%4));
        internal static Dictionary<string,IntVec3> Capture(IList<Pawn> pawns, IList<IntVec3> cells, IntVec3 origin, Rot4 direction)
        {
            var result = new Dictionary<string,IntVec3>();
            for(int i=0;i<Math.Min(pawns.Count,cells.Count);i++) result[pawns[i].GetUniqueLoadID()]=Local(cells[i],origin,direction);
            return result;
        }
        internal static List<IntVec3> Slots(Map map, IntVec3 origin, Rot4 direction, int count)
            => GroupFormation.Cells(origin, Math.Max(15,count), map, direction);
        internal static bool Apply(Map map, IntVec3 origin, Rot4 direction, IList<Pawn> pawns, List<IntVec3> cells, IDictionary<string,IntVec3> positions)
        {
            if(positions==null || positions.Count==0) return true;
            if(cells.Count!=pawns.Count) return false;
            var allowed=new HashSet<IntVec3>(Slots(map,origin,direction,pawns.Count));
            var used=new HashSet<IntVec3>(); var assigned=new Dictionary<int,IntVec3>();
            for(int i=0;i<pawns.Count;i++) if(positions.TryGetValue(pawns[i].GetUniqueLoadID(),out var offset))
            {
                var cell=origin+Building_PhotoStudio.Rotate(offset,direction);
                if(!allowed.Contains(cell) || !used.Add(cell)) return false;
                assigned[i]=cell;
            }
            var result=new List<IntVec3>(cells);
            for(int i=0;i<pawns.Count;i++)
            {
                if(assigned.TryGetValue(i,out var cell)) result[i]=cell;
                else
                {
                    cell=cells[i];
                    if(used.Contains(cell)) { var free=allowed.Where(c=>!used.Contains(c)).OrderBy(c=>c.DistanceToSquared(cell)).ThenBy(c=>c.x).ThenBy(c=>c.z).ToList(); if(free.Count==0)return false; cell=free[0]; }
                    used.Add(cell);result[i]=cell;
                }
            }
            cells.Clear();cells.AddRange(result);return true;
        }
        internal static void Copy<T>(IDictionary<string,T> from, IDictionary<string,T> to)
        { to.Clear(); if(from!=null)foreach(var pair in from)to[pair.Key]=pair.Value; }
    }

    public sealed class PhotoSession : IExposable
    {
        public string name;
        public int mapId, spotId, rotation, reactionMode;
        public bool peopleFocus, useFrame=true;
        public List<string> people=new List<string>();
        public List<string> positionIds=new List<string>();
        public List<IntVec3> positions=new List<IntVec3>();
        public List<int> reactions=new List<int>(), faces=new List<int>();
        public List<string> babyIds=new List<string>(), guardianIds=new List<string>();
        internal Building_PhotoSpot Spot(Map map) => map!=null && map.uniqueID==mapId
            ? map.listerBuildings.AllBuildingsColonistOfClass<Building_PhotoSpot>().FirstOrDefault(s=>s.thingIDNumber==spotId) : null;
        internal Dictionary<string,IntVec3> Positions
        {
            get { var result=new Dictionary<string,IntVec3>();for(int i=0;i<Math.Min(positionIds.Count,positions.Count);i++)result[positionIds[i]]=positions[i];return result; }
        }
        internal Dictionary<string,int> Faces
        {
            get {var result=new Dictionary<string,int>();for(int i=0;i<Math.Min(people.Count,faces.Count);i++)result[people[i]]=faces[i];return result;}
        }
        internal static PhotoSession Capture(Building_PhotoSpot spot, IList<Pawn> people, IList<Pawn> moving, IList<IntVec3> cells,
            Rot4 rotation, int reactionMode, bool focus, bool frame, IDictionary<string,int> reactions, IDictionary<string,int> faces, IDictionary<Pawn,Pawn> guardians=null)
        {
            var result=new PhotoSession {mapId=spot.Map.uniqueID,spotId=spot.thingIDNumber,rotation=rotation.AsInt,reactionMode=reactionMode,peopleFocus=focus,useFrame=frame};
            foreach(var p in people) {string id=p.GetUniqueLoadID();result.people.Add(id);result.reactions.Add(ReactionChoices.Choice(reactions,id));result.faces.Add(PhotoFaces.Choice(faces,id));}
            var slots=PhotoPositions.Capture(moving,cells,spot.Position,rotation);
            foreach(var pair in slots){result.positionIds.Add(pair.Key);result.positions.Add(pair.Value);}
            if(guardians!=null)foreach(var pair in guardians)if(pair.Key!=null && pair.Value!=null){result.babyIds.Add(pair.Key.GetUniqueLoadID());result.guardianIds.Add(pair.Value.GetUniqueLoadID());}
            return result;
        }
        public void ExposeData()
        {
            Scribe_Values.Look(ref name,"name"); Scribe_Values.Look(ref mapId,"map"); Scribe_Values.Look(ref spotId,"spot");
            Scribe_Values.Look(ref rotation,"rotation"); Scribe_Values.Look(ref reactionMode,"reactionMode");
            Scribe_Values.Look(ref peopleFocus,"peopleFocus");Scribe_Values.Look(ref useFrame,"useFrame",true);
            Scribe_Collections.Look(ref people,"people",LookMode.Value);Scribe_Collections.Look(ref positionIds,"positionIds",LookMode.Value);
            Scribe_Collections.Look(ref positions,"positions",LookMode.Value);Scribe_Collections.Look(ref reactions,"reactions",LookMode.Value);
            Scribe_Collections.Look(ref faces,"faces",LookMode.Value);Scribe_Collections.Look(ref babyIds,"babyIds",LookMode.Value);Scribe_Collections.Look(ref guardianIds,"guardianIds",LookMode.Value);
            if(Scribe.mode==LoadSaveMode.PostLoadInit){people=people??new List<string>();positionIds=positionIds??new List<string>();positions=positions??new List<IntVec3>();reactions=reactions??new List<int>();faces=faces??new List<int>();babyIds=babyIds??new List<string>();guardianIds=guardianIds??new List<string>();}
        }
    }
    public sealed class PhotoSessionsGameComponent : GameComponent
    {
        public PhotoSessionsGameComponent(Game game) { }
        public List<PhotoSession> sessions=new List<PhotoSession>();
        internal static PhotoSessionsGameComponent Instance => Current.Game?.GetComponent<PhotoSessionsGameComponent>();
        public override void ExposeData()
        { Scribe_Collections.Look(ref sessions,"mfPhotoSessions",LookMode.Deep); if(Scribe.mode==LoadSaveMode.PostLoadInit){sessions=sessions??new List<PhotoSession>();sessions.RemoveAll(s=>s==null||s.name.NullOrEmpty());} }
        internal void Save(PhotoSession session,string name)
        { session.name=name.Trim();int at=sessions.FindIndex(s=>string.Equals(s.name,session.name,StringComparison.OrdinalIgnoreCase));if(at<0)sessions.Add(session);else sessions[at]=session; }
    }
    internal static class PhotoSessionUI
    {
        internal static void Menu(Func<PhotoSession> capture, Action<PhotoSession> load)
        {
            var store=PhotoSessionsGameComponent.Instance;if(store==null)return;
            var options=new List<FloatMenuOption>{new FloatMenuOption("MF_SessionSave".Translate(),()=>{var session=capture();if(session!=null)Find.WindowStack.Add(new Dialog_PhotoSessionName(session));})};
            options.AddRange(store.sessions.Select(s=>new FloatMenuOption("MF_SessionLoad".Translate(s.name),()=>load(s))));
            if(store.sessions.Count>0)options.Add(new FloatMenuOption("MF_SessionDelete".Translate(),()=>Find.WindowStack.Add(new FloatMenu(store.sessions.Select(s=>new FloatMenuOption(s.name,()=>Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("MF_SessionDeleteConfirm".Translate(s.name),()=>store.sessions.Remove(s))))).ToList()))));
            Find.WindowStack.Add(new FloatMenu(options));
        }
        internal static List<Pawn> Resolve(PhotoSession session, Map map)
        {
            if(session.Spot(map)==null){Messages.Message("MF_SessionMissingSpot".Translate(),MessageTypeDefOf.RejectInput,false);return null;}
            var people=PhotoCapture.VisiblePawns(map).Where(p=>p.IsFreeColonist&&!p.Dead).GroupBy(p=>p.GetUniqueLoadID()).ToDictionary(g=>g.Key,g=>g.First());
            var result=session.people.Where(people.ContainsKey).Select(id=>people[id]).ToList();
            int missing=session.people.Count-result.Count;if(missing>0)Messages.Message("MF_SessionMissingPeople".Translate(missing),MessageTypeDefOf.NeutralEvent,false);
            return result;
        }
    }
    internal sealed class Dialog_PhotoSessionName : Window
    {
        private readonly PhotoSession session; private string name="";
        public override Vector2 InitialSize=>new Vector2(510,250);
        internal Dialog_PhotoSessionName(PhotoSession session){this.session=session;doCloseX=true;forcePause=false;absorbInputAroundWindow=true;closeOnAccept=false;}
        public override void DoWindowContents(Rect r)
        {
            Widgets.Label(new Rect(0,0,r.width,35),"MF_SessionSave".Translate());
            name=Widgets.TextField(new Rect(0,40,r.width,36),name,60);
            Widgets.Label(new Rect(0,85,r.width,60),"MF_SessionHelp".Translate());
            if(Widgets.ButtonText(new Rect(0,r.height-40,r.width,40),"MF_CapturePresetSave".Translate())&&!name.Trim().NullOrEmpty())
            {
                var store=PhotoSessionsGameComponent.Instance;string n=name.Trim();
                if(store.sessions.Any(s=>string.Equals(s.name,n,StringComparison.OrdinalIgnoreCase)))
                    Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("MF_SessionReplace".Translate(n),()=>{store.Save(session,n);Close();}));
                else {store.Save(session,n);Close();}
            }
        }
    }
}
