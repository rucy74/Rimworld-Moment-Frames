using System.Linq;
using LudeonTK;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace MomentFrames
{
    /// <summary>Dev-mode helpers (Debug actions → MomentFrames) so auto-capture can be tested without waiting for a real event.</summary>
    public static class MFDebug
    {
        [DebugAction("MomentFrames", "Start wedding: 2 selected colonists", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void DebugStartWedding()
        {
            var pawns = Find.Selector.SelectedPawns.Where(p => p.IsColonist && p.Spawned && !p.Dead).Take(2).ToList();
            if (pawns.Count < 2)
            {
                Messages.Message("MomentFrames: select two colonists first.", MessageTypeDefOf.RejectInput, false);
                return;
            }
            Map map = pawns[0].Map;
            Thing spotThing = map.listerThings.ThingsOfDef(ThingDef.Named("MarriageSpot")).FirstOrDefault();
            IntVec3 spot = spotThing != null ? spotThing.Position : pawns[0].Position;
            StartWedding(pawns[0], pawns[1], spot);
            Messages.Message("MomentFrames: wedding of " + pawns[0].LabelShort + " and " + pawns[1].LabelShort + " started at " + spot + (spotThing == null ? " (no marriage spot; using pawn position)" : ""), new LookTargets(spot, map), MessageTypeDefOf.PositiveEvent, false);
        }

        [DebugAction("MomentFrames", "Start party: selected colonist hosts", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void DebugStartParty()
        {
            Pawn host = Find.Selector.SelectedPawns.FirstOrDefault(p => p.IsColonist && p.Spawned && !p.Dead);
            if (host == null)
            {
                Messages.Message("MomentFrames: select a colonist first.", MessageTypeDefOf.RejectInput, false);
                return;
            }
            Map map = host.Map;
            Thing spotThing = map.listerThings.ThingsOfDef(ThingDef.Named("PartySpot")).FirstOrDefault();
            IntVec3 spot = spotThing != null ? spotThing.Position : host.Position;
            LordMaker.MakeNewLord(Faction.OfPlayer, new LordJob_Joinable_Party(spot, host, GatheringDefOf.Party), map, new[] { host });
            Messages.Message("MomentFrames: party hosted by " + host.LabelShort + " started at " + spot + " (photo is taken when it ends).", new LookTargets(spot, map), MessageTypeDefOf.PositiveEvent, false);
        }

        [DebugAction("MomentFrames", "Reminisce: selected colonist goes to look at a hung photo", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void DebugViewPhoto()
        {
            Pawn pawn = Find.Selector.SelectedPawns.FirstOrDefault(p => p.IsColonist && p.Spawned && !p.Dead);
            if (pawn == null)
            {
                Messages.Message("MomentFrames: select a colonist first.", MessageTypeDefOf.RejectInput, false);
                return;
            }
            var job = DefDatabase<JoyGiverDef>.GetNamed("MF_ViewPhoto").Worker.TryGiveJob(pawn);
            if (job == null)
            {
                Messages.Message("MomentFrames: no reachable frame with a photo on this map (or the reminisce setting is off).", MessageTypeDefOf.RejectInput, false);
                return;
            }
            pawn.jobs.StartJob(job, JobCondition.InterruptForced);
            Messages.Message("MomentFrames: " + pawn.LabelShort + " goes to look at " + job.targetA.Thing.LabelCap + " (memory after ~10 s if they are in the photo).", new LookTargets(job.targetA.Thing), MessageTypeDefOf.PositiveEvent, false);
        }

        [DebugAction("MomentFrames", "Remember when: 2 selected colonists talk about a shared photo", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void DebugRememberWhen()
        {
            var pawns = Find.Selector.SelectedPawns.Where(p => p.IsColonist && p.Spawned && !p.Dead).Take(2).ToList();
            if (pawns.Count < 2)
            {
                Messages.Message("MomentFrames: select two colonists first.", MessageTypeDefOf.RejectInput, false);
                return;
            }
            var shared = Reminisce.SharedPhoto(pawns[0], pawns[1], out var sharedRec);
            if (shared == null)
            {
                Messages.Message("MomentFrames: no hung photo on this map shows both of them (or the reminisce setting is off).", MessageTypeDefOf.RejectInput, false);
                return;
            }
            var def = DefDatabase<InteractionDef>.GetNamed("MF_RememberWhen");
            if (!pawns[0].interactions.TryInteractWith(pawns[1], def))
            {
                Messages.Message("MomentFrames: interaction refused — they must be awake, within 6 cells with line of sight, and not have talked in the last 120 ticks.", MessageTypeDefOf.RejectInput, false);
                return;
            }
            Messages.Message("MomentFrames: " + pawns[0].LabelShort + " and " + pawns[1].LabelShort + " reminisced about '" + sharedRec?.title + "'.", new LookTargets(pawns[0]), MessageTypeDefOf.PositiveEvent, false);
        }

        [DebugAction("MomentFrames", "Fade preview: toggle +10 years on every framed photo", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void DebugFadePreview()
        {
            Fading.PreviewYears = Fading.PreviewYears > 0 ? 0 : 10;
            Messages.Message("MomentFrames: fade preview " + (Fading.PreviewYears > 0 ? "on (+10 years)" : "off") + (Fading.Enabled ? "" : " — the fading setting is off, so nothing changes"), MessageTypeDefOf.NeutralEvent, false);
        }

        /// <summary>Makes the two pawns fiances (replacing any lover/spouse link between them) and starts the ceremony lord at the spot.</summary>
        public static Lord StartWedding(Pawn a, Pawn b, IntVec3 spot)
        {
            foreach (var rel in a.relations.DirectRelations.Where(r => r.otherPawn == b && (r.def == PawnRelationDefOf.Lover || r.def == PawnRelationDefOf.Fiance || r.def == PawnRelationDefOf.Spouse)).ToList())
                a.relations.RemoveDirectRelation(rel);
            a.relations.AddDirectRelation(PawnRelationDefOf.Fiance, b);
            return LordMaker.MakeNewLord(Faction.OfPlayer, new LordJob_Joinable_MarriageCeremony(a, b, spot), a.Map, new[] { a, b });
        }
    }
}
