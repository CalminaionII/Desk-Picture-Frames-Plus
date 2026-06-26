using System.Collections.Generic;
using System.IO;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
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
        private static HashSet<string> registeredOrigins = new HashSet<string>();

        public void InitializeWithAngle()
        {
            blockMesh = null;
            if (Api?.Side == EnumAppSide.Client)
                MarkDirty(true);
        }

        public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor world)
        {
            base.FromTreeAttributes(tree, world);
            MeshAngleRad = tree.GetFloat("meshAngleRad", 0f);
            OwnerUid = tree.GetString("ownerUid", null);
            blockMesh = null;
        }

        public override void ToTreeAttributes(ITreeAttribute tree)
        {
            base.ToTreeAttributes(tree);
            tree.SetFloat("meshAngleRad", MeshAngleRad);
            if (OwnerUid != null)
                tree.SetString("ownerUid", OwnerUid);
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

            bool isRemoteOwner = OwnerUid != null && OwnerUid != capi.World.Player.PlayerUID;

            if (isRemoteOwner)
            {
                string remoteTexturesFolder = DeskPictureFrameConstants.RemotePlayerTexturesFolder(OwnerUid);

                bool hasTextures = Directory.Exists(remoteTexturesFolder);

                if (!hasTextures)
                {
                    // Don't have textures yet, request them and use placeholder for now
                    DeskPictureFrameNetworkManager.Instance?.RequestTexturesForFrame(OwnerUid, () =>
                    {
                        blockMesh = null;
                        MarkDirty(true);
                    });

                    // Tessellate with own textures as placeholder
                    BuildMesh(capi);
                    return;
                }

                // Have the remote textures, register their folder as an asset origin if not already done
                if (!registeredOrigins.Contains(OwnerUid))
                {
                    string remotePlayerRoot = DeskPictureFrameConstants.RemotePlayerFolder(OwnerUid);
                    capi.Assets.AddModOrigin(DeskPictureFrameConstants.ModId, remotePlayerRoot);
                    registeredOrigins.Add(OwnerUid);
                    capi.Logger.Notification($"[DeskPictureFrame] Registered remote origin for: {OwnerUid}");
                }
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
