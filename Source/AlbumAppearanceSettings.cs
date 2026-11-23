using System.Linq;
using Verse;

namespace MomentFrames
{
    internal static class AlbumAppearanceSettings
    {
        internal static void Draw(Listing_Standard listing, MomentFramesSettings s)
        {
            AlbumStyle.Normalize(s);
            listing.Label("MF_Appearance_Title".Translate());
            listing.Label("MF_Appearance_Hint".Translate());
            if (listing.ButtonText("MF_Appearance_Theme".Translate(AlbumStyle.Theme.Label)))
                Find.WindowStack.Add(new FloatMenu(AlbumStyle.Themes.Select(t => new FloatMenuOption(t.Label, () => s.albumTheme = t.Key)).ToList()));
            if (listing.ButtonText("MF_Appearance_Font".Translate(AlbumStyle.FontLabel(s.albumFont))))
                Find.WindowStack.Add(new FloatMenu(AlbumStyle.FontChoices.Select(f => new FloatMenuOption(AlbumStyle.FontLabel(f), () => s.albumFont = f)).ToList()));
            if (listing.ButtonText("MF_Appearance_Size".Translate(s.albumTextSize)))
                Find.WindowStack.Add(new FloatMenu(AlbumStyle.Sizes.Select(n => new FloatMenuOption(n + "%", () => s.albumTextSize = n)).ToList()));
            if (listing.ButtonText("MF_Appearance_Reset".Translate()))
            {
                s.albumTheme = "graphite"; s.albumFont = "original"; s.albumTextSize = 100;
            }
            listing.GapLine();
        }
    }
}
