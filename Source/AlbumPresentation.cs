using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    /// <summary>Display-only formatting and record navigation. Saved titles and original photos are never changed.</summary>
    internal static class AlbumPresentation
    {
        internal const int SpreadCount = 8;
        internal static string Title(string value) => (value ?? "").Replace('—', '/').Replace('–', '/').Replace('·', '/');
        internal static int SpreadFor(int index, int count) => Mathf.Clamp(Mathf.Max(0, index) / SpreadCount, 0, Mathf.Max(0, (count - 1) / SpreadCount));
        internal static bool SearchMatches(PhotoRecord record, string query)
        {
            if (record == null) return false;
            if (string.IsNullOrWhiteSpace(query)) return true;
            var terms = query.Trim().Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            string text = record.DisplayTitle + "\n" + record.DisplayDesc + "\n" + record.dateLabel + "\n"
                + string.Join(" ", record.people ?? new List<string>())+"\n"+string.Join(" ",record.eventPeopleNames??new List<string>());
            return terms.All(term => text.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0);
        }
        internal static bool EventMatches(PhotoRecord record,string personId,string personName)
        {
            if(record==null)return false;
            if(record.eventPeopleIds?.Count>0)return !personId.NullOrEmpty()&&record.eventPeopleIds.Contains(personId);
            return !personName.NullOrEmpty()&&record.eventPeopleNames?.Contains(personName)==true;
        }
        internal static string EventPeople(PhotoRecord record)=>record?.eventPeopleNames?.Count>0
            ?"MF_Extras_EventPeople".Translate(string.Join(", ",record.eventPeopleNames)).ToString():"";

        internal static PhotoRecord PreviousBirthday(PhotoRecord current, IEnumerable<PhotoRecord> records)
        {
            if (current == null || current.kind != Kinds.Birthday) return null;
            string subject = current.requiredPerson;
            if (subject.NullOrEmpty()) subject = current.eventSubjectIds?.FirstOrDefault();
            // A guest can attend several birthdays: people lists alone do not identify the birthday subject.
            if (subject.NullOrEmpty()) return null;
            return (records ?? Enumerable.Empty<PhotoRecord>()).Where(r => r != null && r != current && r.HasFile
                && r.kind == Kinds.Birthday && r.ticksAbs < current.ticksAbs
                && (r.requiredPerson == subject || r.eventSubjectIds?.Contains(subject) == true))
                .OrderByDescending(r => r.ticksAbs).ThenBy(r => r.id, StringComparer.Ordinal).FirstOrDefault();
        }

        internal static PhotoRecord PreviousView(PhotoRecord current, IEnumerable<PhotoRecord> records)
        {
            if (current == null || !current.HasFile || !current.HasCenter || current.spanCells <= 0f || current.mapId < 0) return null;
            return (records ?? Enumerable.Empty<PhotoRecord>()).Where(r => r != null && r != current && r.HasFile && r.HasCenter
                && r.mapId == current.mapId && r.ticksAbs < current.ticksAbs
                && Mathf.Abs(r.center.x - current.center.x) <= .05f && Mathf.Abs(r.center.z - current.center.z) <= .05f
                && Mathf.Abs(r.spanCells - current.spanCells) <= .05f && Mathf.Abs(r.Aspect - current.Aspect) <= .001f)
                .OrderByDescending(r => r.ticksAbs).ThenBy(r => r.id, StringComparer.Ordinal).FirstOrDefault();
        }

        /// <summary>Keep the selected font size; trim to two lines rather than shrinking a card to 11px.</summary>
        internal static void CardTitle(Rect rect, string title, TextAnchor anchor = TextAnchor.UpperCenter, GameFont font = GameFont.Small)
        {
            title = Title(title).Replace("\n", " ");
            var style = AlbumStyle.StyleFor(title, font);
            style.alignment = anchor;
            style.wordWrap = true;
            style.clipping = TextClipping.Clip;
            string shown = title;
            if (style.CalcHeight(new GUIContent(shown), Mathf.Max(1f, rect.width)) > rect.height)
            {
                int low = 0, high = title.Length;
                while (low < high)
                {
                    int middle = (low + high + 1) / 2;
                    if (style.CalcHeight(new GUIContent(title.Substring(0, middle) + "..."), Mathf.Max(1f, rect.width)) <= rect.height) low = middle;
                    else high = middle - 1;
                }
                if (low > 0 && char.IsHighSurrogate(title[low - 1])) low--;
                shown = title.Substring(0, low) + "...";
                TooltipHandler.TipRegion(rect, title);
            }
            GUI.Label(rect, shown, style);
        }

        internal static float TitleHeight => Mathf.Ceil(AlbumStyle.TextHeight("가나다\n가나다", 600f, GameFont.Small)) + 4f;
        internal static float DateHeight => Mathf.Ceil(AlbumStyle.TextHeight("5500년 1분기 1일", 600f, GameFont.Tiny)) + 4f;
        internal static void CardDate(Rect rect, string value)
        {
            var style = AlbumStyle.StyleFor(value, GameFont.Tiny);
            style.alignment = TextAnchor.UpperCenter; style.wordWrap = false; style.clipping = TextClipping.Clip;
            string visible = value ?? "";
            if (style.CalcSize(new GUIContent(visible)).x > rect.width)
            {
                while (visible.Length > 0 && style.CalcSize(new GUIContent(visible + "...")).x > rect.width) visible = visible.Substring(0, visible.Length - 1);
                visible += "...";
                TooltipHandler.TipRegion(rect, value ?? "");
            }
            GUI.Label(rect, visible, style);
        }
    }
}
