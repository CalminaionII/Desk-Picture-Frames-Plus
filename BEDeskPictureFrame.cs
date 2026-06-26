using System.Collections.Concurrent;
using System.IO;
using System.Text.RegularExpressions;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace DeskPictureFrame
{
    public class BEDeskPictureFrame : BlockEntity
    {
        public float MeshAngleRad = 0f;
        public string OwnerUid = null;
        internal MeshData blockMesh;
        private bool remoteTexturesRequested = false;
        private bool remoteTexturesReady = false;
        private static ConcurrentDictionary<string, byte> registeredOrigins = new();
        private static readonly Regex SafeUidRegex = new Regex(@"^[a-zA-Z0-9_-]+$", RegexOptions.Compiled);

        public void InitializeWithAngle()
        {
            blockMesh = null;
            if (Api?.Side == EnumAppSide.Client)
                MarkDirty(true);
        }

        public override void Initialize(ICoreAPI api)
        {
            base.Initialize(api);

            if (api.Side == EnumAppSide.Client)
                CheckRemoteTextures(api as ICoreClientAPI);
        }

        public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor world)
        {
            base.FromTreeAttributes(tree, world);
            MeshAngleRad = tree.GetFloat("meshAngleRad", 0f);
            OwnerUid = tree.GetString("ownerUid", null);
            blockMesh = null;
            remoteTexturesReady = false;
            remoteTexturesRequested = false;
        }

        public override void ToTreeAttributes(ITreeAttribute tree)
        {
            base.ToTreeAttributes(tree);
            tree.SetFloat("meshAngleRad", MeshAngleRad);
            if (OwnerUid != null)
                tree.SetString("ownerUid", OwnerUid);
        }

        private void CheckRemoteTextures(ICoreClientAPI capi)
        {
            if (capi == null) return;
            if (OwnerUid == null || capi.World.Player == null) return;
            if (OwnerUid == capi.World.Player.PlayerUID) return;

            if (!SafeUidRegex.IsMatch(OwnerUid))
            {
                capi.Logger.Warning($"[DeskPictureFrame] Rejected invalid owner UID: {OwnerUid}");
                return;
            }

            string remoteTexturesFolder = Path.Combine(
                GamePaths.DataPath, "ModData", "deskpictureframe", "remoteplayers", OwnerUid, "textures");

            if (Directory.Exists(remoteTexturesFolder))
            {
                RegisterRemoteOrigin(capi, OwnerUid);
                remoteTexturesReady = true;
                return;
            }

            if (!remoteTexturesRequested)
            {
                remoteTexturesRequested = true;
                DeskPictureFrameNetworkManager.Instance?.RequestTexturesForFrame(OwnerUid, () =>
                {
                    RegisterRemoteOrigin(capi, OwnerUid);
                    remoteTexturesReady = true;
                    blockMesh = null;
                    MarkDirty(true);
                });
            }
        }

        private static void RegisterRemoteOrigin(ICoreClientAPI capi, string ownerUid)
        {
            if (registeredOrigins.TryAdd(ownerUid, 0))
            {
                string remotePlayerRoot = Path.Combine(
                    GamePaths.DataPath, "ModData", "deskpictureframe", "remoteplayers", ownerUid);
                capi.Assets.AddModOrigin("deskpictureframe", remotePlayerRoot);
                capi.Logger.Notification($"[DeskPictureFrame] Registered remote origin for: {ownerUid}");
            }
        }

        public override bool OnTesselation(ITerrainMeshPool mesher, ITesselatorAPI tessThreadTesselator)
        {
            if (blockMesh == null) UpdateBlockMesh(Api as ICoreClientAPI);
            if (blockMesh != null) mesher.AddMeshData(blockMesh);
            return true;
        }

        private void UpdateBlockMesh(ICoreClientAPI capi)
        {
            if (capi == null) return;

            bool isRemoteOwner = OwnerUid != null
                && capi.World.Player != null
                && OwnerUid != capi.World.Player.PlayerUID;

            if (isRemoteOwner && !SafeUidRegex.IsMatch(OwnerUid))
            {
                capi.Logger.Warning($"[DeskPictureFrame] Rejected invalid owner UID: {OwnerUid}");
                BuildMesh(capi);
                return;
            }

            if (isRemoteOwner && !remoteTexturesReady)
            {
                CheckRemoteTextures(capi);
            }

            BuildMesh(capi);
        }

        private void BuildMesh(ICoreClientAPI capi)
        {
            var arlBlockBehavior = Block?.GetBehavior<AttributeRenderingLibrary.BlockBehaviorShapeTexturesFromAttributes>();
            var arlBeBehavior = this.GetBehavior<AttributeRenderingLibrary.BlockEntityBehaviorShapeTexturesFromAttributes>();

            if (arlBlockBehavior != null && arlBeBehavior != null)
            {
                blockMesh = arlBlockBehavior.GetOrCreateMesh(arlBeBehavior.Variants, Block.Shape, Pos, Block.Code.Path, null).Clone();
            }
            else
            {
                capi.Tesselator.TesselateBlock(Block, out blockMesh);
            }

            if (blockMesh != null && MeshAngleRad != 0f)
                blockMesh = blockMesh.Rotate(new Vec3f(0.5f, 0.0f, 0.5f), 0f, MeshAngleRad, 0f);
        }
    }
}
