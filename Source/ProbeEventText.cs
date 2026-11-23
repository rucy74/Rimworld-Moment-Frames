using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace MomentFrames
{
    public static partial class Probe
    {
        private static bool eventTextMode;
        private static Lord eventConcert;
        private static LordJob_Joinable_Concert eventConcertJob;
        private static Building_MusicalInstrument eventPiano;
        private static string eventKey, eventSavedId, eventSavedText;
        private static string[] eventNextKeys;
        private static readonly string[] EventPrefixes = { "concert", "sky_lanterns", "tree_celebration", "drum_dance", "tech_dance", "cannibal_feast", "date_festival", "execution", "prisoner_sacrifice", "animal_sacrifice", "scarification", "blinding", "duel" };
        private static readonly string[][] EventPairs = {
            new[] { "CelebrationSkyLanterns", "CelebrationSkyLanterns", "sky_lanterns" }, new[] { "CelebrationTree", "CelebrationTree", "tree_celebration" },
            new[] { "CelebrationPartyDanceDrum", "CelebrationPartyDanceDrum", "drum_dance" }, new[] { "CelebrationPartyDanceTech", "DancePartyTech", "tech_dance" },
            new[] { "FeastCannibal", "FeastCannibal", "cannibal_feast" }, new[] { "CelebrationPartyChat", "DateRitual", "date_festival" },
            new[] { "Execution", "Execution", "execution" }, new[] { "SacrificePrisoner", "SacrificePrisoner", "prisoner_sacrifice" },
            new[] { "SacrificeAnimal", "SacrificeAnimal", "animal_sacrifice" }, new[] { "ScarificationCeremony", "ScarificationCeremony", "scarification" },
            new[] { "BlindingCeremony", "BlindingCeremony", "blinding" }, new[] { "GladiatorDuel", "GladiatorDuel", "duel" }
        };

        private static void EventCheck(bool ok, string message)
        {
            if (!ok) throw new InvalidOperationException("event text: " + message);
            checks.Add("PASS: event text — " + message);
        }

        private static void EventTextStep()
        {
            switch (phase)
            {
                case 0:
                    FxSet(false); Setup();
                    EventCheck(ModsConfig.RoyaltyActive && ModsConfig.IdeologyActive, "Royalty concerts and Ideology ritual Defs are active in disposable profile");
                    EventTextBanks(); StartEventConcert(); Wait(.4f); phase = 1; return;
                case 1:
                    if (PhotoCapture.Busy) return;
                    for (int i = 0; i < 100 && PhotoStore.Instance.Records.All(r => r.eventKey != eventKey); i++) Find.TickManager.DoSingleTick();
                    testRec = PhotoStore.Instance.Records.FirstOrDefault(r => r.eventKey == eventKey);
                    if (testRec == null) return;
                    EventCheck(testRec.kind == "party" && testRec.title == "MF_ConcertHost".Translate(p1.LabelShort).ToString(), "actual concert inherits party hook but saves concert title with performer");
                    EventCheck(testRec.desc.Contains(p1.LabelShort) && testRec.desc.Contains(eventPiano.def.LabelCap.ToString()), "actual mid-concert photo saves observed performer and playing piano type, not generic party prose or quality suffix");
                    Wait(.3f); phase = 2; return;
                case 2:
                    if (!testRec.HasFile) { EventCheck(PhotoStore.Instance.Records.Contains(testRec), "concert capture remains registered until its file is written"); return; }
                    AccessTools.Method(typeof(LordJob_Joinable_Party), "ApplyOutcome").Invoke(eventConcertJob, new object[] { eventConcert.CurLordToil });
                    EventCheck(PhotoStore.Instance.Records.Count(r => r.eventKey == eventKey) == 1, "concert end fallback does not duplicate its mid-event photograph");
                    Find.MainTabsRoot.EscapeCurrentTab(false); CloseLog(); Shot("event-concert-map.png"); Wait(.5f); phase = 3; return;
                case 3:
                    Require("event-concert-map.png"); MainTabWindow_Album.Open(testRec); Wait(.7f); phase = 4; return;
                case 4:
                    CloseLog(); Shot("event-concert-gallery.png"); Wait(.5f); phase = 5; return;
                case 5:
                    Require("event-concert-gallery.png"); Find.MainTabsRoot.EscapeCurrentTab(false);
                    EventTextFallbacks(); SaveEventText(); Wait(.8f); phase = 6; return;
                case 6:
                    GameDataSaveLoader.LoadGame("mfeventtext"); Wait(1.5f); phase = 7; return;
                case 7:
                    map = Find.CurrentMap; Find.TickManager.Pause(); Find.Storyteller.storytellerComps.Clear(); Find.Storyteller.incidentQueue.Clear();
                    testRec = PhotoStore.Instance.Records.Single(r => r.id == eventSavedId);
                    EventCheck(testRec.title + "\n" + testRec.desc == eventSavedText, "real game save/load preserves event-specific title and sentence exactly");
                    var next = Enumerable.Range(0,8).Select(i => PhotoStore.Instance.Descriptions.Pick("MF_Caption_concert", EventKeys(false), n => 0)).ToArray();
                    EventCheck(next.SequenceEqual(eventNextKeys), "real game save/load continues concert sentence bag with no reset");
                    File.WriteAllText(Path.Combine(dir,"event-text-result.json"), "{\"ok\":true,\"title\":" + Json(testRec.title) + ",\"caption\":" + Json(testRec.desc)
                        + ",\"checks\":[" + string.Join(",",checks.Select(Json)) + "],\"limits\":\"Constructed native concert driven through real LordToilTick, not a naturally occurring player concert. Ritual Def mapping and Describe tested with fixtures; all ritual gameplay not replayed. No user saves, full mod pack or new music tested.\"}");
                    active = false; Application.Quit(); return;
            }
        }

        private static string[] EventKeys(bool detailed) => Enumerable.Range(1,12).Select(i => "MF_Caption_concert" + (detailed ? "_detail" : "") + "_" + i).ToArray();

        private static void EventTextBanks()
        {
            string[] values = { p1.LabelShort + ", " + p2.LabelShort, p1.LabelShort, "winter", map.Parent.LabelCap, "Native event label", "Observed instrument" };
            var samples = new Dictionary<string,string>();
            foreach (string prefix in EventPrefixes)
            {
                var keys = DescriptionCandidates.EligibleKeys("MF_Caption_" + prefix, 2, values, DescriptionCandidates.Lookup);
                EventCheck(keys.Count == 12, prefix + " has twelve eligible event-specific candidates");
                var history = new DescriptionHistory(); var picked = Enumerable.Range(0,12).Select(i => history.Pick(prefix, keys, n => Rand.Range(0,n))).ToArray();
                EventCheck(picked.Distinct().Count() == 12 && picked.All(k => !DescriptionCandidates.Format(DescriptionCandidates.Lookup(k),values).NullOrEmpty()), prefix + " draws twelve complete sentences without a within-cycle repeat");
                samples[prefix] = DescriptionCandidates.Format(DescriptionCandidates.Lookup(picked[0]),values);
            }
            foreach (var pair in EventPairs)
            {
                var pattern = DefDatabase<RitualPatternDef>.GetNamed(pair[0]);
                EventCheck(pattern.ritualBehavior?.defName == pair[1] && EventDescriptions.RitualPrefix(pattern.defName, pattern.ritualBehavior.defName) == pair[2], pair[0] + " uses its real native behavior and specific caption prefix");
                EventCheck(EventDescriptions.RitualPrefix("Modded_" + pair[0], pair[1]) == null && EventDescriptions.RitualPrefix(pair[0], "DifferentBehavior") == null, pair[0] + " rejects unknown mod patterns and mismatched behavior");
                var preceptDef = DefDatabase<PreceptDef>.AllDefsListForReading.FirstOrDefault(d => d.ritualPatternBase == pattern) ?? DefDatabase<PreceptDef>.GetNamed("Festival");
                var ritual = new Precept_Ritual { def=preceptDef, sourcePattern=pattern, behavior=pattern.ritualBehavior.GetInstance() };
                Traverse.Create(ritual).Field("name").SetValue(pattern.shortDescOverride ?? pair[0]);
                var job = new LordJob_Ritual { lord=new Lord { lordManager=map.lordManager, ownedPawns=new List<Pawn> {p1,p2} } };
                Traverse.Create(job).Field("ritual").SetValue(ritual); Traverse.Create(job).Field("organizer").SetValue(p1);
                EventCheck(RitualGroups.Describe(job,out string kind,out string title,out string desc) && kind == (Array.IndexOf(EventPrefixes,pair[2]) <= 6 ? "party" : "ritual")
                    && !desc.NullOrEmpty(), pair[0] + " actual Describe consumer selects specific text and preserves existing photo kind");
                var actualPeople = kind == "party" ? new[] {p1} : new[] {p1,p2};
                var context = new[] {string.Join(", ",actualPeople.Select(p=>p.LabelShort)),actualPeople[0].LabelShort,GenLocalDate.Season(map).LabelCap().ToString(),map.Parent.LabelCap.ToString(),job.RitualLabel,""};
                EventCheck(Enumerable.Range(1,12).Select(i=>DescriptionCandidates.Lookup("MF_Caption_"+pair[2]+"_"+i)).Where(t=>DescriptionCandidates.Fits(t,context))
                    .Select(t=>DescriptionCandidates.Format(t,context)).Contains(desc), pair[0]+" consumer output belongs to its specific bank, not generic fallback");
            }
            EventCheck(EventDescriptions.RitualPrefix(null,null) == null, "unknown ritual facts retain generic wording");
            values[5] = "";
            var baseKeys = DescriptionCandidates.EligibleKeys("MF_Caption_concert",1,values,DescriptionCandidates.Lookup);
            EventCheck(baseKeys.Count == 12 && baseKeys.All(k => !k.Contains("_detail_")), "unobserved instrument chooses twelve non-instrument concert sentences");
            File.WriteAllText(Path.Combine(dir,"event-text-examples.json"), "{" + string.Join(",",samples.Select(kv => Json(kv.Key) + ":" + Json(kv.Value))) + "}");
        }

        private static void StartEventConcert()
        {
            foreach (var l in map.lordManager.lords.ToList()) map.lordManager.RemoveLord(l);
            eventPiano = (Building_MusicalInstrument)ThingMaker.MakeThing(ThingDef.Named("Piano"));
            eventPiano.SetFaction(Faction.OfPlayer); eventPiano.GetComp<CompQuality>().SetQuality(QualityCategory.Normal,ArtGenerationContext.Colony);
            GenSpawn.Spawn(eventPiano,center + new IntVec3(-4,0,0),map,Rot4.North);
            p1.Position = eventPiano.InteractionCell; p1.Notify_Teleported(true,false);
            foreach (var p in map.mapPawns.FreeColonistsSpawned) { if(p.needs.rest != null)p.needs.rest.CurLevel=1; if(p.needs.food != null)p.needs.food.CurLevel=1; }
            eventConcertJob = new LordJob_Joinable_Concert(eventPiano.InteractionCell,p1,DefDatabase<GatheringDef>.GetNamed("Concert"));
            Traverse.Create(eventConcertJob).Field("durationTicks").SetValue(6000);
            eventConcert = LordMaker.MakeNewLord(Faction.OfPlayer,eventConcertJob,map,new[] {p1,p2});
            var play = JobMaker.MakeJob(JobDefOf.Play_MusicalInstrument,eventPiano,eventPiano.InteractionCell);
            play.doUntilGatheringEnded=true; play.expiryInterval=eventConcertJob.DurationTicks;
            p1.jobs.StartJob(play,JobCondition.InterruptForced);
            for(int i=0;i<30;i++)Find.TickManager.DoSingleTick();
            EventCheck(eventPiano.IsBeingPlayed && p1.CurJob?.def == JobDefOf.Play_MusicalInstrument, "organizer is actually playing the spawned piano before the caption observes it");
            eventConcert.ticksInToil=3599; eventKey=Hooks.Key("party",eventConcertJob);
            Find.CameraDriver.SetRootPosAndSize(eventPiano.DrawPos,11f);
        }

        private static void EventTextFallbacks()
        {
            string saved = testRec.desc;
            var ended = new LordJob_Joinable_Concert(eventPiano.InteractionCell,p1,DefDatabase<GatheringDef>.GetNamed("Concert"))
                { lord = new Lord { lordManager=map.lordManager, ownedPawns=new List<Pawn> {p1,p2}, ticksInToil=1500 } };
            string endedKey=Hooks.Key("party",ended);
            if(PhotoStore.Instance.HasKey(endedKey)) throw new InvalidOperationException("event text: end fixture key must be unused");
            EventDescriptions.Party(ended,out string endedTitle,out string endedPrefix,out string endedInstrument);
            AccessTools.Method(typeof(Patch_Party),"Postfix").Invoke(null,new object[] {ended});
            var endedRecord=PhotoStore.Instance.Records.FirstOrDefault(r=>r.eventKey==endedKey);
            string afterward=(!endedInstrument.NullOrEmpty() ? "MF_ConcertAfterwardInstrument".Translate(endedTitle,endedInstrument)
                : "MF_EventAfterward".Translate(endedTitle)).ToString();
            EventCheck(endedRecord != null && endedRecord.title==endedTitle && endedRecord.desc==afterward,
                "actual end-hook consumer records event facts and warns that the later photo may show a different scene");
            p1.jobs.StartJob(JobMaker.MakeJob(JobDefOf.Wait,600),JobCondition.InterruptForced);
            EventDescriptions.Party(eventConcertJob,out string title,out string prefix,out string instrument);
            EventCheck(prefix == "concert" && instrument == null, "finished instrument job does not invent a piano fact from the gathering spot");
            var ordinary = new LordJob_Joinable_Party(center,p1,GatheringDefOf.Party);
            EventDescriptions.Party(ordinary,out title,out prefix,out instrument);
            EventCheck(prefix == "party" && title == "MF_Party".Translate(p1.LabelShort).ToString() && instrument == null, "ordinary parties keep existing title and candidate group");
            EventCheck(!PhotoDesc.PickSpecific("untranslated_mod_event","celebration",new[] {p1},map,"Festival label").NullOrEmpty(), "missing specific bank falls back to existing celebration translation");
            EventCheck(testRec.desc == saved, "existing stored photo text is not regenerated by changed event context");
        }

        private static void SaveEventText()
        {
            var history=PhotoStore.Instance.Descriptions;
            for(int i=0;i<5;i++)history.Pick("MF_Caption_concert",EventKeys(false),n=>0);
            eventSavedId=testRec.id; eventSavedText=testRec.title+"\n"+testRec.desc;
            GameDataSaveLoader.SaveGame("mfeventtext");
            var xml=new XmlDocument();xml.Load(GenFilePaths.FilePathForSavedGame("mfeventtext"));
            EventCheck(xml.SelectNodes("//descriptionHistory/bags/li").Count>0,"actual game XML saves the new concert candidate usage bag");
            eventNextKeys=Enumerable.Range(0,8).Select(i=>history.Pick("MF_Caption_concert",EventKeys(false),n=>0)).ToArray();
            var fresh=new DescriptionHistory();
            EventCheck(!eventNextKeys.SequenceEqual(Enumerable.Range(0,8).Select(i=>fresh.Pick("MF_Caption_concert",EventKeys(false),n=>0))),"positive control: resetting concert usage would change the next choices");
        }
    }
}
