using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    public partial class Dialog_PhotoEdit
    {
        private sealed class EditState
        {
            internal Rect Crop;
            internal Lock LockMode;
            internal string Filter;
            internal List<PhotoImageIcon> Icons;
            internal bool Same(EditState b,bool includeLock=true) => Crop==b.Crop && Filter==b.Filter
                && (!includeLock || LockMode==b.LockMode) && Icons.Count==b.Icons.Count
                && Icons.Zip(b.Icons,(a,c)=>a.icon==c.icon && a.x==c.x && a.y==c.y && a.size==c.size).All(x=>x);
        }
        private readonly List<EditState> undoEdits=new List<EditState>();
        private EditState initialEdit,gestureEdit;
        private bool allowClose,discardDialog,historyRestored;
        private sealed class DiscardPrompt : Dialog_MessageBox
        {
            private readonly Dialog_PhotoEdit owner;
            internal DiscardPrompt(Dialog_PhotoEdit owner,bool sound)
                : base("MF_UX_DiscardEdits".Translate(),"MF_UX_Discard".Translate(),
                    ()=>{owner.allowClose=true;owner.Close(sound);},"MF_UX_KeepEditing".Translate()) {this.owner=owner;}
            public override void PostClose() {owner.discardDialog=false;base.PostClose();}
        }
        private EditState Snapshot() => new EditState {Crop=crop,LockMode=lockMode,Filter=filter,Icons=PhotoImageIcons.Copy(imageIcons)};
        internal bool HasUnsavedChanges => initialEdit!=null && !initialEdit.Same(Snapshot(),false);
        internal bool CanUndoEdit => undoEdits.Count>0;
        internal void BeginEditGesture() { if(gestureEdit==null)gestureEdit=Snapshot(); }
        internal void FinishEditGesture()
        {
            if(gestureEdit!=null && !gestureEdit.Same(Snapshot()))
            {undoEdits.Add(gestureEdit);if(undoEdits.Count>32)undoEdits.RemoveAt(0);}
            gestureEdit=null;
        }
        internal void UndoEdit()
        {
            if(!CanUndoEdit)return;
            var state=undoEdits[undoEdits.Count-1];undoEdits.RemoveAt(undoEdits.Count-1);
            crop=state.Crop;lockMode=state.LockMode;SetFilter(state.Filter);imageIcons=PhotoImageIcons.Copy(state.Icons);
            selectedIcon=-1;dragging=0;iconDragging=false;gestureEdit=null;historyRestored=true;
        }
        internal void ResetCrop() {crop=new Rect(0,0,1,1);lockMode=Lock.Free;}
        public override void Close(bool doCloseSound=true)
        {
            if(!allowClose && !failed && HasUnsavedChanges)
            {
                if(discardDialog)return;discardDialog=true;
                Find.WindowStack.Add(new DiscardPrompt(this,doCloseSound));
                return;
            }
            base.Close(doCloseSound);
        }
        public override void DoWindowContents(Rect inRect)
        {
            var raw=Event.current.rawType;
            bool input=raw==EventType.MouseDown || raw==EventType.MouseUp || raw==EventType.KeyDown || raw==EventType.KeyUp;
            if(input)BeginEditGesture();historyRestored=false;
            DrawEditor(inRect);
            if(historyRestored)gestureEdit=null;
            else if(raw==EventType.MouseUp || raw==EventType.KeyUp || raw==EventType.KeyDown)FinishEditGesture();
        }
    }
}
