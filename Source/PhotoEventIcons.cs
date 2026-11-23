using System;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace MomentFrames
{
    internal enum PhotoParticipantStatus { None, Ready, Moving, Sleeping, Unavailable }

    /// <summary>Information symbols describe recorded kinds and actual availability; they never infer a person's feelings.</summary>
    internal static class PhotoEventIcons
    {
        internal static PhotoIcon For(PhotoRecord record, string eventSubtype = null)
            => ForKind(record?.kind, eventSubtype ?? record?.eventSubtype);

        internal static PhotoIcon ForKind(string kind, string eventSubtype = null)
        {
            if (string.Equals(eventSubtype, "concert", StringComparison.OrdinalIgnoreCase)
                || string.Equals(kind, "concert", StringComparison.OrdinalIgnoreCase)) return PhotoIcon.Music;
            switch (kind?.ToLowerInvariant())
            {
                case Kinds.Birthday: return PhotoIcon.Cake;
                case "wedding": return PhotoIcon.Rings;
                case "memorynight": return PhotoIcon.Projector;
                case "petjoined": case "petphoto": return PhotoIcon.Paw;
                case "group": case "reunion": case "party": return PhotoIcon.Group;
                default: return PhotoIcon.Camera;
            }
        }

        internal static PhotoIcon? ForStatus(PhotoParticipantStatus status)
        {
            switch (status)
            {
                case PhotoParticipantStatus.Ready: return PhotoIcon.Check;
                case PhotoParticipantStatus.Moving: return PhotoIcon.Clock;
                case PhotoParticipantStatus.Sleeping: return PhotoIcon.Sleep;
                case PhotoParticipantStatus.Unavailable: return PhotoIcon.Warning;
                default: return null;
            }
        }

        internal static PhotoIcon? ForParticipant(Pawn pawn, bool available, bool selected,
            LordJob_MFGroupPhoto gathering = null, bool blocked = false)
        {
            if (pawn == null || pawn.Dead) return ForStatus(PhotoParticipantStatus.Unavailable);
            // Sleep stays distinct even when vanilla also marks a sleeping child as downed.
            if (!pawn.Awake()) return ForStatus(PhotoParticipantStatus.Sleeping);
            if (!available || blocked) return ForStatus(PhotoParticipantStatus.Unavailable);
            if (GroupPhoto.MedicalRest(pawn)) return ForStatus(PhotoParticipantStatus.Unavailable);
            if (!selected) return null;
            if (gathering != null && !gathering.Finished)
            {
                if (gathering.Dropped.Contains(pawn)) return ForStatus(PhotoParticipantStatus.Unavailable);
                if (gathering.FixedBirthdaySubject && pawn == gathering.BirthdayPawn)
                {
                    var guardian = gathering.BirthdayGuardian;
                    bool ready = guardian == null || (guardian.carryTracker?.CarriedThing == pawn
                        && guardian.Position == gathering.CellFor(guardian) && !guardian.pather.Moving);
                    return ForStatus(ready ? PhotoParticipantStatus.Ready : PhotoParticipantStatus.Moving);
                }
                if (gathering.lord == null || !gathering.lord.ownedPawns.Contains(pawn)) return ForStatus(PhotoParticipantStatus.Moving);
                if (gathering.NotInPlace.Contains(pawn)) return ForStatus(PhotoParticipantStatus.Moving);
            }
            return ForStatus(PhotoParticipantStatus.Ready);
        }

        internal static void DrawEvent(Rect rect, PhotoRecord record, Color color, string eventSubtype = null)
            => PhotoIconArt.Draw(rect, For(record, eventSubtype), color);

        internal static void DrawStatus(Rect rect, PhotoParticipantStatus status, Color color)
        {
            var icon = ForStatus(status);
            if (icon.HasValue) PhotoIconArt.Draw(rect, icon.Value, color);
        }
    }
}
