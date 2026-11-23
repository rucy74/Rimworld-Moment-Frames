namespace MomentFrames
{
    /// <summary>Heuristic completion evidence. It cannot know furniture the player intends to add later.</summary>
    internal static class BuildingMilestonePolicy
    {
        internal const int QuietTicks = 6000, MinRoomCells = 8, MaxRoomCells = 4096;
        internal static bool ExistingAtBaseline(int unfinished, bool alreadyUsableEvidence)
            => unfinished == 0 || alreadyUsableEvidence;
        internal static bool FitsCapture(float span, float maxSpan) => span > 0f && span <= maxSpan;
        internal static bool NewSpace(int cells, int newCells, bool exactKnownFootprint, int structuralCells)
            => cells >= MinRoomCells && cells <= MaxRoomCells && !exactKnownFootprint
                && (newCells >= 4 || structuralCells >= 4);
        internal static bool Complete(bool enclosed, int openRoofCells, int unfinished, bool knownRoomRole,
            bool usableFurniture, int finishedFloorCells, int stockpileCells, int roomCells)
            => enclosed && openRoofCells == 0 && unfinished == 0 && roomCells >= MinRoomCells
                && knownRoomRole && usableFurniture;
        // A finished floor or a stockpile cell is not proof that a planned facility is ready.
        // Bedrooms and generic rooms are intentionally excluded from the default automatic building record.
        internal static string Facility(string role, bool research, bool books, bool workTable, bool hospitalBeds, bool furnished)
        {
            if (research) return "Laboratory";
            if (books) return "Library";
            if (role == "Hospital" && hospitalBeds) return role;
            if ((role == "Workshop" || role == "Kitchen") && workTable) return role;
            if (furnished && (role == "WorshipRoom" || role == "ThroneRoom" || role == "DiningRoom" || role == "RecRoom")) return role;
            return "";
        }
        internal static bool Settled(int now, int lastConstruction, int stableSince)
            => stableSince >= 0 && now >= lastConstruction && now >= stableSince
                && now - lastConstruction >= QuietTicks && now - stableSince >= QuietTicks;
    }
}
