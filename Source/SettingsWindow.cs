using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    /// <summary>The settings page uses album paper, but keeps controls in the game's readable font.</summary>
    internal sealed class SettingsWindow
    {
        private string category = "capture", search = "", preview;
        private readonly Dictionary<string, bool> expanded = new Dictionary<string, bool>();
        private readonly Dictionary<string, Vector2> positions = new Dictionary<string, Vector2>();
        private Dictionary<string, object> undo;
        private string previewPhotoId, previewPhotoPath;
        private Texture2D previewPhoto;
        private Vector2 searchScroll;
        private float requestedScroll = -1;
        internal readonly HashSet<string> DrawnSettings = new HashSet<string>();
        private MomentFramesSettings settings;

        internal void SelectCategory(string id) { category = id; preview = null; search = ""; }
        internal void SetSearch(string value) { search = value ?? ""; preview = null; searchScroll = Vector2.zero; }
        internal void SetPreview(string value) { preview = SettingsCatalog.Entries.FirstOrDefault(e => e.Category == category && e.Preview == value)?.Id; }
        internal void SetScroll(float y) { requestedScroll = y; }
        internal bool TryCancel()
        {
            if (preview != null) { preview = null; return true; }
            if (search.Length > 0) { SetSearch(""); return true; }
            return false;
        }
        internal void SetExpanded(bool value)
        {
            foreach (var c in SettingsCatalog.Categories)
                foreach (var g in c.Groups) expanded[c.Id + "/" + g.Id] = value;
            if (!value) preview = null;
        }
        private void ExpandCategory(SettingsCatalog.Category c, bool value)
        {
            foreach (var g in c.Groups) expanded[c.Id + "/" + g.Id] = value;
            if (!value) preview = null;
        }
        private bool Open(SettingsCatalog.Category c, SettingsCatalog.Group g)
        {
            string id = c.Id + "/" + g.Id;
            return expanded.TryGetValue(id, out bool value) ? value : c.Id != "capture" && c.Groups[0] == g;
        }
        private static string Tr(string key) => key.Translate();
        private static Color Ink => AlbumStyle.Theme.Ink;
        private static Color Muted => AlbumStyle.Theme.MutedInk;
        private static GUIStyle Style(bool help = false, TextAnchor anchor = TextAnchor.UpperLeft)
        {
            var style = new GUIStyle(Text.fontStyles[(int)GameFont.Small])
                { fontSize = help ? 17 : 20, fontStyle = FontStyle.Normal,
                  alignment = anchor, wordWrap = true, contentOffset = Vector2.zero,
                  padding = new RectOffset(), margin = new RectOffset(),
                  fixedHeight = 0, fixedWidth = 0, clipping = TextClipping.Overflow };
            style.normal.textColor = help ? Muted : Ink;
            return style;
        }
        private static float Height(string text, float width, bool help = false)
            => Mathf.Max(help ? 27 : 30, Style(help).CalcHeight(new GUIContent(text ?? ""), Mathf.Max(1, width)) + 8);
        private static void Label(Rect r, string text, bool help = false, TextAnchor anchor = TextAnchor.UpperLeft)
        {
            if (anchor == TextAnchor.UpperLeft) { r.y += 4; r.height -= 4; }
            Color previous = GUI.color;
            try
            {
                // Waylith's GUI.Label uses the content tint rather than GUIStyle.normal.textColor.
                if (RuntimeAudio.Waylith) GUI.color = help ? Muted : Ink;
                GUI.Label(r, text ?? "", Style(help, anchor));
            }
            finally { GUI.color = previous; }
        }
        private static void Line(Rect r, Color color) => Widgets.DrawBoxSolid(new Rect(r.x, r.yMax - 1, r.width, 1), color);
        private static bool Button(Rect r, string text, bool active = true, bool selected = false)
        {
            Color fill = Color.Lerp(AlbumStyle.Theme.Paper, AlbumStyle.Theme.Accent, selected ? .3f : .09f);
            if (active && Mouse.IsOver(r)) fill = Color.Lerp(fill, AlbumStyle.Theme.Accent, .15f);
            Widgets.DrawBoxSolid(r, fill);
            var edge = Ink; edge.a = selected ? .65f : .2f; Line(r, edge);
            bool enabled = GUI.enabled;
            GUI.enabled = enabled && active;
            Label(r.ContractedBy(5), text, !active, TextAnchor.MiddleCenter);
            bool clicked = GUI.enabled && Widgets.ButtonInvisible(r);
            GUI.enabled = enabled;
            return clicked;
        }
        private static string Entry(Rect r, string value, string name)
        {
            Widgets.DrawBoxSolid(r, AlbumStyle.Theme.Mat);
            var edge = Ink; edge.a = .2f; Line(r, edge);
            var style = new GUIStyle(Text.textFieldStyles[(int)GameFont.Small])
                { fontSize = 20, fontStyle = FontStyle.Normal, contentOffset = Vector2.zero,
                  padding = new RectOffset(8,8,8,8), margin = new RectOffset(),
                  fixedHeight = 0, fixedWidth = 0, clipping = TextClipping.Overflow };
            foreach (var state in new[] { style.normal, style.hover, style.active, style.focused })
            { state.background = null; state.textColor = Ink; }
            GUI.SetNextControlName(name);
            return GUI.TextField(r, value ?? "", style);
        }

        internal void Draw(Rect rect, MomentFramesSettings s)
        {
            if (s == null) return;
            settings = s;
            var color = GUI.color; var font = Text.Font; var anchor = Text.Anchor; bool wrap = Text.WordWrap, enabled = GUI.enabled;
            try
            {
                GUI.color = Color.white; Text.Font = GameFont.Small; Text.Anchor = TextAnchor.UpperLeft; Text.WordWrap = true;
                AlbumStyle.DrawPaper(rect);
                Rect inner = rect.ContractedBy(18);
                // Sketch headings are confined to this region; album font size never shrinks the setting controls.
                using (AlbumStyle.Begin())
                {
                    Text.Font = GameFont.Medium; GUI.color = Ink;
                    var titleStyle = AlbumStyle.StyleFor(Tr("MF_Settings_Title"), GameFont.Medium);
                    titleStyle.contentOffset = Vector2.zero; titleStyle.padding = new RectOffset();
                    titleStyle.fixedHeight = titleStyle.fixedWidth = 0; titleStyle.clipping = TextClipping.Overflow;
                    GUI.Label(new Rect(inner.x, inner.y + 4, inner.width-170, 46), Tr("MF_Settings_Title"), titleStyle);
                }
                GUI.color = Color.white;
                if(Button(new Rect(inner.xMax-160,inner.y,160,44),Tr("MF_Learn_Title")))PhotoGuide.Open();
                Rect sr = new Rect(inner.x, inner.y + 56, inner.width, 44);
                string next = Entry(new Rect(sr.x, sr.y, sr.width - 50, sr.height), search, "MFSettingsSearch");
                if (next != search) SetSearch(next);
                if (search.Length == 0 && GUI.GetNameOfFocusedControl() != "MFSettingsSearch")
                    Label(sr.ContractedBy(8), Tr("MF_Settings_Search"), true);
                if (Button(new Rect(sr.xMax - 44, sr.y, 44, 44), "×", search.Length > 0)) SetSearch("");
                if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Escape)
                {
                    if (TryCancel()) Event.current.Use();
                }
                Rect body = new Rect(inner.x, sr.yMax + 14, inner.width, Mathf.Max(20, inner.yMax - sr.yMax - 14));
                var current = SettingsCatalog.Categories.FirstOrDefault(c => c.Id == category) ?? SettingsCatalog.Categories[0];
                if (search.Length == 0)
                {
                    if (inner.width >= 650)
                    {
                        float nav = 176;
                        float navY = body.y;
                        for (int i = 0; i < SettingsCatalog.Categories.Length; i++)
                        {
                            var c = SettingsCatalog.Categories[i];
                            float navH = Mathf.Max(50,Height(c.Label,nav-10)+14);
                            if (Button(new Rect(body.x, navY, nav, navH), c.Label, selected: c.Id == category)) SelectCategory(c.Id);
                            navY += navH + 6;
                        }
                        body.x += nav + 18; body.width -= nav + 18;
                    }
                    else
                    {
                        if (Button(new Rect(body.x, body.y, body.width, 44), current.Label + "  ▾"))
                            Find.WindowStack.Add(new FloatMenu(SettingsCatalog.Categories.Select(c => new FloatMenuOption(c.Label, () => SelectCategory(c.Id))).ToList()));
                        body.y += 54; body.height -= 54;
                    }
                }
                // Read the category again after a navigation click in this very event.
                current = SettingsCatalog.Categories.First(c => c.Id == category);
                var matches = SettingsCatalog.Entries.Where(e => Match(e)).ToArray();
                bool searching = search.Length > 0;
                string heading = searching ? "MF_Settings_SearchResults".Translate(matches.Length).ToString() : current.Label;
                float headingH = Height(heading,body.width);
                Label(new Rect(body.x, body.y, body.width, headingH), heading);
                body.y += headingH + 6; body.height -= headingH + 6;
                if (!searching)
                {
                    float hintH = Height(current.Hint, body.width, true);
                    Label(new Rect(body.x, body.y, body.width, hintH), current.Hint, true);
                    body.y += hintH + 10; body.height -= hintH + 10;
                    float half = (body.width - 8) / 2;
                    float toolsH = Mathf.Max(44,Mathf.Max(Height(Tr("MF_Settings_ExpandAll"),half-10),Height(Tr("MF_Settings_CollapseAll"),half-10))+10);
                    if (Button(new Rect(body.x, body.y, half, toolsH), Tr("MF_Settings_ExpandAll"))) ExpandCategory(current,true);
                    if (Button(new Rect(body.x + half + 8, body.y, half, toolsH), Tr("MF_Settings_CollapseAll"))) ExpandCategory(current,false);
                    body.y += toolsH + 10; body.height -= toolsH + 10;
                }
                body.height = Mathf.Max(10, body.height);
                float width = body.width - 20;
                float contentHeight = Content(current, matches, width, false);
                var view = new Rect(0, 0, width, Mathf.Max(body.height, contentHeight + 12));
                Vector2 scroll = searching ? searchScroll : positions.TryGetValue(category, out var p) ? p : Vector2.zero;
                if (requestedScroll >= 0) { scroll.y = requestedScroll; requestedScroll = -1; }
                scroll.y = Mathf.Clamp(scroll.y, 0, Mathf.Max(0, view.height - body.height));
                Widgets.BeginScrollView(body, ref scroll, view);
                try { Content(current, matches, width, true); }
                finally { Widgets.EndScrollView(); }
                if (searching) searchScroll = scroll; else positions[category] = scroll;
            }
            finally { GUI.color = color; Text.Font = font; Text.Anchor = anchor; Text.WordWrap = wrap; GUI.enabled = enabled; }
        }
        private bool Match(SettingsCatalog.Setting e)
        {
            if (string.IsNullOrWhiteSpace(search)) return true;
            var c = SettingsCatalog.Categories.First(x => x.Id == e.Category);
            var g = c.Groups.First(x => x.Id == e.Group);
            return MatchText(e.Label + " " + e.Help + " " + c.Label + " " + g.Label);
        }
        private bool MatchText(string text) => search.Split((char[])null,StringSplitOptions.RemoveEmptyEntries)
            .All(word => text.IndexOf(word,StringComparison.CurrentCultureIgnoreCase)>=0);
        private float Content(SettingsCatalog.Category c, SettingsCatalog.Setting[] matches, float width, bool draw)
        {
            float y = 0;
            bool searching = search.Length > 0;
            if ((!searching && c.Id == "capture") || (searching && MatchText(Tr("MF_Settings_AutoCapture") + " " + Tr("MF_Settings_AutoCaptureHelp") + " " + Tr("MF_Settings_Presets") + " " + Tr("MF_Settings_ManualOnly") + " " + PresetLabel(0) + " " + PresetLabel(1) + " " + PresetLabel(2))))
                y += CapturePanel(new Rect(0,y,width,0), draw) + 18;
            if (searching)
            {
                foreach (var e in matches)
                {
                    var ec = SettingsCatalog.Categories.First(x => x.Id == e.Category);
                    var eg = ec.Groups.First(x => x.Id == e.Group);
                    float contextH = Height(ec.Label + " / " + eg.Label,width-8,true);
                    if (draw) Label(new Rect(4,y,width-8,contextH), ec.Label + " / " + eg.Label, true);
                    y += contextH + 4;
                    y += Row(new Rect(0,y,width,0), e, draw) + 8;
                }
                if (matches.Length == 0 && y == 0)
                { float h = Height(Tr("MF_Settings_NoResults"),width); if (draw) Label(new Rect(0,y,width,h),Tr("MF_Settings_NoResults")); y+=h; }
                return y;
            }
            bool appearanceColumns = c.Id == "appearance" && width >= 780;
            float appearanceWidth = appearanceColumns ? (width-24)*.46f : width;
            float appearanceHeight = c.Id == "appearance" ? Appearance(new Rect(0,y,appearanceWidth,0),draw)+18 : 0;
            if (!appearanceColumns) y += appearanceHeight;
            foreach (var g in c.Groups)
            {
                bool besidePreview = appearanceColumns && g == c.Groups[0];
                float x = besidePreview ? appearanceWidth+24 : 0;
                float groupWidth = besidePreview ? width-x : width;
                var entries = SettingsCatalog.Entries.Where(e => e.Category == c.Id && e.Group == g.Id).ToArray();
                bool open = Open(c,g);
                string summary = GroupSummary(entries);
                float headerH = Mathf.Max(45, Height((open ? "▾  " : "▸  ") + g.Label, groupWidth - 20) + 18);
                if (draw)
                {
                    Rect head = new Rect(x,y,groupWidth,headerH);
                    var tint = AlbumStyle.Theme.Accent; tint.a = .12f; Widgets.DrawBoxSolid(head,tint);
                    Label(head.ContractedBy(9),(open ? "▾  " : "▸  ") + g.Label);
                    TooltipHandler.TipRegion(head,summary);
                    if (Widgets.ButtonInvisible(head)) { expanded[c.Id+"/"+g.Id]=!open; if(open)preview=null; }
                }
                y += headerH;
                float summaryH = Height(summary,groupWidth-18,true);
                if (draw) Label(new Rect(x+9,y+4,groupWidth-18,summaryH),summary,true);
                y += summaryH + 14;
                if (open) foreach (var e in entries) y += Row(new Rect(x,y,groupWidth,0),e,draw) + 8;
                if (open && c.Id == "storage" && g.Id == "location") y += StoragePanel(new Rect(x,y,groupWidth,0),draw) + 8;
                y += 10;
                if (besidePreview) y = Mathf.Max(y,appearanceHeight);
            }
            return y;
        }
        private string GroupSummary(SettingsCatalog.Setting[] entries)
        {
            var values=entries.Where(e=>e.Kind==SettingsCatalog.SettingKind.Select || e.Kind==SettingsCatalog.SettingKind.Range)
                .Select(e=>e.Label+": "+Value(e)).ToList();
            var toggles=entries.Where(e=>e.Kind==SettingsCatalog.SettingKind.Toggle).ToArray();
            values.AddRange(toggles.Where(e=>(bool)e.Get(settings)).Select(e=>e.Label));
            if(values.Count==0 && toggles.Length>0)values.Add("MF_UX_GroupOff".Translate());
            string summary=string.Join(" / ",values.Take(3));
            if(values.Count>3)summary+=" / "+"MF_UX_MoreValues".Translate(values.Count-3);
            if (entries.Any(e=>e.Category=="capture") && !settings.autoCaptureEnabled) summary = Tr("MF_Settings_StoredChoices") + " · " + summary;
            return summary;
        }
        private float CapturePanel(Rect r, bool draw)
        {
            float h = 58 + Height(Tr("MF_Settings_AutoCaptureHelp"),r.width-18,true);
            if (draw)
            {
                var bg=AlbumStyle.Theme.Accent;bg.a=.08f;Widgets.DrawBoxSolid(new Rect(r.x,r.y,r.width,h),bg);
                SharedPhotoUI.Check(new Rect(r.x+10,r.y+13,20,20),settings.autoCaptureEnabled);
                Label(new Rect(r.x+42,r.y+7,r.width-54,38),Tr(settings.autoCaptureEnabled ? "MF_Settings_AutoCapture" : "MF_Settings_ManualOnly"));
                if (Widgets.ButtonInvisible(new Rect(r.x,r.y,r.width,46))) settings.autoCaptureEnabled=!settings.autoCaptureEnabled;
                Label(new Rect(r.x+9,r.y+51,r.width-18,h-51),Tr("MF_Settings_AutoCaptureHelp"),true);
            }
            float y=r.y+h+14;
            string title=Tr("MF_Settings_Presets") + " · " + PresetLabel(SettingsCapturePolicy.PresetOf(settings));
            float presetTitleH=Height(title,r.width);
            if(draw) Label(new Rect(r.x,y,r.width,presetTitleH),title);
            y+=presetTitleH+6;
            float cardW=(r.width-16)/3;
            bool narrow=r.width<420;
            float cardH=narrow?44:Mathf.Max(46,Enumerable.Range(0,3).Max(i=>Height(PresetLabel(i),cardW-10))+14);
            for(int i=0;i<3;i++)
            {
                int stage=i;
                Rect b=narrow?new Rect(r.x,y+i*50,r.width,44):new Rect(r.x+i*(cardW+8),y,cardW,cardH);
                if(draw && Button(b,PresetLabel(i),settings.autoCaptureEnabled,SettingsCapturePolicy.PresetOf(settings)==i))
                {
                    var before=SettingsCapturePolicy.Snapshot(settings);
                    if(SettingsCapturePolicy.ApplyPreset(settings,stage)) undo=before;
                }
            }
            y+=narrow?150:cardH+8;
            string key=SettingsCapturePolicy.PresetOf(settings)==SettingsCapturePolicy.Major?"MF_Settings_PresetImportantHint":SettingsCapturePolicy.PresetOf(settings)==SettingsCapturePolicy.Normal?"MF_Settings_PresetNormalHint":"MF_Settings_PresetRichHint";
            string hint=settings.autoCaptureEnabled?(SettingsCapturePolicy.PresetOf(settings)==SettingsCapturePolicy.Custom?Tr("MF_Settings_PresetHelp"):Tr(key)+"\n"+Tr("MF_Settings_PresetHelp")):Tr("MF_Settings_AutoDisabled");
            float hintH=Height(hint,r.width,true);
            if(draw)Label(new Rect(r.x,y,r.width,hintH),hint,true);
            y+=hintH+8;
            if(undo!=null)
            {
                if(draw && Button(new Rect(r.x,y,r.width,44),Tr("MF_Settings_PresetUndo"),settings.autoCaptureEnabled))
                {SettingsCapturePolicy.Restore(settings,undo);undo=null;}
                y+=52;
            }
            return y-r.y;
        }
        private static string PresetLabel(int stage) => Tr(stage==0?"MF_Settings_PresetImportant":stage==1?"MF_Settings_PresetNormal":stage==2?"MF_Settings_PresetRich":"MF_Settings_PresetCustom");
        private string Value(SettingsCatalog.Setting e)
        {
            object value=e.Get(settings);
            if(e.Kind==SettingsCatalog.SettingKind.Toggle)return Tr((bool)value?"MF_Settings_On":"MF_Settings_Off");
            if(e.Kind==SettingsCatalog.SettingKind.Select)return e.Options.FirstOrDefault(o=>Equals(o.Value,value))?.Label ?? Convert.ToString(value);
            if(e.Kind==SettingsCatalog.SettingKind.Range)
            {
                string n=value is float f ? f.ToString("0.0") : Convert.ToString(value);
                return n + (string.IsNullOrEmpty(e.UnitKey)?"":" "+Tr(e.UnitKey));
            }
            return Convert.ToString(value);
        }
        private float Row(Rect r, SettingsCatalog.Setting e, bool draw)
        {
            float w=r.width-20;
            bool active=e.Enabled(settings) && (e.Category!="capture" || settings.autoCaptureEnabled);
            float titleH=Height(e.Label,w-(e.Kind==SettingsCatalog.SettingKind.Toggle?34:0));
            string help=e.Help;
            if(!active)help+=(help.Length>0?"\n":"")+(e.Category=="capture"&&!settings.autoCaptureEnabled?Tr("MF_Settings_AutoDisabled"):e.DisabledReason);
            float helpH=string.IsNullOrEmpty(help)?0:Height(help,w,true)+5;
            float controlH=e.Kind==SettingsCatalog.SettingKind.Toggle?0:e.Kind==SettingsCatalog.SettingKind.Range?Height(Value(e),w)+30:50;
            float exampleH=string.IsNullOrEmpty(e.Preview)?0:50;
            float previewH=preview==e.Id && !string.IsNullOrEmpty(e.Preview)?PreviewHeight(e.Preview,w):0;
            string sizeNote=e.Id=="pxPerCell"?ResolutionNote():null;
            float noteH=sizeNote==null?0:Height(sizeNote,w,true)+4;
            float h=20+titleH+helpH+controlH+exampleH+previewH+noteH;
            if(!draw)return h;
            DrawnSettings.Add(e.Id);
            Color fill=AlbumStyle.Theme.Mat;fill.a=.7f;Widgets.DrawBoxSolid(new Rect(r.x,r.y,r.width,h),fill);
            bool oldEnabled=GUI.enabled; GUI.enabled=oldEnabled&&active;
            object before=e.Get(settings);
            float y=r.y+10;
            Label(new Rect(r.x+10,y,w-(e.Kind==SettingsCatalog.SettingKind.Toggle?34:0),titleH),e.Label,!active);
            if(e.Kind==SettingsCatalog.SettingKind.Toggle)
            {
                SharedPhotoUI.Check(new Rect(r.xMax-30,y,20,20),(bool)before,active);
                if(active && Widgets.ButtonInvisible(new Rect(r.x,y,r.width,titleH))) e.Set(settings,!(bool)before);
            }
            y+=titleH+5;
            if(helpH>0){Label(new Rect(r.x+10,y,w,helpH-5),help,true);y+=helpH;}
            if(e.Kind==SettingsCatalog.SettingKind.Range)
            {
                float valueH=Height(Value(e),w);
                Label(new Rect(r.x+10,y,w,valueH),Value(e),!active);y+=valueH+6;
                float value=Convert.ToSingle(before);
                bool input=Event.current.type==EventType.MouseDown || Event.current.type==EventType.MouseDrag || Event.current.type==EventType.MouseUp || Event.current.type==EventType.KeyDown;
                float next=Widgets.HorizontalSlider(new Rect(r.x+10,y,w,22),value,e.Min,e.Max);
                if(active && input && Mathf.Abs(next-value)>.0001f)
                { next=Mathf.Clamp(Mathf.Round(next/e.Step)*e.Step,e.Min,e.Max);e.Set(settings,next); }
                y+=24;
                if(sizeNote!=null){Label(new Rect(r.x+10,y,w,noteH-4),sizeNote,true);y+=noteH;}
            }
            else if(e.Kind==SettingsCatalog.SettingKind.Select)
            {
                if(e.InlineOptions)
                {
                    float optionWidth=(w-8*(e.Options.Length-1))/e.Options.Length;
                    for(int i=0;i<e.Options.Length;i++)
                        if(Button(new Rect(r.x+10+i*(optionWidth+8),y,optionWidth,44),e.Options[i].Label,active,Equals(e.Options[i].Value,before)))
                            e.Set(settings,e.Options[i].Value);
                }
                else if(Button(new Rect(r.x+10,y,w,44),Value(e)+"  ▾",active))
                    Find.WindowStack.Add(new FloatMenu(e.Options.Select(o=>new FloatMenuOption(o.Label,()=>{object old=e.Get(settings);e.Set(settings,o.Value);Changed(e,old);})).ToList()));
                y+=50;
            }
            else if(e.Kind==SettingsCatalog.SettingKind.Text)
            {
                string next=Entry(new Rect(r.x+10,y,w,44),Convert.ToString(before),"MFSettings_"+e.Id);
                if(active && !Equals(next,before))e.Set(settings,next);
                y+=50;
            }
            else if(e.Kind==SettingsCatalog.SettingKind.Action)
            {
                if(Button(new Rect(r.x+10,y,w,44),e.Label,active))e.Set(settings,null);
                y+=50;
            }
            GUI.enabled=oldEnabled;
            Changed(e,before);
            if(!string.IsNullOrEmpty(e.Preview))
            {
                if(Button(new Rect(r.x+10,y,w,44),Tr(preview==e.Id?"MF_Settings_ExampleClose":"MF_Settings_Example")))
                { preview=preview==e.Id?null:e.Id; if(preview!=null)requestedScroll=Mathf.Max(0,y-4); }
                y+=50;
                if(previewH>0)DrawPreview(new Rect(r.x+10,y,w,previewH),e.Preview);
            }
            return h;
        }
        /// <summary>The line under the resolution slider: the pixel size a photo gets at this setting and what recent photos here take.</summary>
        private string ResolutionNote()
        {
            StorageUsage.Poll();
            PhotoCapture.ImageSize(settings.spanCells, PhotoCapture.DefaultAspect, out int width, out int height);
            string note = "MF_Storage_Resolution".Translate(width, height);
            var usage = StorageUsage.Last;
            if (usage != null && usage.RecentAverage > 0) note += " " + "MF_Storage_ResolutionAverage".Translate(StorageUsage.Bytes(usage.RecentAverage));
            return note;
        }
        /// <summary>Disk use under the photo folder setting: this colony (with its protected originals) and the whole folder. The count runs on
        /// a worker (StorageUsage), so the window only draws the last result.</summary>
        private float StoragePanel(Rect r, bool draw)
        {
            StorageUsage.Poll();
            var usage = StorageUsage.Last;
            var lines = new List<string>();
            if (usage == null) lines.Add(Tr("MF_Storage_Counting"));
            else
            {
                if (usage.HasColony) lines.Add(usage.ColonyProtected > 0
                    ? "MF_Storage_Colony".Translate(usage.ColonyPhotos, StorageUsage.Bytes(usage.ColonyBytes), StorageUsage.Bytes(usage.ColonyProtected))
                    : "MF_Storage_ColonyPlain".Translate(usage.ColonyPhotos, StorageUsage.Bytes(usage.ColonyBytes)));
                lines.Add("MF_Storage_All".Translate(usage.Colonies, StorageUsage.Bytes(usage.RootBytes)));
            }
            float w = r.width - 20;
            string title = Tr("MF_Storage_Title"), hint = Tr("MF_Storage_Hint");
            float titleH = Height(title, w), hintH = Height(hint, w, true);
            float h = 20 + titleH + 4 + lines.Sum(l => Height(l, w)) + hintH + 50;
            if (!draw) return h;
            Color fill = AlbumStyle.Theme.Mat; fill.a = .7f; Widgets.DrawBoxSolid(new Rect(r.x, r.y, r.width, h), fill);
            float y = r.y + 10;
            Label(new Rect(r.x + 10, y, w, titleH), title); y += titleH + 4;
            foreach (string line in lines) { float lh = Height(line, w); Label(new Rect(r.x + 10, y, w, lh), line); y += lh; }
            Label(new Rect(r.x + 10, y, w, hintH), hint, true); y += hintH;
            float bw = Mathf.Min(220, (w - 10) / 2);
            var store = PhotoStore.Instance;
            string folder = store != null && Current.Game != null ? store.FolderPath : null;
            if (Button(new Rect(r.x + 10, y, bw, 40), Tr("MF_OpenFolder"), folder != null && Directory.Exists(folder)))
                Application.OpenURL("file:///" + folder.Replace('\\', '/'));
            if (Button(new Rect(r.x + 20 + bw, y, bw, 40), Tr("MF_StorageTitle"), folder != null))
                Find.WindowStack.Add(new Dialog_PhotoStorage());
            return h;
        }
        private void Changed(SettingsCatalog.Setting e, object before)
        { if(e.Field!=null && SettingsCapturePolicy.Owns(e.Field) && !Equals(before,e.Get(settings)))undo=null; }
        internal void ReleasePreview()
        {
            if (previewPhotoPath != null) PhotoTextureCache.Release(previewPhotoPath);
            previewPhotoPath = previewPhotoId = null; previewPhoto = null;
        }
        private static GUIStyle CaptionStyle(string text)
        {
            var style=AlbumStyle.StyleFor(text,GameFont.Small);
            style.contentOffset=Vector2.zero;style.padding=new RectOffset();style.margin=new RectOffset();
            style.fixedHeight=style.fixedWidth=0;style.clipping=TextClipping.Overflow;
            style.alignment=TextAnchor.UpperLeft;style.wordWrap=true;style.normal.textColor=Ink;
            return style;
        }
        private float Appearance(Rect r,bool draw)
        {
            string hint=Tr("MF_Settings_StylePreview")+" · "+Tr("MF_Settings_PreviewHint");
            float hintH=Height(hint,r.width,true);
            float cardW=Mathf.Min(600,r.width-30),picH=(cardW-24)*.6786f,captionH;
            using(AlbumStyle.Begin())
                captionH=Mathf.Max(32,CaptionStyle(Tr("MF_Settings_PreviewSample")).CalcHeight(new GUIContent(Tr("MF_Settings_PreviewSample")),cardW-24)+12);
            float h=hintH+picH+captionH+60;
            if(!draw)return h;
            Label(new Rect(r.x,r.y,r.width,hintH),hint,true);
            Rect card=new Rect(r.x+(r.width-cardW)/2,r.y+hintH+20,cardW,picH+captionH+30);
            Matrix4x4 old=GUI.matrix;
            try
            {
                if(settings.albumTilt)GUIUtility.RotateAroundPivot(-2,card.center);
                Widgets.DrawBoxSolid(card,AlbumStyle.Theme.Mat);
                Rect pic=new Rect(card.x+12,card.y+12,card.width-24,picH);
                var store=PhotoStore.Instance;
                var photo=store?.Records.LastOrDefault(p=>p.HasFile);
                string path=photo==null?null:store.FilePath(photo);
                if(photo?.id!=previewPhotoId || path!=previewPhotoPath)
                {
                    ReleasePreview(); previewPhotoId=photo?.id;
                    previewPhoto=path==null?null:PhotoTextureCache.AcquireTexture(path);
                    if(previewPhoto!=null)previewPhotoPath=path;
                }
                var sample=previewPhoto??ContentFinder<Texture2D>.Get("UI/MomentFrames/Settings/ExamplePhoto",false);
                Rect uv=previewPhoto!=null?new Rect(0,0,1,1):WidePhoto;
                if(sample!=null)DrawPhoto(pic,sample,uv,Color.white);
                using(AlbumStyle.Begin())
                {
                    Color previous = GUI.color;
                    try
                    {
                        if(RuntimeAudio.Waylith)GUI.color=Ink;
                        GUI.Label(new Rect(card.x+12,pic.yMax+8,card.width-24,captionH),Tr("MF_Settings_PreviewSample"),CaptionStyle(Tr("MF_Settings_PreviewSample")));
                    }
                    finally {GUI.color=previous;}
                }
                if(sample!=null && Widgets.ButtonInvisible(pic))Find.WindowStack.Add(new PhotoExampleWindow(sample,uv,Color.white,Tr("MF_Settings_StylePreview"),previewPhotoPath));
                TooltipHandler.TipRegion(pic,Tr("MF_Settings_EnlargeExample"));
            }
            finally {GUI.matrix=old;GUI.color=Color.white;}
            return h;
        }
        private static readonly Rect WidePhoto=new Rect(.20454545f,.140625f,.63636364f,.59375f);
        private static readonly Rect ClosePhoto=new Rect(.30681818f,.3046875f,.39772727f,.34375f);
        private static readonly Rect ReactionCrop=new Rect(.69444444f,.25130208f,.29079861f,.22135417f);
        private static string PreviewHint(string type)
        {
            string hint=Tr(type=="framing"?"MF_Settings_FramingHint":type=="zoom"?"MF_Settings_ZoomHint":type=="night"?"MF_Settings_NightHint":"MF_Settings_ReactionsHint");
            hint+="\n"+Tr(type=="night"?"MF_Settings_BrightnessSample":type=="reactions"?"MF_Settings_ReactionEnlarged":"MF_Settings_RealPhotoExample");
            return hint+"\n"+Tr("MF_Settings_EnlargeExample");
        }
        private static float PreviewHeight(string type,float width)
        {
            bool stacked=width<590;
            float tileW=stacked?width:(width-12)/2;
            float pictureH=type=="reactions"?tileW*.5075f:tileW*.6786f;
            return Height(PreviewHint(type),width,true)+14+(pictureH+48)*(stacked?2:1)+8+(type=="reactions"?86:0);
        }
        private static void DrawPhoto(Rect r,Texture2D tex,Rect uv,Color tint)
        {
            Color before=GUI.color;
            try
            {
                Widgets.DrawBoxSolid(r,new Color(.10f,.10f,.09f));
                float aspect=tex.width*uv.width/(tex.height*uv.height);
                Rect fitted=r;
                if(r.width/r.height>aspect) {fitted.width=r.height*aspect;fitted.x+=(r.width-fitted.width)/2;}
                else {fitted.height=r.width/aspect;fitted.y+=(r.height-fitted.height)/2;}
                GUI.color=tint;GUI.DrawTextureWithTexCoords(fitted,tex,uv);
            }
            finally {GUI.color=before;}
        }
        private static void DrawPreview(Rect r,string type)
        {
            string hint=PreviewHint(type);
            float hintH=Height(hint,r.width,true);
            Label(new Rect(r.x,r.y,r.width,hintH),hint,true);
            bool stacked=r.width<590;
            float tileW=stacked?r.width:(r.width-12)/2;
            float imageH=type=="reactions"?tileW*.5075f:tileW*.6786f;
            for(int i=0;i<2;i++)
            {
                Rect picture=new Rect(r.x+(stacked?0:i*(tileW+12)),r.y+hintH+14+(stacked?i*(imageH+48):0),tileW,imageH);
                string caption=Tr(type=="reactions"?(i==0?"MF_Settings_After":"MF_Settings_WithPhoto"):type=="night"?(i==0?"MF_Settings_Natural":"MF_Settings_Bright"):(i==0?"MF_Settings_Fewer":"MF_Settings_Wider"));
                var tex=ContentFinder<Texture2D>.Get("UI/MomentFrames/Settings/"+(type=="reactions"?"Reactions"+(i+1):"ExamplePhoto"),false);
                Rect uv=type=="reactions"?ReactionCrop:type=="night"?WidePhoto:i==0?ClosePhoto:WidePhoto;
                Color tint=type=="night"&&i==0?new Color(.38f,.40f,.48f):Color.white;
                if(tex!=null)
                {
                    DrawPhoto(picture,tex,uv,tint);
                    if(Widgets.ButtonInvisible(picture))Find.WindowStack.Add(new PhotoExampleWindow(tex,uv,tint,caption));
                    TooltipHandler.TipRegion(picture,Tr("MF_Settings_EnlargeExample"));
                }
                Label(new Rect(picture.x,picture.yMax+5,picture.width,38),caption,true,TextAnchor.MiddleCenter);
            }
            if(type=="reactions")
            {
                float top=r.y+hintH+14+(imageH+48)*(stacked?2:1)+8;
                float half=(r.width-12)/2;
                for(int i=0;i<2;i++)
                {
                    int icon=i==0?ReactionChoices.Heart:ReactionChoices.HandWave;
                    float x=r.x+i*(half+12);
                    ReactionArt.Draw(new Rect(x,top,54,54),icon,ReactionArt.ColorFor(icon));
                    Label(new Rect(x+64,top,half-64,70),ReactionChoices.Label(icon),false,TextAnchor.MiddleLeft);
                }
            }
        }
        private sealed class PhotoExampleWindow : Window
        {
            private Texture2D texture;
            private readonly Rect uv;
            private readonly Color tint;
            private readonly string caption,path;
            public override Vector2 InitialSize=>new Vector2(Mathf.Min(1200,UI.screenWidth-48),Mathf.Min(820,UI.screenHeight-48));
            internal PhotoExampleWindow(Texture2D tex,Rect uv,Color tint,string caption,string sourcePath=null)
            {
                this.uv=uv;this.tint=tint;this.caption=caption;
                if(sourcePath!=null) {texture=PhotoTextureCache.AcquireTexture(sourcePath);if(texture!=null)path=sourcePath;else texture=tex;}
                else texture=tex;
                doCloseX=true;doCloseButton=true;closeOnClickedOutside=true;absorbInputAroundWindow=true;
            }
            public override void DoWindowContents(Rect inRect)
            {
                AlbumStyle.DrawPaper(inRect);
                Rect area=inRect.ContractedBy(20);
                float labelH=Height(caption,area.width);
                Label(new Rect(area.x,area.y,area.width,labelH),caption);
                Rect picture=new Rect(area.x,area.y+labelH+16,area.width,Mathf.Max(1,area.height-labelH-64));
                if(texture!=null)DrawPhoto(picture,texture,uv,tint);
            }
            public override void PreClose()
            {
                base.PreClose();
                if(path!=null)PhotoTextureCache.Release(path);
                texture=null;
            }
        }
    }
}
