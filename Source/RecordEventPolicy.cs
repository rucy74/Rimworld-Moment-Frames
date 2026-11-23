using System;
using System.Collections.Generic;

namespace MomentFrames
{
    /// <summary>Observable transitions; an existing state at first observation is never a new event.</summary>
    internal sealed class EquipmentObservation
    {
        private readonly Dictionary<string, string> previous = new Dictionary<string, string>(StringComparer.Ordinal);
        internal bool ChangedToPersona(string pawnId, string weaponId, bool persona)
        {
            string state = persona && !string.IsNullOrEmpty(weaponId) ? weaponId : "";
            bool known = previous.TryGetValue(pawnId, out string before);
            previous[pawnId] = state;
            return known && state.Length > 0 && !string.Equals(before, state, StringComparison.Ordinal);
        }
        internal void Clear() => previous.Clear();
    }
    internal static class RecordEventPolicy
    {
        internal static bool NewPlayerFaction(bool wasPlayer, bool isPlayer, bool loading, bool playing)
            => playing && !loading && !wasPlayer && isPlayer;
        internal static bool PendingJoinEvidence(int observedTick, int deadlineTick, int nowTick)
            => observedTick >= 0 && deadlineTick >= observedTick && nowTick >= observedTick && nowTick <= deadlineTick;
        internal static bool KeepJoinEvidence(HashSet<string> known, string pawnId, int observedTick, int deadlineTick, int nowTick)
            => !string.IsNullOrEmpty(pawnId) && PendingJoinEvidence(observedTick, deadlineTick, nowTick) && known.Add(pawnId);
        internal static bool NewInjury(float initialSeverity, float currentSeverity, float initialBleeding, float currentBleeding)
            => currentSeverity - initialSeverity >= 5f || initialBleeding <= 0f && currentBleeding > 0f;
        internal static bool HardFight(string type, bool newLossOrInjury, int enemies, int colonists)
            => newLossOrInjury || type != "animals" && enemies >= Math.Max(1, colonists);
        internal static string AnniversaryDayKey(int year, int dayOfYear) => "letter:day:" + year + ":" + dayOfYear;
        internal static bool IncludePlace(int selectedMap, bool allPlaces, int photoMap, bool survey, bool available)
            => available && !survey && (allPlaces || selectedMap == photoMap);
    }
}
