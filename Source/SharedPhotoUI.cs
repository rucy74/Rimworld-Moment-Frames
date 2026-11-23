using RimWorld;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    internal static class SharedPhotoUI
    {
        internal static float ControlHeight => Dialog_BirthdaySetup.ControlHeight;
        internal static void Label(Rect r, string text, bool muted = false)
        {
            Color old = GUI.color;
            GUI.color = muted ? AlbumStyle.Theme.MutedInk : AlbumStyle.Theme.Ink;
            Text.Font = GameFont.Small; Text.Anchor = TextAnchor.UpperLeft; Text.WordWrap = true;
            AlbumStyle.BodyLabel(r, AlbumPresentation.Title(text)); GUI.color = old;
        }
        internal static void Heading(Rect r, string text)
        {
            Color old = GUI.color;
            GUI.color = AlbumStyle.Theme.Ink; Text.Font = GameFont.Medium; Text.Anchor = TextAnchor.UpperLeft;
            AlbumStyle.BodyLabel(r, text); Text.Font = GameFont.Small; GUI.color = old;
        }
        internal static bool Button(Rect r, string text, bool active = true)
        {
            bool clicked = AlbumStyle.Button(r, "", active);
            TextAnchor anchor = Text.Anchor; bool wrap = Text.WordWrap; Color old = GUI.color;
            Text.Anchor = TextAnchor.MiddleCenter; Text.WordWrap = true;
            GUI.color = active ? AlbumStyle.Theme.Ink : AlbumStyle.Theme.MutedInk;
            GUI.Label(r.ContractedBy(6f), text, AlbumStyle.StyleFor(text, GameFont.Small));
            Text.Anchor = anchor; Text.WordWrap = wrap; GUI.color = old;
            return clicked;
        }
        internal static bool QuietButton(Rect r, string label, bool active = true)
        {
            if (active && Mouse.IsOver(r)) { var c = AlbumStyle.Theme.Accent; c.a = .1f; Widgets.DrawBoxSolid(r, c); }
            Color old = GUI.color;
            GUI.color = active ? AlbumStyle.Theme.Ink : AlbumStyle.Theme.MutedInk;
            Text.Font = GameFont.Small; Text.Anchor = TextAnchor.MiddleLeft; Text.WordWrap = false;
            AlbumStyle.HeaderLabel(r.ContractedBy(4f), label);
            if(active)
            {
                var ink=AlbumStyle.Theme.Accent;ink.a=.65f;
                float width=Mathf.Min(r.width-8f,AlbumStyle.StyleFor(label,GameFont.Small).CalcSize(new GUIContent(label)).x);
                Widgets.DrawBoxSolid(new Rect(r.x+4f,r.center.y+SharedPhotoUI.ControlHeight*.28f,width,1f),ink);
            }
            GUI.color = old; Text.Anchor = TextAnchor.UpperLeft; Text.WordWrap = true;
            return active && Widgets.ButtonInvisible(r);
        }
        internal static void Check(Rect r, bool on, bool active = true)
        {
            Color old = GUI.color; GUI.color = active ? AlbumStyle.Theme.Ink : AlbumStyle.Theme.MutedInk;
            Widgets.DrawBox(r, 1); GUI.color = old;
            if (on)
            {
                var ink = active ? AlbumStyle.Theme.Ink : AlbumStyle.Theme.MutedInk;
                Widgets.DrawLine(new Vector2(r.x + 4f, r.y + 10f), new Vector2(r.x + 8f, r.y + 15f), ink, 2f);
                Widgets.DrawLine(new Vector2(r.x + 8f, r.y + 15f), new Vector2(r.x + 17f, r.y + 5f), ink, 2f);
            }
        }
        internal static void Portrait(Pawn p, Rect r)
        {
            if (p != null) { GUI.DrawTexture(r, Dialog_BirthdaySetup.PortraitFor(p)); return; }
            var ink = AlbumStyle.Theme.MutedInk; ink.a = .4f;
            // A neutral placeholder: never invent the appearance of a person no longer in the game.
            Widgets.DrawBoxSolid(new Rect(r.x + r.width * .37f, r.y + r.height * .18f, r.width * .26f, r.height * .23f), ink);
            Widgets.DrawBoxSolid(new Rect(r.x + r.width * .25f, r.y + r.height * .46f, r.width * .5f, r.height * .38f), ink);
        }
        /// <summary>A preparation window leaves the map usable. Only while a name is being typed does the camera stay still, or W, A, S and D would move it.</summary>
        internal static void KeepCameraFree(Window window) => window.preventCameraMotion = GUIUtility.keyboardControl != 0;
        internal static float ReactionHeight => ControlHeight * 2f + 16f;
        internal static void ReactionControls(Rect area, ref int mode, ref bool remember)
        {
            float half=(area.width-8f)/2f;
            bool enabled=mode!=PhotoReactions.ModeOff, save=mode==PhotoReactions.ModePhoto;
            if(Toggle(new Rect(area.x,area.y,half,ControlHeight),"MF_UX_ReactionsOn".Translate(),enabled))
                mode=enabled?PhotoReactions.ModeOff:PhotoReactions.ModePhoto;
            if(Toggle(new Rect(area.x+half+8f,area.y,half,ControlHeight),"MF_UX_ReactionsSave".Translate(),save,enabled))
                mode=save?PhotoReactions.ModeAfter:PhotoReactions.ModePhoto;
            if(Toggle(new Rect(area.x,area.y+ControlHeight+6f,area.width,ControlHeight),"MF_UX_RememberMode".Translate(),remember)) remember=!remember;
            TooltipHandler.TipRegion(area,"MF_UX_ReactionsHelp".Translate());
        }
        internal static bool Toggle(Rect rect,string text,bool on,bool active=true)
        {
            Check(new Rect(rect.x+6f,rect.center.y-10f,20f,20f),on,active);
            Label(new Rect(rect.x+34f,rect.y+4f,rect.width-40f,rect.height-8f),text,!active);
            return active && Widgets.ButtonInvisible(rect);
        }
        internal static float FrameChoiceHeight(bool saved) => (ControlHeight+8f)*(saved?2f:1f);
        internal static void FrameChoice(Rect area,bool saved,ref bool useSaved,ref bool focus)
        {
            float y=area.y;
            if(saved)
            {
                SavedFrameRow(new Rect(area.x,y,area.width,ControlHeight),ref useSaved);
                y+=ControlHeight+8f;
            }
            focus=FramingControls(new Rect(area.x,y,area.width,ControlHeight),focus,saved&&useSaved);
        }
        private static void SavedFrameRow(Rect row,ref bool useSaved)
        {
            float half=(row.width-8f)/2f;
            var left=new Rect(row.x,row.y,half,row.height);var right=new Rect(left.xMax+8f,row.y,half,row.height);
            if(Button(left,"MF_UX_SavedFrame".Translate()))useSaved=true;
            if(Button(right,"MF_UX_AutoFrame".Translate()))useSaved=false;
            var chosen=useSaved?left:right;
            Widgets.DrawBoxSolid(new Rect(chosen.x+2,chosen.yMax-4,chosen.width-4,3),AlbumStyle.Theme.Accent);
            TooltipHandler.TipRegion(left,"MF_UX_FramePreserved".Translate());
        }
        // A studio frames itself and faces by its own rotation: only a framing saved on the spot is a real choice there.
        internal static float StudioFrameHeight(bool saved,bool useSaved,float width) => (saved?ControlHeight+8f:0f)
            +(saved&&useSaved?0f:AlbumStyle.TextHeight("MF_StudioAutoHelp".Translate(),width)+8f);
        internal static void StudioFrameChoice(Rect area,bool saved,ref bool useSaved)
        {
            float y=area.y;
            if(saved)
            {
                SavedFrameRow(new Rect(area.x,y,area.width,ControlHeight),ref useSaved);
                y+=ControlHeight+8f;
            }
            if(saved&&useSaved) return;
            string help="MF_StudioAutoHelp".Translate();
            Label(new Rect(area.x,y,area.width,AlbumStyle.TextHeight(help,area.width)+4f),help,true);
        }
        internal static void RememberReaction(int mode, bool remember)
        {
            if(!remember || MomentFramesMod.Settings==null) return;
            MomentFramesMod.Settings.preparationReactionMode=Mathf.Clamp(mode,0,2);
            try { LoadedModManager.GetMod<MomentFramesMod>()?.WriteSettings(); }
            catch(System.Exception e) { Log.Warning("[MomentFrames] could not save the remembered photo reaction: "+e.Message); }
        }
        internal static string SearchField(Rect area, string query, int totalSelected, int hiddenSelected)
        {
            var field=new Rect(area.x,area.y,area.width-ControlHeight-6f,ControlHeight);
            string value=AlbumBridgeUI.TextField(field,query ?? "","MF_PreparationNameSearch");
            TooltipHandler.TipRegion(area,"MF_PreparationSearchHelp".Translate(totalSelected,hiddenSelected));
            if(value.NullOrEmpty())
            {
                var placeholder=field.ContractedBy(8f);
                Color old=GUI.color;
                GUI.color=AlbumBridgeUI.PlaceholderInk;
                Text.Font=GameFont.Small;Text.Anchor=TextAnchor.MiddleLeft;Text.WordWrap=false;
                AlbumStyle.BodyLabel(placeholder,"MF_PreparationNameSearch".Translate());
                GUI.color=old;Text.Anchor=TextAnchor.UpperLeft;Text.WordWrap=true;
            }
            if(Button(new Rect(field.xMax+6f,field.y,ControlHeight,ControlHeight),"×")) value="";
            if((query ?? "").NullOrEmpty()) TooltipHandler.TipRegion(field,"MF_PreparationNameSearch".Translate());
            return value;
        }
        internal static float SearchHeight => ControlHeight+12f;
        internal static bool FramingControls(Rect area, bool peopleFocus, bool savedFrame = false)
        {
            float half=(area.width-8f)/2f;
            var left=new Rect(area.x,area.y,half,ControlHeight);var right=new Rect(left.xMax+8f,area.y,half,ControlHeight);
            if(Button(left,"MF_PreparationPeopleFocus".Translate(),!savedFrame)) peopleFocus=true;
            if(Button(right,"MF_PreparationScenery".Translate(),!savedFrame)) peopleFocus=false;
            var chosen=peopleFocus?left:right;
            if(!savedFrame) Widgets.DrawBoxSolid(new Rect(chosen.x+2f,chosen.yMax-4f,chosen.width-4f,3f),AlbumStyle.Theme.Accent);
            TooltipHandler.TipRegion(area,(savedFrame?"MF_CaptureSavedFrameHelp":"MF_PreparationFramingHelp").Translate());
            return peopleFocus;
        }
        internal static float FramingHeight(float width,bool savedFrame) => ControlHeight+8f+(savedFrame?AlbumStyle.TextHeight("MF_CaptureSavedFrame".Translate(),width)+6f:0f);
        internal static void DrawDragHint(bool dragging)
        {
            string hint=(dragging?"MF_CaptureSpotDragRelease":"MF_CaptureSpotDragHint").Translate();
            var oldFont=Text.Font;var oldAnchor=Text.Anchor;var oldColor=GUI.color;bool oldWrap=Text.WordWrap;
            try
            {
                Text.Font=GameFont.Small;Text.Anchor=TextAnchor.UpperLeft;Text.WordWrap=true;
                float width=Mathf.Min(430f,UI.screenWidth-32f),height=Mathf.Max(42f,Text.CalcHeight(hint,width-18f)+16f);
                var mouse=Event.current.mousePosition;
                var box=new Rect(Mathf.Clamp(mouse.x+24f,8f,UI.screenWidth-width-8f),Mathf.Clamp(mouse.y+34f,8f,UI.screenHeight-height-8f),width,height);
                Widgets.DrawBoxSolid(box,new Color(.12f,.13f,.11f,.92f));GUI.color=Color.white;Widgets.Label(box.ContractedBy(8f),hint);
            }
            finally {Text.Font=oldFont;Text.Anchor=oldAnchor;GUI.color=oldColor;Text.WordWrap=oldWrap;}
        }
    }
}
