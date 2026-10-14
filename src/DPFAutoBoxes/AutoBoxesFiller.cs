using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace DPF;   // CHANGE: the namespace your own code files use (the word after "namespace" at their top)

/// <summary>
/// The filler (<c>yourmod:autoboxesfiller</c>) - an invisible block in a space its owner's box reaches into.
/// Draws nothing, is solid only where its owner says so (its own piece of the box), gives the owner's box as its outline, and passes
/// a click, a break, the name, the info and the help on to the owner as if the owner's own block
/// had been aimed at. Placed and removed only by <see cref="AutoBoxes"/>; drops nothing.
/// </summary>
public class BlockAutoBoxesFiller : Block
{
    static BEAutoBoxesFiller Be(IBlockAccessor ba, BlockPos pos) => ba.GetBlockEntity(pos) as BEAutoBoxesFiller;

    static BlockPos OwnerOf(IBlockAccessor ba, BlockPos pos)
    {
        var owner = Be(ba, pos)?.Owner;
        return owner != null && AutoBoxes.Valid(ba, owner) ? owner : null;
    }

    public override Cuboidf[] GetSelectionBoxes(IBlockAccessor blockAccessor, BlockPos pos)
    {
        var be = Be(blockAccessor, pos);
        var owner = OwnerOf(blockAccessor, pos);
        if (owner == null) return [];
        if (blockAccessor.GetBlockEntity(owner) is IAutoBoxesOwner o) return o.AutoBoxesAt(pos);
        // an owner that is not ours: the box this filler carries, seen from here
        return be.Carried == null ? [] : [AutoBoxes.Whole(be.Carried, (pos.X - owner.X, pos.Y - owner.Y, pos.Z - owner.Z))];
    }

    /// <summary>This space's own piece of the owner's box when the owner is solid (collision is tested space by space).</summary>
    public override Cuboidf[] GetCollisionBoxes(IBlockAccessor blockAccessor, BlockPos pos)
    {
        var be = Be(blockAccessor, pos);
        var owner = be?.Owner;
        if (owner == null) return [];
        if (blockAccessor.GetBlockEntity(owner) is IAutoBoxesOwner o) return o.AutoCollisionAt(pos) ?? [];
        if (!be.Solid || be.Carried == null || !AutoBoxes.Valid(blockAccessor, owner)) return [];
        return be.CollisionPiece();
    }

    public override Cuboidf[] GetParticleCollisionBoxes(IBlockAccessor blockAccessor, BlockPos pos) => GetCollisionBoxes(blockAccessor, pos);

    /// <summary>The selection as the owner's own block would have seen it: its position, the hit point re-measured from there.</summary>
    static BlockSelection Forwarded(BlockSelection selection, BlockPos owner)
    {
        var forwarded = selection.Clone();
        forwarded.Position = owner.Copy();
        if (selection.HitPosition != null)
            forwarded.HitPosition = selection.HitPosition.AddCopy(selection.Position.X - owner.X, selection.Position.Y - owner.Y, selection.Position.Z - owner.Z);
        forwarded.SelectionBoxIndex = 0;
        return forwarded;
    }

    public override bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
    {
        var owner = OwnerOf(world.BlockAccessor, blockSel.Position);
        return owner != null && world.BlockAccessor.GetBlock(owner).OnBlockInteractStart(world, byPlayer, Forwarded(blockSel, owner));
    }

    public override bool OnBlockInteractStep(float secondsUsed, IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
    {
        var owner = OwnerOf(world.BlockAccessor, blockSel.Position);
        return owner != null && world.BlockAccessor.GetBlock(owner).OnBlockInteractStep(secondsUsed, world, byPlayer, Forwarded(blockSel, owner));
    }

    public override void OnBlockInteractStop(float secondsUsed, IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
    {
        var owner = OwnerOf(world.BlockAccessor, blockSel.Position);
        if (owner != null) world.BlockAccessor.GetBlock(owner).OnBlockInteractStop(secondsUsed, world, byPlayer, Forwarded(blockSel, owner));
    }

    /// <summary>Breaking any part breaks the owner, which takes its fillers with it.</summary>
    public override void OnBlockBroken(IWorldAccessor world, BlockPos pos, IPlayer byPlayer, float dropQuantityMultiplier = 1)
    {
        if (world.Side != EnumAppSide.Server) return;
        var owner = OwnerOf(world.BlockAccessor, pos);
        if (owner == null)
        {
            world.BlockAccessor.SetBlock(0, pos);
            return;
        }
        if (byPlayer != null && world.Claims.TestAccess(byPlayer, owner, EnumBlockAccessFlags.BuildOrBreak) != EnumWorldAccessResponse.Granted) return;
        world.BlockAccessor.BreakBlock(owner, byPlayer, dropQuantityMultiplier);
    }

    public override ItemStack[] GetDrops(IWorldAccessor world, BlockPos pos, IPlayer byPlayer, float dropQuantityMultiplier = 1) => [];

    public override ItemStack OnPickBlock(IWorldAccessor world, BlockPos pos)
    {
        var owner = OwnerOf(world.BlockAccessor, pos);
        return owner == null ? null : world.BlockAccessor.GetBlock(owner).OnPickBlock(world, owner);
    }

    public override string GetPlacedBlockName(IWorldAccessor world, BlockPos pos)
    {
        var owner = OwnerOf(world.BlockAccessor, pos);
        return owner == null ? base.GetPlacedBlockName(world, pos) : world.BlockAccessor.GetBlock(owner).GetPlacedBlockName(world, owner);
    }

    public override string GetPlacedBlockInfo(IWorldAccessor world, BlockPos pos, IPlayer forPlayer)
    {
        var owner = OwnerOf(world.BlockAccessor, pos);
        return owner == null ? "" : world.BlockAccessor.GetBlock(owner).GetPlacedBlockInfo(world, owner, forPlayer);
    }

    public override WorldInteraction[] GetPlacedBlockInteractionHelp(IWorldAccessor world, BlockSelection selection, IPlayer forPlayer)
    {
        var owner = OwnerOf(world.BlockAccessor, selection.Position);
        return owner == null ? [] : world.BlockAccessor.GetBlock(owner).GetPlacedBlockInteractionHelp(world, Forwarded(selection, owner), forPlayer);
    }
}

/// <summary>The filler's memory: whose box it stands in for. An owner that is gone -> the filler removes itself.</summary>
public class BEAutoBoxesFiller : BlockEntity
{
    public BlockPos Owner { get; private set; }

    /// <summary>The owner's whole box (owner's frame) - only for an owner that cannot implement the interface.</summary>
    public Cuboidf Carried { get; private set; }

    /// <summary>Carried boxes only: whether this space's piece is solid.</summary>
    public bool Solid { get; private set; }

    private Cuboidf[] piece;

    /// <summary>This space's own piece of the carried box.</summary>
    public Cuboidf[] CollisionPiece() => piece ??= Owner == null ? [] : AutoBoxes.PieceAt(Carried, (Pos.X - Owner.X, Pos.Y - Owner.Y, Pos.Z - Owner.Z));

    public void SetOwner(BlockPos owner, Cuboidf carried = null, bool solid = false)
    {
        Owner = owner.Copy();
        Carried = carried?.Clone();
        Solid = solid;
        piece = null;
        MarkDirty(true);
    }

    public override void Initialize(ICoreAPI api)
    {
        base.Initialize(api);
        // An owner removed some other way (a world edit, the mod out and back) would leave an
        // invisible obstacle. Check once after load; an unloaded owner is waited for, not judged.
        // A carried box's owner is a block that is not ours and tells us nothing when it empties,
        // so those fillers keep checking (every 2 s); the others check once.
        if (api.Side == EnumAppSide.Server) RegisterDelayedCallback(_ => CheckOwner(), 2000);
    }

    void CheckOwner()
    {
        if (Owner != null && Api.World.BlockAccessor.GetChunkAtBlockPos(Owner) == null)
        {
            RegisterDelayedCallback(_ => CheckOwner(), 2000);
            return;
        }
        var ba = Api.World.BlockAccessor;
        bool owned = Owner != null && (ba.GetBlockEntity(Owner) is IAutoBoxesOwner o
            ? o.AutoBoxesAt(Pos).Length > 0
            : Carried != null && AutoBoxes.Valid(ba, Owner));
        if (!owned)
        {
            if (ba.GetBlock(Pos) is BlockAutoBoxesFiller) ba.SetBlock(0, Pos);
            return;
        }
        if (Carried != null) RegisterDelayedCallback(_ => CheckOwner(), 2000);
    }

    public override void ToTreeAttributes(ITreeAttribute tree)
    {
        base.ToTreeAttributes(tree);
        if (Owner != null) { tree.SetInt("ownerX", Owner.X); tree.SetInt("ownerY", Owner.Y); tree.SetInt("ownerZ", Owner.Z); }
        if (Carried != null)
        {
            tree.SetFloat("boxX1", Carried.X1); tree.SetFloat("boxY1", Carried.Y1); tree.SetFloat("boxZ1", Carried.Z1);
            tree.SetFloat("boxX2", Carried.X2); tree.SetFloat("boxY2", Carried.Y2); tree.SetFloat("boxZ2", Carried.Z2);
            tree.SetBool("solid", Solid);
        }
    }

    public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldAccessForResolve)
    {
        base.FromTreeAttributes(tree, worldAccessForResolve);
        Owner = tree.HasAttribute("ownerX") ? new BlockPos(tree.GetInt("ownerX"), tree.GetInt("ownerY"), tree.GetInt("ownerZ"), Pos.dimension) : null;
        Carried = tree.HasAttribute("boxX1")
            ? new Cuboidf(tree.GetFloat("boxX1"), tree.GetFloat("boxY1"), tree.GetFloat("boxZ1"), tree.GetFloat("boxX2"), tree.GetFloat("boxY2"), tree.GetFloat("boxZ2"))
            : null;
        Solid = tree.GetBool("solid");
        piece = null;
        if (Owner != null && Carried != null) AutoBoxes.CarriedSeen?.Invoke(Owner, Carried, Solid);
        if (Api?.Side == EnumAppSide.Client) Api.World.BlockAccessor.MarkBlockDirty(Pos);
    }
}
