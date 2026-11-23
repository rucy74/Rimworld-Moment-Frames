using HarmonyLib;
using Verse;

namespace MomentFrames
{
    public static partial class Probe
    {
        /// <summary>Native UI helper only: existing Probe must already have passed its marked disposable-profile gate.</summary>
        public static bool FeedbackRecordsOpen(int selectedMap, int selectedStamp, bool everyPlace, bool changeHistory, int region = 0)
        {
            if (!active || Current.ProgramState != ProgramState.Playing || PhotoStore.Instance == null) return false;
            SettlementOpenTab();
            var state = Traverse.Create(typeof(SettlementRecordsTab));
            state.Method("Reset").GetValue();
            // Draw's first store binding resets unowned state. Bind owner first so the requested view survives its first draw.
            state.Field("owner").SetValue(PhotoStore.Instance);
            state.Method("Select", selectedMap, selectedStamp).GetValue();
            state.Field("allPlaces").SetValue(everyPlace);
            state.Field("historyMode").SetValue(changeHistory);
            state.Field("historyRegion").SetValue(region < 0 ? 0 : region > 4 ? 4 : region);
            return true;
        }
    }
}
