using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI.Group;

namespace MomentFrames
{
    /// <summary>Only a relation visibly verified in the captured frame can choose a special sentence bank.
    /// The returned pawns are transient; the finished description, not live world state, is stored with the photo.</summary>
    internal sealed class PhotoCaptionContext
    {
        internal const string CarriedBabyPrefix = "MF_Caption_carried_baby";
        internal const string AnimalPairPrefix = "MF_Caption_animal_pair";

        internal string Prefix;
        internal List<Pawn> People;
        internal Pawn Subject, Companion;
        internal string[] Values;

        private static bool Alive(Pawn pawn) => pawn != null && !pawn.Dead && !pawn.Destroyed;
        private static bool Human(Pawn pawn) => Alive(pawn) && pawn.RaceProps?.Humanlike == true;

        internal static PhotoCaptionContext Measure(PhotoRecord rec, IList<Pawn> people, Map map)
        {
            if (rec == null || map == null || people == null || !rec.HasCenter
                || (rec.mapId >= 0 && rec.mapId != map.uniqueID)) return null;
            var recorded = people.Where(p => CapturedPhotoRoster.Visible(rec, p, map,
                rec.center, rec.spanCells, rec.Aspect)).Distinct().ToList();

            PhotoCaptionContext Context(string suffix, Pawn subject = null, Pawn companion = null, string parent = null, string detail = null)
            {
                string occasion = rec.eventAge == 1 ? DescriptionCandidates.Lookup("MF_SceneBirthdayFirst")
                    : rec.eventAge > 1 ? "MF_SceneBirthdayAge".Translate(rec.eventAge).ToString()
                    : DescriptionCandidates.Lookup("MF_SceneBirthday");
                return new PhotoCaptionContext
                {
                    Prefix = suffix.StartsWith("MF_Caption_") ? suffix : "MF_Caption_scene_" + suffix,
                    People = recorded, Subject = subject, Companion = companion,
                    Values = new[] { string.Join(", ", recorded.Select(p => p.LabelShort)), recorded.FirstOrDefault()?.LabelShort ?? "",
                        GenLocalDate.Season(map).LabelCap(), rec.captionPlace ?? map.Parent?.LabelCap.ToString() ?? "", "", "",
                        subject?.LabelShort ?? "", companion?.LabelShort ?? "", occasion ?? "", parent ?? "", detail ?? "" }
                };
            }
            string ParentRole(Pawn child, Pawn adult)
            {
                if (child?.relations == null || adult == null) return null;
                if (child.GetMother() == adult) return DescriptionCandidates.Lookup("MF_SceneMother");
                if (child.GetFather() == adult) return DescriptionCandidates.Lookup("MF_SceneFather");
                return child.relations.DirectRelationExists(PawnRelationDefOf.Parent, adult) ? DescriptionCandidates.Lookup("MF_SceneParent") : null;
            }
            bool ThingInFrame(Thing thing)
            {
                if (thing?.Spawned != true || thing.Map != map) return false;
                var area = GenAdj.OccupiedRect(thing);
                float left = rec.center.x - rec.spanCells * rec.Aspect / 2f, right = rec.center.x + rec.spanCells * rec.Aspect / 2f;
                float bottom = rec.center.z - rec.spanCells / 2f, top = rec.center.z + rec.spanCells / 2f;
                return area.minX >= left && area.maxX + 1 <= right && area.minZ >= bottom && area.maxZ + 1 <= top;
            }
            string VisibleWeather()
            {
                var weather = map.weatherManager?.curWeather;
                if(weather==null || weather.rainRate<=0f && weather.snowRate<=0f) return null;
                // A roofed interior does not show the map's rain or snow. Do not borrow outdoor atmosphere for it.
                var frame=rec.eventSubtype=="mf-survey-area"
                    ? PhotoStore.ExplicitCaptureArea(rec,map)
                    : PhotoCapture.CoveredRect(rec.center,rec.spanCells,map,rec.Aspect);
                if(!frame.Cells.Any(c=>!c.Roofed(map))) return null;
                return weather.LabelCap;
            }

            // Keep the event's meaning even if a baby or pet also happens to be in its frame.
            if (rec.eventSubtype == "concert")
            {
                var concert = map.lordManager.lords.Select(l => l.LordJob).OfType<LordJob_Joinable_Concert>()
                    .FirstOrDefault(j => Hooks.Key("party", j) == rec.eventKey);
                bool delayed = Find.TickManager != null && Find.TickManager.TicksAbs - rec.ticksAbs > 2500;
                if (rec.eventEnded)
                    return Context(concert == null ? (delayed ? "concert_later" : "concert_after") : "concert_record");
                Pawn performer = concert?.Organizer;
                var job = performer?.CurJob;
                if (Human(performer) && recorded.Contains(performer) && performer.GetLord() == concert.lord
                    && job?.def == JobDefOf.Play_MusicalInstrument
                    && performer.jobs?.curDriver is JobDriver_PlayMusicalInstrument driver && driver.CurToilIndex == 1
                    && job.targetA.Thing is Building_MusicalInstrument instrument && instrument.IsBeingPlayed
                    && job.targetB.Cell == performer.Position && ThingInFrame(instrument))
                {
                    Pawn attendee = recorded.FirstOrDefault(p => p != performer && Human(p) && p.GetLord() == concert.lord
                        && concert.lord.ownedPawns.Contains(p) && GatheringsUtility.InGatheringArea(p.Position, concert.Spot, map));
                    return Context(attendee != null ? "concert_playing_audience" : "concert_playing", performer, attendee, detail: instrument.def.LabelCap);
                }
                return Context("concert_record");
            }
            if(rec.kind=="party")
            {
                var party=map.lordManager.lords.Select(l=>l.LordJob).OfType<LordJob_Joinable_Party>()
                    .FirstOrDefault(j=>Hooks.Key("party",j)==rec.eventKey);
                bool delayed=Find.TickManager!=null && Find.TickManager.TicksAbs-rec.ticksAbs>2500;
                if(rec.eventEnded || party==null) return Context(delayed?"party_later":"party_after");
                Pawn host=party.Organizer;
                bool visibleHost=Human(host) && recorded.Contains(host) && host.GetLord()==party.lord
                    && GatheringsUtility.InGatheringArea(host.Position,party.Spot,map);
                string suffix=visibleHost?"party_host":recorded.Count==0?"party_scene":recorded.Count>1?"party_group":"party_solo";
                string weather=VisibleWeather();
                return Context(suffix+(weather.NullOrEmpty()?"":"_weather"),visibleHost?host:null,detail:weather);
            }
            if (rec.kind == "wedding")
            {
                var couple = (rec.eventSubjectIds ?? new List<string>()).Select(id => recorded.FirstOrDefault(p =>
                    Human(p) && p.GetUniqueLoadID() == id)).Where(p => p != null).Distinct().ToList();
                if (couple.Count == 2) return Context("wedding_pair", couple[0], couple[1]);
                return Context(rec.deferredCapture ? "wedding_later" : "wedding_scene");
            }
            if (rec.kind == "recruit")
            {
                var recruit = recorded.FirstOrDefault(p => Human(p) && p.GetUniqueLoadID() == rec.requiredPerson)
                    ?? recorded.FirstOrDefault(p => Human(p) && rec.eventPeopleIds?.Contains(p.GetUniqueLoadID()) == true);
                if (recruit != null)
                {
                    var context = Context("MF_Caption_recruit", recruit);
                    context.Values[1] = recruit.LabelShort;
                    return context;
                }
            }
            if(rec.kind==Journey.Kind)
            {
                bool delayed=rec.deferredCapture || Find.TickManager!=null && Find.TickManager.TicksAbs-rec.ticksAbs>2500;
                if(delayed) return Context("journey_later");
                string suffix=recorded.Count==0?"journey_scene":recorded.Count==1?"journey_solo":"journey_group";
                string weather=VisibleWeather();
                return Context(suffix+(weather.NullOrEmpty()?"":"_weather"),recorded.FirstOrDefault(),detail:weather);
            }
            if (rec.kind == Battles.Kind)
            {
                if (rec.eventSubtype == "battle-engagement") return Context("battle_engagement");
                var corpse = map.listerThings.ThingsInGroup(ThingRequestGroup.Corpse).OfType<Corpse>()
                    .FirstOrDefault(c => rec.requiredThings?.Contains(c.ThingID) == true && c.InnerPawn != null && ThingInFrame(c));
                string remnant = corpse?.InnerPawn.RaceProps?.IsMechanoid == true ? DescriptionCandidates.Lookup("MF_SceneMechRemains")
                    : corpse?.InnerPawn.RaceProps?.Animal == true ? DescriptionCandidates.Lookup("MF_SceneAnimalRemains")
                    : corpse?.InnerPawn.RaceProps?.Humanlike == true ? DescriptionCandidates.Lookup("MF_SceneHumanRemains") : null;
                bool delayed = Find.TickManager != null && Find.TickManager.TicksAbs - rec.ticksAbs > 2500;
                var context = Context(remnant == null ? "battle_record" : delayed ? "battle_later" : "battle_remains");
                context.Values[6] = remnant ?? "";
                return context;
            }
            Pawn birthday = rec.kind == Kinds.Birthday ? recorded.FirstOrDefault(p => p.GetUniqueLoadID() == rec.requiredPerson && Human(p)) : null;
            if (birthday != null)
            {
                Pawn holder = BirthdayCarry.Holder(birthday);
                if (birthday.DevelopmentalStage.Baby() && Human(holder) && recorded.Contains(holder) && holder.carryTracker?.CarriedThing == birthday)
                {
                    string relation = ParentRole(birthday, holder);
                    return Context(relation == null ? "birthday_carried" : "birthday_carried_parent", birthday, holder, relation);
                }
                Pawn parent = recorded.FirstOrDefault(p => Human(p) && p != birthday && ParentRole(birthday, p) != null);
                if (parent != null) return Context("birthday_parent", birthday, parent, ParentRole(birthday, parent));
                return Context(recorded.Count(Human) > 1 ? "birthday_group" : "birthday_solo", birthday);
            }

            // Being near a baby, or having a family relation, is not proof of carrying the child.
            foreach (var baby in recorded.Where(p => Human(p) && p.DevelopmentalStage.Baby()))
            {
                Pawn carrier = BirthdayCarry.Holder(baby);
                if (!Human(carrier) || !recorded.Contains(carrier) || carrier.carryTracker?.CarriedThing != baby) continue;
                string relation = ParentRole(baby, carrier);
                return Context(relation == null ? CarriedBabyPrefix : "carried_parent", baby, carrier, relation);
            }

            // A co-photographed player animal and person do not imply ownership, a bond, or a mood.
            if (Faction.OfPlayer == null) return null;
            Pawn animal = recorded.FirstOrDefault(p => Alive(p) && p.RaceProps?.Animal == true && p.Faction == Faction.OfPlayer);
            Pawn person = recorded.FirstOrDefault(p => Human(p) && p.Faction == Faction.OfPlayer
                && animal?.relations?.DirectRelationExists(PawnRelationDefOf.Bond, p) == true)
                ?? recorded.FirstOrDefault(p => Human(p) && p.Faction == Faction.OfPlayer);
            if (animal == null || person == null) return null;
            bool bonded = animal.relations?.DirectRelationExists(PawnRelationDefOf.Bond, person) == true;
            return Context(bonded ? "animal_bond" : AnimalPairPrefix, animal, person, detail: animal.def.LabelCap);
        }
    }
}
