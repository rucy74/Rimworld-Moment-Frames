using System;
using System.IO;
using Verse;

namespace MomentFrames
{
    internal static class PhotoFiles
    {
        // Publish a new complete original; never overwrite another photo or leave a partial PNG at its final path.
        // written: the same bytes already on disk under a temporary name, which then only needs its final name here.
        internal static void WriteOriginal(string path, byte[] bytes, string written = null)
        {
            if (written != null)
                try { File.Move(written, path); return; }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    stream.Write(bytes, 0, bytes.Length);
                File.Move(temporary, path);
            }
            finally
            {
                if (File.Exists(temporary))
                    try { File.Delete(temporary); } catch (Exception e) { Log.Warning("[MomentFrames] temporary photo cleanup: " + e.Message); }
            }
        }

        // Derived images are replaceable. Their generation/write failure must not undo a successful original.
        internal static bool TryDerived(string path, Func<byte[]> make, string written = null)
        {
            try
            {
                byte[] bytes = make();
                if (bytes == null) return false;
                WriteOriginal(path, bytes, written);
                PhotoTextureCache.Forget(path);
                return true;
            }
            catch (Exception e) { Log.Warning("[MomentFrames] could not save derived photo; original retained: " + e.Message); return false; }
        }
    }
}
