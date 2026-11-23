using System;
using System.Reflection;
#if !BRIDGE_POLICY_TEST
using System.Linq;
using UnityEngine;
using Verse;
#endif

namespace MomentFrames
{
    /// <summary>Optional navigation binds the fields it uses, independently of the publishing contract.</summary>
    internal sealed class RimGramWindowContract
    {
        private readonly FieldInfo view, singleId, subScroll, filter, scroll, postId;
        private readonly object singleView, feedView, allFilter, zero;
        internal bool Supported { get; }
        internal RimGramWindowContract(Type window, Type post, Type vector)
        {
            if (window == null || post == null || vector == null) return;
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
            view = window.GetField("view", flags); singleId = window.GetField("singlePostId", flags);
            subScroll = window.GetField("subScroll", flags); filter = window.GetField("filter", flags); scroll = window.GetField("scroll", flags);
            postId = post.GetField("id", BindingFlags.Public | BindingFlags.Instance);
            if (view?.FieldType.IsEnum != true || !Enum.IsDefined(view.FieldType, "SinglePost") || !Enum.IsDefined(view.FieldType, "Feed")
                || filter?.FieldType.IsEnum != true || !Enum.IsDefined(filter.FieldType, "All")
                || singleId?.FieldType != typeof(int) || postId?.FieldType != typeof(int)
                || subScroll?.FieldType != vector || scroll?.FieldType != vector || window.GetConstructor(Type.EmptyTypes) == null) return;
            singleView = Enum.Parse(view.FieldType, "SinglePost"); feedView = Enum.Parse(view.FieldType, "Feed");
            allFilter = Enum.Parse(filter.FieldType, "All"); zero = Activator.CreateInstance(vector); Supported = true;
        }
        internal bool Navigate(object window, object post = null)
        {
            if (!Supported || window == null || !view.DeclaringType.IsInstanceOfType(window)) return false;
            if (post != null && !postId.DeclaringType.IsInstanceOfType(post)) return false;
            filter.SetValue(window, allFilter); scroll.SetValue(window, zero); subScroll.SetValue(window, zero);
            if (post == null) view.SetValue(window, feedView);
            else { singleId.SetValue(window, (int)postId.GetValue(post)); view.SetValue(window, singleView); }
            return true;
        }
    }

#if !BRIDGE_POLICY_TEST
    internal static class OptionalRimGramWindow
    {
        internal static bool Open(object post = null)
            => OpenFrom(post, Find.WindowStack.currentlyDrawnWindow as Dialog_MFPhotoBridge);

        internal static bool OpenFrom(object post, Dialog_MFPhotoBridge source)
        {
            try
            {
                Type type = OptionalPhotoBridges.LoadedType("RimGram.MainTabWindow_RimGram");
                var contract = new RimGramWindowContract(type, OptionalPhotoBridges.LoadedType("RimGram.Post"), typeof(Vector2));
                if (!contract.Supported || !typeof(Window).IsAssignableFrom(type)) return false;
                // An already-open real RimGram window gets the same post, with no second feed instance.
                var real = Find.WindowStack.Windows.FirstOrDefault(window => window.GetType() == type);
                if (real != null)
                {
                    if (!CanFocus(real, source) || !contract.Navigate(real, post)) return false;
                    // A real main tab belongs to the GameUI layer, below dialogs. Close only our draft
                    // dialog so the existing tab is visible; the album and the foreign tab stay open.
                    if (source != null && Find.WindowStack.IsOpen(source)) source.Close();
                    return Focus(real, null);
                }
                var existing = Find.WindowStack.Windows.OfType<Dialog_MFRimGramWindow>().FirstOrDefault();
                if (existing != null) return CanFocus(existing, source) && existing.Navigate(post) && Focus(existing, source);
                var content = Activator.CreateInstance(type) as Window;
                if (content == null || !contract.Navigate(content, post)) return false;
                Find.WindowStack.Add(new Dialog_MFRimGramWindow(content, contract)); return true;
            }
            catch (Exception e) { Log.WarningOnce("[MomentFrames] optional RimGram window unavailable: " + (e.InnerException ?? e).Message, 0x4D465247); return false; }
        }

        private static bool CanFocus(Window target, Dialog_MFPhotoBridge source)
            => WithDraftInputReleased(source, () => Find.WindowStack.GetsInput(target));

        private static bool Focus(Window target, Dialog_MFPhotoBridge source)
            => WithDraftInputReleased(source, () =>
            {
                if (!Find.WindowStack.GetsInput(target)) return false;
                // Public WindowStack ordering preserves the content instance and its window position.
                Find.WindowStack.Notify_ClickedInsideWindow(target);
                return true;
            });

        private static bool WithDraftInputReleased(Dialog_MFPhotoBridge source, Func<bool> action)
        {
            bool modal = source?.absorbInputAroundWindow ?? false;
            try
            {
                // Only our caller's modal is released, for this ordering operation. Other modals
                // retain their normal input protection, and the draft remains open for our host.
                if (source != null) source.absorbInputAroundWindow = false;
                return action();
            }
            finally { if (source != null) source.absorbInputAroundWindow = modal; }
        }
    }

    /// <summary>A separate window hosts RimGram's own public renderer. The album main tab stays open behind it.</summary>
    internal sealed class Dialog_MFRimGramWindow : Window
    {
        private readonly Window content;
        private readonly RimGramWindowContract contract;
        internal Dialog_MFRimGramWindow(Window content, RimGramWindowContract contract)
        {
            this.content = content; this.contract = contract;
            forcePause = false; draggable = true; doCloseX = true; closeOnAccept = false;
            absorbInputAroundWindow = false;
        }
        public override Vector2 InitialSize => new Vector2(Mathf.Min(520f, UI.screenWidth - 40f), Mathf.Min(920f, UI.screenHeight - 70f));
        internal bool Navigate(object post) => contract.Navigate(content, post);
        public override void DoWindowContents(Rect rect)
        {
            // Leave enough room for the host close control, then let RimGram draw and operate its own feed.
            var area = new Rect(rect.x, rect.y + 24f, rect.width, Mathf.Max(1f, rect.height - 24f));
            content.windowRect = windowRect;
            content.DoWindowContents(area);
        }
    }
#endif
}
