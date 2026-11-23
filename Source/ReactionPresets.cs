using System;
using System.Collections.Generic;
using System.Linq;
using Verse;

namespace MomentFrames
{
    /// <summary>Expressions are identities, not emoji font characters. Existing PhotoIcon values stay unchanged.</summary>
    internal static class ReactionChoices
    {
        internal const int Automatic = -1, None = -2, Heart = 100, HandWave = 101,
            Peace = 102, Clap = 103, Shy = 104, Surprise = 105;
        internal static readonly int[] Options = { Automatic, None, Heart, HandWave, (int)PhotoIcon.Smile,
            (int)PhotoIcon.Star, (int)PhotoIcon.Music, Peace, Clap, Shy, Surprise };
        internal static bool Valid(int choice) => choice == Automatic || choice == None || choice == Heart || choice == HandWave
            || choice == Peace || choice == Clap || choice == Shy || choice == Surprise
            || choice >= 0 && choice <= (int)PhotoIcon.Paw;
        internal static int Choice(IDictionary<string,int> choices, string id)
            => id != null && choices != null && choices.TryGetValue(id,out int value) && Valid(value) ? value : Automatic;
        // New preparation drafts opt in to expressions. Legacy records still use Choice's Automatic fallback.
        internal static void EnsurePreparation(IDictionary<string,int> choices, IEnumerable<string> ids, int missing = None)
        {
            foreach(string id in ids.Where(id=>!string.IsNullOrEmpty(id)).Distinct())
                if(!choices.ContainsKey(id)) choices[id]=missing;
        }
        internal static Dictionary<string,int> Read(IList<string> ids, IList<int> icons)
        {
            var result = new Dictionary<string,int>();
            for(int i=0;i<Math.Min(ids?.Count??0,icons?.Count??0);i++)
                if(!string.IsNullOrEmpty(ids[i]) && Valid(icons[i])) result[ids[i]]=icons[i];
            return result;
        }
        internal static bool AllowsRestingBirthdaySubject(string kind, string requiredId, string pawnId,
            IList<string> ids, IList<int> icons)
        {
            if (kind != Kinds.Birthday || string.IsNullOrEmpty(requiredId) || requiredId != pawnId) return false;
            int choice = Automatic;
            for (int i = 0; i < Math.Min(ids?.Count ?? 0, icons?.Count ?? 0); i++)
                if (ids[i] == pawnId && Valid(icons[i])) choice = icons[i];
            // Only a deliberate visible expression allows posing while the birthday subject rests.
            // The last valid assignment has the same precedence as Read; None and Automatic retain normal eligibility.
            return choice > 0 && Valid(choice);
        }
        internal static void Apply(IDictionary<string,int> target, IDictionary<string,int> saved, IEnumerable<string> roster)
        {
            var available = new HashSet<string>(roster.Where(id=>!string.IsNullOrEmpty(id)));
            foreach(var pair in saved.Where(p=>available.Contains(p.Key) && Valid(p.Value))) target[pair.Key]=pair.Value;
        }
        internal static string Label(int icon) => (icon == Automatic ? "MF_CaptureExpressionAuto" : icon == None ? "MF_CaptureExpressionNone"
            : icon == Heart ? "MF_CaptureExpressionHeart" : icon == HandWave ? "MF_CaptureExpressionHand"
            : icon == (int)PhotoIcon.Smile ? "MF_CaptureExpressionSmile" : icon == (int)PhotoIcon.Star ? "MF_CaptureExpressionStar"
            : icon == (int)PhotoIcon.Music ? "MF_CaptureExpressionMusic"
            : icon == Peace ? "MF_CaptureExpressionPeace" : icon == Clap ? "MF_CaptureExpressionClap"
            : icon == Shy ? "MF_CaptureExpressionShy" : icon == Surprise ? "MF_CaptureExpressionSurprise"
            : "MF_CaptureExpressionAuto").Translate();
    }

    public sealed class MFReactionPreset : IExposable
    {
        public string name;
        public List<string> pawnIds = new List<string>();
        public List<int> icons = new List<int>();
        public List<string> facePawnIds = new List<string>();
        public List<int> faces = new List<int>();
        public void ExposeData()
        {
            Scribe_Values.Look(ref name,"name");
            Scribe_Collections.Look(ref pawnIds,"pawnIds",LookMode.Value);
            Scribe_Collections.Look(ref icons,"icons",LookMode.Value);
            Scribe_Collections.Look(ref facePawnIds,"facePawnIds",LookMode.Value);
            Scribe_Collections.Look(ref faces,"faces",LookMode.Value);
            if(Scribe.mode == LoadSaveMode.PostLoadInit)
            { pawnIds=pawnIds??new List<string>(); icons=icons??new List<int>(); }
        }
    }

    /// <summary>Multiple named expression presets belong to this colony save; loading one never changes guests or their positions.</summary>
    public sealed class ReactionPresetsGameComponent : GameComponent
    {
        public ReactionPresetsGameComponent(Game game) { }
        public List<MFReactionPreset> presets = new List<MFReactionPreset>();
        internal static ReactionPresetsGameComponent Instance => Current.Game?.GetComponent<ReactionPresetsGameComponent>();
        internal void Save(string name, IDictionary<string,int> choices, IEnumerable<string> selectedIds, IDictionary<string,int> faces=null)
        {
            name = (name??"").Trim(); if(name.Length==0) return;
            var ids = selectedIds.Where(id=>!string.IsNullOrEmpty(id)).Distinct().OrderBy(id=>id).ToList();
            var preset = new MFReactionPreset { name=name, pawnIds=ids, icons=ids.Select(id=>ReactionChoices.Choice(choices,id)).ToList() };
            if(faces!=null){preset.facePawnIds=new List<string>(ids);preset.faces=ids.Select(id=>PhotoFaces.Choice(faces,id)).ToList();}
            int index=presets.FindIndex(p=>string.Equals(p.name,name,StringComparison.OrdinalIgnoreCase));
            if(index<0) presets.Add(preset); else presets[index]=preset;
        }
        public override void ExposeData()
        {
            Scribe_Collections.Look(ref presets,"mfReactionPresets",LookMode.Deep);
            if(Scribe.mode==LoadSaveMode.PostLoadInit)
            { presets=presets??new List<MFReactionPreset>(); presets.RemoveAll(p=>p==null || string.IsNullOrWhiteSpace(p.name)); }
        }
    }
}
