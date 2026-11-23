using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace MomentFrames
{
    // 1.7.4985 Camera.Render -> CameraTextureBaker.Render does not sort its draw
    // items, while CameraCaptureFeature (the normal offscreen path) does. Apply
    // the engine's own ordering only inside our synchronous camera render.
    internal static class NativePhotoOrder
    {
        [ThreadStatic] internal static bool Active;
        internal static int SortCount;
        internal static void Render(Camera camera)
        {
            bool previous=Active;
            try {Active=true;camera.Render();}
            finally {Active=previous;}
        }
    }

    [HarmonyPatch]
    internal static class Patch_NativePhotoOrder
    {
        private static readonly Type baker=AccessTools.TypeByName("RimWorldWaylith.Rendering.CameraTextureBaker");
        private static readonly Type item=AccessTools.TypeByName("Waylith.Rendering.FinalDrawItem");
        private static readonly Type comparer=AccessTools.TypeByName("Waylith.Rendering.DrawSortKeyComparer");
        private static readonly MethodInfo target=baker==null?null:AccessTools.Method(baker,"RecordAndSubmit");
        private static MethodInfo sort;
        private static object ordering;
        private static bool Prepare() => target!=null && item!=null && comparer!=null;
        private static MethodBase TargetMethod() => target;
        private static void Prefix(object items)
        {
            if(!NativePhotoOrder.Active)return;
            if(sort==null)
            {
                sort=items.GetType().GetMethod("Sort",new[]{typeof(System.Collections.Generic.IComparer<>).MakeGenericType(item)});
                ordering=AccessTools.Field(comparer,"Instance").GetValue(null);
            }
            sort.Invoke(items,new[]{ordering});
            NativePhotoOrder.SortCount++;
        }
    }
}
