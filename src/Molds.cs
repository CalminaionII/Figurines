using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace Figurines;

/// <summary>
/// The figurine mold - ONE whole clay-formed block for every figurine (Calm, 2026-10-08: "making
/// the whole thing and not parts then a right click with the figure picks it"). Right-click it with
/// a wooden figurine and it takes that figurine's creature and pose (the figurine is kept). Fired,
/// it keeps them; poured full through the hole in its top, it gives that figurine in that metal.
/// The two-part version is parked in backup/two-part-mold-parked-2026-10-08/.
///
/// The design travels on the stack under <see cref="FigurineData.MoldKey"/> and in the block
/// entity while placed - pick it up, put it down, fire it: it is never lost.
/// </summary>
internal static class Mold
{
    public static FigurineData DesignOf(ItemStack stack) => FigurineData.Of(stack, FigurineData.MoldKey);

    public static ItemStack StackWith(Block block, FigurineData design)
    {
        var stack = new ItemStack(block);
        design?.WriteTo(stack, FigurineData.MoldKey);
        return stack;
    }

    public static string DesignName(FigurineData design)
    {
        string name = ItemFigurine.CreatureName(design.Creature);
        name = name.Length > 0 ? char.ToUpperInvariant(name[0]) + name.Substring(1) : name;
        return design.Pose == "normal" ? name : name + ", " + Lang.Get("figurines:pose-" + design.Pose);
    }

    public static string Name(Block block, FigurineData design)
    {
        string baseName = Lang.Get("figurines:block-" + block.Code.Path);
        return design == null ? baseName : Lang.Get("figurines:mold-name-with", baseName, DesignName(design));
    }

    public static void Info(StringBuilder dsc, FigurineData design, bool raw)
    {
        if (design != null) dsc.AppendLine(Lang.Get("figurines:mold-design", DesignName(design)));
        if (raw) dsc.AppendLine(Lang.Get(design == null ? "figurines:mold-blank" : "figurines:mold-ready"));
    }

    public static float FacingPlayer(IPlayer player, BlockSelection sel)
    {
        var target = sel.DidOffset ? sel.Position.AddCopy(sel.Face.Opposite) : sel.Position;
        double dx = player.Entity.Pos.X - (target.X + sel.HitPosition.X);
        double dz = player.Entity.Pos.Z - (target.Z + sel.HitPosition.Z);
        return (float)Math.Round(Math.Atan2(dx, dz) / GameMath.PIHALF) * GameMath.PIHALF;
    }

    /// <summary>Into the player's hands, or at their feet.</summary>
    public static void Give(IWorldAccessor world, IPlayer player, ItemStack stack, BlockPos pos)
    {
        if (!player.InventoryManager.TryGiveItemstack(stack))
            world.SpawnItemEntity(stack, pos.ToVec3d().Add(0.5, 0.2, 0.5));
    }
}

/// <summary>The raw (unfired) mold. Plain right-click places it; sneak + right-click puts it in ground storage for a pit kiln.</summary>
public sealed class BlockFigurineMoldRaw : Block
{
    private static readonly AssetLocation ClaySound = new("game:sounds/effect/clayform");

    public override bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
    {
        if (world.BlockAccessor.GetBlockEntity(blockSel.Position) is not BEFigurineMoldRaw be) return false;
        if (!world.Claims.TryAccess(byPlayer, blockSel.Position, EnumBlockAccessFlags.Use)) return false;
        var held = byPlayer.InventoryManager.ActiveHotbarSlot;
        var capi = world.Api as ICoreClientAPI;

        // A WOODEN figurine gives the mold its design; the figurine is kept.
        if (held.Itemstack?.Collectible is ItemFigurine)
        {
            var figurine = FigurineData.Of(held.Itemstack);
            if (figurine == null || figurine.Material != "wood") { capi?.TriggerIngameError(this, "notwood", Lang.Get("figurines:mold-needs-wood")); return false; }
            if (be.Design != null) { capi?.TriggerIngameError(this, "imprinted", Lang.Get("figurines:mold-already")); return false; }
            world.PlaySoundAt(ClaySound, blockSel.Position, 0, byPlayer);
            if (world.Side == EnumAppSide.Server) be.SetDesign(figurine.In("wood"));
            return true;
        }

        // DRY GRASS on the placed mold starts a pit kiln round it (Calm, 2026-10-08: it is placed like a
        // block, so dry grass did nothing - the game only builds a kiln on ground storage).
        if (held.Itemstack?.Collectible is ItemDryGrass)
        {
            if (be.Design == null) { capi?.TriggerIngameError(this, "notfireable", Lang.Get("figurines:mold-kiln-blank")); return true; }
            StartKiln(world, byPlayer, blockSel, be.Design);
            return true;
        }

        // Pick up with an empty hand, keeping the design.
        if (held.Empty)
        {
            if (world.Side == EnumAppSide.Server)
            {
                Mold.Give(world, byPlayer, Mold.StackWith(this, be.Design), blockSel.Position);
                world.BlockAccessor.SetBlock(0, blockSel.Position);
            }
            return true;
        }
        return false;
    }

    /// <summary>
    /// Swap the placed mold for ground storage holding it (as sneak-placing would have), then let the
    /// game build its pit kiln there - vssurvivalmod BlockPitkiln.TryCreateKiln, the same call
    /// ItemDryGrass makes, with all its checks and messages. Both sides, as ItemDryGrass does. If the
    /// kiln cannot be built, the mold goes back as it was.
    /// </summary>
    private void StartKiln(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel, FigurineData design)
    {
        var pos = blockSel.Position;
        if (world.GetBlock(new AssetLocation("groundstorage")) is not BlockGroundStorage storage
            || world.GetBlock(new AssetLocation("pitkiln")) is not BlockPitkiln kiln) return;
        var props = GetBehavior<CollectibleBehaviorGroundStorable>()?.StorageProps;
        if (props == null) return;

        world.BlockAccessor.SetBlock(storage.BlockId, pos);
        if (world.BlockAccessor.GetBlockEntity(pos) is BlockEntityGroundStorage beg)
        {
            beg.ForceStorageProps(props);
            beg.MeshAngle = Mold.FacingPlayer(byPlayer, blockSel);
            beg.Inventory[0].Itemstack = Mold.StackWith(this, design);
            beg.Inventory[0].MarkDirty();
            if (kiln.TryCreateKiln(world, byPlayer, pos)) return;
        }

        world.BlockAccessor.SetBlock(BlockId, pos);
        (world.BlockAccessor.GetBlockEntity(pos) as BEFigurineMoldRaw)?.SetDesign(design);
    }

    public override bool DoPlaceBlock(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel, ItemStack byItemStack)
    {
        bool placed = base.DoPlaceBlock(world, byPlayer, blockSel, byItemStack);
        if (placed && world.BlockAccessor.GetBlockEntity(blockSel.Position) is BEFigurineMoldRaw be) be.SetDesign(Mold.DesignOf(byItemStack));
        return placed;
    }

    private static FigurineData DesignAt(IWorldAccessor world, BlockPos pos) => (world.BlockAccessor.GetBlockEntity(pos) as BEFigurineMoldRaw)?.Design;

    public override ItemStack[] GetDrops(IWorldAccessor world, BlockPos pos, IPlayer byPlayer, float dropQuantityMultiplier = 1) =>
        [Mold.StackWith(this, DesignAt(world, pos))];

    public override ItemStack OnPickBlock(IWorldAccessor world, BlockPos pos) => Mold.StackWith(this, DesignAt(world, pos));

    public override string GetHeldItemName(ItemStack itemStack) => Mold.Name(this, Mold.DesignOf(itemStack));

    public override string GetPlacedBlockName(IWorldAccessor world, BlockPos pos) => Mold.Name(this, DesignAt(world, pos));

    public override void GetHeldItemInfo(ItemSlot inSlot, StringBuilder dsc, IWorldAccessor world, bool withDebugInfo)
    {
        base.GetHeldItemInfo(inSlot, dsc, world, withDebugInfo);
        Mold.Info(dsc, Mold.DesignOf(inSlot.Itemstack), true);
    }

    /// <summary>
    /// What the kiln fires it into. Both kilns ask THIS, with the stack in hand (public source,
    /// vssurvivalmod BEPitKiln.OnFired and BEBeeHiveKiln.ConvertItemToBurned), and clone the
    /// answer's smelted stack - so the fired mold comes out already carrying the design.
    /// A mold with no design has nothing to fire into: the kiln says "not fireable".
    /// </summary>
    public override CombustibleProperties GetCombustibleProperties(IWorldAccessor world, ItemStack stack, BlockPos pos)
    {
        var props = base.GetCombustibleProperties(world, stack, pos);
        if (stack == null) return props;
        var design = Mold.DesignOf(stack);
        if (design == null || props?.SmeltedStack?.ResolvedItemstack == null) return null;

        var fired = props.Clone();
        var firedStack = props.SmeltedStack.ResolvedItemstack.Clone();
        design.WriteTo(firedStack, FigurineData.MoldKey);
        fired.SmeltedStack.ResolvedItemstack = firedStack;
        return fired;
    }

    public override WorldInteraction[] GetPlacedBlockInteractionHelp(IWorldAccessor world, BlockSelection selection, IPlayer forPlayer)
    {
        var help = new List<WorldInteraction>();
        if (DesignAt(world, selection.Position) == null)
        {
            var figurine = ItemFigurine.Make(world, new FigurineData { Creature = world.Api.ModLoader.GetModSystem<FigurinesModSystem>().Creatures.FirstOrDefault()?.Code ?? "bear" });
            help.Add(new WorldInteraction { ActionLangCode = "figurines:mold-help-imprint", MouseButton = EnumMouseButton.Right, Itemstacks = [figurine] });
        }
        else if (world.GetItem(new AssetLocation("drygrass")) is { } grass)
        {
            help.Add(new WorldInteraction { ActionLangCode = "figurines:mold-help-kiln", MouseButton = EnumMouseButton.Right, Itemstacks = [new ItemStack(grass)] });
        }
        help.Add(new WorldInteraction { ActionLangCode = "figurines:mold-help-pickup", MouseButton = EnumMouseButton.Right, RequireFreeHand = true });
        return help.Concat(base.GetPlacedBlockInteractionHelp(world, selection, forPlayer)).ToArray();
    }
}

public sealed class BEFigurineMoldRaw : BlockEntity
{
    public FigurineData Design { get; private set; }

    public void SetDesign(FigurineData design)
    {
        Design = design;
        MarkDirty(true);
    }

    public override void ToTreeAttributes(ITreeAttribute tree)
    {
        base.ToTreeAttributes(tree);
        if (Design != null) Design.WriteTo(tree.GetOrAddTreeAttribute(FigurineData.MoldKey));
    }

    public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldAccessForResolve)
    {
        base.FromTreeAttributes(tree, worldAccessForResolve);
        Design = FigurineData.FromTree(tree.GetTreeAttribute(FigurineData.MoldKey));
    }

    public override void GetBlockInfo(IPlayer forPlayer, StringBuilder dsc)
    {
        base.GetBlockInfo(forPlayer, dsc);
        Mold.Info(dsc, Design, true);
    }
}

/// <summary>
/// The fired mold: the game's own tool mold (pour from a crucible, wait for it to cool,
/// right-click to take it out), with the design kept and the figurine given in the poured metal.
/// </summary>
public sealed class BlockFigurineMoldFired : BlockToolMold
{
    public override bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
    {
        // The game's pick-up of an EMPTY mold makes a fresh stack and would lose the design
        // (vssurvivalmod BEToolMold.OnPlayerInteract, not overridable) - so take that case here.
        if (world.BlockAccessor.GetBlockEntity(blockSel.Position) is BEFigurineMoldFired be
            && !byPlayer.Entity.Controls.ShiftKey && be.FillLevel == 0 && !be.Shattered
            && byPlayer.Entity.Controls.HandUse == EnumHandInteract.None
            && (byPlayer.InventoryManager.ActiveHotbarSlot.Empty || byPlayer.InventoryManager.ActiveHotbarSlot.Itemstack.Collectible is BlockToolMold))
        {
            if (!world.Claims.TryAccess(byPlayer, blockSel.Position, EnumBlockAccessFlags.Use)) return false;
            if (world.Side == EnumAppSide.Server)
            {
                Mold.Give(world, byPlayer, Mold.StackWith(this, be.Design), blockSel.Position);
                world.BlockAccessor.SetBlock(0, blockSel.Position);
                if (Sounds?.Place != null) world.PlaySoundAt(Sounds.Place, blockSel.Position, -0.5, byPlayer);
            }
            return true;
        }
        return base.OnBlockInteractStart(world, byPlayer, blockSel);
    }

    public override bool DoPlaceBlock(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel, ItemStack byItemStack)
    {
        bool placed = base.DoPlaceBlock(world, byPlayer, blockSel, byItemStack);
        if (placed && world.BlockAccessor.GetBlockEntity(blockSel.Position) is BEFigurineMoldFired be) be.SetDesign(Mold.DesignOf(byItemStack));
        return placed;
    }

    /// <summary>The mold with its design, and the figurine if one has hardened in it.</summary>
    public override ItemStack[] GetDrops(IWorldAccessor world, BlockPos pos, IPlayer byPlayer, float dropQuantityMultiplier = 1)
    {
        if (world.BlockAccessor.GetBlockEntity(pos) is not BEFigurineMoldFired be) return [new ItemStack(this)];
        var drops = new List<ItemStack>();
        if (!be.Shattered) drops.Add(Mold.StackWith(this, be.Design));
        if (be.CastFigurine() is { } figurine) drops.Add(figurine);
        return drops.ToArray();
    }

    public override ItemStack OnPickBlock(IWorldAccessor world, BlockPos pos) =>
        Mold.StackWith(this, (world.BlockAccessor.GetBlockEntity(pos) as BEFigurineMoldFired)?.Design);

    public override string GetHeldItemName(ItemStack itemStack) => Mold.Name(this, Mold.DesignOf(itemStack));

    public override string GetPlacedBlockName(IWorldAccessor world, BlockPos pos) =>
        Mold.Name(this, (world.BlockAccessor.GetBlockEntity(pos) as BEFigurineMoldFired)?.Design);

    public override void GetHeldItemInfo(ItemSlot inSlot, StringBuilder dsc, IWorldAccessor world, bool withDebugInfo)
    {
        base.GetHeldItemInfo(inSlot, dsc, world, withDebugInfo);
        Mold.Info(dsc, Mold.DesignOf(inSlot.Itemstack), false);   // the metals are on its handbook page (Calm, 2026-10-16)
    }

    /// <summary>The metals this mold takes: every material marked <c>castable</c> in the materials files.</summary>
    public static string[] CastableMetals(Block block) =>
        FigurinesModSystem.Instance?.Materials.Values.Where(m => m.Castable).Select(m => m.Code).ToArray() ?? [];
}

/// <summary>
/// The game's tool mold block entity with the design added. The crucible talks to it through
/// ILiquidMetalSink (vssurvivalmod BlockSmeltedContainer), so re-implementing that interface here
/// lets CanReceive refuse metals a figurine is not cast in - the game's own CanReceive is not
/// virtual. TryTakeContents IS virtual, and is where the figurine is handed out.
/// </summary>
public sealed class BEFigurineMoldFired : BlockEntityToolMold, ILiquidMetalSink
{
    public FigurineData Design { get; private set; }

    public void SetDesign(FigurineData design)
    {
        Design = design;
        MarkDirty(true);
    }

    /// <summary>The metal in it: "copper" from ingot-copper.</summary>
    private string Metal => MetalContent?.Collectible.Variant["metal"] ?? MetalContent?.Collectible.LastCodePart();

    // The game's own check (vssurvivalmod BlockEntityToolMold.CanReceive) with our metals list in place of its
    // GetMoldedStacks: that reads the blocktype's "drop", which is GONE - the handbook built a "Can be used to mold" row
    // from it, the same design-less item over and over (Calm's screenshot, 2026-10-16). The metals are on the page instead.
    public new bool CanReceive(ItemStack metal)
    {
        if (Design == null || metal == null || Shattered) return false;
        string code = metal.Collectible.Variant["metal"] ?? metal.Collectible.LastCodePart();
        if (!BlockFigurineMoldFired.CastableMetals(Block).Contains(code)) return false;
        return MetalContent == null || (MetalContent.Collectible.Equals(MetalContent, metal, GlobalConstants.IgnoredStackAttributes) && !IsFull);
    }

    /// <summary>The finished figurine, if the mold is full and the metal hard; null otherwise.</summary>
    public ItemStack CastFigurine()
    {
        if (Design == null || Shattered || MetalContent == null || !IsFull || !IsHardened) return null;
        return ItemFigurine.Make(Api.World, Design.In(Metal));
    }

    protected override bool TryTakeContents(IPlayer byPlayer)
    {
        if (Shattered || MetalContent == null || FillLevel == 0) return false;
        if (!IsFull || !IsHardened) return false;

        Api.World.PlaySoundAt(new AssetLocation("sounds/block/ingot"), Pos, -0.5, byPlayer, false);
        if (Api.Side == EnumAppSide.Server)
        {
            var figurine = CastFigurine();
            if (figurine != null) Mold.Give(Api.World, byPlayer, figurine, Pos);
            MetalContent = null;
            FillLevel = 0;
            MarkDirty(true);
        }
        UpdateRenderer();
        return true;
    }

    public override void ToTreeAttributes(ITreeAttribute tree)
    {
        base.ToTreeAttributes(tree);
        if (Design != null) Design.WriteTo(tree.GetOrAddTreeAttribute(FigurineData.MoldKey));
    }

    public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldForResolve)
    {
        Design = FigurineData.FromTree(tree.GetTreeAttribute(FigurineData.MoldKey));
        base.FromTreeAttributes(tree, worldForResolve);
    }

    public override void GetBlockInfo(IPlayer forPlayer, StringBuilder dsc)
    {
        Mold.Info(dsc, Design, false);
        // the game's shattered line reads the molded stacks from "drop", which these molds no longer have
        if (Shattered) { dsc.AppendLine(Lang.Get("figurines:mold-shattered")); return; }
        base.GetBlockInfo(forPlayer, dsc);
    }
}
