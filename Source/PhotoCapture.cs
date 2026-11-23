using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    /// <summary>Renders the map around a cell into an image using the game camera, at the end of a frame. The image has a fixed number of pixels per
    /// map cell (setting, default 192 for cropping and close inspection), so a wider shot is a bigger file rather than a blurrier one; width follows the aspect (default 3:2).</summary>
    public static class PhotoCapture
    {
        public const int DefaultPxPerCell = 192;   // Keep the scene framing; give saved originals room for cropping.
        public const int MinPxPerCell = 32, MaxPxPerCell = 256;
        public const int MaxSide = 4096;           // a colony-wide shot (61 cells at 64 px would be 5856 wide) is scaled down to fit this
        public const int ThumbHeight = 256;        // album cards, chronicle rows, map frames and the gallery rail draw this
        public const float MinSpan = 5f;
        public const float MaxSpan = 81f;
        public const float DefaultAspect = 1.5f;
        public const float MinAspect = 2f / 3f;   // portrait limit
        public const float MaxAspect = 3f;        // panorama limit

        public static float ClampAspect(float aspect) => Mathf.Clamp(aspect > 0.01f ? aspect : DefaultAspect, MinAspect, MaxAspect);

        /// <summary>Probe only (Workshop showcase close-ups of one frame): pixels per cell past the setting's range; 0 = the setting.</summary>
        internal static int ProbePixelsPerCell;

        public static int PixelsPerCell => ProbePixelsPerCell > 0 ? ProbePixelsPerCell : Mathf.Clamp(MomentFramesMod.Settings?.pxPerCell ?? DefaultPxPerCell, MinPxPerCell, MaxPxPerCell);

        /// <summary>Pixel size of a photo of this span/aspect at the current setting: span × pixels per cell, both sides capped at MaxSide (same proportions).</summary>
        public static void ImageSize(float span, float aspect, out int width, out int height)
        {
            float h = Mathf.Clamp(span, MinSpan, MaxSpan) * PixelsPerCell, w = h * ClampAspect(aspect);
            float k = Mathf.Min(1f, Mathf.Min(MaxSide / w, MaxSide / h));
            width = Mathf.Max(64, Mathf.RoundToInt(w * k));
            height = Mathf.Max(64, Mathf.RoundToInt(h * k));
        }

        /// <summary>Map cells covered by a capture (centre in world units, vertical span in cells, width/height), clipped to the map.</summary>
        public static CellRect CoveredRect(Vector3 center, float span, Map map, float aspect = DefaultAspect)
        {
            float halfH = span / 2f, halfW = span * ClampAspect(aspect) / 2f;
            var r = CellRect.FromLimits(Mathf.FloorToInt(center.x - halfW), Mathf.FloorToInt(center.z - halfH), Mathf.CeilToInt(center.x + halfW) - 1, Mathf.CeilToInt(center.z + halfH) - 1);
            return r.ClipInsideMap(map);
        }

        public class Result
        {
            internal int capturedAbs = -1; // The actual render frame, so sequential survey images retain their own shutter time.
            public byte[] png;      // the encoded original: JPEG or PNG by the "Photo file format" setting (PhotoImageFormat)
            public byte[] thumb;
            internal byte[] preview;
            internal PreparedCapture prepared; // this photo's files, when the album asked the worker to write them
            internal PhotoPixels pixels;
            internal PhotoReactions.Plan reactions;
            internal PhotoCaptionContext captionContext;
            internal List<string> capturedPeopleNames, capturedPeopleIds; // exact render-time participants for sequential survey captures
            internal Vector4 peopleBox;
            internal bool? subjectsAtShutter;
        }
        internal static double LastRenderMilliseconds, LastEncodeMilliseconds;
        internal static void ApplyPeopleBox(PhotoRecord record, Result result)
        {
            record.boxX0 = result.peopleBox.x; record.boxY0 = result.peopleBox.y;
            record.boxX1 = result.peopleBox.z; record.boxY1 = result.peopleBox.w;
        }

        public static bool Busy { get; private set; }
        private static PhotoRecord activeRecord;
        internal static bool InProgress(string photoId) => Busy && !string.IsNullOrEmpty(photoId)
            && activeRecord?.id == photoId && !activeRecord.captureCancelled;
        private static int cleanFrame = -1;
        internal static bool HideGuides => Busy && pendingMap == Find.CurrentMap && cleanFrame >= 0 && Time.frameCount >= cleanFrame;

        /// <summary>One frame definition for both saved metadata and the renderer, including direct event hooks.</summary>
        internal static void KeepInside(Map map, ref Vector3 center, ref float span, ref float aspect)
        {
            aspect = ClampAspect(aspect);
            span = Mathf.Clamp(span, MinSpan, MaxSpan);
            if (map == null) return;
            span = Mathf.Min(span, map.Size.z, map.Size.x / aspect);
            float hx = span * aspect / 2f, hz = span / 2f;
            center = new Vector3(Mathf.Clamp(center.x, hx, map.Size.x - hx), 0f,
                Mathf.Clamp(center.z, hz, map.Size.z - hz));
        }

        internal static bool InFrame(Pawn pawn, Map map, Vector3 center, float span, float aspect)
        {
            if (!VisualPosition(pawn, map, out Vector3 d)) return false;
            return Mathf.Abs(d.x - center.x) <= span * aspect / 2f - .45f
                && d.z - .45f >= center.z - span / 2f && d.z + .75f <= center.z + span / 2f;
        }

        internal static bool VisualPosition(Pawn pawn, Map map, out Vector3 position)
        {
            position = default;
            Thing visual = pawn?.Spawned == true ? pawn : pawn?.Corpse;
            if (visual?.Spawned == true && visual.Map == map) { position = visual.DrawPos; return true; }
            Pawn carrier = BirthdayCarry.Holder(pawn);
            if (carrier?.Spawned != true || carrier.Map != map || carrier.carryTracker.CarriedThing != pawn) return false;
            position = carrier.DrawPos;
            PawnRenderUtility.CalculateCarriedDrawPos(carrier, pawn, ref position, out _);
            return true;
        }

        internal static IEnumerable<Pawn> VisiblePawns(Map map)
        {
            if (map == null) yield break;
            foreach (var pawn in map.mapPawns.AllPawnsSpawned)
            {
                yield return pawn;
                if (pawn.carryTracker?.CarriedThing is Pawn carried) yield return carried;
            }
            foreach (var corpse in map.listerThings.ThingsInGroup(ThingRequestGroup.Corpse))
                if (corpse is Corpse body) yield return body.InnerPawn;
        }

        /// <summary>New game / load. A capture runs as a coroutine on the camera for a frame or two; loading a save in that window can end it
        /// without its finally block, which left Busy set and every later photo of the session refused.</summary>
        internal static void Reset()
        {
            var faces=pendingFaces;pendingFaces=null;
            generation++;
            Busy = false;
            activeRecord = null;
            pendingFrame = -1;
            pendingMap = null;
            pendingRealLight = false;
            pendingReactions = null;
            cleanFrame = -1;
            pendingArea = null;
            areaExpandedMap = null;
            areaExpandedFrame = areaExpandedGeneration = -1;
            RestoreCaptureState(() => faces?.Dispose(), "facial posing");
        }

        /// <summary>Probe diagnostics: the frames in which the view rect was expanded and the render ran, and the expanded rect.</summary>
        public static int LastExpandFrame = -1, LastRenderFrame = -1;
        public static CellRect LastUnion;

        // The capture pending for the next frame: Map.MapUpdate (Harmony prefix) widens the culling rect right before that frame's draw calls.
        private static Map pendingMap;
        private static int generation;
        private static int pendingFrame = -1;
        private static Vector3 pendingCenter;
        private static float pendingSpan, pendingAspect;
        private static CellRect? pendingArea;
        private static Map areaExpandedMap;
        private static int areaExpandedFrame = -1, areaExpandedGeneration = -1;

        /// <summary>Called first thing in Map.MapUpdate. In the frame a capture renders, the culling rect (normally the camera view) is widened to the
        /// photo area right before the map submits its draw calls, so off-screen terrain, buildings and pawns are rendered into the photo too.
        /// Every other frame this is two compares. (LongEventHandler.ExecuteWhenFinished runs immediately when no long event is active and a
        /// WaitForFixedUpdate did not land in the render frame either — both left off-screen parts of a photo black; probe runs r24/r25.)</summary>
        internal static void OnMapUpdate(Map map)
        {
            if (pendingFrame < 0 || map != pendingMap || Time.frameCount < pendingFrame) return;
            if (map != Find.CurrentMap) return; // A background update must not consume the viewed-map capture frame.
            pendingFrame = -1;
            cleanFrame = Time.frameCount;
            bool expanded = ExpandViewRect(map, pendingCenter, pendingSpan, pendingAspect, pendingArea);
            if (pendingArea.HasValue && expanded)
            {
                areaExpandedMap = map;
                areaExpandedFrame = Time.frameCount;
                areaExpandedGeneration = generation;
            }
        }

        private static readonly FieldInfo RootPosF = AccessTools.Field(typeof(CameraDriver), "rootPos");
        private static readonly FieldInfo RootSizeF = AccessTools.Field(typeof(CameraDriver), "rootSize");
        private static readonly FieldInfo ViewRectFrameF = AccessTools.Field(typeof(CameraDriver), "lastViewRectGetFrame");
        private static readonly FieldInfo ViewRectF = AccessTools.Field(typeof(CameraDriver), "lastViewRect");
        private static readonly MethodInfo ApplyPosM = AccessTools.Method(typeof(CameraDriver), "ApplyPositionToGameObject");

        public static bool Capture(Map map, IntVec3 center, float spanCells, Action<Result> done, float aspect = DefaultAspect)
        {
            return Capture(map, center.ToVector3Shifted(), spanCells, done, aspect);
        }

        private static bool pendingRealLight;
        private static PhotoReactions.Plan pendingReactions;
        private static PhotoFaces.Lease pendingFaces;

        /// <summary>Bounded full-map/quarter capture. Ordinary photography keeps its original limits.</summary>
        internal static bool CaptureArea(Map map, CellRect area, Action<Result> done, int maxSide = 4096, PhotoRecord captionRecord = null, string albumFolder = null)
        {
            if (Busy || map == null || map != Find.CurrentMap || Find.Camera == null || Find.CameraDriver == null) return false;
            area = area.ClipInsideMap(map);
            if (area.Width < 1 || area.Height < 1) return false;
            Busy = true;
            pendingMap = map;
            areaExpandedMap = null;
            areaExpandedFrame = areaExpandedGeneration = -1;
            pendingArea = area;
            pendingCenter = new Vector3(area.minX + area.Width / 2f, 0f, area.minZ + area.Height / 2f);
            pendingSpan = area.Height;
            pendingAspect = area.Width / (float)area.Height;
            pendingRealLight = false;
            pendingReactions = null;
            int frame = Time.frameCount;
            pendingFrame = cleanFrame = frame + 1;
            try
            {
                Find.CameraDriver.StartCoroutine(Run(map, pendingCenter, pendingSpan, pendingAspect, done, frame, captionRecord, generation, maxSide, albumFolder));
                return true;
            }
            catch (Exception e)
            {
                Reset(); Log.Warning("[MomentFrames] could not start the map capture: " + e.Message); return false;
            }
        }

        /// <param name="center">World position (x/z in cells); y is ignored.</param>
        /// <param name="spanCells">Vertical extent in cells.</param>
        /// <param name="aspect">Image width / height; the horizontal extent is spanCells * aspect. Clamped to the portrait/panorama limits.</param>
        /// <param name="realLight">Keep the sky's light even when "bright photos" is on: for a disaster or a sky event the light is the subject
        /// (fallout's green, an aurora's colours, an eclipse's dark), and the daylight trick removed all of it (probe r129).</param>
        public static bool Capture(Map map, Vector3 center, float spanCells, Action<Result> done, float aspect = DefaultAspect, bool realLight = false, PhotoRecord reactionRecord = null)
            => Capture(map, center, spanCells, done, aspect, realLight, reactionRecord, null);

        /// <param name="albumFolder">The album's folder, when the photo is headed there: the worker then writes its files too, so saving costs the game thread almost nothing.</param>
        internal static bool Capture(Map map, Vector3 center, float spanCells, Action<Result> done, float aspect, bool realLight, PhotoRecord reactionRecord, string albumFolder)
        {
            if (Busy || map == null || map != Find.CurrentMap || Find.Camera == null || Find.CameraDriver == null) return false;
            try
            {
                Busy = true;
                activeRecord = reactionRecord;
                pendingRealLight = realLight;
                float span = Mathf.Clamp(spanCells, MinSpan, MaxSpan), asp = ClampAspect(aspect);
                KeepInside(map, ref center, ref span, ref asp);
                pendingReactions = PhotoReactions.CreatePlan(reactionRecord, map);
                pendingFaces=PhotoFaces.Begin(map,reactionRecord);
                pendingArea = null;
                int startFrame = Time.frameCount;
                // The next frame's MapUpdate (prefix → OnMapUpdate) draws the map with the widened rect; the coroutine renders at the end of that frame.
                pendingMap = map;
                pendingCenter = center;
                pendingSpan = span;
                pendingAspect = asp;
                pendingFrame = startFrame + 1;
                cleanFrame = pendingFrame; // selection/dynamic drawing can precede MapUpdate in that frame
                Find.CameraDriver.StartCoroutine(Run(map, center, span, asp, done, startFrame, reactionRecord, generation, 0, albumFolder));
            }
            catch (Exception e)
            {
                Reset();
                Log.Warning("[MomentFrames] could not prepare the capture: " + e);
                return false;
            }
            return true;
        }

        private static IEnumerator Run(Map map, Vector3 center, float span, float aspect, Action<Result> done, int startFrame, PhotoRecord captionRecord, int lease, int explicitSide = 0, string albumFolder = null)
        {
            var faces=pendingFaces;
            Result result = null;
            try
            {
                while (Time.frameCount <= startFrame) yield return null;   // now in the next frame, after its MapUpdate
                yield return new WaitForEndOfFrame();
                if (lease == generation && map == Find.CurrentMap && captionRecord?.captureCancelled != true)
                {
                    if (explicitSide <= 0 || AreaExpandedFor(map, lease))
                    {
                        var timer = System.Diagnostics.Stopwatch.StartNew();
                        result = Render(map, center, span, aspect, pendingRealLight, pendingReactions, captionRecord, explicitSide);
                        LastRenderMilliseconds = timer.Elapsed.TotalMilliseconds;
                    }
                    else Log.Warning("[MomentFrames] full-map capture view was not expanded in its render frame; not stored.");
                }
                if (result?.pixels != null)
                {
                    RestoreCaptureState(() => faces?.Dispose(), "facial posing");
                    // Readback is complete; only the byte encoding remains. Restore normal
                    // map guides while the worker finishes without releasing this request.
                    pendingFrame = cleanFrame = -1;
                    pendingArea = null;
                    pendingReactions = null;
                    Result captured = result;
                    bool jpeg = PhotoImageFormat.SaveJpeg;   // settings are read here, never on the worker
                    var encode = Task.Run(() => {
                        var timer = System.Diagnostics.Stopwatch.StartNew();
                        captured.png = PhotoImageFormat.Encode(captured.pixels, jpeg);
                        captured.preview = captured.pixels.Resize(PhotoPreviews.Side).Encode();
                        LastEncodeMilliseconds = timer.Elapsed.TotalMilliseconds;
                        if (albumFolder == null) return;
                        // A failure is not reported from here: the game thread then writes the photo the ordinary way and says what went wrong.
                        try { captured.prepared = ExternalPhotoFiles.Prepare(albumFolder, captured.png, captured.thumb, captured.preview); }
                        catch (Exception) { }
                        if (lease != System.Threading.Volatile.Read(ref generation)) captured.prepared?.Release();
                    });
                    float encodeDeadline = Time.realtimeSinceStartup + 120f;
                    while (!encode.IsCompleted && lease == generation && Time.realtimeSinceStartup < encodeDeadline) yield return null;
                    if (lease != generation) { encode.ContinueWith(_ => captured.prepared?.Release()); yield break; }
                    if (!encode.IsCompleted) { Log.Warning("[MomentFrames] photo encoding timed out; capture released."); encode.ContinueWith(_ => captured.prepared?.Release()); result = null; }
                    else if (encode.IsFaulted) { Log.Warning("[MomentFrames] large photo encoding failed: " + encode.Exception); result = null; }
                }
            }
            finally
            {
                RestoreCaptureState(() => faces?.Dispose(), "facial posing");
                if (lease == generation)
                {
                    pendingFaces=null;
                    Busy = false;
                    activeRecord = null;
                    pendingFrame = -1;
                    cleanFrame = -1;
                    pendingReactions = null;
                    pendingArea = null;
                    try { done?.Invoke(result); }
                    catch (Exception e) { Log.Error("[MomentFrames] capture callback failed: " + e); }
                }
            }
        }

        private static bool AreaExpandedFor(Map map, int lease)
        {
            if (!pendingArea.HasValue || areaExpandedMap != map || areaExpandedGeneration != lease
                || areaExpandedFrame != Time.frameCount || LastExpandFrame != Time.frameCount) return false;
            CellRect area = pendingArea.Value;
            return LastUnion.minX <= area.minX && LastUnion.maxX >= area.maxX
                && LastUnion.minZ <= area.minZ && LastUnion.maxZ >= area.maxZ;
        }

        /// <summary>A surveyed quadrant can legitimately be entirely unknown. Read fog only; never reveal cells for a photograph.</summary>
        private static bool KnownFogArea(Map map, CellRect? area)
        {
            if (map?.fogGrid == null || !area.HasValue || area.Value.Width < 1 || area.Value.Height < 1) return false;
            foreach (IntVec3 cell in area.Value.Cells)
                if (!map.fogGrid.IsFogged(cell)) return false;
            return true;
        }

        private static bool ExpandViewRect(Map map, Vector3 center, float span, float aspect, CellRect? area = null)
        {
            try
            {
                if (ViewRectF == null || ViewRectFrameF == null) return false;
                CellRect cur = Find.CameraDriver.CurrentViewRect;
                CellRect want = (area ?? CoveredRect(center, span, map, aspect)).ExpandedBy(3).ClipInsideMap(map);
                CellRect union = CellRect.FromLimits(Math.Min(cur.minX, want.minX), Math.Min(cur.minZ, want.minZ), Math.Max(cur.maxX, want.maxX), Math.Max(cur.maxZ, want.maxZ));
                ViewRectF.SetValue(null, union);
                ViewRectFrameF.SetValue(null, Time.frameCount);
                LastExpandFrame = Time.frameCount;
                LastUnion = union;
                return true;
            }
            catch (Exception e)
            {
                Log.Warning("[MomentFrames] could not expand the camera view rect: " + e);
                return false;
            }
        }

        private static Result Render(Map map, Vector3 center, float span, float aspect, bool realLight = false, PhotoReactions.Plan reactions = null, PhotoRecord captionRecord = null, int explicitSide = 0)
        {
            LastRenderFrame = Time.frameCount;
            // Some event hooks attach their typed detail just after RequestNow returns. Choose from the record at the actual shutter,
            // while retaining the user's mode from the start of the request.
            if (reactions?.Record != null) reactions = PhotoReactions.CreatePlan(reactions.Record, map, reactions.Mode);
            int width, height;
            if (explicitSide > 0) SettlementLayout.ImageSize(Mathf.RoundToInt(span * aspect), Mathf.RoundToInt(span), explicitSide, out width, out height);
            else ImageSize(span, aspect, out width, out height);
            int thumbWidth = Mathf.RoundToInt(ThumbHeight * aspect);
            int thumbHeight = ThumbHeight;
            if (explicitSide > 0)
            {
                thumbWidth = Mathf.Clamp(thumbWidth, 1, Mathf.RoundToInt(ThumbHeight * MaxAspect));
                thumbHeight = Mathf.Clamp(Mathf.RoundToInt(thumbWidth / aspect), 1, ThumbHeight);
            }
            Camera cam = Find.Camera;
            CameraDriver drv = Find.CameraDriver;
            RenderTexture prevTarget = cam.targetTexture;
            RenderTexture prevActive = RenderTexture.active;
            Vector3 prevPos = cam.transform.position;
            float prevOrtho = cam.orthographicSize;
            float prevAspect = cam.aspect;
            float prevFarClip = cam.farClipPlane;
            bool prevEnabled = drv.enabled;
            Vector3 prevRoot = RootPosF != null ? (Vector3)RootPosF.GetValue(drv) : prevPos;
            float prevRootSize = RootSizeF != null ? (float)RootSizeF.GetValue(drv) : prevOrtho;
            RenderTexture rt = null, thumbRt = null;
            Texture2D tex = null, thumbTex = null;
            bool bright = !realLight && (MomentFramesMod.Settings == null || MomentFramesMod.Settings.brightPhotos);
            Color prevOverlay = MatBases.LightOverlay.color;
            var capturedCameraColor = Find.CameraColor;
            float prevSaturation = capturedCameraColor != null ? capturedCameraColor.saturation : 1f;
            try
            {
                if (bright)
                {
                    // Same trick the game uses for biomes without sky lighting: a fully transparent light overlay = daylight everywhere.
                    MatBases.LightOverlay.color = new Color(1f, 1f, 1f, 0f);
                    if (Find.CameraColor != null) Find.CameraColor.saturation = 1f;
                }
                rt = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32);
                drv.enabled = false;
                Vector3 target = new Vector3(center.x, 0f, center.z);
                if (RootPosF != null && RootSizeF != null && ApplyPosM != null)
                {
                    RootPosF.SetValue(drv, target);
                    RootSizeF.SetValue(drv, span * 0.5f);
                    ApplyPosM.Invoke(drv, null);
                }
                // Set the final camera state explicitly as well, so camera mods that patch ApplyPositionToGameObject cannot change the framing.
                // Altitude: the driver lowers the camera as it zooms in (15 + (size-11)/49*50), so below ~10 cells of span it sits under the pawn
                // draw altitude (~8.4) and every pawn is clipped away — terrain, buildings and shadows are lower and stayed (r36–r38: wedding
                // and group photos with shadows but no people). Orthographic, so the height changes nothing but the clipping.
                cam.transform.position = new Vector3(target.x, Mathf.Max(cam.transform.position.y, CameraDriver.MinAltitude), target.z);
                // Large survey spans raise the vanilla camera beyond its normal 65-cell altitude.
                // Its fixed 65.5 far plane then clips the ground while higher pawns remain visible.
                cam.farClipPlane = Mathf.Max(prevFarClip, cam.transform.position.y + 2f);
                cam.orthographicSize = span * 0.5f;
                cam.aspect = (float)width / height;
                cam.targetTexture = rt;
                RenderTexture.active = rt;
                // Freeze facts next to the shutter, independently of whether visual reactions are enabled.
                // A missing/failed image never consumes the persistent caption bag.
                var captionContext = PhotoDesc.ObserveCapture(captionRecord, map);
                PhotoForeground.Submit(map, captionRecord);
                NativePhotoOrder.Render(cam);

                tex = new Texture2D(width, height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                tex.Apply(false, false);
                if (NearlyBlack(tex) && !(explicitSide > 0 && AreaExpandedFor(map, generation) && KnownFogArea(map, pendingArea)))
                {
                    Log.Warning($"[MomentFrames] rendered photo was blank (map {map.uniqueID}, center {center}, span {span}); not stored.");
                    return null;
                }

                PhotoReactions.Bake(tex, cam, reactions);
                thumbRt = RenderTexture.GetTemporary(thumbWidth, thumbHeight, 0, RenderTextureFormat.ARGB32);
                Graphics.Blit(tex, thumbRt);
                RenderTexture.active = thumbRt;
                thumbTex = new Texture2D(thumbWidth, thumbHeight, TextureFormat.RGB24, false);
                thumbTex.ReadPixels(new Rect(0, 0, thumbWidth, thumbHeight), 0, 0, false);
                thumbTex.Apply(false, false);

                // Verified in the probe run r02: ReadPixels from the camera-rendered texture is already upright (north on top).
                var capturedPeople = VisiblePawns(map).Where(p => CapturedPhotoRoster.Visible(captionRecord, p, map,
                    center, span, aspect)).Distinct().ToList();
                var measured = new PhotoRecord { center=center, spanCells=span, aspect=aspect };
                measured.SetPeople(capturedPeople); measured.MeasurePeople(map);
                return new Result { pixels = new PhotoPixels(tex.width, tex.height, tex.GetRawTextureData()),
                    thumb = thumbTex.EncodeToPNG(), reactions = reactions, captionContext = captionContext,
                    peopleBox = new Vector4(measured.boxX0, measured.boxY0, measured.boxX1, measured.boxY1),
                    capturedAbs = Find.TickManager.TicksAbs, capturedPeopleNames = capturedPeople.Select(p => p.LabelShort).ToList(),
                    capturedPeopleIds = capturedPeople.Select(p => p.GetUniqueLoadID()).ToList(),
                    subjectsAtShutter = captionRecord == null ? (bool?)null : PhotoStore.RequiredSubjectsPresent(captionRecord, map) };
            }
            catch (Exception e)
            {
                Log.Warning("[MomentFrames] capture failed: " + e);
                return null;
            }
            finally
            {
                RestoreCaptureState(() => MatBases.LightOverlay.color = prevOverlay, "light overlay");
                if (capturedCameraColor != null) RestoreCaptureState(() => capturedCameraColor.saturation = prevSaturation, "camera saturation");
                RestoreCaptureState(() => cam.targetTexture = prevTarget, "camera target");
                RestoreCaptureState(() => cam.aspect = prevAspect, "camera aspect");
                RestoreCaptureState(() => cam.farClipPlane = prevFarClip, "camera far clip");
                RestoreCaptureState(() => cam.orthographicSize = prevOrtho, "camera size");
                RestoreCaptureState(() => cam.transform.position = prevPos, "camera position");
                if (RootPosF != null) RestoreCaptureState(() => RootPosF.SetValue(drv, prevRoot), "driver root position");
                if (RootSizeF != null) RestoreCaptureState(() => RootSizeF.SetValue(drv, prevRootSize), "driver root size");
                RestoreCaptureState(() => drv.enabled = prevEnabled, "camera driver");
                if (ViewRectFrameF != null) RestoreCaptureState(() => ViewRectFrameF.SetValue(null, -1), "view cache");
                RestoreCaptureState(() => RenderTexture.active = prevActive, "active render target");
                if (tex != null) RestoreCaptureState(() => UnityEngine.Object.Destroy(tex), "photo texture");
                if (thumbTex != null) RestoreCaptureState(() => UnityEngine.Object.Destroy(thumbTex), "thumbnail texture");
                if (thumbRt != null) RestoreCaptureState(() => RenderTexture.ReleaseTemporary(thumbRt), "thumbnail render target");
                if (rt != null) RestoreCaptureState(() => RenderTexture.ReleaseTemporary(rt), "photo render target");
            }
        }

        /// <summary>Only undo MF's temporary camera state. One failed restoration must not prevent other state or texture cleanup.</summary>
        private static void RestoreCaptureState(Action restore, string stage)
        {
            try { restore(); }
            catch (Exception e)
            {
                try { Log.Warning("[MomentFrames] could not restore capture " + stage + ": " + e.Message); }
                catch { } // Logging during teardown is also MF cleanup; it must not strand the remaining render textures.
            }
        }

        /// <summary>Width/height of a PNG; when thumb is null, also produces the thumbnail (bilinear on the CPU — a one-off per import,
        /// and no render-texture orientation question). Used for photos imported from another game's folder.</summary>
        public static float MakeThumb(byte[] png, ref byte[] thumb)
        {
            var tex = new Texture2D(2, 2, TextureFormat.RGB24, false);
            Texture2D small = null;
            try
            {
                if (png == null || !tex.LoadImage(png, false)) return DefaultAspect;
                float aspect = tex.height > 0 ? (float)tex.width / tex.height : DefaultAspect;
                if (thumb == null) thumb = MakeThumb(tex);
                return aspect;
            }
            finally
            {
                if (small != null) UnityEngine.Object.Destroy(small);
                UnityEngine.Object.Destroy(tex);
            }
        }

        /// <summary>Build a thumbnail from the readable render without decoding the full PNG again.</summary>
        internal static byte[] MakeThumb(Texture2D source)
        {
            float scale = Mathf.Min(ThumbHeight * MaxAspect / source.width, (float)ThumbHeight / source.height);
            int tw = Mathf.Max(1, Mathf.RoundToInt(source.width * scale)), th = Mathf.Max(1, Mathf.RoundToInt(source.height * scale));
            Texture2D small = null;
            try
            {
                var px = new Color[tw * th];
                for (int y = 0; y < th; y++) for (int x = 0; x < tw; x++)
                    px[y * tw + x] = source.GetPixelBilinear((x + .5f) / tw, (y + .5f) / th);
                small = new Texture2D(tw, th, TextureFormat.RGB24, false);
                small.SetPixels(px); small.Apply(false, false); return small.EncodeToPNG();
            }
            finally { if (small != null) UnityEngine.Object.Destroy(small); }
        }

        /// <summary>Samples ~20000 pixels straight from the RGB24 buffer (no copy of what can be a 30 MB image).</summary>
        private static bool NearlyBlack(Texture2D texture)
        {
            var raw = texture.GetRawTextureData<byte>();
            int px = texture.width * texture.height;
            if (raw.Length < px * 3) return false;
            int stride = Math.Max(1, px / 20000), lit = 0;
            for (int i = 0; i < px; i += stride)
            {
                int o = i * 3;
                if ((raw[o] > 3 || raw[o + 1] > 3 || raw[o + 2] > 3) && ++lit > 2) return false;
            }
            return true;
        }

        /// <summary>Two compares per map per frame; the work happens only in the one frame a photo is rendered in (see OnMapUpdate).</summary>
        [HarmonyPatch(typeof(Map), nameof(Map.MapUpdate))]
        internal static class Patch_MapUpdate_Capture
        {
            private static void Postfix(Map __instance)
            {
                if (__instance != Find.CurrentMap) return;
                try { Dialog_BirthdaySetup.DrawPreview(); }
                catch (Exception e) { Dialog_BirthdaySetup.StopPreviewDrawing();Hooks.Fail("birthday preview", e); }
                try { Dialog_GroupPhotoSetup.DrawPreview(); }
                catch (Exception e) { Dialog_GroupPhotoSetup.StopPreviewDrawing();Hooks.Fail("group preview", e); }
            }
            private static void Prefix(Map __instance)
            {
                try { OnMapUpdate(__instance); }
                catch (Exception e) { Hooks.Fail("capture view", e); }
            }
        }
    }
}
