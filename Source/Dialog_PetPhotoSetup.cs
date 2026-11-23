using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    /// <summary>Exactly one animal and one colonist, using the album's paper, portraits and text settings.</summary>
    public sealed class Dialog_PetPhotoSetup : Window
    {
        private readonly Building_PhotoSpot spot;
        private Vector2 animalScroll, ownerScroll;
        private bool ownerChosen, followAnimal = true, followOwner = true;
        internal Pawn SelectedPet { get; private set; }
        internal Pawn SelectedOwner { get; private set; }
        internal Rect AnimalListRect { get; private set; }
        internal Rect OwnerListRect { get; private set; }
        protected override float Margin => 0f;
        public override Vector2 InitialSize => new Vector2(Mathf.Max(1f, Mathf.Min(860f, UI.screenWidth - 40f)),
            Mathf.Max(1f, Mathf.Min(760f, UI.screenHeight - 40f)));

        public Dialog_PetPhotoSetup(Building_PhotoSpot spot, Pawn animal = null)
        {
            this.spot = spot;
            doCloseX = false;
            doCloseButton = false;
            doWindowBackground = false;
            closeOnClickedOutside = false;
            closeOnAccept = false;
            forcePause = false;
            if (spot?.Map == null) return;
            var animals = PetPhotos.Animals(spot.Map).ToList();
            SelectedPet = animal != null && animals.Contains(animal) ? animal
                : animals.FirstOrDefault(p => PetPhotos.EligibleAnimal(p)) ?? animals.FirstOrDefault();
            SelectedOwner = SelectedPet == null ? spot.Map.mapPawns.FreeColonistsSpawned.FirstOrDefault(GroupPhoto.CanJoin)
                : PetPhotos.DefaultOwner(SelectedPet);
        }

        internal bool SelectAnimal(Pawn animal)
        {
            if (spot?.Map == null || animal == null || animal.Map != spot.Map || !PetPhotos.EligibleAnimal(animal)) return false;
            if (SelectedPet == animal) return false;
            SelectedPet = animal;
            if (!ownerChosen || !GroupPhoto.CanJoin(SelectedOwner))
            {
                SelectedOwner = PetPhotos.DefaultOwner(animal);
                ownerChosen = false;
                followOwner = true;
            }
            return true;
        }

        internal bool SelectOwner(Pawn owner)
        {
            if (spot?.Map == null || owner == null || owner.Map != spot.Map || !GroupPhoto.CanJoin(owner)) return false;
            if (SelectedOwner == owner) { ownerChosen = true; return false; }
            SelectedOwner = owner;
            ownerChosen = true;
            return true;
        }

        internal bool StartSelected() => PetPhotos.Start(spot, SelectedOwner, SelectedPet) != null;

        private static string Problem(Pawn pawn, bool animal)
        {
            bool available = animal ? PetPhotos.EligibleAnimal(pawn) : GroupPhoto.CanJoin(pawn);
            if (available) return "";
            string why = animal ? PetPhotos.WhyNotAnimal(pawn) : GroupPhoto.WhyNot(pawn);
            return why.NullOrEmpty() ? "MF_Pet_Unavailable".Translate().ToString() : why;
        }

        private static float RowHeight(Pawn pawn, float width, bool animal)
        {
            float textWidth = Mathf.Max(1f, width - 124f);
            string why = Problem(pawn, animal);
            return Mathf.Max(80f, AlbumStyle.TextHeight(pawn.LabelShort, textWidth)
                + (why.NullOrEmpty() ? 0f : AlbumStyle.TextHeight(why, textWidth) + 4f) + 18f);
        }

        private void DrawRoster(Rect area, List<Pawn> pawns, bool animal, ref Vector2 scroll, ref bool follow)
        {
            string heading = (animal ? "MF_PetSetupAnimal" : "MF_PetSetupOwner").Translate();
            float headingHeight = Mathf.Max(SharedPhotoUI.ControlHeight, AlbumStyle.TextHeight(heading, area.width, GameFont.Medium));
            SharedPhotoUI.Heading(new Rect(area.x, area.y, area.width, headingHeight), heading);
            Rect viewport = new Rect(area.x, area.y + headingHeight + 6f, area.width,
                Mathf.Max(1f, area.height - headingHeight - 6f));
            if (animal) AnimalListRect = viewport; else OwnerListRect = viewport;
            if (pawns.Count == 0)
            {
                string empty = (animal ? "MF_Pet_NoAnimals" : "MF_Pet_NoOwners").Translate();
                SharedPhotoUI.Label(new Rect(viewport.x + 8f, viewport.y + 12f, viewport.width - 16f,
                    AlbumStyle.TextHeight(empty, viewport.width - 16f)), empty, true);
                return;
            }
            float width = Mathf.Max(1f, viewport.width - 18f);
            var heights = pawns.Select(p => RowHeight(p, width, animal)).ToList();
            float total = heights.Sum() + (pawns.Count - 1) * 6f;
            var selected = animal ? SelectedPet : SelectedOwner;
            if (follow)
            {
                float selectedY = 0f;
                for (int i = 0; i < pawns.Count && pawns[i] != selected; i++) selectedY += heights[i] + 6f;
                scroll.y = Mathf.Clamp(selectedY, 0f, Mathf.Max(0f, total - viewport.height));
                follow = false;
            }
            Widgets.BeginScrollView(viewport, ref scroll, new Rect(0f, 0f, width, Mathf.Max(viewport.height, total)));
            float y = 0f;
            for (int i = 0; i < pawns.Count; i++)
            {
                var pawn = pawns[i];
                Rect row = new Rect(0f, y, width, heights[i]);
                y += heights[i] + 6f;
                if (row.yMax < scroll.y || row.y > scroll.y + viewport.height) continue;
                string why = Problem(pawn, animal);
                bool available = why.NullOrEmpty(), chosen = selected == pawn;
                var tint = AlbumStyle.Theme.Accent;
                tint.a = chosen ? .12f : available && Mouse.IsOver(row) ? .07f : 0f;
                Widgets.DrawBoxSolid(row, tint);
                SharedPhotoUI.Check(new Rect(row.x + 10f, row.center.y - 10f, 20f, 20f), chosen, available);
                Rect portrait = new Rect(row.x + 40f, row.center.y - 32f, 64f, 64f);
                if (animal)
                    GUI.DrawTexture(portrait, PortraitsCache.Get(pawn, new Vector2(64f, 64f), Rot4.South, Vector3.zero, 1f));
                else SharedPhotoUI.Portrait(pawn, portrait);
                float textWidth = Mathf.Max(1f, row.width - 124f);
                float nameHeight = AlbumStyle.TextHeight(pawn.LabelShort, textWidth);
                float whyHeight = why.NullOrEmpty() ? 0f : AlbumStyle.TextHeight(why, textWidth) + 4f;
                float textY = row.y + (row.height - nameHeight - whyHeight) / 2f;
                SharedPhotoUI.Label(new Rect(row.x + 112f, textY, textWidth, nameHeight), pawn.LabelShort, !available);
                if (!why.NullOrEmpty())
                    SharedPhotoUI.Label(new Rect(row.x + 112f, textY + nameHeight + 4f, textWidth, whyHeight - 4f), why, true);
                if (available && Widgets.ButtonInvisible(row))
                {
                    if (animal) SelectAnimal(pawn); else SelectOwner(pawn);
                }
                TooltipHandler.TipRegion(row, pawn.LabelShort + (why.NullOrEmpty() ? "" : "\n" + why));
            }
            Widgets.EndScrollView();
        }

        public override void DoWindowContents(Rect inRect)
        {
            SharedPhotoUI.KeepCameraFree(this);
            using (AlbumStyle.Begin())
            {
                AlbumStyle.DrawPaper(inRect);
                if (spot == null || !spot.Spawned || spot.Destroyed) { Close(); return; }
                Rect page = inRect.ContractedBy(inRect.width < 600f ? 16f : 24f);
                float control = SharedPhotoUI.ControlHeight;
                string title = "MF_PetSetupTitle".Translate();
                float titleHeight = Mathf.Max(control, AlbumStyle.TextHeight(title, page.width - control - 12f, GameFont.Medium));
                AlbumStyle.DrawWash(new Rect(page.x - 4f, page.y + titleHeight * .5f, page.width - control, titleHeight * .5f));
                SharedPhotoUI.Heading(new Rect(page.x, page.y, page.width - control - 12f, titleHeight), title);
                if (SharedPhotoUI.Button(new Rect(page.xMax - control, page.y, control, control), "×")) { Close(); return; }
                string help = "MF_PetSetupHelp".Translate();
                float helpY = page.y + titleHeight + 8f, helpHeight = AlbumStyle.TextHeight(help, page.width);
                SharedPhotoUI.Label(new Rect(page.x, helpY, page.width, helpHeight), help, true);
                bool canStart = PetPhotos.CanStart(spot, SelectedOwner, SelectedPet, out string why);
                string status = canStart ? "MF_Pet_WaitAll".Translate().ToString()
                    : why.NullOrEmpty() ? "MF_Pet_Unavailable".Translate().ToString() : why;
                float statusHeight = AlbumStyle.TextHeight(status, page.width) + 6f;
                float half = (page.width - 10f) / 2f;
                string cancel = "MF_PetSetupCancel".Translate(), start = "MF_PetSetupStart".Translate();
                float buttonHeight = Mathf.Max(control, AlbumStyle.TextHeight(cancel, half - 12f) + 12f,
                    AlbumStyle.TextHeight(start, half - 12f) + 12f);
                float footerY = page.yMax - statusHeight - buttonHeight - 12f;
                Rect lists = new Rect(page.x, helpY + helpHeight + 14f, page.width,
                    Mathf.Max(1f, footerY - helpY - helpHeight - 26f));
                var animals = PetPhotos.Animals(spot.Map).Concat(new[] { SelectedPet }).Where(p => p != null)
                    .Distinct().OrderBy(p => p.LabelShort).ToList();
                var owners = spot.Map.mapPawns.FreeColonistsSpawned.Concat(new[] { SelectedOwner }).Where(p => p != null)
                    .Distinct().OrderBy(p => p.LabelShort).ToList();
                Rect animalArea, ownerArea;
                if (lists.width >= 660f)
                {
                    float column = (lists.width - 20f) / 2f;
                    animalArea = new Rect(lists.x, lists.y, column, lists.height);
                    ownerArea = new Rect(lists.x + column + 20f, lists.y, column, lists.height);
                }
                else
                {
                    float section = Mathf.Max(1f, (lists.height - 16f) / 2f);
                    animalArea = new Rect(lists.x, lists.y, lists.width, section);
                    ownerArea = new Rect(lists.x, lists.y + section + 16f, lists.width, section);
                }
                DrawRoster(animalArea, animals, true, ref animalScroll, ref followAnimal);
                DrawRoster(ownerArea, owners, false, ref ownerScroll, ref followOwner);
                SharedPhotoUI.Label(new Rect(page.x, footerY, page.width, statusHeight), status, canStart);
                if (SharedPhotoUI.Button(new Rect(page.x, page.yMax - buttonHeight, half, buttonHeight), cancel)) { Close(); return; }
                if (SharedPhotoUI.Button(new Rect(page.x + half + 10f, page.yMax - buttonHeight, half, buttonHeight), start, canStart)
                    && StartSelected()) Close();
            }
        }
    }
}
