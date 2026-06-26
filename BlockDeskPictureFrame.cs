using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace DeskPictureFrame
{
    public class BlockDeskPictureFrame : Block
    {
        public override bool TryPlaceBlock(IWorldAccessor world, IPlayer byPlayer, ItemStack itemstack, BlockSelection blockSel, ref string failureCode)
        {
            bool placed = base.TryPlaceBlock(world, byPlayer, itemstack, blockSel, ref failureCode);
            if (!placed) return false;

            if (world.BlockAccessor.GetBlockEntity(blockSel.Position) is BEDeskPictureFrame be)
            {
                BlockPos blockPos = blockSel.DidOffset ? blockSel.Position.AddCopy(blockSel.Face.Opposite) : blockSel.Position;
                double dx = byPlayer.Entity.Pos.X - (blockPos.X + blockSel.HitPosition.X);
                double dz = byPlayer.Entity.Pos.Z - (blockPos.Z + blockSel.HitPosition.Z);
                float yaw = (float)Math.Atan2(dx, dz);
                float angle = (float)Math.Round(yaw / (GameMath.PI / 2f)) * (GameMath.PI / 2f);

                be.MeshAngleRad = angle;
                be.OwnerUid = byPlayer.PlayerUID;
                be.blockMesh = null;

                if (world.Side == EnumAppSide.Server)
                    be.MarkDirty(true);
                else
                    be.InitializeWithAngle();
            }

            return true;
        }
        public override ItemStack[] GetDrops(IWorldAccessor world, BlockPos pos, IPlayer byPlayer, float dropQuantityMultiplier = 1)
        {
            // Only handle crate drops, everything else uses default behaviour
            if (!Code.Path.StartsWith("deskframebox-"))
                return base.GetDrops(world, pos, byPlayer, dropQuantityMultiplier);

            var be = world.BlockAccessor.GetBlockEntity(pos);
            var arlBehavior = be?.GetBehavior<AttributeRenderingLibrary.BlockEntityBehaviorShapeTexturesFromAttributes>();

            string metal = "brass";
            if (arlBehavior?.Variants != null)
                metal = arlBehavior.Variants.Get("metal") ?? "brass";

            string collection = Code.Path.Replace("deskframebox-", "");

            List<ItemStack> drops = new List<ItemStack>();

            for (int i = 1; i <= 5; i++)
            {
                AssetLocation frameCode;

                switch (collection)
                {
                    case "portraitsingle":
                        frameCode = new AssetLocation($"deskpictureframe:deskportraitsingle-{i}");
                        break;
                    case "landscapesingle":
                        frameCode = new AssetLocation($"deskpictureframe:desklandscapesingle-{i}");
                        break;
                    case "portraitwall":
                        frameCode = new AssetLocation($"deskpictureframe:wall-portrait-{i}-north");
                        break;
                    case "landscapewall":
                        frameCode = new AssetLocation($"deskpictureframe:wall-landscape-{i}-north");
                        break;
                    default:
                        continue;
                }

                Block resolvedBlock = world.GetBlock(frameCode);
                if (resolvedBlock == null)
                {
                    world.Logger.Warning($"[DeskPictureFrame] Could not resolve block for drop: {frameCode}");
                    continue;
                }

                ItemStack stack = new ItemStack(resolvedBlock);
                if (stack.Block != null)
                {
                    stack.Attributes.GetOrAddTreeAttribute("types").SetString("metal", metal);
                    drops.Add(stack);
                }
            }

            return drops.ToArray();
        }
        public override void OnBlockBroken(IWorldAccessor world, BlockPos pos, IPlayer byPlayer, float dropQuantityMultiplier = 1)
        {
            if (world.Side == EnumAppSide.Server)
            {
                ItemStack[] drops = GetDrops(world, pos, byPlayer, dropQuantityMultiplier);
                if (drops != null)
                {
                    foreach (ItemStack drop in drops)
                    {
                        if (!byPlayer.InventoryManager.TryGiveItemstack(drop))
                        {
                            // Inventory full, drop on ground instead
                            world.SpawnItemEntity(drop, pos.ToVec3d().Add(0.5, 0.5, 0.5));
                        }
                    }
                }
                world.BlockAccessor.SetBlock(0, pos);
            }
        }
    }
}
    


