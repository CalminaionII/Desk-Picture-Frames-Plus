using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace DeskPictureFrame
{
    public class BEDeskPictureFrame : BlockEntity
    {
        /// <summary>Stands in for an image id we do not have the bytes for yet. Resolves to
        /// the shipped `textures/img/placeholder.png`, which is deliberately not a picture so
        /// "still loading" cannot be mistaken for "this is what they chose".</summary>
        public const string PlaceholderImageId = "placeholder";

        public float MeshAngleRad = 0f;

        /// <summary>Who placed it. Kept for attribution only - it no longer has anything to do
        /// with which picture is shown, which is the whole point of the redesign.</summary>
        public string OwnerUid = null;

        internal MeshData blockMesh;

        /// <summary>
        /// Texture code ("image1", "image2", "image") -> content id of the picture this frame
        /// holds. Empty on a frame placed before this existed, which then falls through to the
        /// blocktype's static textures exactly as it always did.
        /// </summary>
        public Dictionary<string, string> ImageIds { get; private set; } = new();

        /// <summary>Tried once per loaded block entity - see UpgradeLegacyFrame.</summary>
        private bool triedLegacyUpgrade;

        public override void Initialize(ICoreAPI api)
        {
            base.Initialize(api);
            if (api.Side == EnumAppSide.Client) { FetchMissingImages(); UpgradeLegacyFrame(); }
            else CollapseToVariantOne();
        }

        /// <summary>
        /// Moves a frame onto variant 1 of its own kind, e.g. frame-desk-landscape-3 ->
        /// frame-desk-landscape-1, keeping the side on a wall frame.
        ///
        /// The numbered variants only still exist because a remap has to be one to one - the
        /// wiki is explicit that two codes cannot become one - so every old code needs its own
        /// target. Once a frame's picture is a real id, the number means nothing, and leaving
        /// it would keep five near-identical blocks alive in every world forever.
        ///
        /// ONLY runs once the frame has ids. Until then the number is the only record of which
        /// picture an old frame showed, and collapsing it would silently reset the picture -
        /// which is exactly what the first attempt at this rename did.
        ///
        /// `ExchangeBlock` swaps the block and leaves the block entity alone, so the ids, the
        /// owner and the mesh angle all come through.
        /// </summary>
        private void CollapseToVariantOne()
        {
            if (Api == null || Api.Side != EnumAppSide.Server) return;
            if (ImageIds.Count == 0) return;

            string[] parts = Block.Code.Path.Split('-');
            bool changed = false;
            for (int i = 0; i < parts.Length; i++)
            {
                // Only a picture slot is ever a bare 2-5; sides and collections are words.
                if (parts[i].Length == 1 && parts[i][0] >= '2' && parts[i][0] <= '5')
                {
                    parts[i] = "1";
                    changed = true;
                }
            }
            if (!changed) return;

            Block target = Api.World.GetBlock(Block.Code.CopyWithPath(string.Join("-", parts)));
            if (target == null) return;

            BlockPos pos = Pos.Copy();
            int id = target.BlockId;
            // Deferred: Initialize runs while the block entity is still being set up.
            Api.World.RegisterCallback(dt => Api?.World?.BlockAccessor?.ExchangeBlock(id, pos), 1);
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
            MeshAngleRad = tree.GetFloat("meshAngleRad", 0f);
            OwnerUid = tree.GetString("ownerUid", null);
            ImageIds = ReadIds(tree.GetTreeAttribute("imageIds"));
            blockMesh = null;
            if (Api?.Side == EnumAppSide.Client) { FetchMissingImages(); UpgradeLegacyFrame(); }
        }

        public override void ToTreeAttributes(ITreeAttribute tree)
        {
            base.ToTreeAttributes(tree);
            tree.SetFloat("meshAngleRad", MeshAngleRad);
            if (OwnerUid != null) tree.SetString("ownerUid", OwnerUid);

            if (ImageIds.Count > 0)
            {
                var idTree = new TreeAttribute();
                foreach (var pair in ImageIds) idTree.SetString(pair.Key, pair.Value);
                tree["imageIds"] = idTree;
            }
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
            if (triedLegacyUpgrade || ImageIds.Count > 0) return;
            if (Api is not ICoreClientAPI capi) return;
            if (OwnerUid == null || OwnerUid != capi.World.Player?.PlayerUID) return;

            triedLegacyUpgrade = true;

            var ids = CaptureLegacyIds(Block);
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

                net.RequestImage(pair.Value, () =>
                {
                    blockMesh = null;
                    MarkDirty(true);
                });
            }
        }

        /// <summary>The picture slots this block has, as texture codes: image1, image2, ...</summary>
        public static IEnumerable<string> SlotsOf(Block block)
        {
            if (block?.Textures == null) yield break;
            foreach (var pair in block.Textures)
            {
                if (pair.Key.StartsWith("image")) yield return pair.Key;
            }
        }

        /// <summary>
        /// The OLD numbered-slot key each of this block's slots used to point at, e.g.
        /// "image1" -> "desk-landscape-images/image1/3".
        ///
        /// Compatibility only. Frames placed before pictures had ids stored no id at all -
        /// their picture was "slot 3 of the placer's folder". The blocktype records the folder
        /// it used to use in `attributes/legacyImageFolders`, and the number comes from the
        /// block code variant, so those frames can still resolve the picture they were placed
        /// with. Nothing new is ever written in this form.
        /// </summary>
        public static Dictionary<string, string> LegacyKeysOf(Block block)
        {
            var keys = new Dictionary<string, string>();

            var folders = block?.Attributes?["legacyImageFolders"];
            if (folders == null || !folders.Exists) return keys;

            foreach (string code in SlotsOf(block))
            {
                string folder = folders[code].AsString(null);
                string slot = block.Variant?[code];
                if (folder != null && slot != null) keys[code] = folder + "/" + slot;
            }
            return keys;
        }

        /// <summary>
        /// The ids a frame should start with when nothing else says otherwise: whatever used
        /// to live in the numbered slot this block code names.
        ///
        /// With one flat folder there is no slot to read from any more, so a NEW frame simply
        /// has no picture until one is chosen. This exists so a migrated install behaves as it
        /// did - a frame bought and placed after migrating still shows the picture that slot
        /// used to hold. Returns nothing on a fresh install, which is correct: the frame shows
        /// the placeholder until you pick something.
        /// </summary>
        public static Dictionary<string, string> CaptureLegacyIds(Block block)
        {
            var ids = new Dictionary<string, string>();
            foreach (var pair in LegacyKeysOf(block))
            {
                string id = DeskPictureFrameImageStore.LegacyIdFor(pair.Value);
                if (id != null) ids[pair.Key] = id;
            }
            return ids;
        }

        /// <summary>The ARL variant name carrying a texture code's image id. "image1" -> "img1".
        /// Deliberately NOT "image1", which is already a block code variant holding the slot
        /// number - reusing the name would collide.</summary>
        public static string VariantNameFor(string textureCode) => "img" + textureCode.Substring("image".Length);

        public void SetImageIds(Dictionary<string, string> ids)
        {
            ImageIds = ids ?? new Dictionary<string, string>();
            blockMesh = null;
            if (Api?.Side == EnumAppSide.Client) FetchMissingImages();
            else CollapseToVariantOne();   // an old frame that just gained ids can drop its number
            MarkDirty(true);
        }

        public override bool OnTesselation(ITerrainMeshPool mesher, ITesselatorAPI tessThreadTesselator)
        {
            if (blockMesh == null) BuildMesh(Api as ICoreClientAPI);
            if (blockMesh != null) mesher.AddMeshData(blockMesh);
            return true;
        }

        private void BuildMesh(ICoreClientAPI capi)
        {
            if (capi == null) return;

            var arlBlockBehavior = Block?.GetBehavior<AttributeRenderingLibrary.BlockBehaviorShapeTexturesFromAttributes>();
            var arlBeBehavior = this.GetBehavior<AttributeRenderingLibrary.BlockEntityBehaviorShapeTexturesFromAttributes>();

            if (arlBlockBehavior != null && arlBeBehavior != null)
            {
                blockMesh = arlBlockBehavior
                    .GetOrCreateMesh(BuildVariants(arlBeBehavior), Block.Shape, Pos, Block.Code.Path, null)
                    .Clone();
            }
            else
            {
                capi.Tesselator.TesselateBlock(Block, out blockMesh);
            }

            if (blockMesh != null && MeshAngleRad != 0f)
                blockMesh = blockMesh.Rotate(new Vec3f(0.5f, 0.0f, 0.5f), 0f, MeshAngleRad, 0f);
        }

        /// <summary>
        /// The variants this frame's mesh is built from: the block entity's own (metal), the
        /// block code's (the slot numbers), and one `imgN` per picture slot.
        ///
        /// The `imgN` entries are what make each picture a DISTINCT asset path, and they also
        /// give ARL's mesh cache the right granularity for free - it keys on
        /// "{block.Code}-{variants}", so two frames holding different pictures cannot share a
        /// cached mesh.
        ///
        /// An id we do not have the bytes for yet becomes the placeholder rather than being
        /// left out: leaving it out would drop the whole set back to the `*` texture entry and
        /// render this viewer's OWN picture, which is exactly the silent wrongness this
        /// redesign exists to remove.
        /// </summary>
        private AttributeRenderingLibrary.Variants BuildVariants(
            AttributeRenderingLibrary.BlockEntityBehaviorShapeTexturesFromAttributes arlBeBehavior)
        {
            // NOT Variants.Clone(). ARL's Clone does `new() { Elements = Elements }`, which
            // copies the REFERENCE - mutating the "clone" mutates the original, and would
            // write these ids into the block entity's persisted variants.
            var variants = new AttributeRenderingLibrary.Variants();

            if (Block?.Variant != null)
            {
                foreach (KeyValuePair<string, string> pair in Block.Variant)
                    variants.Set(pair.Key, pair.Value);
            }

            var own = arlBeBehavior?.Variants;
            if (own != null) variants.Set(own.GetElements());

            var legacyKeys = LegacyKeysOf(Block);

            foreach (string code in SlotsOf(Block))
            {
                string id = ImageIds.TryGetValue(code, out string chosen) ? chosen : null;

                // A frame from before ids existed keeps the picture it was placed with, via
                // the folder+slot it used to point at. It gets a real id the moment anyone
                // re-captures or re-picks it.
                if (id == null && legacyKeys.TryGetValue(code, out string oldKey))
                    id = DeskPictureFrameImageStore.LegacyIdFor(oldKey);

                if (id == null) continue;

                variants.Set(VariantNameFor(code),
                             DeskPictureFrameImageStore.ClientHas(id) ? id : PlaceholderImageId);
            }

            return variants;
        }
    }
}
