using System;
using System.IO;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace MomentFrames
{
    public static partial class Probe
    {
        private static bool reviewFixesMode;
        private static PhotoRecord reviewFixesOriginal;
        private static int reviewFixesStage;

        private static void ReviewFixCheck(bool ok, string message)
        {
            if (!ok) throw new InvalidOperationException("review fixes: " + message);
            checks.Add("PASS: " + message);
        }

        private static void ReviewBirthdayFitness()
        {
            var people = map.mapPawns.FreeColonistsSpawned.Take(3).ToList();
            while (people.Count < 3)
            {
                var p = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, Faction.OfPlayer);
                GenSpawn.Spawn(p, center + new IntVec3(people.Count * 2, 0, 2), map);
                people.Add(p);
            }
            foreach (var p in people) FixReady(p);
            var subject = people[0]; var guest = people[1];
            ReviewFixCheck(Birthday.StartPrepared(subject, 31, center, people, Rot4.North, null), "healthy birthday fixture starts normally");
            var job = Birthday.Active(subject);
            ReviewFixCheck(job != null && !job.Finished, "birthday fixture owns a live registered gathering");
            guest.needs.food.CurLevel = 0f;
            var hungry = BirthdayPreparationPlan.Create(subject, center, people, Rot4.North, job);
            ReviewFixCheck(!hungry.CanStart && hungry.Problems.ContainsKey(guest), "birthday edit rejects an unfit existing guest with a specific row problem");
            ReviewFixCheck(!Birthday.StartPrepared(subject, 31, center, people, Rot4.North, job), "final birthday confirmation rejects the unfit existing guest");
            ReviewFixCheck(job.Members.SequenceEqual(people), "rejected birthday edit leaves the original roster intact");
            var dialog = new Dialog_BirthdaySetup(subject, 31, job, center);
            ReviewFixCheck(!Traverse.Create(dialog).Method("Eligible", guest).GetValue<bool>(), "birthday portrait row excludes a hungry existing guest");
            dialog.PostClose();
            FixReady(guest);
            ReviewFixCheck(BirthdayPreparationPlan.Create(subject, center, people, Rot4.North, job).CanStart,
                "healthy existing gathering members retain permission to edit their own roster");
            ReviewFixCheck(Birthday.StartPrepared(subject, 31, center, people, Rot4.North, job), "healthy birthday roster edit is accepted");
            if (ModsConfig.BiotechActive)
            {
                var baby = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDefOf.Colonist, Faction.OfPlayer,
                    forceGenerateNewPawn: true, fixedBiologicalAge: 1f, fixedChronologicalAge: 1f,
                    allowDowned: true, developmentalStages: DevelopmentalStage.Baby));
                GenSpawn.Spawn(baby, center + new IntVec3(1, 0, 3), map);
                ReviewFixCheck(baby.DevelopmentalStage.Baby(), "guardian regression uses a real Biotech baby");
                ReviewFixCheck(BirthdayCarry.CanCarry(guest, baby, job), "healthy existing gathering guardian can still collect a baby");
                guest.needs.food.CurLevel = 0f;
                ReviewFixCheck(!BirthdayCarry.CanCarry(guest, baby, job), "hungry existing gathering member cannot be selected as baby guardian");
                FixReady(guest);
                baby.Destroy(DestroyMode.Vanish);
            }
            job.Cancel();
            for (int i = 0; i < 20 && map.lordManager.lords.Contains(job.lord); i++) Find.TickManager.DoSingleTick();
            foreach (var p in people) FixReady(p);
            ReviewFixCheck(people.All(p => p.GetLord() == null), "fitness regression releases all fixture colonists");
        }

        private static void ReviewFixesStep()
        {
            switch (reviewFixesStage)
            {
                case 0:
                    FxSet(false); FixSetup();
                    var settings = MomentFramesMod.Settings;
                    settings.msColonyStart = settings.msPersonal = settings.msHistoric = settings.msMechlink = settings.msRareAnimal = false;
                    settings.msEnding = settings.msSky = settings.msDisaster = settings.msJourney = settings.lifeEvents = false;
                    settings.capturePetArrivals = false;
                    settings.birthdayMode = Birthday.ModeOff;
                    settings.captureReactionMode = PhotoReactions.ModeOff;
                    if (!GenCommandLine.TryGetCommandLineArg("momentFramesPhotoEditorView", out _)) ReviewBirthdayFitness();
                    reviewFixesOriginal = PhotoStore.Instance.Request(map, center, Kinds.Test, "review:original", "Review source photograph", "", map.mapPawns.FreeColonistsSpawned, 16f);
                    ReviewFixCheck(reviewFixesOriginal != null, "regression source photo is queued through production capture");
                    reviewFixesStage = 1; Wait(.5f); return;
                case 1:
                    if (!reviewFixesOriginal.HasFile) { Wait(.1f); return; }
                    File.Copy(PhotoStore.Instance.FilePath(reviewFixesOriginal), Path.Combine(dir, "review-source.png"), true);
                    ReviewEditingChecks(reviewFixesOriginal);
                    ReviewEditingStartUi(reviewFixesOriginal);
                    reviewFixesStage = 2; return;
                case 2:
                    if (!reviewEditingUiDone) { Wait(.1f); return; }
                    if (GenCommandLine.TryGetCommandLineArg("momentFramesPhotoEditorView", out _))
                    {
                        File.WriteAllText(Path.Combine(dir, "photo-editor-view-result.json"), "{\"ok\":true,\"checks\":[" +
                            string.Join(",", checks.Select(Json)) +
                            "],\"limits\":\"Disposable Core/Harmony; editor input, crop and PNG fixtures, not user saves or full modpack.\"}");
                        active = false; GenScene.GoToMainMenu(); Application.Quit(); return;
                    }
                    if (!ReviewDisplaysStep(reviewFixesOriginal)) return;
                    reviewFixesStage = 3; return;
                case 3:
                    if (!ReviewWorldFixesStep()) return;
                    reviewFixesStage = 4; return;
                case 4:
                    File.WriteAllText(Path.Combine(dir, "review-fixes-result.json"), "{\"ok\":true,\"checks\":[" +
                        string.Join(",", checks.Select(Json)) +
                        "],\"limits\":\"Disposable Core/Harmony and optional official DLC; controlled boundary fixtures, not user saves or full modpack.\"}");
                    active = false; GenScene.GoToMainMenu(); Application.Quit(); return;
            }
        }
    }
}
