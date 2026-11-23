using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using RimWorld;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    /// <summary>Steam Workshop media (2026-11-01; -momentFramesSteamMedia on a shots run, optionally -momentFramesSteamExtras=folderA;folderB):
    /// frame sequences rendered by the mod's own camera for animated GIFs (a family walking into a lit studio and sitting for the portrait,
    /// a group lining up at a photo spot with emotes, a memory night's film), native UI frames (the album in every theme, the editor's looks)
    /// and stills of the wall and the memory tree. Each scene runs on its own; one that throws is recorded and the next one starts. Staged
    /// data exists only in this throwaway profile; the photo folders are only read. Sequence frames go to steam/{scene}/NNN.png with the
    /// game tick and real time of each frame in times.txt; the GIFs are put together outside the game.</summary>
    public static partial class Probe
    {
        private static bool steamMediaMode;
        private static string steamExtras;
        private static int steamScene, steamStep, steamFrame, steamHold, steamTakes;
        private static bool steamPending;
        private static string steamSeq;
        private static Vector3 steamAt;
        private static float steamSpan;
        private static bool steamLight;
        private static StringBuilder steamTimes;
        private static readonly string[] SteamScenes = { "cast", "studio", "group", "night", "wall", "life", "symbol", "album", "editor", "ui", "auto", "lang", "reserve", "time", "dcalbum" };
        private static string steamOnly;
        private static string steamSymbol;   // the whole colony's portrait, the photo the "symbol" scene throws across the land
        private static readonly StringBuilder steamLayout = new StringBuilder();
        private static readonly List<string> steamPhotos = new List<string>();   // photos the scenes took, for the wall, the album and the editor
        private static Building_PhotoStudio steamBackdrop;
        private static List<Building_PhotoStudioLamp> steamLamps;
        private static List<Pawn> steamPeople;
        private static LordJob_MFGroupPhoto steamJob;
        private static Building_PhotoProjector steamProjector;
        private static Building_PhotoFrame steamStyleFrame;
        private static readonly FrameStyle[] SteamStyles = { FrameStyle.Material, FrameStyle.BlackMat, FrameStyle.Gilded, FrameStyle.Polaroid, FrameStyle.Canvas };

        private class SteamExtra
        {
            public int folder;
            public string prefix, kind;
            public Func<string> title;
            public string[] people;
        }

        // The author's later colonies (folder 0: a year and a half of autumn play, folder 1: the first studio portraits), titled with the
        // mod's own templates and this colony's names so the album, chronicle and filters stay consistent.
        private static readonly SteamExtra[] SteamExtraPhotos =
        {
            new SteamExtra { folder = 1, prefix = "0006_", kind = "group", title = () => "MF_GroupPhoto".Translate(3), people = new[] { "Evie", "Elna", "Karl" } },
            new SteamExtra { folder = 1, prefix = "0003_", kind = "petphoto", title = () => "MF_PetPairTitle".Translate(Ko("Kelo"), Ko("Biscuit")), people = new[] { "Kelo" } },
            new SteamExtra { folder = 0, prefix = "0012_", kind = "disaster", title = () => "MF_DisasterDuring".Translate(Ko("heat wave")), people = new[] { "Julian", "Moray" } },
            new SteamExtra { folder = 0, prefix = "0019_", kind = "party", title = () => "MF_Party".Translate(Ko("Mitsuya")), people = new[] { "Mitsuya", "Julian", "Celeste", "Kelo" } },
            new SteamExtra { folder = 0, prefix = "0025_", kind = "birthday", title = () => "MF_BirthdayTitle".Translate(Ko("Julian"), 45), people = new[] { "Julian", "Celeste", "Victoria", "Evie" } },
            new SteamExtra { folder = 0, prefix = "0027_", kind = "anniversary", title = () => "MF_Anniversary".Translate(1), people = new string[0] },
            new SteamExtra { folder = 0, prefix = "0028_", kind = "journey", title = () => "MF_JourneyFirstBiome".Translate(Ko("mushroom jungle")), people = new[] { "Mitsuya", "Moray" } },
            new SteamExtra { folder = 0, prefix = "0029_", kind = "party", title = () => "MF_Celebration".Translate(Ko("Unity festival"), Ko("Victoria")), people = new[] { "Victoria", "Julian", "Celeste", "Karl" } },
            new SteamExtra { folder = 0, prefix = "0034_", kind = "wedding", title = () => "MF_Wedding".Translate(Ko("Julian"), Ko("Celeste")), people = new[] { "Julian", "Celeste", "Victoria", "Mitsuya", "Moray", "Evie" } },
            new SteamExtra { folder = 0, prefix = "0035_", kind = "journey", title = () => "MF_JourneyFirstBiome".Translate(Ko("eye forest")), people = new[] { "Julian", "Evie" } },
            new SteamExtra { folder = 0, prefix = "0036_", kind = "battle", title = () => "MF_Battle_animals".Translate(), people = new[] { "Karl", "Kelo", "Mushinto" } },
        };

        // ---------- the cast: the races the author plays with, in everyday clothes (same subset as the Little Playgrounds film) ----------

        private class SteamActor
        {
            public string name, kind, hair;
            public int age;
            public bool male, child;
            public string[] wear;   // all or nothing: a pawn the set does not fit stops the scene rather than film what the generator left
            public Color? hairColor;
        }

        private static readonly SteamActor[] SteamActors =
        {
            new SteamActor { name = "Victoria", kind = "Milira_Colonist", age = 25, wear = new[] { "Milira_DelicateDressA_II" } },
            new SteamActor { name = "Celeste", kind = "Kiiro_Colonist", age = 24, wear = new[] { "Kiiro_CasualDress" } },
            new SteamActor { name = "Mitsuya", kind = "Miho_PlayerColonist", age = 24, wear = new[] { "Miho_Apparel_OnSkin_Simple" } },
            new SteamActor { name = "Moray", kind = "RatkinColonist", age = 24, wear = new[] { "RK_ApronSkirt" } },
            new SteamActor { name = "Evie", kind = "Mincho_Colonist", age = 24, wear = new[] { "Mincho_Knitwear" } },
            new SteamActor { name = "Elna", kind = "Colonist", age = 26, hair = "PPHFl03", wear = new[] { "Apparel_CollarShirt", "Apparel_Pants" } },
            new SteamActor { name = "Julian", kind = "Colonist", age = 30, male = true, hair = "PPHMs08", wear = new[] { "Apparel_CollarShirt", "Apparel_Pants" } },
            new SteamActor { name = "Mushinto", kind = "Colonist", age = 28, male = true, hair = "PPHMs10", wear = new[] { "Apparel_BasicShirt", "Apparel_Pants" } },
            new SteamActor { name = "Lily", kind = "RatkinColonist", age = 8, child = true, hair = "RK_Bob1", hairColor = new Color(.56f, .34f, .19f), wear = new[] { "RK_ApronSkirtChildren" } },   // r7: a long black fringe hid her face
            new SteamActor { name = "Kelo", kind = "Kiiro_Colonist", age = 26, wear = new[] { "Kiiro_HighWaistedDress" } },
        };

        /// <summary>Replaces the quick-test colonists when the race mods are loaded; false leaves the vanilla colonists to ShotsImport.</summary>
        private static bool SteamCast()
        {
            if (!steamMediaMode || SteamActors.Any(a => DefDatabase<PawnKindDef>.GetNamedSilentFail(a.kind) == null)) return false;
            foreach (var p in map.mapPawns.FreeColonistsSpawned.ToList())
            {
                p.DeSpawn();
                Find.WorldPawns.PassToWorld(p, RimWorld.Planet.PawnDiscardDecideMode.KeepForever);
            }
            Color[] shirts = { new Color(.92f, .84f, .69f), new Color(.59f, .72f, .65f), new Color(.60f, .76f, .83f), new Color(.89f, .68f, .57f) };
            Color[] hairs = { new Color(.23f, .15f, .10f), new Color(.14f, .12f, .11f), new Color(.40f, .25f, .15f) };
            int k = 0;
            Rand.PushState(20261101);   // the same faces and hair on every run, so a re-film matches the rest of the page
            foreach (var a in SteamActors)
            {
                var p = PawnGenerator.GeneratePawn(new PawnGenerationRequest(DefDatabase<PawnKindDef>.GetNamed(a.kind), Faction.OfPlayer, forceGenerateNewPawn: true, canGeneratePawnRelations: false,
                    allowDowned: false, fixedBiologicalAge: a.age, fixedChronologicalAge: a.age, fixedGender: a.male ? Gender.Male : Gender.Female,
                    developmentalStages: a.child ? DevelopmentalStage.Child : DevelopmentalStage.Adult, dontGiveWeapon: true));
                p.Name = new NameTriple(Ko(a.name), Ko(a.name), Ko("Rowe"));
                GenSpawn.Spawn(p, CellFinder.RandomClosewalkCellNear(new IntVec3(center.x, 0, center.z - 14), map, 5), map);
                if (a.hair != null && DefDatabase<HairDef>.GetNamedSilentFail(a.hair) is HairDef hair)
                {
                    p.story.hairDef = hair;
                    p.story.HairColor = a.hairColor ?? hairs[k % hairs.Length];
                }
                if (p.def == ThingDefOf.Human && !a.child)
                {
                    p.story.bodyType = a.male ? BodyTypeDefOf.Male : BodyTypeDefOf.Female;
                    if (p.style != null) p.style.beardDef = DefDatabase<BeardDef>.GetNamed("NoBeard");
                }
                if (a.wear != null)
                {
                    var defs = a.wear.Select(DefDatabase<ThingDef>.GetNamedSilentFail).ToList();
                    if (defs.All(d => d?.apparel != null && (d.apparel.developmentalStageFilter & p.DevelopmentalStage) != 0 && ApparelUtility.HasPartsToWear(p, d)))
                    {
                        p.apparel.DestroyAll();
                        foreach (var d in defs)
                        {
                            var cloth = (Apparel)ThingMaker.MakeThing(d, d.MadeFromStuff ? ThingDefOf.Cloth : null);
                            // a race's own clothes keep their painted colours under white; plain clothes get a shirt colour
                            cloth.SetColor(!d.defName.StartsWith("Apparel_") ? Color.white : d.defName.Contains("Pants") ? new Color(.43f, .48f, .52f) : shirts[k % shirts.Length]);
                            p.apparel.Wear(cloth, false);
                        }
                    }
                    else throw new InvalidOperationException("steam cast: " + a.name + " cannot wear " + string.Join(",", a.wear));   // never film a pawn in whatever the generator left
                }
                p.Drawer.renderer.SetAllGraphicsDirty();
                PortraitsCache.SetDirty(p);
                k++;
            }
            Rand.PopState();
            checks.Add("INFO: steam cast — " + string.Join(", ", map.mapPawns.FreeColonistsSpawned.Select(p => p.LabelShort + "/" + p.def.defName)));
            return true;
        }

        /// <summary>Mincho stand tall, and whoever stands right behind one hides her head (r7). She takes a place with nobody behind it;
        /// null keeps the game's own arrangement.</summary>
        private static Dictionary<string, IntVec3> SteamPlaces(IntVec3 spot, List<Pawn> people)
        {
            var tall = ShotPawn("Evie");
            var plan = PhotoGatheringPlan.Create(map, spot, people, Rot4.North);
            if (tall == null || !plan.Moving.Contains(tall)) return null;
            var seats = Building_PhotoStudio.At(map, spot)?.SeatCells() ?? new List<IntVec3>();
            var cells = plan.Cells;
            int mine = plan.Moving.IndexOf(tall);
            var open = Enumerable.Range(0, cells.Count).Where(i => SteamNobodyBehind(cells, cells[i]) && !seats.Contains(cells[i])).OrderBy(i => cells[i].z).ToList();
            if (open.Count == 0) return null;
            if (open[0] != mine)
            {
                var other = cells[open[0]];
                cells[open[0]] = cells[mine];
                cells[mine] = other;
            }
            return PhotoPositions.Capture(plan.Moving, cells, spot, Rot4.North);
        }

        private static bool SteamNobodyBehind(IList<IntVec3> cells, IntVec3 c) => !cells.Any(o => o.x == c.x && o.z > c.z);

        /// <summary>The smile where this pawn's installed face textures have one (Facial Animation), otherwise the face as it is.</summary>
        private static Dictionary<string, int> SteamFaces(IEnumerable<Pawn> people)
            => people.ToDictionary(p => p.GetUniqueLoadID(), p => PhotoFaces.Available && PhotoFaces.Supports(p, 1) ? 1 : 0);

        private static bool SteamCastSheet()
        {
            switch (steamStep)
            {
                case 0:
                    {
                        Directory.CreateDirectory(Path.Combine(dir, "steam"));
                        var row = new IntVec3(center.x - 5, 0, center.z - 16);
                        var floor = CellRect.FromLimits(row.x - 2, row.z - 3, row.x + 12, row.z + 3);
                        foreach (var c in floor)
                        {
                            foreach (var t in c.GetThingList(map).ToList()) if (!(t is Pawn) && t.def.destroyable) t.Destroy(DestroyMode.Vanish);
                            map.terrainGrid.SetTerrain(c, DefDatabase<TerrainDef>.GetNamed("WoodPlankFloor"));
                        }
                        var people = map.mapPawns.FreeColonistsSpawned.ToList();
                        var sb = new StringBuilder("name\trace\tage\tgender\thair\tapparel\tsmile\n");
                        for (int i = 0; i < people.Count; i++)
                        {
                            var p = people[i];
                            Teleport(p, row + new IntVec3(i, 0, 0));
                            sb.Append(p.LabelShort).Append('\t').Append(p.def.defName).Append('\t').Append(p.ageTracker.AgeBiologicalYears).Append('\t').Append(p.gender).Append('\t')
                                .Append(p.story?.hairDef?.defName).Append('\t').Append(string.Join(",", p.apparel.WornApparel.Select(a => a.def.defName))).Append('\t')
                                .Append(PhotoFaces.Available && PhotoFaces.Supports(p, 1)).Append('\n');
                        }
                        File.WriteAllText(Path.Combine(dir, "steam", "cast.tsv"), sb.ToString());
                        checks.Add("INFO: steam cast — faces available " + PhotoFaces.Available);
                        Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                        JumpToHour(11f);
                        Find.TickManager.DoSingleTick();
                        foreach (var p in people)
                        {
                            p.jobs?.StopAll();
                            p.pather.StopDead();
                            p.Rotation = Rot4.South;
                        }
                        SettleDraw();
                        Wait(0.5f);
                        steamStep = 1;
                        return false;
                    }
                case 1:
                    PhotoCapture.ProbePixelsPerCell = 160;
                    ShowCapture(Path.Combine("steam", "cast.png"), new Vector3(center.x, 0f, center.z - 15.6f), 5f, 2.4f);
                    steamStep = 2;
                    return false;
                default:
                    if (!ShowCaptured()) return false;
                    PhotoCapture.ProbePixelsPerCell = 0;
                    return true;
            }
        }

        /// <summary>More of the author's photos after the shots set, same rules as ShotsImport (date from the file name, people by name).</summary>
        private static void SteamImportExtras()
        {
            if (steamExtras.NullOrEmpty()) return;
            var folders = steamExtras.Split(';').Select(f => f.Trim()).ToArray();
            var store = PhotoStore.Instance;
            var records = (List<PhotoRecord>)HarmonyLib.AccessTools.Field(typeof(PhotoStore), "records").GetValue(store);
            var ll = Find.WorldGrid.LongLatOf(map.Tile);
            int now = Find.TickManager.TicksAbs;
            int y0 = GenDate.Year(now, ll.x), q0 = (int)GenDate.Quadrum(now, ll.x), d0 = GenDate.DayOfQuadrum(now, ll.x);
            int index = records.Count + 1, added = 0;
            foreach (var sp in SteamExtraPhotos)
            {
                if (sp.folder >= folders.Length || !Directory.Exists(folders[sp.folder])) continue;
                string folder = folders[sp.folder];
                string file = Directory.GetFiles(folder, sp.prefix + "*.png").Select(Path.GetFileName).FirstOrDefault(f => !f.EndsWith(".thumb.png"));
                if (file == null) throw new InvalidOperationException("no photo starting " + sp.prefix + " in " + folder);
                var slug = file.Split('_')[1].Split('-').Select(int.Parse).ToArray();
                int ticks = now + (((slug[0] - y0) * 4 + (slug[1] - 1 - q0)) * 15 + (slug[2] - 1 - d0)) * GenDate.TicksPerDay + GenDate.TicksPerHour * 6;
                byte[] png = File.ReadAllBytes(Path.Combine(folder, file));
                string thumbSrc = Path.Combine(folder, Path.GetFileNameWithoutExtension(file) + ".thumb.png");
                byte[] thumb = File.Exists(thumbSrc) ? File.ReadAllBytes(thumbSrc) : null;
                float aspect = PhotoCapture.MakeThumb(png, ref thumb);
                var pawns = sp.people.Select(ShotPawn).Where(p => p != null).ToList();
                var rec = new PhotoRecord
                {
                    id = Guid.NewGuid().ToString("N"),
                    kind = sp.kind,
                    eventKey = "shots:x" + sp.folder + "_" + sp.prefix,
                    title = sp.title(),
                    desc = ShotDesc(sp.kind, pawns, ticks, ll, null),
                    ticksAbs = ticks,
                    capturedTicksAbs = ticks,
                    dateLabel = GenDate.DateFullStringAt(ticks, ll),
                    dateSlug = file.Split('_')[1],
                    year = slug[0],
                    spanCells = 19f,
                    aspect = PhotoCapture.ClampAspect(aspect),
                    mapId = map.uniqueID,
                    center = map.Center.ToVector3Shifted()
                };
                rec.SetPeople(pawns);
                rec.file = $"{index++:0000}_{rec.dateSlug}_{rec.kind}_{rec.id.Substring(0, 8)}.png";
                File.WriteAllBytes(Path.Combine(store.FolderPath, rec.file), png);
                if (thumb != null) File.WriteAllBytes(store.ThumbPath(rec), thumb);
                records.Add(rec);
                added++;
            }
            store.NotifyChanged();
            checks.Add("INFO: steam — " + added + " more of the author's photos imported");
        }

        private static PhotoRecord SteamRec(string key) => PhotoStore.Instance.Records.FirstOrDefault(r => r.eventKey == "shots:" + key);

        /// <summary>The scenes' own photos stay; only stray automatic ones (a "first day", a gathering) are dropped.</summary>
        private static void SteamTidy()
        {
            var store = PhotoStore.Instance;
            foreach (var r in store.Records.Where(r => (r.eventKey == null || !r.eventKey.StartsWith("shots:")) && !steamPhotos.Contains(r.id)).ToList()) store.Remove(r);
            Messages.Clear();
        }

        private static void SteamBegin(string seq, Vector3 at, float span, int pixelsPerCell, bool realLight)
        {
            steamSeq = seq;
            steamAt = at;
            steamSpan = span;
            steamLight = realLight;
            steamFrame = 0;
            steamPending = false;
            steamTimes = new StringBuilder("frame\tticks\treal\n");
            PhotoCapture.ProbePixelsPerCell = pixelsPerCell;
            Directory.CreateDirectory(Path.Combine(dir, "steam", seq));
            steamLayout.Append(seq).Append('\t').Append(at.x.ToString("0.00", CultureInfo.InvariantCulture)).Append('\t')
                .Append(at.z.ToString("0.00", CultureInfo.InvariantCulture)).Append('\t').Append(span.ToString("0.00", CultureInfo.InvariantCulture)).Append('\n');
        }

        /// <summary>Waits for the frame in flight; then starts the next one and returns true, or false while it is still rendering.</summary>
        private static bool SteamFrameReady()
        {
            if (steamPending && steamScreenPath != null)
            {
                // The real screen, with the game's own interface: the cover's "what you are looking at becomes a photo".
                if (!File.Exists(steamScreenPath) || new FileInfo(steamScreenPath).Length == 0) return false;
                steamScreenPath = null;
                steamPending = false;
                return true;
            }
            if (steamPending && !ShowCaptured()) return false;
            steamPending = false;
            return true;
        }

        private static bool SteamScreenMode => GenCommandLine.TryGetCommandLineArg("momentFramesSteamScreen", out _);

        /// <summary>A filmed frame takes far longer than a tenth of a second, so the card and the flash run on the film's clock instead:
        /// each frame after the photo is saved is a tenth of a second later in their animation. The window and the flash are the mod's own.</summary>
        private static int steamFxFrame = -1;
        private static void SteamFxClock()
        {
            var pop = Find.WindowStack.WindowOfType<Window_PhotoPop>();
            if (pop == null) { steamFxFrame = -1; return; }
            steamFxFrame++;
            float shown = Time.realtimeSinceStartup - steamFxFrame * 0.1f;
            HarmonyLib.Traverse.Create(pop).Field("opened").SetValue(shown);
            HarmonyLib.Traverse.Create(typeof(PhotoFx)).Field("flashStart").SetValue(shown);
        }

        /// <summary>The emotes that follow a photo run on real time too (2 s): each one is shown a tenth of a second older per filmed frame,
        /// so the film keeps them as long as the game does (2026-11-07: they stayed five seconds in the first colony film).</summary>
        private static readonly Dictionary<object, int> steamReactionFrames = new Dictionary<object, int>();
        private static void SteamReactionClock()
        {
            var live = HarmonyLib.Traverse.Create(typeof(PhotoReactions)).Field("live").GetValue() as System.Collections.IList;
            if (live == null) return;
            float now = Time.realtimeSinceStartup;
            foreach (object item in live)
            {
                steamReactionFrames.TryGetValue(item, out int n);
                steamReactionFrames[item] = n + 1;
                HarmonyLib.Traverse.Create(item).Field("Start").SetValue(now - n * 0.1f);
            }
        }

        /// <summary>DC post (2026-11-06): the author's own colony album, read only from a photo folder and its index.tsv, with the mod's own
        /// titles, dates and sentences. The test world's photos are cleared first. Each spread of the photo tab is one shot, newest first.</summary>
        private static string steamDcFolder;
        private static bool SteamDcAlbum()
        {
            var s = MomentFramesMod.Settings;
            var store = PhotoStore.Instance;
            var album = AlbumDef.TabWindow as MainTabWindow_Album;
            switch (steamStep)
            {
                case 0:
                    {
                        if (!GenCommandLine.TryGetCommandLineArg("momentFramesDcAlbum", out steamDcFolder) || !Directory.Exists(steamDcFolder))
                            throw new InvalidOperationException("dcalbum needs -momentFramesDcAlbum=<a colony photo folder>");
                        Find.MainTabsRoot.EscapeCurrentTab(false);
                        var records = (List<PhotoRecord>)HarmonyLib.AccessTools.Field(typeof(PhotoStore), "records").GetValue(store);
                        records.Clear();
                        int now = Find.TickManager.TicksAbs, n = 0;
                        foreach (string line in File.ReadAllLines(Path.Combine(steamDcFolder, "index.tsv"), Encoding.UTF8))
                        {
                            var f = line.Split('\t');
                            if (line.StartsWith("#") || f.Length < 8 || f[1] == "survey") continue;
                            string src = Path.Combine(steamDcFolder, f[0]);
                            if (!File.Exists(src)) continue;
                            n++;
                            var rec = new PhotoRecord
                            {
                                id = Guid.NewGuid().ToString("N"), kind = f[1], eventKey = "dc:" + f[0], title = f[4], desc = f[5],
                                dateSlug = f[2], dateLabel = f[3], year = int.Parse(f[6], CultureInfo.InvariantCulture),
                                ticksAbs = now - (200 - n) * GenDate.TicksPerHour, aspect = PhotoCapture.ClampAspect(float.Parse(f[7], CultureInfo.InvariantCulture)),
                                spanCells = 19f, mapId = -1, file = f[0]
                            };
                            rec.capturedTicksAbs = rec.ticksAbs;
                            File.Copy(src, Path.Combine(store.FolderPath, f[0]), true);
                            string thumb = Path.Combine(steamDcFolder, Path.GetFileNameWithoutExtension(f[0]) + ".thumb.png");
                            if (File.Exists(thumb)) File.Copy(thumb, store.ThumbPath(rec), true);
                            records.Add(rec);
                        }
                        store.NotifyChanged();
                        checks.Add("INFO: dcalbum — " + n + " photos of " + Path.GetFileName(steamDcFolder));
                        Find.Selector.ClearSelection();
                        SteamCleanScreen();
                        Messages.Clear();
                        Find.CameraDriver.SetRootPosAndSize(new Vector3(center.x + 0.5f, 0f, center.z + 4f), 16f);
                        Directory.CreateDirectory(Path.Combine(dir, "steam", "dcalbum"));
                        s.albumFont = "original";
                        s.albumOpacity = 1f;
                        AlbumOpen(false, false);
                        var t = HarmonyLib.Traverse.Create(album);
                        t.Field("includeBirthdays").SetValue(true);   // the birthdays are half of this colony's story
                        t.Field("cacheValid").SetValue(false);
                        t.Field("page").SetValue(0);
                        steamFrame = 0;
                        Wait(2f);
                        steamStep = 1;
                        return false;
                    }
                case 1:
                    Shot(Path.Combine("steam", "dcalbum", "page-" + steamFrame.ToString("00") + ".png"));
                    Wait(1f); steamStep = 2; return false;
                case 2:
                    HarmonyLib.Traverse.Create(album).Field("page").SetValue(++steamFrame);
                    Wait(1.5f); steamStep = 3; return false;
                case 3:
                    if (HarmonyLib.Traverse.Create(album).Field("page").GetValue<int>() == steamFrame && steamFrame < 40) { steamStep = 1; return false; }
                    Find.MainTabsRoot.EscapeCurrentTab(false);
                    return true;
            }
            return true;
        }
        private static string steamScreenPath;
        private static bool steamScreenPatched;
        private static bool SteamNoAlerts() => false;

        private static void SteamFrame()
        {
            string frameFile = Path.Combine("steam", steamSeq, steamFrame.ToString("000") + ".png");
            if (SteamScreenMode)
            {
                CloseLog();
                Messages.Clear();
                Prefs.DevMode = false; Find.PlaySettings.showLearningHelper = false;
                foreach (var letter in Find.LetterStack.LettersListForReading.ToList()) Find.LetterStack.RemoveLetter(letter);
                if (!steamScreenPatched)
                {
                    // The test world's "need beds" list is about the test world, not about anything on the cover.
                    steamScreenPatched = true;
                    new HarmonyLib.Harmony("momentframes.probe.steamscreen").Patch(HarmonyLib.AccessTools.Method(typeof(RimWorld.AlertsReadout), nameof(RimWorld.AlertsReadout.AlertsReadoutOnGUI)),
                        prefix: new HarmonyLib.HarmonyMethod(typeof(Probe), nameof(SteamNoAlerts)));
                }
                Find.CameraDriver.SetRootPosAndSize(steamAt, 7.5f);
                SteamFxClock();
                steamScreenPath = Path.Combine(dir, frameFile);
                Shot(frameFile);
            }
            else ShowCapture(frameFile, steamAt, steamSpan, 16f / 9f, steamLight);
            steamTimes.Append(steamFrame).Append('\t').Append(Find.TickManager.TicksGame).Append('\t')
                .Append(Time.realtimeSinceStartup.ToString("0.000", CultureInfo.InvariantCulture)).Append('\n');
            steamFrame++;
            steamPending = true;
        }

        private static void SteamEnd()
        {
            File.WriteAllText(Path.Combine(dir, "steam", steamSeq, "times.txt"), steamTimes.ToString());
            checks.Add("INFO: steam sequence " + steamSeq + " — " + steamFrame + " frames");
        }

        private static void SteamTicks(int n, bool lamps = false)
        {
            for (int i = 0; i < n; i++)
            {
                if (lamps) SteamPower();
                Find.TickManager.DoSingleTick();
            }
            SettleDraw();
            // Ticked while paused, the game's per-frame visuals never run, and turning to face the way a pawn walks is one of them:
            // they walked sideways in the colony film (2026-11-07, trace.tsv: 9 of 14 steps facing elsewhere). Turn them as play would.
            foreach (var p in map.mapPawns.AllPawnsSpawned) steamUpdateRotation?.Invoke(p.rotationTracker, null);
        }
        private static readonly System.Reflection.MethodInfo steamUpdateRotation = HarmonyLib.AccessTools.Method(typeof(Pawn_RotationTracker), "UpdateRotation");

        /// <summary>The fixture supplies the studio lights' power directly; it does not build a power network.</summary>
        private static void SteamPower()
        {
            if (steamLamps == null) return;
            foreach (var lamp in steamLamps.Where(l => l.Spawned)) lamp.GetComp<CompPowerTrader>().PowerOn = lamp.GetComp<CompFlickable>()?.SwitchIsOn ?? true;
        }

        private static void SteamCopyPhoto(string id, string name)
        {
            var rec = PhotoStore.Instance.Get(id);
            if (rec?.HasFile != true) throw new InvalidOperationException("no saved photo for " + name);
            File.Copy(PhotoStore.Instance.FilePath(rec), Path.Combine(dir, "steam", name), true);
            File.WriteAllText(Path.Combine(dir, "steam", Path.ChangeExtension(name, ".txt")), rec.title + "\n" + rec.desc + "\n" + rec.dateLabel);
            if (!steamPhotos.Contains(rec.id)) steamPhotos.Add(rec.id);
            checks.Add("PASS: steam photo " + name + " — " + rec.title);
        }

        private static void SteamCleanup()
        {
            showCaptureStart = null;
            steamPending = false;
            PhotoCapture.ProbePixelsPerCell = 0;
            try { steamJob?.Cancel(); } catch { }
            steamJob = null;
            foreach (var l in map.lordManager.lords.ToList()) map.lordManager.RemoveLord(l);
            foreach (var w in Find.WindowStack.Windows.Where(w => w is Dialog_PhotoEdit || w is Dialog_ModSettings || w is Window_PhotoPop).ToList()) Find.WindowStack.TryRemove(w, false);
            Find.MainTabsRoot.EscapeCurrentTab(false);
        }

        private static void SteamReady(IEnumerable<Pawn> people)
        {
            foreach (var p in people)
            {
                if (p.needs?.rest != null) p.needs.rest.CurLevel = 1f;
                if (p.needs?.food != null) p.needs.food.CurLevel = 1f;
                if (p.needs?.joy != null) p.needs.joy.CurLevel = 0.8f;
                if (!p.Awake()) RestUtility.WakeUp(p);
                if (p.Drafted) p.drafter.Drafted = false;
                if (p.InMentalState) p.MentalState.RecoverFromState();
                p.jobs?.EndCurrentJob(Verse.AI.JobCondition.InterruptForced);
            }
        }

        /// <summary>One step per call; true when every scene is done.</summary>
        private static bool SteamStep()
        {
            if (steamScene >= SteamScenes.Length)
            {
                File.WriteAllText(Path.Combine(dir, "steam", "layout.tsv"), "seq\tx\tz\tspan\n" + steamLayout);
                return true;
            }
            string scene = SteamScenes[steamScene];
            if (steamOnly == null) steamOnly = GenCommandLine.TryGetCommandLineArg("momentFramesSteamOnly", out var only) ? "," + only + "," : "";
            if (steamOnly.Length > 0 && !steamOnly.Contains("," + scene + ",")) { steamScene++; return false; }
            if (steamOnly.Length == 0 && (scene == "lang" || scene == "reserve" || scene == "time" || scene == "dcalbum")) { steamScene++; return false; }   // a translation check, not page media
            bool done;
            try
            {
                switch (scene)
                {
                    case "cast": done = SteamCastSheet(); break;
                    case "symbol": done = SteamSymbol(); break;
                    case "life": done = SteamLife(); break;
                    case "studio": done = SteamStudio(); break;
                    case "group": done = SteamGroup(); break;
                    case "night": done = SteamNight(); break;
                    case "wall": done = SteamWall(); break;
                    case "album": done = SteamAlbum(); break;
                    case "editor": done = SteamEditor(); break;
                    case "ui": done = SteamUi(); break;
                    case "lang": done = SteamLang(); break;
                    case "auto": done = SteamAuto(); break;
                    case "reserve": done = SteamReserve(); break;
                    case "time": done = SteamTime(); break;
                    case "dcalbum": done = SteamDcAlbum(); break;
                    default: done = true; break;
                }
            }
            catch (Exception e)
            {
                checks.Add("FAIL: steam scene " + scene + " at step " + steamStep + ": " + e.Message);
                File.AppendAllText(Path.Combine(dir, "steam-errors.txt"), scene + " step " + steamStep + "\n" + e + "\n\n");
                try { SteamCleanup(); } catch { }
                done = true;
            }
            if (done)
            {
                steamScene++;
                steamStep = 0;
                steamPending = false;
                PhotoCapture.ProbePixelsPerCell = 0;
                Wait(1f);
            }
            return false;
        }

        // ---------- the studio: a dim room, the family comes in, the lights come on, they sit, the shutter ----------

        private static bool SteamStudio()
        {
            switch (steamStep)
            {
                case 0:
                    {
                        deadline = Time.realtimeSinceStartup + 1800f;
                        Directory.CreateDirectory(Path.Combine(dir, "steam"));
                        SteamTidy();
                        foreach (var animal in map.mapPawns.AllPawnsSpawned.Where(a => a.RaceProps.Animal).ToList()) animal.Destroy();   // r8: a large pet crossed the memory night's screen
                        var s = MomentFramesMod.Settings;
                        s.autoCaptureEnabled = false;
                        s.fxFlash = s.fxShutter = s.fxPopup = s.fxFreeze = s.fxCamera = false;
                        var b = new IntVec3(center.x - 12, 0, center.z - 3);   // clear of the wall (north) and the group's spot (east)
                        var room = CellRect.FromLimits(b.x - 8, b.z - 6, b.x + 8, b.z + 3);   // the lights stand six cells out on either side
                        foreach (var c in room.ExpandedBy(3))
                        {
                            foreach (var t in c.GetThingList(map).ToList()) if (!(t is Pawn) && t.def.destroyable) t.Destroy(DestroyMode.Vanish);
                            map.roofGrid.SetRoof(c, null);
                        }
                        var planks = DefDatabase<TerrainDef>.GetNamed("WoodPlankFloor");
                        foreach (var c in room) map.terrainGrid.SetTerrain(c, planks);
                        foreach (var c in room.EdgeCells)
                        {
                            bool door = c.x == b.x && c.z == room.minZ;
                            var wall = ThingMaker.MakeThing(door ? ThingDefOf.Door : ThingDefOf.Wall, ThingDefOf.WoodLog);
                            wall.SetFaction(Faction.OfPlayer);
                            GenSpawn.Spawn(wall, c, map);
                        }
                        steamBackdrop = (Building_PhotoStudio)ThingMaker.MakeThing(ThingDef.Named("MF_StudioBackdrop"));
                        steamBackdrop.SetFaction(Faction.OfPlayer);
                        GenSpawn.Spawn(steamBackdrop, b, map);
                        PreparationRefreshRegions();
                        foreach (var c in room) map.roofGrid.SetRoof(c, RoofDefOf.RoofConstructed);
                        PreparationRefreshRegions();
                        steamBackdrop.SetBackdrop(1);   // the garden scenery
                        if (!steamBackdrop.PlaceSet(0)) throw new InvalidOperationException("studio set did not place");
                        foreach (var bp in map.listerThings.AllThings.OfType<Blueprint_Build>().Where(x => x.def.entityDefToBuild?.defName.StartsWith("MF_Studio") == true).ToList())
                        {
                            var def = (ThingDef)bp.def.entityDefToBuild;
                            var c = bp.Position;
                            var rot = bp.Rotation;
                            bp.Destroy();
                            var built = ThingMaker.MakeThing(def);
                            built.SetFaction(Faction.OfPlayer);
                            GenSpawn.Spawn(built, c, map, rot);
                        }
                        steamLamps = steamBackdrop.Lamps().ToList();
                        if (steamLamps.Count != 2) throw new InvalidOperationException("studio has " + steamLamps.Count + " lights");
                        // A sitting: two adults and a child, waiting outside the door.
                        var mother = ShotPawn("Celeste") ?? map.mapPawns.FreeColonistsSpawned.First(p => p.gender == Gender.Female);
                        var father = ShotPawn("Victoria") ?? ShotPawn("Julian") ?? map.mapPawns.FreeColonistsSpawned.First(p => p.gender == Gender.Male);
                        var child = ShotPawn("Lily") ?? map.mapPawns.FreeColonistsSpawned.FirstOrDefault(p => p.DevelopmentalStage.Child());
                        if (child == null && ModsConfig.BiotechActive)
                        {
                            child = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDefOf.Colonist, Faction.OfPlayer, forceGenerateNewPawn: true, canGeneratePawnRelations: false,
                                fixedBiologicalAge: 7, fixedChronologicalAge: 7, fixedGender: Gender.Female, developmentalStages: DevelopmentalStage.Child));
                            child.Name = new NameTriple(Ko("Lily"), Ko("Lily"), Ko("Rowe"));
                            GenSpawn.Spawn(child, b + new IntVec3(0, 0, -9), map);
                            child.relations.AddDirectRelation(PawnRelationDefOf.Parent, mother);
                            if (father != mother && father.gender != mother.gender) child.relations.AddDirectRelation(PawnRelationDefOf.Parent, father);
                        }
                        steamPeople = new List<Pawn> { father, mother };
                        if (child != null) steamPeople.Insert(1, child);
                        foreach (var p in map.mapPawns.FreeColonistsSpawned.Where(p => !steamPeople.Contains(p)).ToList())
                            if (p.Position.InHorDistOf(b, 14f)) Teleport(p, CellFinder.RandomClosewalkCellNear(new IntVec3(center.x + 10, 0, center.z - 9), map, 4));
                        int k = 0;
                        foreach (var p in steamPeople) Teleport(p, b + new IntVec3(-1 + k++, 0, -9));
                        SteamReady(steamPeople);
                        // the backdrop with a little room above it, the floor in front where they walk in
                        var art = steamBackdrop.ArtworkBounds;
                        float span = Mathf.Max(6.6f, art.Height + 3.6f);
                        var frameCenter = new Vector3((art.minX + art.maxX + 1) / 2f, 0f, art.maxZ + 1.4f - span / 2f);
                        Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                        SteamTicks(3, true);
                        SteamBegin("studio", frameCenter, span, Mathf.RoundToInt(720f / span), true);
                        steamHold = 0;
                        steamStep = 1;
                        return false;
                    }
                case 1:
                    // a few frames of the empty room, lights off
                    if (!SteamFrameReady()) return false;
                    if (steamFrame < 4) { SteamTicks(6, true); SteamFrame(); return false; }
                    steamJob = (LordJob_MFGroupPhoto)GroupPhoto.StartPrepared(map, steamBackdrop.Position, steamPeople, Rot4.North, faces: SteamFaces(steamPeople))?.LordJob;
                    if (steamJob == null) throw new InvalidOperationException("studio gathering did not start");
                    steamTakes = 0;
                    steamStep = 2;
                    return false;
                case 2:
                    {
                        if (!SteamFrameReady()) return false;
                        if (steamFrame > 400) throw new TimeoutException("studio: no photo after " + steamFrame + " frames (" + steamJob.Result + ", in place " + steamJob.InPlace + ")");
                        if (steamJob.Takes > steamTakes)
                        {
                            steamTakes = steamJob.Takes;
                            if (++steamHold > 14) { SteamEnd(); steamStep = 3; return false; }
                            SteamTicks(4, true);
                            SteamFrame();
                            return false;
                        }
                        if (steamTakes > 0 && ++steamHold > 14) { SteamEnd(); steamStep = 3; return false; }
                        // slow while they are in view (r2: the walk to the sofa passed in about five frames), quick while they are still outside
                        var view = PhotoCapture.CoveredRect(steamAt, steamSpan, map, 16f / 9f);
                        bool seen = steamPeople.Any(p => view.Contains(p.Position));
                        SteamTicks(steamTakes > 0 ? 4 : seen ? 3 : 14, true);
                        SteamFrame();
                        return false;
                    }
                case 3:
                    if (PhotoCapture.Busy) return false;
                    SteamCopyPhoto(steamJob.PhotoId, "studio-garden.png");
                    // Two retakes in the same sitting: the plain backdrop, then the birthday one. At the setting's own resolution from here on.
                    PhotoCapture.ProbePixelsPerCell = 0;
                    steamBackdrop.SetBackdrop(0);
                    steamTakes = steamJob.Takes;
                    steamJob.ShootNow();
                    steamStep = 4;
                    return false;
                case 4:
                    SteamPower();
                    if (steamJob.Takes <= steamTakes) { if (!steamJob.Shooting) Find.TickManager.DoSingleTick(); return false; }
                    if (PhotoCapture.Busy) return false;
                    SteamCopyPhoto(steamJob.PhotoId, "studio-plain.png");
                    steamBackdrop.SetBackdrop(2);
                    steamTakes = steamJob.Takes;
                    steamJob.ShootNow();
                    steamStep = 5;
                    return false;
                case 5:
                    SteamPower();
                    if (steamJob.Takes <= steamTakes) { if (!steamJob.Shooting) Find.TickManager.DoSingleTick(); return false; }
                    if (PhotoCapture.Busy) return false;
                    SteamCopyPhoto(steamJob.PhotoId, "studio-birthday.png");
                    steamBackdrop.SetBackdrop(1);
                    steamJob.Cancel();
                    SteamTicks(15, true);
                    {
                        // Everyone at once, in front of the garden: the colony's own portrait, for the "symbol" scene.
                        var all = map.mapPawns.FreeColonistsSpawned.Where(p => !p.DevelopmentalStage.Baby()).ToList();
                        SteamReady(all);
                        // Framed close: from far off, the floor around the backdrop read as a big wooden hall and not as a photograph (user: couldn't tell what it was).
                        steamBackdrop.SetFrame(steamBackdrop.DrawPos + new Vector3(0f, 0f, -.15f), 5.6f, 1.5f);
                        steamJob = (LordJob_MFGroupPhoto)GroupPhoto.StartPrepared(map, steamBackdrop.Position, all, Rot4.North, faces: SteamFaces(all), positions: SteamPlaces(steamBackdrop.Position, all))?.LordJob;
                        if (steamJob == null) throw new InvalidOperationException("the whole colony's portrait did not start");
                        steamTakes = 0;
                        steamHold = 0;
                    }
                    steamStep = 6;
                    return false;
                case 6:
                    SteamPower();
                    if (steamJob.Takes == 0)
                    {
                        if (++steamHold > 1500) throw new TimeoutException("whole colony portrait: " + steamJob.Result + ", in place " + steamJob.InPlace);
                        if (!steamJob.Shooting) SteamTicks(10, true);
                        return false;
                    }
                    if (PhotoCapture.Busy) return false;
                    SteamCopyPhoto(steamJob.PhotoId, "colony-portrait.png");
                    steamSymbol = steamJob.PhotoId;
                    steamStep = 7;
                    return false;
                default:
                    steamJob.Cancel();
                    steamJob = null;
                    SteamTicks(15);
                    return true;
            }
        }

        // ---------- a group at the garden arch: they come from all sides, line up, and the emotes they picked show in the photo ----------

        private static bool SteamGroup()
        {
            switch (steamStep)
            {
                case 0:
                    {
                        SteamTidy();
                        foreach (var l in map.lordManager.lords.ToList()) map.lordManager.RemoveLord(l);
                        JumpToHour(11f);   // daylight
                        Prefs.PlantWindSway = false;   // r3: swaying grass changed every pixel of every frame (a 5 MB GIF)
                        var spot = new IntVec3(center.x + 8, 0, center.z + 1);
                        // A flagstone terrace under the arch in a full lawn, a few flowers by the arch's feet and a tree on either side
                        // (r1: bare mud and a marsh edge; r2: patchy grass drawn over the arch and flower rows like a crop field).
                        var lawn = CellRect.CenteredOn(spot, 10);
                        var terrace = CellRect.FromLimits(spot.x - 3, spot.z - 4, spot.x + 3, spot.z + 2);
                        var soil = DefDatabase<TerrainDef>.GetNamed("SoilRich");
                        var stone = DefDatabase<TerrainDef>.GetNamed("FlagstoneSandstone");
                        foreach (var c in lawn)
                        {
                            if (!c.InBounds(map)) continue;
                            foreach (var t in c.GetThingList(map).ToList()) if (!(t is Pawn) && t.def.destroyable) t.Destroy(DestroyMode.Vanish);
                            map.terrainGrid.SetTerrain(c, terrace.Contains(c) ? stone : soil);
                            map.roofGrid.SetRoof(c, null);
                        }
                        var arch = (Building_PhotoStudio)ThingMaker.MakeThing(ThingDef.Named("MF_StudioArch"));
                        arch.SetFaction(Faction.OfPlayer);
                        GenSpawn.Spawn(arch, spot, map);
                        void Plant(string def, IntVec3 c)
                        {
                            if (!c.InBounds(map) || terrace.Contains(c) || c.GetEdifice(map) != null || c.GetPlant(map) != null) return;
                            var plant = (Plant)ThingMaker.MakeThing(ThingDef.Named(def));
                            plant.Growth = 1f;
                            GenSpawn.Spawn(plant, c, map);
                        }
                        foreach (int side in new[] { -1, 1 })
                        {
                            Plant("Plant_TreeOak", spot + new IntVec3(side * 7, 0, 2));
                            Plant("Plant_Rose", spot + new IntVec3(side * 4, 0, 0));
                            Plant("Plant_Rose", spot + new IntVec3(side * 4, 0, -1));
                            Plant("Plant_Daylily", spot + new IntVec3(side * 4, 0, 1));
                            Plant("Plant_Daylily", spot + new IntVec3(side * 5, 0, -1));
                        }
                        foreach (var c in lawn) Plant("Plant_Grass", c);
                        var people = map.mapPawns.FreeColonistsSpawned.Where(p => !p.DevelopmentalStage.Baby()).Take(10).ToList();
                        // nine, so the two rows leave one front place with nobody behind it for the tall one (see SteamPlaces)
                        var spare = ShotPawn("Mushinto");
                        if (spare != null && ShotPawn("Evie") != null && people.Remove(spare)) Teleport(spare, CellFinder.RandomClosewalkCellNear(new IntVec3(center.x + 8, 0, center.z - 14), map, 3));
                        SteamReady(people);
                        int k = 0;
                        foreach (var p in people)
                        {
                            float a = k++ * 0.7f + 0.3f;
                            var c = new IntVec3(spot.x + Mathf.RoundToInt(Mathf.Cos(a) * 9f), 0, spot.z - 2 + Mathf.RoundToInt(Mathf.Sin(a) * 5f));
                            Teleport(p, CellFinder.RandomClosewalkCellNear(c, map, 2));
                        }
                        int[] icons = { ReactionChoices.Heart, ReactionChoices.HandWave, ReactionChoices.Peace, (int)PhotoIcon.Star, ReactionChoices.Clap,
                            (int)PhotoIcon.Music, ReactionChoices.Heart, (int)PhotoIcon.Smile, ReactionChoices.HandWave };
                        // Only with nobody behind: a bubble floats above its owner, so the front row's would cover the faces behind them (r6).
                        var places = SteamPlaces(spot, people);
                        var layout = PhotoGatheringPlan.Create(map, spot, people, Rot4.North, null, null, places);
                        var reactions = new Dictionary<string, int>();
                        for (int i = 0; i < layout.Moving.Count; i++)
                            reactions[layout.Moving[i].GetUniqueLoadID()] = SteamNobodyBehind(layout.Cells, layout.Cells[i]) ? icons[i % icons.Length] : ReactionChoices.None;
                        steamJob = (LordJob_MFGroupPhoto)GroupPhoto.StartPrepared(map, spot, people, Rot4.North, reactionMode: PhotoReactions.ModePhoto, reactions: reactions, faces: SteamFaces(people), positions: places)?.LordJob;
                        if (steamJob == null) throw new InvalidOperationException("group photo did not start");
                        Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                        SteamTicks(2);
                        SteamBegin("group", arch.DrawPos + new Vector3(0f, 0f, -0.4f), 7.2f, 100, false);   // r2: the art bounds put the arch off to the left; r3: its top was cut
                        steamTakes = 0;
                        steamHold = 0;
                        steamStep = 1;
                        return false;
                    }
                case 1:
                    {
                        if (!SteamFrameReady()) return false;
                        if (steamFrame > 400) throw new TimeoutException("group: no photo after " + steamFrame + " frames (" + steamJob.Result + ", in place " + steamJob.InPlace + ")");
                        if (steamJob.Takes > steamTakes) steamTakes = steamJob.Takes;
                        if (steamTakes > 0 && ++steamHold > 16) { SteamEnd(); steamStep = 2; return false; }
                        SteamTicks(steamTakes > 0 ? 3 : 8);
                        SteamFrame();
                        return false;
                    }
                case 2:
                    if (PhotoCapture.Busy) return false;
                    SteamCopyPhoto(steamJob.PhotoId, "group-emotes.png");
                    steamJob.Cancel();
                    steamJob = null;
                    SteamTicks(15);
                    return true;
            }
            return true;
        }

        // ---------- memory night: the colony watching the film, the slides changing ----------

        private static bool SteamNight()
        {
            switch (steamStep)
            {
                case 0:
                    {
                        SteamTidy();
                        foreach (var l in map.lordManager.lords.ToList()) map.lordManager.RemoveLord(l);
                        var at = new IntVec3(center.x + 6, 0, center.z - 10);
                        foreach (var c in CellRect.CenteredOn(at, 9))
                        {
                            foreach (var t in c.GetThingList(map).ToList()) if (!(t is Pawn) && t.def.destroyable) t.Destroy(DestroyMode.Vanish);
                            map.roofGrid.SetRoof(c, null);
                        }
                        steamProjector = (Building_PhotoProjector)ThingMaker.MakeThing(ThingDef.Named("MF_Projector"), ThingDefOf.Steel);
                        steamProjector.SetFaction(Faction.OfPlayer);
                        GenSpawn.Spawn(steamProjector, at, map, Rot4.North);
                        PrepareNightScene(steamProjector);
                        JumpToHour(21.5f);
                        SteamReady(map.mapPawns.FreeColonistsSpawned.ToList());
                        steamProjector.SetCustomProjectionSize(5.6f, 3.15f);   // a big screen, the film is what they came for
                        {
                            // floor out to the frame's edges (r11: the map's sand on both sides of the veranda)
                            var screen = MemoryNight.ScreenCenter(steamProjector).ToIntVec3();
                            foreach (var c in CellRect.FromLimits(screen.x - 10, screen.z - 9, screen.x + 10, screen.z + 5).ClipInsideMap(map))
                            {
                                foreach (var thing in c.GetThingList(map).ToList())
                                    if (!(thing is Pawn) && thing.def.destroyable && thing.Faction != Faction.OfPlayer) thing.Destroy(DestroyMode.Vanish);
                                map.terrainGrid.SetTerrain(c, DefDatabase<TerrainDef>.GetNamed("WoodPlankFloor"));
                            }
                        }
                        var film = new[] { SteamRec("x0_0029_")?.id, steamPhotos.ElementAtOrDefault(0), SteamRec("x0_0034_")?.id, SteamRec("x0_0035_")?.id, steamSymbol,
                            SteamRec("x0_0019_")?.id, SteamRec("x0_0036_")?.id, SteamRec("x0_0028_")?.id }.Where(x => x != null).ToList();
                        if (film.Count >= 3) steamProjector.SetSelectedFilm(film);
                        else steamProjector.SetFilm("", "", "");
                        string why = MemoryNight.CannotStart(steamProjector);
                        if (why != null) throw new InvalidOperationException("memory night cannot start: " + why);
                        if (!MemoryNight.Start(steamProjector)) throw new InvalidOperationException("memory night did not start");
                        Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                        steamHold = 0;
                        steamStep = 1;
                        return false;
                    }
                case 1:
                    {
                        // until everyone is seated and the film runs (r1: 1800 ticks left half of them still walking in)
                        var lord = map.lordManager.lords.LastOrDefault(l => l.LordJob is LordJob_MFMemoryNight);
                        var night = lord?.LordJob as LordJob_MFMemoryNight;
                        if (night == null) throw new InvalidOperationException("memory night ended before the film");
                        if (!night.Watching)
                        {
                            if (++steamHold > 60) throw new TimeoutException("memory night never started watching");
                            SteamTicks(150);
                            return false;
                        }
                        SteamTicks(240);
                        foreach (var p in map.mapPawns.FreeColonistsSpawned.Where(p => !lord.ownedPawns.Contains(p)).ToList())
                            if (p.Position.InHorDistOf(steamProjector.Position, 14f)) Teleport(p, CellFinder.RandomClosewalkCellNear(new IntVec3(center.x - 4, 0, center.z + 14), map, 4));
                        SteamBegin("night", MemoryNight.ScreenCenter(steamProjector) + new Vector3(0f, 0f, -2.6f), 9.4f, 84, true);   // r2: the screen's top edge was cut; r13: the second row's feet
                        checks.Add("INFO: steam night — " + lord.ownedPawns.Count + " watching");
                        steamStep = 2;
                        return false;
                    }
                case 2:
                    if (!SteamFrameReady()) return false;
                    if (steamFrame >= 56)
                    {
                        SteamEnd();
                        steamStep = 3;
                        return false;
                    }
                    SteamTicks(MemoryNight.SlideTicks / 8);   // eight frames to a slide
                    File.AppendAllText(Path.Combine(dir, "steam", "night", "slides.txt"), steamFrame + "\t" + (steamProjector.Current?.eventKey ?? steamProjector.Current?.id) + "\n");   // which photo is on the screen, for the still
                    SteamFrame();
                    return false;
                case 3:
                    {
                        if (PhotoCapture.Busy) return false;
                        var night = PhotoStore.Instance.Records.LastOrDefault(r => r.kind == "memorynight" && r.HasFile);
                        if (night != null) SteamCopyPhoto(night.id, "night-photo.png");
                        else checks.Add("INFO: steam night — the evening's own photo is not saved yet");
                        foreach (var l in map.lordManager.lords.ToList()) map.lordManager.RemoveLord(l);
                        JumpToHour(12f);
                        SteamTicks(30);
                        Prefs.PlantWindSway = true;
                        return true;
                    }
            }
            return true;
        }

        // ---------- stills: the wall of frames, the five looks of one frame, the memory tree ----------

        private static bool SteamWall()
        {
            int wz = center.z + 9;
            switch (steamStep)
            {
                case 0:
                    {
                        SteamTidy();
                        foreach (var p in map.mapPawns.FreeColonistsSpawned.ToList())
                            if (p.Position.z > center.z + 2 && Mathf.Abs(p.Position.x - center.x) < 12) Teleport(p, CellFinder.RandomClosewalkCellNear(new IntVec3(center.x, 0, center.z - 12), map, 4));
                        var planks = DefDatabase<TerrainDef>.GetNamed("WoodPlankFloor");
                        for (int x = center.x - 10; x <= center.x + 10; x++)
                        {
                            for (int z = wz - 6; z < wz; z++)
                            {
                                var c = new IntVec3(x, 0, z);
                                foreach (var t in c.GetThingList(map).ToList()) if (!(t is Pawn) && t.def.destroyable) t.Destroy(DestroyMode.Vanish);
                                map.terrainGrid.SetTerrain(c, planks);
                            }
                            var cell = new IntVec3(x, 0, wz);
                            foreach (var t in cell.GetThingList(map).ToList()) if (!(t is Pawn) && t.def.destroyable) t.Destroy(DestroyMode.Vanish);
                            var wall = ThingMaker.MakeThing(ThingDefOf.Wall, ThingDefOf.WoodLog);
                            wall.SetFaction(Faction.OfPlayer);
                            GenSpawn.Spawn(wall, cell, map);
                        }
                        string Pick(int i, string fallback) => i < steamPhotos.Count ? steamPhotos[i] : SteamRec(fallback)?.id ?? ShotRec("0003_").id;
                        var plan = new[]
                        {
                            ("MF_PhotoFrame", -6, FrameStyle.Material, SteamRec("x0_0029_")?.id ?? ShotRec("0006_").id, ThingDefOf.WoodLog),
                            ("MF_PhotoFrameWide", -4, FrameStyle.Gilded, Pick(0, "x1_0006_"), ThingDefOf.Steel),
                            ("MF_PhotoFrame", -1, FrameStyle.Polaroid, SteamRec("x0_0034_")?.id ?? ShotRec("0007_").id, ThingDefOf.WoodLog),
                            ("MF_PhotoFrame", 1, FrameStyle.BlackMat, Pick(2, "x1_0003_"), ThingDefOf.WoodLog),
                            ("MF_PhotoFrameWide", 3, FrameStyle.Canvas, SteamRec("x0_0019_")?.id ?? ShotRec("0003_").id, ThingDefOf.WoodLog),
                            ("MF_PhotoFrame", 6, FrameStyle.Material, Pick(3, "x0_0036_"), ThingDefOf.Steel),
                        };
                        var frames = new List<Building_PhotoFrame>();
                        foreach (var (d, dx, st, id, stuff) in plan)
                        {
                            var f = Spawn(d, stuff, new IntVec3(center.x + dx, 0, wz - 1), Rot4.North);
                            f.SetPhoto(id);
                            f.SetStyle(st);
                            f.RefreshBinding();
                            frames.Add(f);
                        }
                        steamStyleFrame = frames[3];
                        var easel = Spawn("MF_PhotoEasel", ThingDefOf.WoodLog, new IntVec3(center.x - 3, 0, wz - 3), Rot4.North);
                        easel.SetPhoto(Pick(1, "x0_0029_"));
                        easel.RefreshBinding();
                        var collage = (Building_PhotoCollage)ThingMaker.MakeThing(ThingDef.Named("MF_PhotoCollage"), ThingDefOf.WoodLog);
                        collage.SetFaction(Faction.OfPlayer);
                        GenSpawn.Spawn(collage, new IntVec3(center.x + 2, 0, wz - 3), map, Rot4.North);
                        string[] ids = { ShotRec("0002_").id, SteamRec("x0_0012_")?.id ?? ShotRec("0004_").id, ShotRec("0009_5500-4-02_relicfound").id, SteamRec("x0_0027_")?.id ?? ShotRec("0008_").id };
                        for (int i = 0; i < ids.Length; i++) collage.SetSlot(i, ids[i]);
                        collage.RefreshBinding();
                        JumpToHour(12f);
                        SteamTicks(5);
                        Find.Selector.ClearSelection();
                        Wait(1f);
                        steamStep = 1;
                        return false;
                    }
                case 1:
                    PhotoCapture.ProbePixelsPerCell = 150;
                    ShowCapture(Path.Combine("steam", "wall.png"), new Vector3(center.x + 0.5f, 0f, wz - 2f), 6.4f, 16f / 9f);
                    steamStep = 2;
                    return false;
                case 2:
                    if (!ShowCaptured()) return false;
                    // the whole row as a panorama, five cells tall (the camera's least span), the frames large (r4: a 16:9 shot left them small)
                    ShowCapture(Path.Combine("steam", "wall-row.png"), new Vector3(center.x + 0.5f, 0f, wz - 1.4f), 5f, 3f);
                    steamStep = 20;
                    return false;
                case 20:
                    if (!ShowCaptured()) return false;
                    Directory.CreateDirectory(Path.Combine(dir, "steam", "looks"));
                    steamFrame = 0;
                    steamStep = 3;
                    return false;
                case 3:
                    steamStyleFrame.SetStyle(SteamStyles[steamFrame]);
                    Wait(0.3f);
                    steamStep = 4;
                    return false;
                case 4:
                    PhotoCapture.ProbePixelsPerCell = 260;
                    ShowCapture(Path.Combine("steam", "looks", steamFrame.ToString("000") + ".png"), new Vector3(steamStyleFrame.Position.x + 0.5f, 0f, wz + 0.45f), 1.9f, 1.5f);
                    steamStep = 5;
                    return false;
                case 5:
                    if (!ShowCaptured()) return false;
                    if (++steamFrame < SteamStyles.Length) { steamStep = 3; return false; }
                    steamStyleFrame.SetStyle(FrameStyle.BlackMat);
                    {
                        // the memory tree beside the wall, filled from the album on its own
                        var c = new IntVec3(center.x - 15, 0, center.z + 4);
                        foreach (var cell in CellRect.CenteredOn(c, 4))
                            foreach (var t in cell.GetThingList(map).ToList()) if (!(t is Pawn) && t.def.destroyable) t.Destroy(DestroyMode.Vanish);
                        var tree = (Building_PhotoTree)ThingMaker.MakeThing(ThingDef.Named("MF_MemoryTree"), ThingDefOf.WoodLog);
                        tree.SetFaction(Faction.OfPlayer);
                        GenSpawn.Spawn(tree, c, map, Rot4.North);
                        tree.RefreshSelection();
                        SteamTicks(3);
                        steamAt = tree.DrawPos;
                    }
                    Wait(1f);
                    steamStep = 6;
                    return false;
                case 6:
                    PhotoCapture.ProbePixelsPerCell = 140;
                    ShowCapture(Path.Combine("steam", "memory-tree.png"), steamAt + new Vector3(0f, 0f, 0.3f), 5.6f, 16f / 9f);
                    steamStep = 7;
                    return false;
                case 7:
                    if (!ShowCaptured()) return false;
                    PhotoCapture.ProbePixelsPerCell = 0;
                    checks.Add("INFO: steam wall, five looks and memory tree rendered");
                    return true;
            }
            return true;
        }

        // ---------- the colony's symbol: its portrait at the projector's largest size, thrown across the land behind everything on it ----------

        private static bool SteamSymbol()
        {
            switch (steamStep)
            {
                case 0:
                    {
                        SteamTidy();
                        foreach (var l in map.lordManager.lords.ToList()) map.lordManager.RemoveLord(l);
                        JumpToHour(11f);
                        string id = steamSymbol ?? steamPhotos.FirstOrDefault() ?? ShotRec("0003_").id;
                        var at = new IntVec3(center.x, 0, center.z - 48);
                        float h = 40f / PhotoStore.Instance.Get(id).Aspect, cz = at.z + 1.7f + h / 2f;
                        // Far enough to see what it is (user: at 32 cells it read as a room): the picture beside houses, a farm and the earlier
                        // scenes' buildings, about a third of the frame's width.
                        steamAt = new Vector3(center.x + .5f, 0f, center.z - 29f);
                        const float frameSpan = 60f, frameAspect = 16f / 9f;
                        var frame = PhotoCapture.CoveredRect(steamAt, frameSpan, map, frameAspect);
                        // Grass and trees may stand over the picture's edges, as on real ground, but not over the people in it.
                        var people = CellRect.FromLimits(at.x - 14, Mathf.FloorToInt(cz - h / 2f) + 3, at.x + 15, Mathf.CeilToInt(cz + h / 2f) - 3);
                        int north = center.z - 19;   // north of this row stand the earlier scenes, which stay as the settlement's middle
                        var garden = CellRect.CenteredOn(new IntVec3(center.x + 8, 0, center.z + 1), 10);   // the arch's flower beds and trees stay
                        var soil = DefDatabase<TerrainDef>.GetNamed("Soil");
                        map.fogGrid.ClearAllFog();
                        foreach (var c in frame)
                        {
                            bool open = c.z < north;
                            foreach (var thing in c.GetThingList(map).ToList())
                                if (!(thing is Pawn) && thing.def.destroyable && (open || (thing is Plant ? !garden.Contains(c) : thing.Faction != Faction.OfPlayer))) thing.Destroy(DestroyMode.Vanish);
                            map.roofGrid.SetRoof(c, null);
                            // one ground across the whole shot (r11: the map's own sand and rock kept to the north half); built floors and the arch's lawn stay
                            var ground = c.GetTerrain(map);
                            if (open || (!ground.layerable && ground.defName != "SoilRich")) map.terrainGrid.SetTerrain(c, soil);
                        }
                        foreach (var a in map.mapPawns.AllPawnsSpawned.Where(a => !a.IsColonist && frame.Contains(a.Position)).ToList()) a.Destroy();
                        void Put(string def, ThingDef stuff, IntVec3 c, Rot4 rot)
                        {
                            if (!c.InBounds(map) || DefDatabase<ThingDef>.GetNamedSilentFail(def) == null) return;
                            var thing = ThingMaker.MakeThing(ThingDef.Named(def), stuff);
                            thing.SetFaction(Faction.OfPlayer);
                            GenSpawn.Spawn(thing, c, map, rot);
                        }
                        void Room(int x, int z, int w, int d)
                        {
                            var room = CellRect.FromLimits(x, z, x + w - 1, z + d - 1).ClipInsideMap(map);
                            foreach (var c in room) map.terrainGrid.SetTerrain(c, DefDatabase<TerrainDef>.GetNamed("WoodPlankFloor"));
                            foreach (var c in room.EdgeCells)
                            {
                                if (c.GetEdifice(map) != null) continue;
                                bool door = c.x == room.CenterCell.x && c.z == room.minZ;
                                Put(door ? "Door" : "Wall", ThingDefOf.WoodLog, c, Rot4.North);
                            }
                            Put("Bed", ThingDefOf.WoodLog, room.CenterCell + new IntVec3(-2, 0, 1), Rot4.South);
                            Put("Table1x2c", ThingDefOf.WoodLog, room.CenterCell + new IntVec3(2, 0, 0), Rot4.North);
                            Put("DiningChair", ThingDefOf.WoodLog, room.CenterCell + new IntVec3(1, 0, 0), Rot4.East);
                        }
                        // a house and a cabin on the west, a cabin and two fields on the east, a gravel road between them under the picture
                        Room(at.x - 46, at.z, 12, 10);
                        Room(at.x - 35, at.z, 9, 10);
                        Room(at.x - 44, at.z + 16, 10, 8);
                        Room(at.x + 28, at.z + 20, 10, 8);
                        var gravel = DefDatabase<TerrainDef>.GetNamedSilentFail("Gravel");
                        if (gravel != null)
                            foreach (var road in new[] { CellRect.FromLimits(at.x - 42, at.z - 3, at.x + 40, at.z - 2), CellRect.FromLimits(at.x - 25, at.z - 2, at.x - 24, north + 6), CellRect.FromLimits(at.x + 22, at.z - 2, at.x + 23, north + 6) })
                                foreach (var c in road.ClipInsideMap(map)) map.terrainGrid.SetTerrain(c, gravel);
                        var rich = DefDatabase<TerrainDef>.GetNamed("SoilRich");
                        foreach (var (crop, field) in new[] { ("Plant_Potato", CellRect.FromLimits(at.x + 26, at.z, at.x + 42, at.z + 7)), ("Plant_Corn", CellRect.FromLimits(at.x + 26, at.z + 9, at.x + 42, at.z + 16)) })
                            foreach (var c in field.ClipInsideMap(map))
                            {
                                map.terrainGrid.SetTerrain(c, rich);
                                var plant = (Plant)ThingMaker.MakeThing(ThingDef.Named(crop));
                                plant.Growth = .8f;
                                GenSpawn.Spawn(plant, c, map);
                            }
                        steamProjector = (Building_PhotoProjector)ThingMaker.MakeThing(ThingDef.Named("MF_Projector"), ThingDefOf.Steel);
                        steamProjector.SetFaction(Faction.OfPlayer);
                        GenSpawn.Spawn(steamProjector, at, map, Rot4.North);
                        if (!steamProjector.SetCustomProjectionSize(40f, 40f)) throw new InvalidOperationException("the largest projection does not fit this map");
                        steamProjector.SetProjectionDepth(ProjectionGeometry.BehindDepth);
                        HarmonyLib.Traverse.Create(steamProjector).Field("fixedPhotoId").SetValue(id);
                        var folk = map.mapPawns.FreeColonistsSpawned.ToList();
                        for (int i = 0; i < folk.Count; i++)
                            Teleport(folk[i], CellFinder.RandomClosewalkCellNear(new IntVec3(at.x - 16 + i * 4, 0, at.z - 1 - i % 2), map, 1));
                        SteamReady(folk);
                        var scatter = new System.Random(20261101);
                        string[] trees = { "Plant_TreeOak", "Plant_TreePoplar", "Plant_TreeOak", "Plant_TreeBirch" };
                        foreach (var c in frame)
                        {
                            if (people.Contains(c) || c.GetTerrain(map) != soil || c.GetEdifice(map) != null || c.GetFirstPawn(map) != null || c.GetPlant(map) != null) continue;
                            int roll = scatter.Next(100);
                            float patch = 24f + 26f * Mathf.Sin(c.x * .31f + 1.3f) * Mathf.Cos(c.z * .27f);
                            string def = roll < 6 ? trees[scatter.Next(trees.Length)] : roll < 8 ? "Plant_Dandelion" : roll < 8 + patch ? "Plant_Grass" : null;
                            if (def == null) continue;
                            var plant = (Plant)ThingMaker.MakeThing(ThingDef.Named(def));
                            plant.Growth = def.Contains("Tree") ? .55f + (float)scatter.NextDouble() * .45f : 1f;
                            GenSpawn.Spawn(plant, c, map);
                        }
                        PreparationRefreshRegions();
                        SteamTicks(40);
                        Find.Selector.ClearSelection();
                        // where the picture lies in the frame, for the GIF's pull-back
                        float fw = frameSpan * frameAspect;
                        steamLayout.Append("symbol-picture\t").Append(((at.x + .5f - steamAt.x + fw / 2f) / fw).ToString("0.000", CultureInfo.InvariantCulture)).Append('\t')
                            .Append(((steamAt.z + frameSpan / 2f - cz) / frameSpan).ToString("0.000", CultureInfo.InvariantCulture)).Append('\t')
                            .Append((40f / fw).ToString("0.000", CultureInfo.InvariantCulture)).Append('\n');
                        Wait(1f);
                        steamStep = 1;
                        return false;
                    }
                case 1:
                    PhotoCapture.ProbePixelsPerCell = 38;
                    ShowCapture(Path.Combine("steam", "symbol.png"), steamAt, 60f, 16f / 9f, true);
                    steamStep = 2;
                    return false;
                default:
                    if (!ShowCaptured()) return false;
                    PhotoCapture.ProbePixelsPerCell = 0;
                    checks.Add("INFO: steam symbol — the colony's portrait at the projector's largest size (40 tiles), the settlement around it");
                    return true;
            }
        }

        private static bool steamNoTips;
        private static bool steamTipPatched;
        private static bool SteamTipPrefix() => !steamNoTips;

        /// <summary>The offscreen window keeps whatever mouse position it last had, and a tooltip kept landing on the album (r1, r6).</summary>
        private static void SteamHideTips()
        {
            steamNoTips = true;
            if (steamTipPatched) return;
            steamTipPatched = true;
            new HarmonyLib.Harmony("momentframes.probe.steamtips").Patch(HarmonyLib.AccessTools.Method(typeof(TooltipHandler), "TipRegion", new[] { typeof(Rect), typeof(TipSignal) }),
                prefix: new HarmonyLib.HarmonyMethod(typeof(Probe), nameof(SteamTipPrefix)));
            new HarmonyLib.Harmony("momentframes.probe.steamalerts").Patch(HarmonyLib.AccessTools.Method(typeof(AlertsReadout), "AlertsReadoutOnGUI"),
                prefix: new HarmonyLib.HarmonyMethod(typeof(Probe), nameof(SteamTipPrefix)));   // and the test colony's warnings behind the windows
        }

        // What a player sees behind a window: no dev toolbar, no letters or warnings from the test world.
        private static void SteamCleanScreen()
        {
            CloseLog();
            Messages.Clear();
            Prefs.DevMode = false;
            Find.PlaySettings.showLearningHelper = false;
            foreach (var letter in Find.LetterStack.LettersListForReading.ToList()) Find.LetterStack.RemoveLetter(letter);
            SteamHideTips();
        }

        // ---------- native UI: the album in each of its themes, then the chronicle and the gallery ----------

        private static bool SteamAlbum()
        {
            var s = MomentFramesMod.Settings;
            switch (steamStep)
            {
                case 0:
                    {
                        SteamTidy();
                        // The scenes' photos after the newest imported one, so the first page opens on them (staged dates, this profile only).
                        var store = PhotoStore.Instance;
                        var ll = Find.WorldGrid.LongLatOf(map.Tile);
                        int last = store.Records.Where(r => !steamPhotos.Contains(r.id)).Max(r => r.ticksAbs);
                        for (int i = 0; i < steamPhotos.Count; i++)
                        {
                            var r = store.Get(steamPhotos[i]);
                            if (r == null) continue;
                            r.ticksAbs = r.capturedTicksAbs = last + (steamPhotos.Count - i) * GenDate.TicksPerDay;
                            r.dateLabel = GenDate.DateFullStringAt(r.ticksAbs, ll);
                            r.year = GenDate.Year(r.ticksAbs, ll.x);
                        }
                        store.NotifyChanged();
                        Find.Selector.ClearSelection();
                        SteamCleanScreen();
                        Find.CameraDriver.SetRootPosAndSize(new Vector3(center.x + 0.5f, 0f, center.z + 4f), 16f);
                        Directory.CreateDirectory(Path.Combine(dir, "steam", "album"));
                        s.albumFont = "original";
                        s.albumOpacity = 1f;
                        AlbumOpen(false, false);
                        steamFrame = 0;
                        Wait(1f);
                        steamStep = 1;
                        return false;
                    }
                case 1:
                    {
                        s.albumTheme = AlbumStyle.Themes[steamFrame].Key;
                        // A 1280x720 window that keeps clear of wherever the game thinks the mouse is (r1: a tab's tooltip showed in two themes).
                        var w = (MainTabWindow_Album)AlbumDef.TabWindow;
                        Vector2 m = UI.MousePositionOnUIInverted;
                        float h = 720f, y = 60f;
                        if (m.x >= 0f && m.x <= UI.screenWidth && m.y >= 0f && m.y <= UI.screenHeight)
                            y = m.y + 14f + h <= UI.screenHeight - 35f ? m.y + 14f : m.y - 14f - h >= 0f ? m.y - 14f - h : y;
                        w.windowRect = new Rect((UI.screenWidth - 1280f) / 2f, y, 1280f, h);
                        if (steamFrame == 0) steamLayout.Append("mouse\t").Append(m.x).Append('\t').Append(m.y).Append('\n');
                        Wait(0.8f);
                        steamStep = 2;
                        return false;
                    }
                case 2:
                    SteamCleanScreen();
                    Shot(Path.Combine("steam", "album", steamFrame.ToString("00") + "-" + s.albumTheme + ".png"));
                    if (steamFrame == 0)
                    {
                        var w = (MainTabWindow_Album)AlbumDef.TabWindow;
                        steamLayout.Append("album-window\t").Append(w.windowRect.x).Append('\t').Append(w.windowRect.y).Append('\t').Append(w.windowRect.width).Append('\t').Append(w.windowRect.height).Append('\n');
                    }
                    Wait(0.4f);
                    steamStep = ++steamFrame < AlbumStyle.Themes.Length ? 1 : 3;
                    return false;
                case 3:
                    s.albumTheme = "leather";
                    AlbumOpen(true, true);
                    Wait(1.2f);
                    steamStep = 4;
                    return false;
                case 4:
                    CloseLog();
                    Shot(Path.Combine("steam", "album", "chronicle-leather.png"));
                    Wait(0.6f);
                    steamStep = 5;
                    return false;
                case 5:
                    s.albumTheme = "graphite";
                    Find.MainTabsRoot.EscapeCurrentTab(false);
                    return true;
            }
            return true;
        }

        // ---------- native UI: the editor going through its looks on a studio portrait ----------

        private static bool SteamEditor()
        {
            switch (steamStep)
            {
                case 0:
                    {
                        SteamTidy();
                        var rec = (steamPhotos.Count > 0 ? PhotoStore.Instance.Get(steamPhotos[0]) : null) ?? SteamRec("x0_0029_") ?? ShotRec("0006_");
                        steamEdited = rec;
                        Find.WindowStack.Add(new Dialog_PhotoEdit(rec));
                        Directory.CreateDirectory(Path.Combine(dir, "steam", "editor"));
                        steamFrame = 0;
                        Wait(1.2f);
                        steamStep = 1;
                        return false;
                    }
                case 1:
                    {
                        var editor = Find.WindowStack.WindowOfType<Dialog_PhotoEdit>();
                        if (editor == null) throw new InvalidOperationException("the editor did not open");
                        var t = HarmonyLib.Traverse.Create(editor);
                        if (steamFrame == 1) t.Field("crop").SetValue(new Rect(0.14f, 0.08f, 0.72f, 0.84f));
                        t.Method("SetFilter", PhotoFilters.All[Mathf.Max(0, steamFrame - 1)]).GetValue();
                        if (steamFrame == 0)
                            steamLayout.Append("editor-window\t").Append(editor.windowRect.x).Append('\t').Append(editor.windowRect.y).Append('\t').Append(editor.windowRect.width).Append('\t').Append(editor.windowRect.height).Append('\n');
                        Wait(0.8f);
                        steamStep = 2;
                        return false;
                    }
                case 2:
                    CloseLog();
                    Messages.Clear();
                    // frame 0: the photo as taken; then the same crop in each look
                    Shot(Path.Combine("steam", "editor", steamFrame.ToString("00") + "-" + PhotoFilters.All[Mathf.Max(0, steamFrame - 1)] + ".png"));
                    Wait(0.4f);
                    steamStep = ++steamFrame <= PhotoFilters.All.Length ? 1 : 3;
                    return false;
                case 3:
                    {
                        // what the player keeps: the edit beside the original in the album
                        Find.WindowStack.TryRemove(typeof(Dialog_PhotoEdit), false);
                        var store = PhotoStore.Instance;
                        var sepia = store.SaveEdit(steamEdited, new Rect(0.14f, 0.08f, 0.72f, 0.84f), PhotoFilters.Sepia);
                        if (sepia == null) throw new InvalidOperationException("the edit was not saved");
                        store.SetShown(steamEdited, sepia.id);
                        MainTabWindow_Album.ShowVersion(steamEdited, sepia.id);
                        Wait(1.5f);
                        steamStep = 4;
                        return false;
                    }
                case 4:
                    CloseLog();
                    Messages.Clear();
                    Shot(Path.Combine("steam", "editor", "result.png"));
                    Wait(0.6f);
                    steamStep = 5;
                    return false;
                case 5:
                    Find.MainTabsRoot.EscapeCurrentTab(false);
                    return true;
            }
            return true;
        }

        // ---------- a lived-in room: photos on a bedroom wall, two colonists walking in to look at them (the mod's own viewing job) ----------

        private static PhotoRecord steamEdited;
        private static Pawn steamViewerA, steamViewerB;

        private static void SteamPut(string def, IntVec3 c, Rot4 rot, bool stuffed = true)
        {
            var d = DefDatabase<ThingDef>.GetNamedSilentFail(def);
            if (d == null || !c.InBounds(map)) { checks.Add("INFO: steam life — no " + def); return; }
            var thing = ThingMaker.MakeThing(d, stuffed && d.MadeFromStuff ? ThingDefOf.WoodLog : null);
            thing.SetFaction(Faction.OfPlayer);
            GenSpawn.Spawn(thing, c, map, rot);
            thing.TryGetComp<CompRefuelable>()?.Refuel(20f);
        }

        private static bool SteamViewing(Pawn p) => p.CurJobDef?.defName == "MF_ViewPhoto" && !p.pather.Moving;

        private static bool SteamLife()
        {
            switch (steamStep)
            {
                case 0:
                    {
                        SteamTidy();
                        foreach (var l in map.lordManager.lords.ToList()) map.lordManager.RemoveLord(l);
                        JumpToHour(12f);
                        int rx = center.x + 24, rz = center.z + 26;
                        var room = CellRect.FromLimits(rx - 7, rz - 7, rx + 7, rz).ClipInsideMap(map);
                        foreach (var c in room.ExpandedBy(4).ClipInsideMap(map))
                        {
                            foreach (var thing in c.GetThingList(map).ToList()) if (!(thing is Pawn) && thing.def.destroyable) thing.Destroy(DestroyMode.Vanish);
                            map.roofGrid.SetRoof(c, null);
                            map.terrainGrid.SetTerrain(c, DefDatabase<TerrainDef>.GetNamed("Soil"));
                        }
                        map.fogGrid.ClearAllFog();
                        foreach (var c in room) map.terrainGrid.SetTerrain(c, DefDatabase<TerrainDef>.GetNamed("WoodPlankFloor"));
                        // carpets are generated per colour, so take one by name (r1: "Carpet" alone is only the template)
                        var rug = DefDatabase<TerrainDef>.AllDefsListForReading.Where(d => d.defName.Contains("Carpet") && !d.defName.Contains("Morbid")).OrderBy(d => d.defName.Contains("Red") ? 0 : d.defName.Contains("Brown") ? 1 : 2).ThenBy(d => d.defName).FirstOrDefault();   // r2: the morbid colours have a pattern like veins
                        checks.Add("INFO: steam life — rug " + (rug?.defName ?? "none"));
                        if (rug != null) foreach (var c in CellRect.FromLimits(rx - 2, rz - 5, rx + 3, rz - 3)) map.terrainGrid.SetTerrain(c, rug);
                        foreach (var c in room.EdgeCells)
                        {
                            bool door = c.x == rx + 1 && c.z == room.minZ;
                            var wall = ThingMaker.MakeThing(door ? ThingDefOf.Door : ThingDefOf.Wall, ThingDefOf.WoodLog);
                            wall.SetFaction(Faction.OfPlayer);
                            GenSpawn.Spawn(wall, c, map);
                        }
                        PreparationRefreshRegions();
                        foreach (var c in room) map.roofGrid.SetRoof(c, RoofDefOf.RoofConstructed);
                        PreparationRefreshRegions();
                        SteamPut("Bed", new IntVec3(rx - 4, 0, rz - 1), Rot4.South);
                        SteamPut("EndTable", new IntVec3(rx - 3, 0, rz - 1), Rot4.South);
                        SteamPut("Dresser", new IntVec3(rx + 5, 0, rz - 1), Rot4.South);
                        SteamPut("PlantPot", new IntVec3(rx + 5, 0, rz - 3), Rot4.North);
                        var flower = (Plant)ThingMaker.MakeThing(ThingDef.Named("Plant_Rose"));
                        flower.Growth = 1f;
                        GenSpawn.Spawn(flower, new IntVec3(rx + 5, 0, rz - 3), map);
                        foreach (var c in new[] { new IntVec3(rx - 1, 0, rz - 1), new IntVec3(rx + 3, 0, rz - 1), new IntVec3(rx - 4, 0, rz - 4), new IntVec3(rx + 6, 0, rz - 5) }) SteamPut("TorchLamp", c, Rot4.North, false);
                        string portrait = steamSymbol ?? steamPhotos.FirstOrDefault() ?? ShotRec("0003_").id;
                        var main = Spawn("MF_PhotoFrameWide", ThingDefOf.WoodLog, new IntVec3(rx + 1, 0, rz - 1), Rot4.North);
                        main.SetPhoto(portrait);
                        main.SetStyle(FrameStyle.Gilded);
                        main.RefreshBinding();
                        var left = Spawn("MF_PhotoFrame", ThingDefOf.WoodLog, new IntVec3(rx - 2, 0, rz - 1), Rot4.North);
                        left.SetPhoto(SteamRec("x0_0034_")?.id ?? ShotRec("0006_").id);
                        left.SetStyle(FrameStyle.Polaroid);
                        left.RefreshBinding();
                        var right = Spawn("MF_PhotoFrame", ThingDefOf.WoodLog, new IntVec3(rx + 4, 0, rz - 1), Rot4.North);
                        right.SetPhoto(steamPhotos.FirstOrDefault() ?? SteamRec("x0_0029_")?.id ?? ShotRec("0007_").id);
                        right.SetStyle(FrameStyle.BlackMat);
                        right.RefreshBinding();
                        // Two of them come in through the door; one frame takes one viewer at a time, so each goes to her own.
                        // (r1: a child wandered off again before reaching hers; an adult with wings also reads better from behind.)
                        steamViewerA = ShotPawn("Celeste") ?? map.mapPawns.FreeColonistsSpawned.First();
                        steamViewerB = ShotPawn("Victoria") ?? map.mapPawns.FreeColonistsSpawned.First(x => x != steamViewerA);
                        var both = new List<Pawn> { steamViewerA, steamViewerB };
                        SteamReady(both);
                        Teleport(steamViewerA, new IntVec3(rx + 1, 0, rz - 9));
                        Teleport(steamViewerB, new IntVec3(rx + 1, 0, rz - 11));
                        var view = DefDatabase<JobDef>.GetNamed("MF_ViewPhoto");
                        foreach (var p in both) if (p.needs?.joy != null) p.needs.joy.CurLevel = 0.2f;
                        steamViewerA.jobs.StartJob(JobMaker.MakeJob(view, main), Verse.AI.JobCondition.InterruptForced);
                        steamViewerB.jobs.StartJob(JobMaker.MakeJob(view, left), Verse.AI.JobCondition.InterruptForced);
                        Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                        SteamTicks(2);
                        steamHold = 0;
                        SteamBegin("life", new Vector3(rx + .5f, 0f, rz - 1.65f), 5.4f, 134, true);   // r1: the photos were cut off at the top and small
                        steamStep = 1;
                        return false;
                    }
                case 1:
                    if (!SteamFrameReady()) return false;
                    if (steamFrame >= 150 || (SteamViewing(steamViewerA) && SteamViewing(steamViewerB) && ++steamHold > 10))
                    {
                        SteamEnd();
                        checks.Add("INFO: steam life — viewing " + SteamViewing(steamViewerA) + "/" + SteamViewing(steamViewerB) + " after " + steamFrame + " frames");
                        steamStep = 2;
                        return false;
                    }
                    SteamTicks(3);
                    SteamFrame();
                    return false;
                case 2:
                    if (PhotoCapture.Busy) return false;
                    PhotoCapture.ProbePixelsPerCell = 170;
                    ShowCapture(Path.Combine("steam", "life-still.png"), steamAt, 5.4f, 16f / 9f, true);
                    steamStep = 3;
                    return false;
                default:
                    if (!ShowCaptured()) return false;
                    PhotoCapture.ProbePixelsPerCell = 0;
                    foreach (var p in new[] { steamViewerA, steamViewerB }) p.jobs?.EndCurrentJob(Verse.AI.JobCondition.InterruptForced);
                    return true;
            }
        }

        // ---------- native UI stills: the settings' disk-usage card, a colonist's chronicle tab ----------

        // ---------- the mod at work by itself: real events on the map, filmed until the photo it takes of them is saved ----------

        private static readonly string[] SteamAutoClips = { "wedding", "party", "fight", "pet" };
        private static int steamAutoClip, steamAutoTicks, steamAutoShotFrame, steamAutoSkip;
        private static Func<PhotoRecord> steamAutoDone;
        private static Action steamAutoEnd;

        /// <summary>Open ground for one clip: nothing left from the clip before, one plain floor (r5: random grass patches made every photo look like a checkerboard).</summary>
        private static void SteamAutoYard(CellRect rect, string floor)
        {
            var ground = DefDatabase<TerrainDef>.GetNamed(floor);
            foreach (var c in rect.ClipInsideMap(map))
            {
                foreach (var thing in c.GetThingList(map).ToList()) if (!(thing is Pawn) && thing.def.destroyable) thing.Destroy(DestroyMode.Vanish);
                map.roofGrid.SetRoof(c, null);
                map.terrainGrid.SetTerrain(c, ground);
            }
            map.fogGrid.ClearAllFog();
            PreparationRefreshRegions();
        }

        /// <summary>The photo the mod just took of this kind: never one of the imported photos of the same kind, or one already used.</summary>
        private static PhotoRecord SteamAutoRecord(string kind) => PhotoStore.Instance.Records.FirstOrDefault(r => r.kind == kind && !steamPhotos.Contains(r.id) && (r.eventKey == null || !r.eventKey.StartsWith("shots:")));

        private static void SteamAutoPlant(string def, IntVec3 c)
        {
            var d = DefDatabase<ThingDef>.GetNamedSilentFail(def);
            if (d == null || !c.InBounds(map)) return;
            foreach (var thing in c.GetThingList(map).Where(x => x is Plant).ToList()) thing.Destroy(DestroyMode.Vanish);
            var plant = (Plant)ThingMaker.MakeThing(d);
            plant.Growth = 1f;
            GenSpawn.Spawn(plant, c, map);
        }

        /// <summary>The ones in this clip in a ring, rested and free; everyone else well outside the picture (r5: ten people scattered over the frame read as clutter).</summary>
        private static List<Pawn> SteamAutoCast(IntVec3 at, int radius, params string[] names)
        {
            var everyone = map.mapPawns.FreeColonistsSpawned.ToList();
            var people = names.Select(ShotPawn).Where(x => x != null).ToList();
            SteamReady(everyone);
            int away = 0;
            foreach (var other in everyone.Except(people))
            {
                Teleport(other, at + new IntVec3(-14 + away++ * 3, 0, -28));
                other.jobs?.StartJob(JobMaker.MakeJob(JobDefOf.Wait, 20000), Verse.AI.JobCondition.InterruptForced);   // r8: left free, they walked over and joined the gathering
            }
            for (int i = 0; i < people.Count; i++)
            {
                double angle = i * 2 * Math.PI / Math.Max(1, people.Count);
                var c = at + new IntVec3((int)Math.Round(Math.Cos(angle) * radius), 0, (int)Math.Round(Math.Sin(angle) * radius * .8));
                people[i].jobs?.EndCurrentJob(Verse.AI.JobCondition.InterruptForced);
                Teleport(people[i], c);
            }
            return people;
        }

        private static void SteamAutoWedding(IntVec3 at)
        {
            SteamAutoYard(CellRect.CenteredOn(at, 18), "WoodPlankFloor");
            foreach (int x in new[] { -5, 5 })
            {
                SteamPut("TorchLamp", at + new IntVec3(x, 0, 3), Rot4.North, false);
                SteamPut("PlantPot", at + new IntVec3(x, 0, 1), Rot4.North);
                SteamAutoPlant("Plant_Rose", at + new IntVec3(x, 0, 1));
            }
            SteamAutoCast(at, 6, "Julian", "Celeste", "Victoria", "Mitsuya", "Moray", "Elna", "Kelo");   // not Evie: she stands tall and hid the others (r6)
            if (!steamSpectatePatched)
            {
                steamSpectatePatched = true;
                new HarmonyLib.Harmony("momentframes.probe.steamspectate").Patch(HarmonyLib.AccessTools.Method(typeof(SpectatorCellFinder), nameof(SpectatorCellFinder.FindSingleBestSide)),
                    postfix: new HarmonyLib.HarmonyMethod(typeof(Probe), nameof(SteamSpectateSide)));
            }
            steamSpectateNorth = true;
            var a = ShotPawn("Julian") ?? map.mapPawns.FreeColonistsSpawned.First();
            var b = ShotPawn("Celeste") ?? map.mapPawns.FreeColonistsSpawned.First(x => x != a);
            if (MFDebug.StartWedding(a, b, at) == null) throw new InvalidOperationException("no marriage ceremony");
            steamAutoTicks = 10;
            steamAutoSkip = 4800;   // the ceremony opens with a long party; the film starts as the couple take their places
            SteamBegin("auto-wedding", at.ToVector3Shifted() + new Vector3(0f, 0f, 1.2f), 6.6f, 80, true);
            steamAutoDone = () =>
            {
                if (steamFrame == 40) steamAutoTicks = 60;   // they stand through the vows: nothing moves until it is done
                if (a.CurJobDef == JobDefOf.MarryAdjacentPawn && b.CurJobDef == JobDefOf.MarryAdjacentPawn)
                {
                    // Ticked while paused, the couple keep the direction they arrived in; in play the game turns them to each other.
                    a.rotationTracker.FaceCell(b.Position); b.rotationTracker.FaceCell(a.Position);
                    // A slow last moment: the shutter comes right after the vows, not a second later with the guests already leaving.
                    if ((a.jobs.curDriver as JobDriver_MarryAdjacentPawn)?.TicksLeftToMarry <= 130) steamAutoTicks = 2;
                }
                return SteamAutoRecord("wedding");
            };
            steamAutoEnd = () => steamSpectateNorth = false;
        }

        private static bool steamSpectatePatched, steamSpectateNorth;

        /// <summary>The game chooses the guests' side by a tally that came out south, their backs to the camera (r6); north is just as valid a choice.</summary>
        private static void SteamSpectateSide(ref SpectateRectSide __result)
        {
            if (steamSpectateNorth) __result = SpectateRectSide.Up;
        }

        private static void SteamAutoParty(IntVec3 at)
        {
            SteamAutoYard(CellRect.CenteredOn(at, 18), "Soil");
            SteamPut("Campfire", at, Rot4.North, false);
            JumpToHour(20.5f);   // firelight in the dark: the party is the only thing to look at
            var people = SteamAutoCast(at, 3, "Mitsuya", "Julian", "Celeste", "Victoria", "Moray", "Kelo");
            var host = ShotPawn("Mitsuya") ?? map.mapPawns.FreeColonistsSpawned.First();
            var job = new LordJob_Joinable_Party(at, host, GatheringDefOf.Party);
            HarmonyLib.Traverse.Create(job).Field("durationTicks").SetValue(2400);
            Verse.AI.Group.LordMaker.MakeNewLord(Faction.OfPlayer, job, map, new[] { host });
            string key = Hooks.Key("party", job);
            var store = PhotoStore.Instance;
            steamAutoTicks = 9;
            steamAutoSkip = 1100;   // photographed once the party is well under way
            SteamBegin("auto-party", at.ToVector3Shifted(), 6.4f, 84, true);
            steamAutoDone = () =>
            {
                if (steamFrame == 0)   // r7: left to wander they drifted out of the picture; they come in from the dark and stand around the fire
                    for (int i = 0; i < people.Count; i++)
                    {
                        double angle = i * 2 * Math.PI / people.Count + .4;
                        Teleport(people[i], at + new IntVec3((int)Math.Round(Math.Cos(angle) * 8), 0, (int)Math.Round(Math.Sin(angle) * 6)));
                        var ring = at + new IntVec3((int)Math.Round(Math.Cos(angle) * 3), 0, (int)Math.Round(Math.Sin(angle) * 2.4));
                        people[i].jobs.StartJob(JobMaker.MakeJob(JobDefOf.Goto, ring), Verse.AI.JobCondition.InterruptForced);
                        var stay = JobMaker.MakeJob(JobDefOf.Wait, 3000);
                        people[i].jobs.jobQueue.EnqueueLast(stay);
                    }
                foreach (var p in people)
                    if (!p.pather.Moving) p.Rotation = Rot4.FromAngleFlat((at - p.Position).AngleFlat);
                return store.Records.FirstOrDefault(r => r.eventKey == key);
            };
            steamAutoEnd = () => { foreach (var p in people) p.jobs?.ClearQueuedJobs(); };
        }

        private static void SteamAutoFight(IntVec3 at)
        {
            var s = MomentFramesMod.Settings;
            SteamAutoYard(CellRect.CenteredOn(at, 22), "Soil");
            for (int z = -3; z <= 3; z++) SteamPut("Sandbags", at + new IntVec3(-3, 0, z), Rot4.North, false);
            s.battleMode = Battles.ModeEvery;
            Battles.CalmTicks = 0;
            Battles.Reset();
            var people = SteamAutoCast(at, 1, "Julian", "Mushinto", "Elna", "Moray");
            for (int i = 0; i < people.Count; i++)
            {
                Teleport(people[i], at + new IntVec3(-5, 0, -3 + i * 2));
                people[i].equipment?.DestroyAllEquipment();
                people[i].equipment?.AddEquipment((ThingWithComps)ThingMaker.MakeThing(ThingDef.Named("Gun_BoltActionRifle")));
                people[i].drafter.Drafted = true;
                people[i].drafter.FireAtWill = false;   // a moment to see what is coming
                people[i].Rotation = Rot4.East;
            }
            var enemies = new List<Pawn>();
            var kind = DefDatabase<PawnKindDef>.GetNamed("Warg");
            for (int i = 0; i < 3; i++)
            {
                var beast = PawnGenerator.GeneratePawn(kind);
                GenSpawn.Spawn(beast, at + new IntVec3(13, 0, -3 + i * 3), map, Rot4.West);
                beast.mindState.mentalStateHandler.TryStartMentalState(MentalStateDefOf.ManhunterPermanent, null, true);
                enemies.Add(beast);
            }
            Battles.Begin(map, "animals", null);
            var store = PhotoStore.Instance;
            int firedAt = -1;
            steamAutoTicks = 4;
            steamAutoSkip = 30;   // the beasts are already running when the film starts
            SteamBegin("auto-fight", at.ToVector3Shifted() + new Vector3(0.6f, 0f, 0f), 8.4f, 80, true);
            steamAutoDone = () =>
            {
                foreach (var p in people) p.Rotation = Rot4.East;   // r5: they stood facing away while their rifles pointed at the beasts
                if (steamFrame == 4) foreach (var p in people) p.drafter.FireAtWill = true;
                var fight = Battles.Active.FirstOrDefault(f => f.map == map);
                if (fight != null && fight.shotAttempted && firedAt < 0) { firedAt = steamFrame; steamAutoShotFrame = steamFrame; }
                if (firedAt >= 0 && steamFrame >= firedAt + 8 && fight != null && !PhotoCapture.Busy)
                {
                    foreach (var e in enemies.Where(e => !e.Dead)) e.Kill(null);
                    fight.sawDanger = true;
                    HarmonyLib.Traverse.Create(typeof(Battles)).Field("lastCheck").SetValue(-99999);
                    Battles.Poll(store, Find.TickManager.TicksGame);
                }
                return firedAt < 0 ? null : SteamAutoRecord(Battles.Kind);
            };
            steamAutoEnd = () =>
            {
                foreach (var e in enemies.Where(e => e.Spawned || e.Corpse?.Spawned == true).ToList()) (e.Corpse as Thing ?? e).Destroy(DestroyMode.Vanish);
                foreach (var p in people)
                {
                    p.drafter.Drafted = false;
                    p.equipment?.DestroyAllEquipment();
                }
            };
        }

        private static void SteamAutoPet(IntVec3 at)
        {
            SteamAutoYard(CellRect.CenteredOn(at, 18), "Soil");
            SteamAutoPlant("Plant_TreeOak", at + new IntVec3(-4, 0, 1));
            foreach (var c in new[] { at + new IntVec3(3, 0, 1), at + new IntVec3(4, 0, 0), at + new IntVec3(4, 0, 1) }) SteamAutoPlant("Plant_Daylily", c);
            var owner = SteamAutoCast(at, 1, "Celeste").FirstOrDefault() ?? map.mapPawns.FreeColonistsSpawned.First();
            Teleport(owner, at + new IntVec3(-4, 0, -1));
            var cat = PawnGenerator.GeneratePawn(DefDatabase<PawnKindDef>.GetNamed("Cat"));
            cat.Name = new NameSingle(Ko("Biscuit"));
            GenSpawn.Spawn(cat, at + new IntVec3(1, 0, -1), map, Rot4.West);
            owner.jobs.StartJob(JobMaker.MakeJob(JobDefOf.Goto, at + new IntVec3(-1, 0, -1)), Verse.AI.JobCondition.InterruptForced);
            owner.jobs.jobQueue.EnqueueLast(JobMaker.MakeJob(JobDefOf.Wait, 3000));   // r8: she wandered off again and the cat too, far apart in the photo
            cat.jobs.StartJob(JobMaker.MakeJob(JobDefOf.Wait, 3000), Verse.AI.JobCondition.InterruptForced);
            string key = PetMilestones.Key(cat);
            var store = PhotoStore.Instance;
            bool tamed = false;
            steamAutoTicks = 5;
            SteamBegin("auto-pet", at.ToVector3Shifted() + new Vector3(0f, 0f, -0.6f), 5.6f, 110, true);
            steamAutoDone = () =>
            {
                if (!owner.pather.Moving) owner.Rotation = Rot4.East;
                if (!tamed && steamFrame >= 22)
                {
                    tamed = true;
                    InteractionWorker_RecruitAttempt.DoRecruit(owner, cat, false);   // the game's own successful tame
                }
                return store.Records.FirstOrDefault(r => r.eventKey == key);
            };
            steamAutoEnd = () =>
            {
                owner.jobs?.ClearQueuedJobs();
                if (cat.Spawned) cat.Destroy(DestroyMode.Vanish);
            };
        }

        private static bool SteamAuto()
        {
            var s = MomentFramesMod.Settings;
            switch (steamStep)
            {
                case 0:
                    {
                        if (steamAutoClip >= SteamAutoClips.Length)
                        {
                            s.autoCaptureEnabled = false;
                            return true;
                        }
                        SteamTidy();
                        ClearHostiles(map);
                        foreach (var l in map.lordManager.lords.ToList()) map.lordManager.RemoveLord(l);
                        JumpToHour(11f);
                        Prefs.PlantWindSway = false;   // every blade of grass changing every frame made the GIF several times larger
                        foreach (var animal in map.mapPawns.AllPawnsSpawned.Where(p => p.RaceProps.Animal).ToList()) animal.Destroy(DestroyMode.Vanish);   // a stray cat kept walking through the ceremony
                        s.autoCaptureEnabled = true;
                        s.fxFlash = s.fxShutter = s.fxPopup = s.fxFreeze = s.fxCamera = false;   // the flash is added to the film; a popup would stop the clock
                        if (SteamScreenMode) s.fxFlash = s.fxPopup = true;   // the real screen (DC post): the mod's own flash and card, timed by SteamFxClock
                        steamAutoShotFrame = -1;
                        steamHold = 0;
                        var at = new IntVec3(center.x, 0, center.z);
                        switch (SteamAutoClips[steamAutoClip])
                        {
                            case "wedding": SteamAutoWedding(at); break;
                            case "party": SteamAutoParty(at); break;
                            case "fight": SteamAutoFight(at); break;
                            default: SteamAutoPet(at); break;
                        }
                        Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                        steamStep = 1;
                        return false;
                    }
                case 1:
                    {
                        if (!SteamFrameReady()) return false;
                        if (steamAutoSkip > 0)
                        {
                            SteamTicks(Math.Min(120, steamAutoSkip));
                            steamAutoSkip -= 120;
                            return false;
                        }
                        // A queued photo starts in the mod's own per-frame update, only while no capture is running: film nothing until it is done.
                        if (PhotoStore.Instance.HasPending)
                        {
                            if (!PhotoCapture.Busy) SteamTicks(2);
                            return false;
                        }
                        var rec = steamAutoDone();
                        if (rec != null && steamAutoShotFrame < 0) steamAutoShotFrame = steamFrame;
                        if (rec != null && rec.HasFile && !PhotoCapture.Busy && steamFrame >= steamAutoShotFrame + (SteamScreenMode ? 34 : 4))
                        {
                            SteamEnd();
                            File.WriteAllText(Path.Combine(dir, "steam", steamSeq, "shot.txt"), steamAutoShotFrame.ToString());
                            steamStep = 2;
                            return false;
                        }
                        if (steamFrame > 320) throw new TimeoutException(steamSeq + ": no photo after " + steamFrame + " frames");
                        SteamTicks(steamAutoTicks);
                        SteamFrame();
                        return false;
                    }
                default:
                    {
                        if (PhotoCapture.Busy) return false;
                        var rec = steamAutoDone();
                        SteamCopyPhoto(rec.id, steamSeq + ".png");
                        steamAutoEnd();
                        PhotoCapture.ProbePixelsPerCell = 0;
                        steamAutoClip++;
                        steamStep = 0;
                        return false;
                    }
            }
        }

        // ---------- every window a new player meets, one picture each, to see whether a language's labels still fit ----------

        private static List<KeyValuePair<string, Action>> steamLangShots;

        private static List<Pawn> reservePeople;
        private static int reserveLines;

        private static int ReserveErrors() => Log.Messages.Count(m => m.text != null && m.text.Contains("Tried to release"));

        /// <summary>A player's group photo logged "Tried to release ... that wasn't reserved": which job does that, and does it need a photo at all?</summary>
        private static bool SteamReserve()
        {
            var report = Path.Combine(dir, "reserve.txt");
            var gardening = DefDatabase<JobDef>.GetNamedSilentFail("IdleJob_Gardening");
            if (steamStep == 0)
            {
                SteamTidy();
                foreach (var l in map.lordManager.lords.ToList()) map.lordManager.RemoveLord(l);
                JumpToHour(12f);
                var at = center;
                SteamAutoYard(CellRect.CenteredOn(at, 18), "Soil");
                var corn = ThingDef.Named("Plant_Corn");
                var zone = new Zone_Growing(map.zoneManager);
                map.zoneManager.RegisterZone(zone);
                zone.SetPlantDefToGrow(corn);
                var young = new List<IntVec3>();
                var ripe = new List<Plant>();
                foreach (var c in CellRect.CenteredOn(at + new IntVec3(-6, 0, 0), 3).Cells)
                {
                    zone.AddCell(c);
                    var plant = (Plant)GenSpawn.Spawn(corn, c, map);
                    plant.sown = true;
                    plant.Growth = c.x < at.x - 6 ? .4f : 1f;
                    if (plant.Growth < 1f) young.Add(c); else ripe.Add(plant);
                }
                reservePeople = map.mapPawns.FreeColonistsSpawned.Take(4).ToList();
                for (int i = 0; i < reservePeople.Count; i++) Teleport(reservePeople[i], at + new IntVec3(-11, 0, -3 + i * 2));
                File.WriteAllText(report, "gardening job loaded: " + (gardening != null) + ", people " + reservePeople.Count + ", young " + young.Count + ", ripe " + ripe.Count + ", errors at start " + ReserveErrors() + "\n");
                for (int i = 0; i < 2 && gardening != null; i++)
                    reservePeople[i].jobs.StartJob(JobMaker.MakeJob(gardening, young[i]), Verse.AI.JobCondition.InterruptForced);
                var harvest = JobMaker.MakeJob(JobDefOf.Harvest);
                foreach (var r in ripe.Take(6)) harvest.AddQueuedTarget(Verse.AI.TargetIndex.A, r);
                reservePeople[2].jobs.StartJob(harvest, Verse.AI.JobCondition.InterruptForced);
                steamStep = 1;
                return false;
            }
            if (steamStep == 1)
            {
                bool Working(Pawn p) => p.CurJob != null && !p.pather.Moving && p.CurJob.targetA.Thing is Plant;
                for (int i = 0; i < 300 && !reservePeople.Take(3).All(Working); i++) Find.TickManager.DoSingleTick();
                foreach (var p in reservePeople.Take(3))
                    File.AppendAllText(report, p.LabelShort + ": " + p.CurJobDef?.defName + " on " + p.CurJob?.targetA.Thing + ", working " + Working(p) + "\n");
                if (reserveLines++ < 40 && !reservePeople.Take(3).All(Working)) return false;
                steamStep = 2;
                return false;
            }
            if (steamStep == 2)
            {
                int before = ReserveErrors();
                var drafted = reservePeople[0];
                string job = drafted.CurJobDef?.defName;
                drafted.drafter.Drafted = true;   // no photo involved: the plain "draft" button
                File.AppendAllText(report, "draft " + drafted.LabelShort + " while " + job + ": errors +" + (ReserveErrors() - before) + "\n");
                drafted.drafter.Drafted = false;
                before = ReserveErrors();
                var group = new[] { reservePeople[1], reservePeople[2], reservePeople[3] };
                string jobs = string.Join(", ", group.Select(p => p.LabelShort + "=" + p.CurJobDef?.defName));
                var lord = GroupPhoto.StartPrepared(map, center + new IntVec3(6, 0, 0), group, Rot4.South);
                File.AppendAllText(report, "group photo (" + jobs + ") started " + (lord != null) + ": errors +" + (ReserveErrors() - before) + "\n");
                foreach (var m in Log.Messages.Where(m => m.text != null && m.text.Contains("Tried to release")))
                    File.AppendAllText(report, "LINE " + m.text + "\n" + m.StackTrace + "\n");
                if (lord != null) map.lordManager.RemoveLord(lord);
                return true;
            }
            return true;
        }

        private static bool SteamLang()
        {
            if (steamStep == 0)
            {
                SteamTidy();
                Directory.CreateDirectory(Path.Combine(dir, "steam", "lang"));   // the screenshot call says nothing when the folder is missing
                var mod = LoadedModManager.GetMod<MomentFramesMod>();
                var spot = map.listerBuildings.allBuildingsColonist.OfType<Building_PhotoSpot>().FirstOrDefault();
                var who = ShotPawn("Celeste") ?? map.mapPawns.FreeColonistsSpawned.First();
                var shots = steamLangShots = new List<KeyValuePair<string, Action>>();
                void Add(string name, Action open) => shots.Add(new KeyValuePair<string, Action>(name, open));
                void Tab(string name, string tab) => Add("album-" + name, () =>
                {
                    Find.MainTabsRoot.SetCurrentTab(DefDatabase<MainButtonDef>.GetNamed("MF_Album"));
                    var window = Find.WindowStack.WindowOfType<MainTabWindow_Album>();
                    var kind = typeof(MainTabWindow_Album).GetNestedType("Tab", System.Reflection.BindingFlags.NonPublic);
                    HarmonyLib.AccessTools.Method(typeof(MainTabWindow_Album), "SetTab").Invoke(window, new[] { Enum.Parse(kind, tab) });
                });
                void Pick(string name, Thing thing) => Add("gizmos-" + name, () =>
                {
                    if (thing == null || !thing.Spawned) throw new InvalidOperationException("not on the map");
                    Find.Selector.ClearSelection();
                    Find.Selector.Select(thing, false);
                });
                foreach (var category in SettingsCatalog.Categories)
                {
                    string id = category.Id;
                    Add("settings-" + id, () =>
                    {
                        Find.WindowStack.Add(new Dialog_ModSettings(mod));
                        ShotsSettingsPage().SelectCategory(id);
                        ShotsSettingsPage().SetExpanded(true);
                        ShotsSettingsPage().SetScroll(0f);
                    });
                }
                foreach (string topic in PhotoGuide.Topics)
                    for (int page = 0; page < 3; page++)
                    {
                        int at = page;
                        Add("guide-" + topic + "-" + at, () =>
                        {
                            var guide = new Dialog_PhotoGuide();
                            Find.WindowStack.Add(guide);
                            guide.Select(topic, at);
                        });
                    }
                Tab("photos", "Photos");
                Tab("chronicle", "Chronicle");
                Tab("gallery", "Gallery");
                Tab("settlement", "Settlement");
                Tab("imported", "Imported");
                Add("group-setup", () => Find.WindowStack.Add(new Dialog_GroupPhotoSetup(spot)));
                Add("birthday-setup", () => Find.WindowStack.Add(new Dialog_BirthdaySetup(who, 30, null, null)));
                Add("studio-furnishing", () => Find.WindowStack.Add(new Dialog_StudioFurnishing(steamBackdrop)));
                Add("studio-background", () => Find.WindowStack.Add(new Dialog_StudioBackground(steamBackdrop)));
                Add("memory-night", () => Find.WindowStack.Add(new Dialog_MemoryNightSetup(steamProjector)));
                Add("projection-size", () => Find.WindowStack.Add(new Dialog_ProjectionSize(steamProjector)));
                Add("choose-photo", () => Find.WindowStack.Add(new Dialog_ChoosePhoto(steamStyleFrame)));
                Add("photo-storage", () => Find.WindowStack.Add(new Dialog_PhotoStorage()));
                Add("imported-photos", () => Find.WindowStack.Add(new Dialog_ExternalPhotos(false)));
                Pick("studio", steamBackdrop);
                Pick("projector", steamProjector);
                Pick("frame", steamStyleFrame);
                steamStep = 1;
                return false;
            }
            int index = (steamStep - 1) / 3;
            if (index >= steamLangShots.Count) return true;
            string shotName = steamLangShots[index].Key;
            switch ((steamStep - 1) % 3)
            {
                case 0:
                    try { steamLangShots[index].Value(); }
                    catch (Exception e) { checks.Add("INFO: steam lang — " + shotName + " did not open: " + (e.InnerException ?? e).Message); steamStep += 3; return false; }
                    Wait(1f);
                    steamStep++;
                    return false;
                case 1:
                    CloseLog();
                    Messages.Clear();
                    Shot(Path.Combine("steam", "lang", shotName + ".png"));
                    Wait(.5f);
                    steamStep++;
                    return false;
                default:
                    foreach (var window in Find.WindowStack.Windows.Where(w => w is Dialog_ModSettings || w.GetType().Namespace == "MomentFrames").ToList()) window.Close(false);
                    Find.MainTabsRoot.EscapeCurrentTab(false);
                    Find.Selector.ClearSelection();
                    Wait(.3f);
                    steamStep++;
                    return false;
            }
        }

        private static bool SteamUi()
        {
            switch (steamStep)
            {
                case 0:
                    {
                        SteamTidy();
                        var s = MomentFramesMod.Settings;
                        s.fxFlash = s.fxShutter = s.fxPopup = s.fxFreeze = s.fxCamera = true;   // the page as a new player sees it
                        s.autoCaptureEnabled = true;
                        s.pxPerCell = PhotoCapture.DefaultPxPerCell;
                        SteamCleanScreen();
                        Find.WindowStack.Add(new Dialog_ModSettings(LoadedModManager.GetMod<MomentFramesMod>()));
                        Wait(1f);
                        steamStep = 1;
                        return false;
                    }
                case 1:
                    ShotsSettingsPage().SetExpanded(true);
                    ShotsSettingsPage().SelectCategory("storage");
                    ShotsSettingsPage().SetScroll(150f);
                    Wait(3f);   // the folder count runs on a worker
                    steamStep = 2;
                    return false;
                case 2:
                    SteamCleanScreen();
                    Shot(Path.Combine("steam", "settings-storage.png"));
                    Wait(1f);
                    steamStep = 3;
                    return false;
                case 3:
                    ShotsSettingsPage().SelectCategory("capture");
                    ShotsSettingsPage().SetScroll(0f);
                    Wait(1.2f);
                    steamStep = 4;
                    return false;
                case 4:
                    CloseLog();
                    Shot(Path.Combine("steam", "settings-capture.png"));
                    Wait(1f);
                    steamStep = 5;
                    return false;
                case 5:
                    {
                        Find.WindowStack.TryRemove(typeof(Dialog_ModSettings));
                        MomentFramesMod.Settings.autoCaptureEnabled = false;
                        var who = ShotPawn("Celeste") ?? map.mapPawns.FreeColonistsSpawned.First();
                        Find.Selector.ClearSelection();
                        Find.Selector.Select(who, false);
                        InspectPaneUtility.OpenTab(typeof(ITab_Pawn_Chronicle));
                        Find.CameraDriver.SetRootPosAndSize(who.DrawPos, 14f);
                        Wait(1.5f);
                        steamStep = 6;
                        return false;
                    }
                case 6:
                    CloseLog();
                    Messages.Clear();
                    Shot(Path.Combine("steam", "pawn-chronicle.png"));
                    Wait(1f);
                    steamStep = 7;
                    return false;
                default:
                    Find.Selector.ClearSelection();
                    return true;
            }
        }
    }
}
