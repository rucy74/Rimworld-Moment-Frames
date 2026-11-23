using RimWorld;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    /// <summary>Photos on frames yellow with age (a setting, off by default). Nothing is saved and nothing ticks: the tint is a pure function of
    /// the photo's age at draw time, applied through a MaterialPropertyBlock so the shared photo material is never modified. The album, the
    /// viewer and the files stay as they are — only the frames on the map fade. An excellent or better frame fades at half speed; a relic photo from
    /// another colony arrives already half faded.</summary>
    [StaticConstructorOnStartup]
    public static class Fading
    {
        /// <summary>Multiply tint at full fade: yellowed and a little darker, like a print left in the sun.</summary>
        public static readonly Color FullTint = new Color(0.90f, 0.78f, 0.58f);
        public const int MinYears = 2, MaxYears = 40, DefaultYears = 10;

        /// <summary>Debug/probe: every photo is drawn this many years older than it is (not saved).</summary>
        public static int PreviewYears;

        public static bool Enabled => MomentFramesMod.Settings != null && MomentFramesMod.Settings.fade;

        public static int FullYears => Mathf.Clamp(MomentFramesMod.Settings?.fadeYears ?? DefaultYears, MinYears, MaxYears);

        /// <summary>0 = fresh, 1 = fully faded. Pure: age in ticks, whether the frame protects the print (half speed), whether the photo is a relic
        /// (starts at 0.5), and the years until a plain frame reaches 1.</summary>
        public static float Fade01(int ageTicks, bool protectedFrame, bool relic, int fullYears)
        {
            float years = Mathf.Max(0, ageTicks) / (float)GenDate.TicksPerYear;
            float f = years / Mathf.Max(1, fullYears);
            if (protectedFrame) f *= 0.5f;
            if (relic) f = 0.5f + 0.5f * f;
            return Mathf.Clamp01(f);
        }

        public static Color Tint(float fade) => Color.Lerp(Color.white, FullTint, fade);

        /// <summary>Tint for this record drawn now (white when the setting is off).</summary>
        public static Color TintFor(PhotoRecord rec, bool protectedFrame)
        {
            if (!Enabled || rec == null) return Color.white;
            int age = Find.TickManager.TicksAbs - rec.ticksAbs + PreviewYears * GenDate.TicksPerYear;
            return Tint(Fade01(age, protectedFrame, rec.kind == Kinds.Relic, FullYears));
        }

        private static MaterialPropertyBlock block;

        /// <summary>Draws the photo quad with the tint. White = the plain draw call (the common case: setting off or a fresh photo).</summary>
        public static void DrawPhoto(Matrix4x4 matrix, Material mat, Color tint, Mesh mesh = null)
        {
            if (mesh == null) mesh = MeshPool.plane10;
            if (tint == Color.white)
            {
                Graphics.DrawMesh(mesh, matrix, mat, 0);
                return;
            }
            if (block == null) block = new MaterialPropertyBlock();
            block.SetColor("_Color", tint);
            Graphics.DrawMesh(mesh, matrix, mat, 0, null, 0, block);
        }
    }
}
