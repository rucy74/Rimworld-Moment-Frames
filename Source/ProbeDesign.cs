using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    // Added after the save/load regression: all changes and captures stay in the marked disposable profile.
    public static partial class Probe
    {
        private static int designPhase;
        private static PhotoRecord designPhoto;
        private static Letter_MFYear designLetter;

        private static void DesignRequire(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("design regression: " + message);
            checks.Add("PASS: design — " + message);
        }

        private static bool ContainsRect(Rect parent, Rect child) => child.width > 0 && child.height > 0
            && child.xMin >= parent.xMin - .01f && child.yMin >= parent.yMin - .01f
            && child.xMax <= parent.xMax + .01f && child.yMax <= parent.yMax + .01f;

        private static Letter_MFYear DesignLetter(int year) => (Letter_MFYear)LetterMaker.MakeLetter(
            "MF_YearLetterTitle".Translate(year), "MF_YearLetterText".Translate(year, 3, ""),
            DefDatabase<LetterDef>.GetNamed("MF_YearLetter"));

        // A staged veranda for the screenshot, not a product building or a change to the player's colony.
        private static void PrepareNightScene(Building_PhotoProjector p)
        {
            var c = MemoryNight.ScreenCenter(p).ToIntVec3();
            var planks = DefDatabase<TerrainDef>.GetNamed("WoodPlankFloor");
            for (int dx = -6; dx <= 6; dx++)
                for (int dz = -7; dz <= 2; dz++)
                {
                    var cell = c + new IntVec3(dx, 0, dz);
                    if (!cell.InBounds(map)) continue;
                    cell.GetPlant(map)?.Destroy(DestroyMode.Vanish);
                    map.terrainGrid.SetTerrain(cell, planks);
                }
            foreach (int dx in new[] { -3, -1, 1, 3 })
                foreach (int dz in new[] { -3, -5 })
                {
                    var cell = c + new IntVec3(dx, 0, dz);
                    if (!cell.Standable(map) || cell.GetEdifice(map) != null) continue;
                    var chair = ThingMaker.MakeThing(ThingDef.Named("DiningChair"), ThingDefOf.WoodLog);
                    chair.SetFaction(Faction.OfPlayer);
                    GenSpawn.Spawn(chair, cell, map, Rot4.North);
                }
            foreach (int dx in new[] { -5, 5 })
                foreach (int dz in new[] { -2, -6 })
                {
                    var cell = c + new IntVec3(dx, 0, dz);
                    if (!cell.Standable(map) || cell.GetEdifice(map) != null) continue;
                    var lamp = ThingMaker.MakeThing(ThingDef.Named("TorchLamp"));
                    lamp.SetFaction(Faction.OfPlayer);
                    GenSpawn.Spawn(lamp, cell, map);
                    lamp.TryGetComp<CompRefuelable>()?.Refuel(20f);
                }
            checks.Add("INFO: design showcase staged wooden veranda, eight chairs and four torch lamps in disposable map");
        }

        private static void DesignStep()
        {
            var store = PhotoStore.Instance;
            var album = (MainTabWindow_Album)AlbumDef.TabWindow;
            switch (designPhase)
            {
                case 0:
                    CloseLog();
                    Messages.Clear();
                    designPhoto = store.Get(savedFramePhotoId) ?? store.Records.First(r => r.HasFile);
                    foreach (float width in new[] { 1000f, 1280f })
                    {
                        var body = new Rect(22f, 76f, width - 44f, 468f);
                        MainTabWindow_Album.GalleryLayout(body, out var image, out var info, out var rail);
                        MainTabWindow_Album.GalleryInfoLayout(info, out var title, out var meta, out var story, out var actions);
                        var blocks = new[] { title, meta, story, actions };
                        DesignRequire(blocks.All(r => ContainsRect(info, r)) && title.yMax <= meta.yMin && meta.yMax <= story.yMin
                            && story.yMax <= actions.yMin && title.width >= 550f && story.width >= 650f,
                            "gallery title/date/story/actions fit without overlap at " + width + "x600");
                        var buttons = Enumerable.Range(0, 4).Select(i => MainTabWindow_Album.GalleryActionRect(actions, i, 4)).ToArray();
                        DesignRequire(buttons.All(r => ContainsRect(actions, r)) && buttons.Zip(buttons.Skip(1), (a, b) => a.xMax < b.xMin).All(x => x),
                            "four gallery actions stay inside the story page at width " + width);
                        var band = MainTabWindow_Album.VersionBandRect(image);
                        var viewport = MainTabWindow_Album.VersionViewportRect(band);
                        DesignRequire(ContainsRect(image, band) && ContainsRect(band, viewport)
                            && MainTabWindow_Album.VersionContentRect(11).width > viewport.width,
                            "eleven versions use a contained scroll viewport at width " + width);
                    }
                    while (designPhoto.edits.Count < 10)
                    {
                        var edit = store.SaveEdit(designPhoto, new Rect(0, 0, 1, 1), PhotoFilters.None);
                        if (edit == null) throw new InvalidOperationException("design edit could not be written");
                        edit.title = "편집 " + designPhoto.edits.Count;
                    }
                    designPhoto.title = "함께 버틴 첫 겨울, 정착지의 오래 기억할 저녁";
                    designPhoto.desc = "눈이 그치고 식탁에 다시 모였다. 처음 만났을 때의 낯설음은 어느새 사라졌고, 다 같이 버틴 겨울 이야기는 밤이 깊도록 이어졌다.\n이 사진은 다음 겨울에도 꺼내 보고 싶은 정착지의 기록이다.";
                    store.NotifyChanged();
                    string used = designPhoto.Shown.id;
                    MainTabWindow_Album.ShowVersion(designPhoto, "");
                    DesignRequire(designPhoto.Shown.id == used && album.GalleryVersion == "", "viewing the original leaves the used edit intact");
                    var menu = album.GalleryMoreOptions(designPhoto);
                    var use = menu.FirstOrDefault(o => o.Label == "MF_DesignAlbum_UseVersion".Translate().ToString());
                    DesignRequire(use?.action != null && menu.Any(o => o.Label == "MF_OpenFolder".Translate().ToString()), "More retains use-version and folder actions");
                    use.action();
                    DesignRequire(designPhoto.Shown == null, "use-version menu changes album/frame representation to original");
                    MainTabWindow_Album.ShowVersion(designPhoto, designPhoto.edits[9].id);
                    var deletion = album.GalleryMoreOptions(designPhoto).First(o => o.Label == "MF_DeleteEdit".Translate().ToString());
                    deletion.action();
                    DesignRequire(Find.WindowStack.Windows.OfType<Dialog_MessageBox>().Any() && designPhoto.edits.Count == 10, "deleting a version still asks before removing its file");
                    Find.WindowStack.TryRemove(typeof(Dialog_MessageBox), false);
                    MainTabWindow_Album.ShowVersion(designPhoto, "");
                    album.windowRect = new Rect(60f, 55f, 1000f, 600f);
                    Wait(.8f); designPhase = 1; return;
                case 1:
                    Shot("design-gallery-min.png");
                    Wait(.8f); designPhase = 11; return;
                case 11:
                    MainTabWindow_Album.ShowVersion(designPhoto, designPhoto.edits[9].id);
                    Wait(.8f); designPhase = 2; return;
                case 2:
                    Shot("design-versions-last.png");
                    Wait(.8f); designPhase = 12; return;
                case 12:
                    DesignRequire(Traverse.Create(album).Field("versionScroll").GetValue<Vector2>().x > 0f, "the last of ten edits scrolls into view in the actual gallery");
                    MainTabWindow_Album.OpenYear(9999);
                    DesignRequire(album.EmptyMessageKey == "MF_DesignAlbum_NoMatches"
                        && Traverse.Create(album).Method("Filtered").GetValue<List<PhotoRecord>>().Count == 0, "an unmatched year reports filters, while photos still exist");
                    Wait(.8f); designPhase = 3; return;
                case 3:
                    Shot("design-empty-filter.png");
                    Wait(.8f); designPhase = 13; return;
                case 13:
                    album.ClearFilters();
                    DesignRequire(album.YearFilter == 0 && album.PersonFilter == null
                        && Traverse.Create(album).Method("Filtered").GetValue<List<PhotoRecord>>().Count > 0, "Clear filters restores visible photos and resets person/year");
                    Find.MainTabsRoot.EscapeCurrentTab(false);
                    var candidates = store.Records.Where(r => r.HasFile).Take(9).ToList();
                    DesignRequire(candidates.Count == 9, "nine real photos available for full-place-list test");
                    var pin = new Places.Pin { at = Vector2.zero };
                    pin.photos.AddRange(candidates);
                    Places.Options(pin)[8].action();
                    var picker = Find.WindowStack.Windows.OfType<Dialog_ChoosePhoto>().Last();
                    var ids = Traverse.Create(picker).Field("allowedIds").GetValue<HashSet<string>>();
                    DesignRequire(ids.SetEquals(candidates.Select(r => r.id)), "place overflow opens exactly that pin's nine photos");
                    var probeRec = new PhotoRecord { title = "Winter portrait", desc = "조용한 저녁", people = new List<string> { "Victoria" },
                        edits = new List<PhotoEdit> { new PhotoEdit { id = "a", file = "edit.png", title = "다시 모인 날", note = "winter note" } }, shownEdit = "a" };
                    DesignRequire(Dialog_ChoosePhoto.MatchesSearch(probeRec, "PORTRAIT") && Dialog_ChoosePhoto.MatchesSearch(probeRec, "저녁")
                        && Dialog_ChoosePhoto.MatchesSearch(probeRec, "victoria") && Dialog_ChoosePhoto.MatchesSearch(probeRec, "다시 모인")
                        && !Dialog_ChoosePhoto.MatchesSearch(probeRec, "unmatched") && Dialog_ChoosePhoto.MatchesSearch(probeRec, "  "),
                        "picker finds original/used text and people, case-insensitively, and handles empty/no matches");
                    Traverse.Create(picker).Field("search").SetValue(candidates[0].people.FirstOrDefault() ?? candidates[0].title);
                    Wait(.8f); designPhase = 4; return;
                case 4:
                    Shot("design-place-search.png");
                    Wait(.8f); designPhase = 14; return;
                case 14:
                    var placePicker = Find.WindowStack.Windows.OfType<Dialog_ChoosePhoto>().Last();
                    var pickId = Traverse.Create(placePicker).Field("allowedIds").GetValue<HashSet<string>>().First();
                    Traverse.Create(placePicker).Field("onPick").GetValue<Action<string>>()(pickId);
                    DesignRequire(album.GalleryCurrent?.id == pickId, "picking from a place list opens that photograph's gallery");
                    placePicker.Close();
                    Find.MainTabsRoot.EscapeCurrentTab(false);
                    int year = store.Records.Where(r => r.HasFile && r.year != 0).GroupBy(r => r.year).OrderByDescending(g => g.Count()).First().Key;
                    var samples = new List<PhotoRecord>();
                    foreach (int n in new[] { 0, 1, 2 })
                    {
                        samples = Enumerable.Range(0, n).Select(i => new PhotoRecord { id = "s" + i, year = year, kind = "wedding", file = "x", ticksAbs = i }).ToList();
                        DesignRequire(Dialog_MFYearPhotos.SelectHighlights(samples, year).Count == n, "year highlights tolerate " + n + " remaining photos");
                    }
                    samples.Add(new PhotoRecord { id = "d", year = year, kind = "recruit", file = "x", ticksAbs = 50 });
                    samples.Add(new PhotoRecord { id = "e", year = year, kind = "group", file = "x", ticksAbs = 100 });
                    samples.Add(new PhotoRecord { id = "private", year = year, kind = Kinds.Birthday, file = "x" });
                    samples.Add(new PhotoRecord { id = "other", year = year - 1, kind = "baby", file = "x" });
                    samples.Add(new PhotoRecord { id = "relic", year = year, kind = Kinds.Relic, file = "x" });
                    var highlights = Dialog_MFYearPhotos.SelectHighlights(samples, year);
                    DesignRequire(highlights.Count == 3 && highlights.Select(r => r.kind).Distinct().Count() == 3
                        && highlights.All(r => r.year == year && r.HasFile && !Kinds.PersonalOnly(r.kind) && r.kind != Kinds.Relic)
                        && highlights.Select(r => r.id).SequenceEqual(Dialog_MFYearPhotos.SelectHighlights(samples.AsEnumerable().Reverse(), year).Select(r => r.id)),
                        "year highlights cover three event kinds, omit other/private years and stay deterministic");
                    var peopleSamples = new[]
                    {
                        new PhotoRecord { id = "1", file = "x", year = year, kind = "wedding", people = new List<string> { "A" } },
                        new PhotoRecord { id = "2", file = "x", year = year, kind = "wedding", people = new List<string> { "A" } },
                        new PhotoRecord { id = "3", file = "x", year = year, kind = "wedding", people = new List<string> { "B" } },
                        new PhotoRecord { id = "4", file = "x", year = year, kind = "wedding", people = new List<string> { "C" } },
                        new PhotoRecord { id = "manual", file = "x", year = year, kind = Kinds.Manual, people = new List<string> { "D", "E", "F" } }
                    };
                    var chosen = Dialog_MFYearPhotos.SelectHighlights(peopleSamples, year);
                    DesignRequire(chosen.All(r => r.kind == "wedding") && chosen.SelectMany(r => r.people).Distinct().Count() == 3,
                        "representative selection prefers milestones and new people over duplicate faces");
                    var layout = Dialog_MFYearPhotos.LayoutFor(new Rect(0, 0, 964f, 604f), 3);
                    DesignRequire(layout.Cards.All(r => ContainsRect(layout.PhotoArea, r)) && layout.PhotoArea.yMax < layout.Hint.yMin
                        && layout.AlbumButton.xMax < layout.NightButton.xMin && layout.NightButton.xMax < layout.CloseButton.xMin,
                        "year letter's three photos, hint and actions fit without overlap");
                    designLetter = DesignLetter(year); designLetter.year = year;
                    Find.LetterStack.ReceiveLetter(designLetter);
                    designLetter.OpenLetter();
                    DesignRequire(Find.WindowStack.Windows.OfType<Dialog_MFYearPhotos>().Any(w => w.Year == year && w.RepresentativePhotos.Count == 3),
                        "a real year letter opens the three-photo retrospective window");
                    Wait(.8f); designPhase = 5; return;
                case 5:
                    CloseLog();
                    Shot("design-year-letter.png");
                    Wait(.8f); designPhase = 6; return;
                case 6:
                    var yearWindow = Find.WindowStack.Windows.OfType<Dialog_MFYearPhotos>().Last();
                    int selectedYear = yearWindow.Year;
                    yearWindow.OpenAlbum();
                    DesignRequire(album.YearFilter == selectedYear && !Find.WindowStack.Windows.OfType<Dialog_MFYearPhotos>().Any()
                        && !Find.LetterStack.LettersListForReading.Contains(designLetter), "the year-page action opens its year and consumes the closed notification");
                    Find.MainTabsRoot.EscapeCurrentTab(false);
                    designLetter.OpenLetter();
                    var opened = Find.WindowStack.Windows.OfType<Dialog_MFYearPhotos>().Last();
                    string firstPhoto = opened.RepresentativePhotos[0].id;
                    opened.OpenPhoto(0);
                    DesignRequire(album.GalleryCurrent?.id == firstPhoto && !Find.WindowStack.Windows.OfType<Dialog_MFYearPhotos>().Any(), "a representative print opens its full gallery photo and closes the page");
                    Find.MainTabsRoot.EscapeCurrentTab(false);
                    Wait(.8f); designPhase = 7; return;
                case 7:
                    foreach (string shot in new[] { "design-gallery-min.png", "design-versions-last.png", "design-empty-filter.png", "design-place-search.png", "design-year-letter.png" }) Require(shot);
                    Finish(); return;
            }
        }
    }
}
