using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
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

                // A FRAME THAT ALREADY HOLDS PICTURES KEEPS THEM, and the SERVER applies
                // them, because it has the itemstack right here.
                //
                // This used to be done from the client, which sent the ids in a packet - and
                // that races the placement itself: the packet could reach the server before it
                // had created the block entity, whereupon the ids were silently dropped and
                // the frame came back blank. Reading the stack server-side has no race at all.
                var carried = FrameImageIdsOf(itemstack);

                if (world.Side == EnumAppSide.Server)
                {
                    if (carried.Count > 0) be.SetImageIds(carried);
                    be.MarkDirty(true);
                }
                else
                {
                    be.InitializeWithAngle();

                    // Only a frame with no pictures of its own needs the client: recovering
                    // what an old numbered slot used to hold depends on this player's folder,
                    // which the server cannot see. Not time critical, and it retries on sight.
                    if (carried.Count == 0)
                    {
                        var legacy = BEDeskPictureFrame.CaptureLegacyIds(this);
                        if (legacy.Count > 0)
                        {
                            DeskPictureFrameNetworkManager.ClientInstance
                                ?.SendFrameImages(blockSel.Position, legacy);
                        }
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// Sneak-right-click re-captures the frame from the player's CURRENT folder.
        ///
        /// A placed frame keeps the picture it was placed with, which is the point of the
        /// redesign - but it removes the old side effect where editing a file silently updated
        /// every frame in the world. This puts that back as a deliberate, per-frame action.
        ///
        /// Reads the KEY (ShiftKey) rather than the Sneak ACTION, as vanilla's own cabinet and
        /// mannequin do: reading the action makes the behaviour depend on the player's keybinds
        /// and look intermittent (shared reference sect.7). WorldData.EntityControls is used because
        /// Entity.Controls is filled from the local keyboard and the two can disagree for a frame.
        /// </summary>
        public override bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
        {
            if (byPlayer?.WorldData?.EntityControls?.ShiftKey == true)
            {
                var be2 = world.BlockAccessor.GetBlockEntity(blockSel.Position) as BEDeskPictureFrame;

                if (world.Side == EnumAppSide.Server)
                {
                    // Someone else's claim: say so and change nothing. The packet handler
                    // enforces this too - that is the gate a modified client cannot skip -
                    // but refusing here means no block entity is spawned for a player who
                    // was never going to be allowed to use it.
                    if (!world.Claims.TryAccess(byPlayer, blockSel.Position, EnumBlockAccessFlags.Use))
                    {
                        return true;
                    }

                    // Wall frames were BlockGeneric until now and have no block entity, so one
                    // already in a world has nowhere to keep a picture. Give it one the first
                    // time somebody interacts, rather than making people break and replace it.
                    if (be2 == null)
                    {
                        world.BlockAccessor.SpawnBlockEntity("BEDeskPictureFrame", blockSel.Position);
                    }
                }
                else
                {
                    new GuiDialogPicturePicker(
                        (ICoreClientAPI)world.Api, blockSel.Position.Copy(), this, be2?.ImageIds)
                        .TryOpen();
                }
                return true;
            }

            return base.OnBlockInteractStart(world, byPlayer, blockSel);
        }

        public override WorldInteraction[] GetPlacedBlockInteractionHelp(IWorldAccessor world, BlockSelection selection, IPlayer forPlayer)
        {
            var mine = new WorldInteraction
            {
                ActionLangCode = "deskpictureframe:blockhelp-choosepicture",
                MouseButton = EnumMouseButton.Right,
                // Kept in step with the ShiftKey read above. Vanilla renders this as
                // "Shift"; player-facing text elsewhere should say Sneak, since naming a
                // key is wrong for anyone who rebound it (shared reference sect.7).
                HotKeyCode = "shift"
            };

            var inherited = base.GetPlacedBlockInteractionHelp(world, selection, forPlayer)
                            ?? Array.Empty<WorldInteraction>();

            var all = new WorldInteraction[inherited.Length + 1];
            all[0] = mine;
            Array.Copy(inherited, 0, all, 1, inherited.Length);
            return all;
        }
        /// <summary>
        /// Copies the frame's picture ids onto a stack as ARL variants (`types/img1`, ...).
        ///
        /// A frame on a shelf, in a window display or in ground storage is rendered from the
        /// ITEMSTACK, not from a block entity - ARL's GenContainedMesh does
        /// `Variants.FromStack(slot.Itemstack)`. Anything the block entity knows and the stack
        /// does not is invisible to every display surface in the game.
        ///
        /// So the ids travel with the item, and a picked-up frame keeps its picture - on a
        /// shelf, in the inventory, and when placed again.
        /// </summary>
        private static void StampImageIds(ItemStack stack, IWorldAccessor world, BlockPos pos)
        {
            if (stack == null) return;
            if (world.BlockAccessor.GetBlockEntity(pos) is not BEDeskPictureFrame be) return;
            if (be.ImageIds.Count == 0) return;

            var types = stack.Attributes
                             .GetOrAddTreeAttribute(AttributeRenderingLibrary.Variants.RootAttributeName);

            foreach (var pair in be.ImageIds)
                types.SetString(BEDeskPictureFrame.VariantNameFor(pair.Key), pair.Value);
        }

        /// <summary>The pictures an itemstack is carrying, as texture code -> id.
        /// Stored under the ARL variant names (img1, img2, ...) so ARL can render a frame in
        /// an inventory or on a shelf from the stack alone.</summary>
        public static Dictionary<string, string> FrameImageIdsOf(ItemStack stack)
        {
            var found = new Dictionary<string, string>();
            if (stack?.Block == null) return found;

            var ids = BEDeskPictureFrame.ReadIds(
                stack.Attributes?.GetTreeAttribute(
                    AttributeRenderingLibrary.Variants.RootAttributeName));

            foreach (string code in BEDeskPictureFrame.SlotsOf(stack.Block))
            {
                if (ids.TryGetValue(BEDeskPictureFrame.VariantNameFor(code), out string id))
                    found[code] = id;
            }
            return found;
        }

        public override ItemStack OnPickBlock(IWorldAccessor world, BlockPos pos)
        {
            ItemStack stack = base.OnPickBlock(world, pos);
            StampImageIds(stack, world, pos);
            return stack;
        }

        public override ItemStack[] GetDrops(IWorldAccessor world, BlockPos pos, IPlayer byPlayer, float dropQuantityMultiplier = 1)
        {
            // Only handle collection drops, everything else uses default behaviour
            if (!Code.Path.StartsWith("frame-collection-"))
            {
                ItemStack[] baseDrops = base.GetDrops(world, pos, byPlayer, dropQuantityMultiplier);
                if (baseDrops != null)
                {
                    foreach (ItemStack drop in baseDrops) StampImageIds(drop, world, pos);
                }
                return baseDrops;
            }

            // Frames that come out of a collection are new and belong to nobody yet - deliberately
            // not stamped, so they show the opener's own pictures until someone places them.

            var be = world.BlockAccessor.GetBlockEntity(pos);
            var arlBehavior = be?.GetBehavior<AttributeRenderingLibrary.BlockEntityBehaviorShapeTexturesFromAttributes>();

            string metal = "brass";
            if (arlBehavior?.Variants != null)
                metal = arlBehavior.Variants.Get("metal") ?? "brass";

            string collection = Code.Path.Replace("frame-collection-", "");

            List<ItemStack> drops = new List<ItemStack>();

            // Five frames, all variant 1. The numbered variants still EXIST as codes - they have
            // to, so every old block has its own remap target - but they are presentation only
            // now that the picture is chosen in game, so there is nothing to spread five across.
            for (int i = 1; i <= 5; i++)
            {
                AssetLocation frameCode;

                switch (collection)
                {
                    case "desk-portrait":
                        frameCode = new AssetLocation("deskpictureframe:frame-desk-portrait-1");
                        break;
                    case "desk-landscape":
                        frameCode = new AssetLocation("deskpictureframe:frame-desk-landscape-1");
                        break;
                    case "wall-portrait":
                        frameCode = new AssetLocation("deskpictureframe:frame-wall-portrait-1-north");
                        break;
                    case "wall-landscape":
                        frameCode = new AssetLocation("deskpictureframe:frame-wall-landscape-1-north");
                        break;
                    default:
                        continue;
                }

                // GetBlock must be checked BEFORE constructing the stack - the ItemStack
                // constructor dereferences the block, so an unresolvable code NREs there.
                Block frameBlock = world.GetBlock(frameCode);
                if (frameBlock == null) continue;

                ItemStack stack = new ItemStack(frameBlock);
                stack.Attributes.GetOrAddTreeAttribute("types").SetString("metal", metal);
                drops.Add(stack);
            }

            return drops.ToArray();
        }
        public override void OnBlockBroken(IWorldAccessor world, BlockPos pos, IPlayer byPlayer, float dropQuantityMultiplier = 1)
        {
            // DELIBERATE: everything this class covers hands its drops straight to the breaker,
            // frames and crates alike, and only falls back to the ground when the inventory is
            // full. That is the intended behaviour - do not "fix" it to a ground drop.
            //
            // With no player at all (an explosion, or another mod breaking the block) there is
            // no inventory to give to, so that case goes through base, which handles null.
            if (byPlayer == null)
            {
                base.OnBlockBroken(world, pos, byPlayer, dropQuantityMultiplier);
                return;
            }

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

                // base is not called on this path, so the break sound has to be played here.
                if (Sounds != null)
                    world.PlaySoundAt(Sounds.GetBreakSound(byPlayer), pos.X + 0.5, pos.Y + 0.5, pos.Z + 0.5, pos.dimension, byPlayer);
                world.BlockAccessor.SetBlock(0, pos);
            }
        }
    }
}
    


