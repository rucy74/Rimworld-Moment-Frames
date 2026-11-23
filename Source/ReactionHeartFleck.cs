using UnityEngine;
using Verse;

namespace MomentFrames
{
    /// <summary>Native thrown-fleck animation for photographic speech bubbles. Only our effect is hidden by another shutter.</summary>
    public struct MFPhotoHeartFleck : IFleck
    {
        private FleckThrown heart;
        public void Setup(FleckCreationData data) => heart.Setup(data);
        public bool TimeInterval(float deltaTime,Map map) => heart.TimeInterval(deltaTime,map);
        public Vector3 GetPosition() => heart.GetPosition();
        internal static bool DrawAllowed(bool captureBusy) => !captureBusy;
        public void Draw(DrawBatch batch)
        {
            // Flecks may batch on a render worker. Busy is a plain flag; Time.frameCount/HideGuides must not be queried on that worker.
            if(DrawAllowed(PhotoCapture.Busy)) heart.Draw(batch);
        }
    }
    public sealed class MFPhotoHeartSystem : FleckSystemBase<MFPhotoHeartFleck>
    {
        public MFPhotoHeartSystem(FleckManager parent) : base(parent) { }
    }
    /// <summary>Use the same combined art as the saved photograph and preparation preview.</summary>
    public sealed class MFPhotoExpressionGraphic : Graphic_Fleck
    {
        public override void Init(GraphicRequest req)
        {
            req.texture=ReactionArt.Texture(req.path=="Things/Mote/SpeechSymbols/Chitchat"
                ?ReactionChoices.HandWave:ReactionChoices.Heart) ?? req.texture;
            base.Init(req);
        }
    }
}
