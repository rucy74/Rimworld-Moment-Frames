using System;
using System.Text.RegularExpressions;

namespace MomentFrames
{
    /// <summary>Small corrections to known generated sentences; stored originals and user-written notes remain unchanged.</summary>
    internal static class GeneratedCaptionDisplay
    {
        internal static string Text(PhotoRecord record, string text, bool editedNote)
        {
            bool legacySurvey = record != null && Kinds.IsSurvey(record.kind) && !record.userDescription
                && record.eventKey?.StartsWith("survey:", StringComparison.Ordinal) == true
                && Regex.IsMatch(text ?? "", @"^전체지도을 기록했다\. 지도 범위 [0-9]+ × [0-9]+칸\.$");
            if (record == null || editedNote || record.userDescription || !record.generatedDesc && !legacySurvey) return CaptionText.Clean(text);
            string result = text ?? "";
            if (Kinds.IsSurvey(record.kind)) result = result.Replace("전체지도을 기록했다.", "전체지도를 기록했다.");
            if (record.generatedDesc && record.HasFile && record.kind == Journey.Kind && string.IsNullOrEmpty(record.captionTemplate))
            {
                var oldFact = new Regex("^" + Regex.Escape(record.title ?? "") + @"\. 사진에는 [0-9]+명이 담겼다\.");
                if (oldFact.IsMatch(result))
                {
                    int count = record.peopleIds?.Count > 0 ? record.peopleIds.Count : record.people?.Count ?? 0;
                    string fact = (record.title ?? "") + (count == 0 ? ". 장소의 모습이 담겼다." : ". 사진에는 " + count + "명이 담겼다.");
                    result = oldFact.Replace(result, fact, 1);
                }
            }
            string key = record.captionTemplate ?? "";
            if (record.generatedDesc && (key.StartsWith("MF_Caption_scene_birthday_parent_", StringComparison.Ordinal)
                || key.StartsWith("MF_Caption_scene_birthday_carried_parent_", StringComparison.Ordinal)
                || key.StartsWith("MF_Caption_scene_carried_parent_", StringComparison.Ordinal)) && record.captionValues?.Count > 9)
            {
                string name = record.captionValues[7], relation = record.captionValues[9];
                if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(relation) && !result.Contains(name))
                {
                    int at = result.IndexOf(relation, StringComparison.Ordinal);
                    if (at >= 0)
                    {
                        int end = at + relation.Length;
                        string suffix = result.Substring(end);
                        if (suffix.StartsWith("와", StringComparison.Ordinal)) suffix = "와(과)" + suffix.Substring(1);
                        result = result.Substring(0, end) + " " + name + suffix;
                    }
                }
            }
            return CaptionText.Clean(result);
        }
    }
}
