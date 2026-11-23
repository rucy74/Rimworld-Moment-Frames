using System.Text.RegularExpressions;

namespace MomentFrames
{
    internal static class CaptionText
    {
        private static readonly Regex Names = new Regex(@"\(\*Name\)(.*?)\(/Name\)", RegexOptions.Singleline);
        private static readonly Regex Particles = new Regex(@"([가-힣A-Za-z0-9]+)(을\(를\)|를\(을\)|이\(가\)|가\(이\)|은\(는\)|는\(은\)|와\(과\)|과\(와\))");
        internal static string Clean(string text)
        {
            text = Names.Replace(text ?? "", "$1");
            return Particles.Replace(text, m =>
            {
                string word = m.Groups[1].Value, form = m.Groups[2].Value;
                char last = word[word.Length - 1];
                bool final = last >= '가' && last <= '힣' && (last - '가') % 28 != 0;
                return word + (form == "을(를)" || form == "를(을)" ? (final ? "을" : "를")
                    : form == "이(가)" || form == "가(이)" ? (final ? "이" : "가")
                    : form == "은(는)" || form == "는(은)" ? (final ? "은" : "는") : (final ? "과" : "와"));
            });
        }
    }
}
