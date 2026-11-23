using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace MomentFrames
{
    /// <summary>Capture stages only own automatic event choices. They never reset appearance, manual photography, or chronicles.</summary>
    internal static class SettingsCapturePolicy
    {
        internal const int Custom = -1, Major = 0, Normal = 1, Rich = 2;
        private static readonly string[] names =
        {
            "birthdayEvery", "msBaby", "msDeath", "msRecruit", "msResurrect", "msPersonal", "capturePetArrivals",
            "captureWeddings", "captureParties", "captureFunerals", "captureRituals", "msRole", "msBestow", "msRelic", "battleMode",
            "msMechBoss", "msDisaster", "msSky", "msMechlink", "msRelicFound", "msRareAnimal", "msAnomaly", "msMonolith", "msJourney",
            "msColonyStart", "msLegendary", "msShip", "msHistoric", "msEnding"
        };
        private static readonly FieldInfo[] fields = names.Select(n => typeof(MomentFramesSettings).GetField(n)).ToArray();
        internal static IReadOnlyList<string> OwnedFields => names;

        internal static bool Automatic(MomentFramesSettings settings) => settings?.autoCaptureEnabled == true;
        internal static bool ShouldCancel(bool automaticCapture, MomentFramesSettings settings) => automaticCapture && !Automatic(settings);
        internal static bool Owns(string field) => Array.IndexOf(names, field) >= 0;

        internal static Dictionary<string, object> Snapshot(MomentFramesSettings settings)
        {
            var result = new Dictionary<string, object>();
            foreach (var field in fields) result.Add(field.Name, field.GetValue(settings));
            return result;
        }

        internal static void Restore(MomentFramesSettings settings, IReadOnlyDictionary<string, object> snapshot)
        {
            // Validate the whole snapshot before changing a field, so an incomplete snapshot cannot partly reset choices.
            foreach (var field in fields)
                if (!snapshot.TryGetValue(field.Name, out object value) || value == null || value.GetType() != field.FieldType)
                    throw new ArgumentException("Invalid capture stage snapshot: " + field.Name, nameof(snapshot));
            foreach (var field in fields) field.SetValue(settings, snapshot[field.Name]);
        }

        private static Dictionary<string, object> Values(int stage)
        {
            var result = names.ToDictionary(n => n, n => n == "birthdayEvery" ? (object)1 : n == "battleMode" ? (object)2 : true);
            if (stage == Major)
            {
                result["birthdayEvery"] = 5;
                result["battleMode"] = 1;
                foreach (string n in new[] { "captureParties", "captureRituals", "msRole", "msBestow", "msLegendary", "msSky", "msRareAnimal", "msAnomaly", "msJourney" })
                    result[n] = false;
            }
            else if (stage == Normal)
            {
                result["birthdayEvery"] = 2;
                result["captureParties"] = result["msSky"] = result["msLegendary"] = false;
            }
            return result;
        }

        internal static int PresetOf(MomentFramesSettings settings)
        {
            if (settings == null) return Custom;
            for (int stage = Major; stage <= Rich; stage++)
            {
                var preset = Values(stage);
                if (fields.All(f => Equals(f.GetValue(settings), preset[f.Name]))) return stage;
            }
            return Custom;
        }

        internal static bool ApplyPreset(MomentFramesSettings settings, int stage)
        {
            if (settings == null || stage < Major || stage > Rich || PresetOf(settings) == stage) return false;
            Restore(settings, Values(stage));
            return true;
        }
    }
}
