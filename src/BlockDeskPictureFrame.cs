using System;
using System.Collections.Generic;
using DPF;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace DeskPictureFrame
{
    public class BlockDeskPictureFrame : Block, IContainedMeshSource
    {
        /// <summary>
        /// A frame that hangs on a wall (<c>"wallMounted": true</c>). It does what the game's
        /// HorizontalAttachable did with four side codes, in one code (Calm, 2026-10-09: "as minimal as
        /// possible"): placed only against the side of a block, facing out from it, and dropped when
        /// that block goes. The frame is turned by its angle like every other frame.
        /// </summary>
        public bool WallMounted { get; private set; }

        public override void OnLoaded(ICoreAPI api)
        {
            base.OnLoaded(api);
            WallMounted = Attributes?["wallMounted"].AsBool(false) == true;
        }

        /// <summary>The angle a frame is turned by to face out of a block face - the same angle placing it by hand gives.</summary>
        public static float AngleFacing(BlockFacing face) => (float)Math.Atan2(face.Normali.X, face.Normali.Z);

        /// <summary>The way a frame turned by this angle faces, or null for an angle between sides.</summary>
        public static BlockFacing FacingOf(float angle)
        {
            int x = (int)Math.Round(Math.Sin(angle)), z = (int)Math.Round(Math.Cos(angle));
            foreach (var face in BlockFacing.HORIZONTALS)
                if (face.Normali.X == x && face.Normali.Z == z) return face;
            return null;
        }

        /// <summary>True when a frame at <paramref name="pos"/> facing out of <paramref name="face"/> has a wall behind it to hang on.</summary>
        private bool CanHangOn(IWorldAccessor world, BlockPos pos, BlockFacing face)
        {
            BlockPos wallPos = pos.AddCopy(face.Opposite);
            return world.BlockAccessor.GetBlock(wallPos).CanAttachBlockAt(world.BlockAccessor, this, wallPos, face);
        }

        /// <summary>The face a wall frame placed here would hang out of: the clicked side first, else any side with a wall (as the game's own attachables).</summary>
        private BlockFacing WallFaceFor(IWorldAccessor world, BlockSelection blockSel)
        {
            if (blockSel.Face.IsHorizontal && CanHangOn(world, blockSel.Position, blockSel.Face)) return blockSel.Face;
            foreach (var face in BlockFacing.HORIZONTALS)
                if (CanHangOn(world, blockSel.Position, face)) return face;
            return null;
        }

        public override bool TryPlaceBlock(IWorldAccessor world, IPlayer byPlayer, ItemStack itemstack, BlockSelection blockSel, ref string failureCode)
        {
            BlockFacing wallFace = null;
            if (WallMounted)
            {
                wallFace = WallFaceFor(world, blockSel);
                if (wallFace == null)
                {
                    failureCode = "requirehorizontalattachable";   // the game's own "needs a wall" message
                    return false;
                }
            }

            bool placed = base.TryPlaceBlock(world, byPlayer, itemstack, blockSel, ref failureCode);
            if (!placed) return false;

            if (world.BlockAccessor.GetBlockEntity(blockSel.Position) is BEDeskPictureFrame be)
            {
                BlockPos blockPos = blockSel.DidOffset ? blockSel.Position.AddCopy(blockSel.Face.Opposite) : blockSel.Position;
                // No player (another mod placing it - a builder, a schematic): no one to face, so it
                // faces the way it was drawn rather than crashing on the missing player.
                float yaw = 0f;
                if (byPlayer?.Entity != null)
                {
                    double dx = byPlayer.Entity.Pos.X - (blockPos.X + blockSel.HitPosition.X);
                    double dz = byPlayer.Entity.Pos.Z - (blockPos.Z + blockSel.HitPosition.Z);
                    yaw = (float)Math.Atan2(dx, dz);
                }
                // A wall frame faces out of its wall; anything else faces whoever placed it.
                float angle = wallFace != null
                    ? AngleFacing(wallFace)
                    : (float)Math.Round(yaw / (GameMath.PI / 2f)) * (GameMath.PI / 2f);

                be.MeshAngleRad = angle;
                be.AutoBoxesAngleChanged();
                be.OwnerUid = byPlayer?.PlayerUID;
                be.blockMesh = null;

                // A FRAME THAT ALREADY HOLDS PICTURES KEEPS THEM, and the SERVER applies
                // them, because it has the itemstack right here.
                //
                // This used to be done from the client, which sent the ids in a packet - and
                // that races the placement itself: the packet could reach the server before it
                // had created the block entity, whereupon the ids were silently dropped and
                // the frame came back blank. Reading the stack server-side has no race at all.
                var carried = FrameImageIdsOf(itemstack);

                bool chosenBefore = itemstack?.Attributes?.GetTreeAttribute(
                                        FrameMesher.TypesKey)?
                                        .GetBool("imagesChosen", false) ?? false;


                if (world.Side == EnumAppSide.Server)
                {
                    if (carried.Count > 0) be.SetImageIds(carried);
                    else if (chosenBefore) be.ImagesChosen = true;
                    be.MarkDirty(true);
                }
                else
                {
                    // THE CLIENT APPLIES THE CARRIED IDS TOO, from the very itemstack in its own
                    // hand. The server does the authoritative version a line above, but its sync
                    // can arrive after this block entity exists - and until it does, a frame with
                    // no ids renders the `*` texture and the picker opens on "no picture" for a
                    // frame that plainly has one.
                    //
                    // This is NOT the racy design the note above warns about: nothing is being
                    // sent anywhere. The stack is already here and the answer is already in it.
                    if (carried.Count > 0) be.SetImageIds(carried);
                    else if (chosenBefore) be.ImagesChosen = true;

                    be.InitializeWithAngle();

                    // Only a frame with no pictures of its own needs the client: recovering
                    // what an old numbered slot used to hold depends on this player's folder,
                    // which the server cannot see. Not time critical, and it retries on sight.
                    if (carried.Count == 0 && !chosenBefore)
                    {
                        var legacy = be.CaptureLegacyIds();
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
        /// <summary>A boxed set of frames (frame-collection-*): plain packaging, never holds a picture.</summary>
        public bool IsCollection => Code?.Path?.StartsWith("frame-collection-") == true;

        public override bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
        {
            // Boxed sets get no picture picker - sneak + right-click does what right-click does.
            if (!IsCollection && byPlayer?.WorldData?.EntityControls?.ShiftKey == true)
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
            // No "choose picture" on a boxed set.
            if (IsCollection) return base.GetPlacedBlockInteractionHelp(world, selection, forPlayer);

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
        /// Copies the frame's metal and picture ids onto a stack (`types/metal`, `types/img1`, ...).
        ///
        /// A frame on a shelf, in a window display or in ground storage is drawn from the
        /// ITEMSTACK, not from a block entity (<see cref="GenMesh"/>). Anything the block entity
        /// knows and the stack does not is invisible to every display surface in the game.
        ///
        /// So the metal and ids travel with the item, and a picked-up frame keeps its look - on
        /// a shelf, in the inventory, and when placed again. Same tree and names ARL used, so a
        /// stack from before 1.2.0 reads the same.
        /// </summary>
        private static void StampImageIds(ItemStack stack, IWorldAccessor world, BlockPos pos)
        {
            if (stack?.Block is not BlockDeskPictureFrame) return;
            if (world.BlockAccessor.GetBlockEntity(pos) is not BEDeskPictureFrame be) return;
            FrameMesher.WriteMetal(stack.Attributes, be.Metal);
            // The flag travels even when there are no ids - that IS the case it exists for: a
            // frame deliberately emptied, picked up, and put back down must stay empty rather
            // than resurrecting whatever the old numbered slot held.
            if (be.ImagesChosen)
            {
                stack.Attributes
                     .GetOrAddTreeAttribute(FrameMesher.TypesKey)
                     .SetBool("imagesChosen", true);
            }

            // An old frame still waiting for its owner to turn its numbered picture into an id keeps
            // the old key on the stack, so placing it again still finds that picture.
            if (!be.ImagesChosen && be.ImageIds.Count == 0)
                BEDeskPictureFrame.WriteStrings(stack.Attributes, "legacyKeys", be.EffectiveLegacyKeys);

            if (be.ImageIds.Count == 0) return;

            var types = stack.Attributes
                             .GetOrAddTreeAttribute(FrameMesher.TypesKey);

            foreach (var pair in be.ImageIds)
                types.SetString(BEDeskPictureFrame.VariantNameFor(pair.Key), pair.Value);

        }

        /// <summary>
        /// The pictures an itemstack is carrying, as texture code -> id.
        ///
        /// **READ BACK FROM THE ATTRIBUTE NAMES, NEVER FROM THE BLOCK'S TEXTURES.** This used to
        /// ask SlotsOf for the block's picture slots and look each one up - and `Block.Textures`
        /// is CLIENT ONLY. On a dedicated server it is empty, so the slot list came back with
        /// nothing, every id on the stack was ignored, and a picked-up frame was placed blank.
        ///
        /// It looked like it worked because the frame then rendered its `*` texture, which
        /// resolves to the viewer's own picture - so whoever placed it saw a picture and nobody
        /// else did. Proved from the logs 2026-09-04: the same stack read 1 picture on the
        /// client and 0 on the server, "offers slots [none]".
        ///
        /// The variant names carry the slot in them ("img1" is the picture for "image1"), so
        /// inverting the name needs nothing but the tree and works identically on both sides.
        /// </summary>
        public static Dictionary<string, string> FrameImageIdsOf(ItemStack stack)
        {
            var found = new Dictionary<string, string>();

            var ids = BEDeskPictureFrame.ReadIds(
                stack?.Attributes?.GetTreeAttribute(FrameMesher.TypesKey));

            foreach (var pair in ids)
            {
                string code = BEDeskPictureFrame.TextureCodeFor(pair.Key);
                if (code != null) found[code] = pair.Value;
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

            string metal = (world.BlockAccessor.GetBlockEntity(pos) as BEDeskPictureFrame)?.Metal ?? "brass";

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
        /// <summary>A wall frame whose wall is gone drops, as the game's own wall attachables do.</summary>
        public override void OnNeighbourBlockChange(IWorldAccessor world, BlockPos pos, BlockPos neibpos)
        {
            base.OnNeighbourBlockChange(world, pos, neibpos);
            if (!WallMounted || world.Side != EnumAppSide.Server) return;
            if (world.BlockAccessor.GetBlockEntity(pos) is not BEDeskPictureFrame be) return;
            var face = FacingOf(be.MeshAngleRad);
            if (face != null && !CanHangOn(world, pos, face)) world.BlockAccessor.BreakBlock(pos, null);
        }

        /// <summary>
        /// An OLD numbered frame as an item (a chest, a hand, a shelf) turning into its number-free
        /// code - the old blocktype's Convert transition (the old glass panes' way). The game's own
        /// transition makes a bare new stack; this is the same frame, so its metal, pictures and
        /// everything else come along, plus the old number's picture key if it still needs one.
        /// </summary>
        public override ItemStack OnTransitionNow(ItemSlot slot, TransitionableProperties props)
        {
            ItemStack result = base.OnTransitionNow(slot, props);
            ItemStack old = slot?.Itemstack;
            if (props?.Type != EnumTransitionType.Convert || old == null || result?.Block is not BlockDeskPictureFrame) return result;

            var attributes = old.Attributes.Clone();
            attributes.RemoveAttribute("transitionstate");
            bool chosen = (attributes[FrameMesher.TypesKey] as ITreeAttribute)?.GetBool("imagesChosen", false) == true
                          || FrameImageIdsOf(old).Count > 0;
            if (!chosen && attributes["legacyKeys"] == null)
                BEDeskPictureFrame.WriteStrings(attributes, "legacyKeys", BEDeskPictureFrame.LegacyKeysFor(this));

            return new ItemStack(result.Block, old.StackSize) { Attributes = attributes };
        }

        /// <summary>The handbook's "Drops when broken" is the page's own stack, metal and all - as ARL showed it.</summary>
        public override BlockDropItemStack[] GetDropsForHandbook(ItemStack handbookStack, IPlayer forPlayer)
        {
            if (handbookStack?.Block is not BlockDeskPictureFrame) return base.GetDropsForHandbook(handbookStack, forPlayer);
            return [new BlockDropItemStack(handbookStack.Clone())];
        }

        // ---------- drawn as an item: hand, slot, dropped, handbook, and on displays ----------

        public override void OnBeforeRender(ICoreClientAPI capi, ItemStack itemstack, EnumItemRenderTarget target, ref ItemRenderInfo renderinfo)
        {
            var meshRef = FrameMesher.ItemRef(capi, this, FrameLook.OfStack(itemstack));
            if (meshRef != null)
            {
                renderinfo.ModelRef = meshRef;
                renderinfo.NormalShaded = true;
            }
            base.OnBeforeRender(capi, itemstack, target, ref renderinfo);
        }

        /// <summary>
        /// Shelves, display cases, ground piles and Window Display ask here, from the TESSELATION
        /// thread. A texture not in the atlas yet is queued and the display drawn again once it is;
        /// meanwhile this mesh has the shape's own texture there, under a "|wait" key.
        /// </summary>
        public MeshData GenMesh(ItemSlot slot, ITextureAtlasAPI targetAtlas, BlockPos atBlockPos)
        {
            if (api is not ICoreClientAPI capi || slot?.Itemstack == null) return null;
            return FrameMesher.Contained(capi, this, FrameLook.OfStack(slot.Itemstack), atBlockPos)?.Clone();
        }

        public string GetMeshCacheKey(ItemSlot slot)
        {
            var look = FrameLook.OfStack(slot?.Itemstack);
            if (api is not ICoreClientAPI capi) return Code + "|" + (look.Metal ?? "-");
            string key = Code + "|" + look.Key(capi);
            return FrameMesher.TexturesReady(capi, look) ? key : key + "|wait";
        }

        public override void OnBlockBroken(IWorldAccessor world, BlockPos pos, IPlayer byPlayer, float dropQuantityMultiplier = 1)
        {
            // DELIBERATE: everything this class covers hands its drops straight to the breaker,
            // frames and crates alike, and only falls back to the ground when the inventory is
            // full. That is the intended behaviour - do not "fix" it to a ground drop.
            //
            // With no player at all (an explosion, or another mod breaking the block) there is
            // no inventory to give to, so that case goes through base, which handles null.
            // Creative too: the game gives nothing for a block broken in creative, and base does that.
            if (byPlayer == null || byPlayer.WorldData?.CurrentGameMode == EnumGameMode.Creative)
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
        public override Cuboidf[] GetSelectionBoxes(IBlockAccessor blockAccessor, BlockPos pos)
        {
            var autoSel = (blockAccessor.GetBlockEntity(pos) as IAutoBoxesOwner)?.AutoBoxesAt(pos);
            if (autoSel != null && autoSel.Length > 0) return autoSel;
            return base.GetSelectionBoxes(blockAccessor, pos);
        }
            public override Cuboidf[] GetCollisionBoxes(IBlockAccessor blockAccessor, BlockPos pos)
    {
        var autoCol = (blockAccessor.GetBlockEntity(pos) as IAutoBoxesOwner)?.AutoCollisionAt(pos);
        if (autoCol != null && autoCol.Length > 0) return autoCol;
        return base.GetCollisionBoxes(blockAccessor, pos);
    }
    }
}
    


