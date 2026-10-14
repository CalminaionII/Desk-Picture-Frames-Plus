using System.Collections.Generic;
using DPF;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace DeskPictureFrame
{
    public class BEDeskPictureFrame : BlockEntity, IAutoBoxesOwner
    {
        /// <summary>Stands in for an image id we do not have the bytes for yet. Resolves to
        /// the shipped `textures/img/placeholder.png`, which is deliberately not a picture so
        /// "still loading" cannot be mistaken for "this is what they chose".</summary>
        public const string PlaceholderImageId = "placeholder";

        Cuboidf autoBox;
        Dictionary<(int x, int y, int z), Cuboidf> autoPieces = new();
        List<(int x, int y, int z)> autoHeld = new();

        public float MeshAngleRad = 0f;

        /// <summary>Who placed it. Kept for attribution only - it no longer has anything to do
        /// with which picture is shown, which is the whole point of the redesign.</summary>
        public string OwnerUid = null;

        /// <summary>
        /// True once somebody has actually chosen what this frame shows - INCLUDING choosing
        /// nothing.
        ///
        /// **IT IS WHAT SEPARATES "EMPTY" FROM "NEVER SET".** The legacy fallback below exists so
        /// a frame placed before pictures had ids keeps the one it was placed with, by resolving
        /// the numbered slot it used to point at. Without this flag that fallback fires for a
        /// slot somebody has just deliberately cleared, so the old picture comes straight back -
        /// and the picker, which reads the real ids, correctly shows "no picture" while the frame
        /// shows something. Reported in game 2026-09-04, twice over: clearing a slot, and picking
        /// a frame up and putting it down again.
        ///
        /// Persisted, and carried on the itemstack, so it survives both.
        /// </summary>
        public bool ImagesChosen;

        /// <summary>
        /// The metal this frame is made of ("copper"), from the stack it was placed from. Null on a
        /// frame with none recorded, which draws in the shape's own brass.
        ///
        /// Saved under the same <c>types</c> tree Attribute Rendering Library used, so a frame placed
        /// before 1.2.0 keeps its metal - see <see cref="FrameMesher.TypesKey"/>.
        /// </summary>
        public string Metal;

        internal MeshData blockMesh;

        /// <summary>
        /// Texture code ("image1", "image2", "image") -> content id of the picture this frame
        /// holds. Empty on a frame placed before this existed, which then falls through to the
        /// blocktype's static textures exactly as it always did.
        /// </summary>
        public Dictionary<string, string> ImageIds { get; private set; } = new();

        /// <summary>Tried once per loaded block entity - see UpgradeLegacyFrame.</summary>
        private bool triedLegacyUpgrade;

        /// <summary>
        /// The OLD numbered-slot key each slot pointed at ("image1" -> "desk-landscape-images/image1/3"),
        /// carried over when an old numbered frame became its number-free code (1.2.0). Only on a frame
        /// from before pictures had ids that nobody has chosen a picture for since; the owner's client
        /// still turns it into a real id on sight (UpgradeLegacyFrame). Cleared once a choice is made.
        /// </summary>
        public Dictionary<string, string> LegacyKeys { get; private set; } = new();


        public override void Initialize(ICoreAPI api)
        {
            base.Initialize(api);
            if (api.Side == EnumAppSide.Client) { FetchMissingImages(); UpgradeLegacyFrame(); }
            else ConvertLegacy();

            AutoBoxesMeasureTurned();

            // A boxed set that was given a picture before that was blocked goes back to plain.
            // Opening one already gives brand-new empty frames, so nothing chosen is lost.
            if (api.Side == EnumAppSide.Server && ImageIds.Count > 0 && (Block as BlockDeskPictureFrame)?.IsCollection == true)
                RegisterDelayedCallback(_ => SetImageIds(new Dictionary<string, string>()), 1);
        }


        /// <summary>
        /// Turns an OLD numbered frame (frame-desk-landscape-3, frame-wall-portrait-1-north) into its
        /// number-free code (frame-desk-landscape, frame-wall-portrait) - the blocktype's
        /// <c>convertsTo</c>. Calm, 2026-10-09: one code per design, "as minimal as possible".
        ///
        /// NOTHING IS LOST. `ExchangeBlock` swaps the block and leaves this block entity alone, so
        /// the ids, metal, owner and angle all come through. A frame from before ids that still
        /// depends on its number for its picture keeps that as <see cref="LegacyKeys"/>; an old wall
        /// frame takes its angle from its side code.
        /// </summary>
        private void ConvertLegacy()
        {
            if (Api == null || Api.Side != EnumAppSide.Server) return;
            string target = Block?.Attributes?["convertsTo"].AsString(null);
            if (target == null) return;
            Block to = Api.World.GetBlock(new AssetLocation(target));
            if (to == null) return;

            if (!ImagesChosen && ImageIds.Count == 0 && LegacyKeys.Count == 0) LegacyKeys = LegacyKeysFor(Block);
            // An old wall frame faces out of the wall its side code names. Its saved angle came from
            // where the placer stood, which could disagree - and the wall check would then look for
            // the wrong wall. The side code always knew the real one.
            if (Block.Variant?["side"] is string side && BlockFacing.FromCode(side) is { } toWall)
                MeshAngleRad = BlockDeskPictureFrame.AngleFacing(toWall.Opposite);

            BlockPos pos = Pos.Copy();
            int id = to.BlockId;
            // Deferred: Initialize runs while the block entity is still being set up.
            Api.World.RegisterCallback(dt => Api?.World?.BlockAccessor?.ExchangeBlock(id, pos), 1);
        }

        public override void OnExchanged(Block block)
        {
            base.OnExchanged(block);
            blockMesh = null;
            if (Api == null) return;
            AutoBoxesMeasureTurned();
            if (Api.Side == EnumAppSide.Server) autoHeld = AutoBoxes.Sync(Api.World, Pos, autoPieces.Keys, autoHeld);
        }

        public void InitializeWithAngle()
        {
            blockMesh = null;
            if (Api?.Side == EnumAppSide.Client)
            {
                FetchMissingImages();
                MarkDirty(true);
            }
        }

        public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor world)
        {
            base.FromTreeAttributes(tree, world);
            autoHeld = AutoBoxes.Read(tree.GetString("autoboxesHeld"));
            MeshAngleRad = tree.GetFloat("meshAngleRad", 0f);
            if (Api != null) AutoBoxesMeasureTurned();
            OwnerUid = tree.GetString("ownerUid", null);
            Metal = FrameMesher.MetalOf(tree);
            ImagesChosen = tree.GetBool("imagesChosen", false);
            ImageIds = ReadIds(tree.GetTreeAttribute("imageIds"));
            LegacyKeys = ReadStrings(tree.GetTreeAttribute("legacyKeys"));
            blockMesh = null;
            if (Api?.Side == EnumAppSide.Client) { FetchMissingImages(); UpgradeLegacyFrame(); }
        }

        public override void ToTreeAttributes(ITreeAttribute tree)

        {
            base.ToTreeAttributes(tree);
            tree.SetString("autoboxesHeld", AutoBoxes.Write(autoHeld));
            tree.SetFloat("meshAngleRad", MeshAngleRad);
            if (OwnerUid != null) tree.SetString("ownerUid", OwnerUid);
            FrameMesher.WriteMetal(tree, Metal);
            if (ImagesChosen) tree.SetBool("imagesChosen", true);

            if (ImageIds.Count > 0)
            {
                var idTree = new TreeAttribute();
                foreach (var pair in ImageIds) idTree.SetString(pair.Key, pair.Value);
                tree["imageIds"] = idTree;
            }
            WriteStrings(tree, "legacyKeys", LegacyKeys);
        }

        /// <summary>A tree of plain strings (the legacy keys) - no id check, they are folder paths.</summary>
        public static Dictionary<string, string> ReadStrings(ITreeAttribute stringTree)
        {
            var found = new Dictionary<string, string>();
            if (stringTree == null) return found;
            foreach (var entry in stringTree)
                if ((entry.Value as StringAttribute)?.value is string value) found[entry.Key] = value;
            return found;
        }

        public static void WriteStrings(ITreeAttribute tree, string key, IReadOnlyDictionary<string, string> values)
        {
            if (values == null || values.Count == 0) return;
            var sub = new TreeAttribute();
            foreach (var pair in values) sub.SetString(pair.Key, pair.Value);
            tree[key] = sub;
        }

        public static Dictionary<string, string> ReadIds(ITreeAttribute idTree)
        {
            var ids = new Dictionary<string, string>();
            if (idTree == null) return ids;

            foreach (var entry in idTree)
            {
                string id = (entry.Value as StringAttribute)?.value;
                if (DeskPictureFrameImageStore.IsWellFormedId(id)) ids[entry.Key] = id;
            }
            return ids;
        }

        /// <summary>
        /// Bakes a pre-id frame's picture into a real image id, once.
        ///
        /// A frame placed before this system existed stores no id; its picture is recovered
        /// from the folder+slot it used to point at. But that recovery is CLIENT-LOCAL - it
        /// depends on a migration this client ran - so anyone joining fresh would see a
        /// placeholder where the owner sees a picture. That is precisely the "everyone sees
        /// something different" problem the whole redesign removes.
        ///
        /// So the owner's own client upgrades them: once, on first sight, it sends the
        /// recovered ids to the server, which persists and syncs them like any other choice.
        /// After that the frame is indistinguishable from one placed today and looks the same
        /// to everybody. Nothing has to be broken and replaced.
        ///
        /// Only the owner can do this, because only their folder held the picture.
        /// </summary>
        private void UpgradeLegacyFrame()
        {
            if (triedLegacyUpgrade || ImagesChosen || ImageIds.Count > 0) return;
            if (Api is not ICoreClientAPI capi) return;
            if (OwnerUid == null || OwnerUid != capi.World.Player?.PlayerUID) return;

            triedLegacyUpgrade = true;

            var ids = CaptureLegacyIds();
            if (ids.Count == 0) return;

            DeskPictureFrameNetworkManager.ClientInstance?.SendFrameImages(Pos.Copy(), ids);
            capi.Logger.Notification(
                $"[DeskPictureFrame] Kept the picture on an older frame at {Pos} by giving it a permanent id.");
        }

        /// <summary>Asks the server for any picture this frame uses that we do not have.
        /// Main thread only - it touches the disk and the network.</summary>
        private void FetchMissingImages()
        {
            var net = DeskPictureFrameNetworkManager.ClientInstance;
            if (net == null) return;

            foreach (var pair in ImageIds)
            {
                if (DeskPictureFrameImageStore.ClientHas(pair.Value)) continue;

                // OURS FIRST. The cache is filled on demand now, so a picture we own is simply
                // not in it until something needs to draw it - and reading our own file beats
                // asking the server for bytes already sitting on this disk.
                if (DeskPictureFrameImageStore.EnsureCached(Api as ICoreClientAPI, pair.Value))
                {
                    blockMesh = null;
                    MarkDirty(true);
                    continue;
                }

                net.RequestImage(pair.Value, () =>
                {
                    blockMesh = null;
                    MarkDirty(true);
                });
            }
        }

        /// <summary>The picture slots this block has, as texture codes: image1, image2, ...</summary>
        /// <summary>The picture slots a frame has, from its texture codes.
        ///
        /// **CLIENT ONLY. `Block.Textures` is empty on a server** - textures are a render-side
        /// concern - so anything that has to work server-side must get its slots elsewhere.
        /// FrameImageIdsOf reads them out of the attribute names for exactly that reason. This
        /// is fine where it is used: building a mesh, and listing slots in the picker.</summary>
        public static IEnumerable<string> SlotsOf(Block block)
        {
            if (block?.Textures == null) yield break;
            foreach (var pair in block.Textures)
            {
                if (pair.Key.StartsWith("image")) yield return pair.Key;
            }
        }

        /// <summary>
        /// The OLD numbered-slot key each slot of an old numbered block pointed at, e.g.
        /// "image1" -> "desk-landscape-images/image1/3".
        ///
        /// Compatibility only. Frames placed before pictures had ids stored no id at all - their
        /// picture was "slot 3 of the placer's folder". The old blocktype records the folder it used
        /// in `attributes/legacyImageFolders`, and the number is the block code variant. Read from
        /// the VARIANTS (named after the slots), not the textures, so it works on a server too.
        /// Empty for a number-free frame - those carry <see cref="LegacyKeys"/> instead.
        /// </summary>
        public static Dictionary<string, string> LegacyKeysFor(Block block)
        {
            var keys = new Dictionary<string, string>();

            var folders = block?.Attributes?["legacyImageFolders"];
            if (folders == null || !folders.Exists || block.Variant == null) return keys;

            foreach (var pair in block.Variant)
            {
                if (!pair.Key.StartsWith("image")) continue;
                string folder = folders[pair.Key].AsString(null);
                if (folder != null && pair.Value != null) keys[pair.Key] = folder + "/" + pair.Value;
            }
            return keys;
        }

        /// <summary>This frame's old-number keys: carried over, else its own block's (an old numbered block not converted yet).</summary>
        public Dictionary<string, string> EffectiveLegacyKeys => LegacyKeys.Count > 0 ? LegacyKeys : LegacyKeysFor(Block);

        /// <summary>
        /// The ids a frame should start with when nothing else says otherwise: whatever used to
        /// live in the old numbered slot it came from. Empty on a fresh install, which is correct:
        /// the frame shows the placeholder until you pick something.
        /// </summary>
        public Dictionary<string, string> CaptureLegacyIds()
        {
            var ids = new Dictionary<string, string>();
            foreach (var pair in EffectiveLegacyKeys)
            {
                string id = DeskPictureFrameImageStore.LegacyIdFor(pair.Value);
                if (id != null) ids[pair.Key] = id;
            }
            return ids;
        }

        /// <summary>The name a texture code's image id is kept under on a stack. "image1" -> "img1".
        /// Deliberately NOT "image1", which is already a block code variant holding the slot
        /// number - reusing the name would collide.</summary>
        public static string VariantNameFor(string textureCode) => "img" + textureCode.Substring("image".Length);

        /// <summary>The inverse: "img1" -> "image1", and null for anything that is not one of
        /// ours. Reading the slot out of the NAME is what lets a stack be understood on a
        /// server, where Block.Textures is empty and there is no slot list to consult.</summary>
        public static string TextureCodeFor(string variantName)
        {
            if (variantName == null || !variantName.StartsWith("img")) return null;
            return "image" + variantName.Substring("img".Length);
        }

        public void SetImageIds(Dictionary<string, string> ids)
        {
            ImageIds = ids ?? new Dictionary<string, string>();

            // AN EMPTY MAP IS STILL A CHOICE. Setting this only when ids arrived would leave
            // "clear this frame" indistinguishable from "never touched", which is the whole bug.
            ImagesChosen = true;
            LegacyKeys = new Dictionary<string, string>();   // a choice is made - the old number means nothing now
            blockMesh = null;
            if (Api?.Side == EnumAppSide.Client) FetchMissingImages();
            MarkDirty(true);
        }

        public override bool OnTesselation(ITerrainMeshPool mesher, ITesselatorAPI tessThreadTesselator)
        {
            var mesh = blockMesh ?? BuildMesh(Api as ICoreClientAPI);
            if (mesh != null) mesher.AddMeshData(mesh);
            return true;
        }
        public override void OnBlockPlaced(ItemStack byItemStack = null)
        {
            base.OnBlockPlaced(byItemStack);
            // Both sides: the metal comes off the stack in the placer's hand, as ARL did it, and an
            // old frame's picture keys with it (a converted old numbered stack, see OnTransitionNow).
            if (byItemStack != null)
            {
                Metal = FrameMesher.MetalOf(byItemStack.Attributes);
                LegacyKeys = ReadStrings(byItemStack.Attributes.GetTreeAttribute("legacyKeys"));
            }
            if (Api.Side == EnumAppSide.Server) autoHeld = AutoBoxes.Sync(Api.World, Pos, autoPieces.Keys, autoHeld);
        }
        public override void OnBlockRemoved()
        {
            if (Api.Side == EnumAppSide.Server) AutoBoxes.ReleaseAll(Api.World, Pos, autoHeld);
            base.OnBlockRemoved();
        }

        /// <summary>
        /// The placed frame's mesh, turned to its angle. Kept in <see cref="blockMesh"/> unless a
        /// texture was still on its way into the atlas - then the frame is drawn again once it is in.
        /// </summary>
        private MeshData BuildMesh(ICoreClientAPI capi)
        {
            if (capi == null || Block == null) return null;

            MeshData mesh = FrameMesher.Placed(capi, Block, new FrameLook(Metal, ShownIds()), Pos, out bool waiting)?.Clone();
            if (mesh == null)
            {
                capi.Tesselator.TesselateBlock(Block, out mesh);
                waiting = false;
            }

            if (mesh != null && MeshAngleRad != 0f)
                mesh = mesh.Rotate(new Vec3f(0.5f, 0.0f, 0.5f), 0f, MeshAngleRad, 0f);
            if (!waiting) blockMesh = mesh;
            return mesh;
        }

        /// <summary>
        /// The picture id for each of this frame's slots: its chosen one, or for a frame from before
        /// ids the one its old numbered slot held, or the placeholder.
        ///
        /// An id we do not have the bytes for yet is drawn as the placeholder (FrameLook.Shown)
        /// rather than left out: leaving it out would fall back to the blocktype's texture and,
        /// in the old ARL days, render this viewer's OWN picture - the silent wrongness the
        /// redesign exists to remove.
        /// </summary>
        private Dictionary<string, string> ShownIds()
        {
            var ids = new Dictionary<string, string>();
            var legacyKeys = EffectiveLegacyKeys;

            foreach (string code in SlotsOf(Block))
            {
                string id = ImageIds.TryGetValue(code, out string chosen) ? chosen : null;

                // A frame from before ids existed keeps the picture it was placed with, via
                // the folder+slot it used to point at. It gets a real id the moment anyone
                // re-captures or re-picks it.
                //
                // NOT ONCE A CHOICE HAS BEEN MADE. After that an empty slot means empty, and
                // guessing from the old numbering would put back a picture somebody has just
                // removed.
                if (id == null && !ImagesChosen && legacyKeys.TryGetValue(code, out string oldKey))
                    id = DeskPictureFrameImageStore.LegacyIdFor(oldKey);

                // AN EMPTY SLOT SETS THE PLACEHOLDER EXPLICITLY, so "no picture" can never show
                // what the slot held before.
                ids[code] = id ?? PlaceholderImageId;
            }
            return ids;
        }
        public Cuboidf[] AutoBoxesAt(BlockPos cell)
        {
            var o = (cell.X - Pos.X, cell.Y - Pos.Y, cell.Z - Pos.Z);
            return autoPieces.ContainsKey(o) ? [AutoBoxes.Whole(autoBox, o)] : [];
        }

        public Cuboidf[] AutoCollisionAt(BlockPos cell) =>
            AutoBoxes.PieceAt(autoBox, (cell.X - Pos.X, cell.Y - Pos.Y, cell.Z - Pos.Z));
        void AutoBoxesMeasureTurned()
        {
            autoBox = AutoBoxesMeasure.FromModel(Api, Block, false)?.RotatedCopyRad(0f, MeshAngleRad, 0f, new Vec3d(0.5, 0.0, 0.5));
            autoPieces = AutoBoxes.Cut(autoBox);
        }

        public void AutoBoxesAngleChanged()
        {
            AutoBoxesMeasureTurned();
            if (Api.Side == EnumAppSide.Server) autoHeld = AutoBoxes.Sync(Api.World, Pos, autoPieces.Keys, autoHeld);
        }
    }

}


