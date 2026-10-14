using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace Figurines;

/// <summary>
/// The wooden figurine blank, placed in the world. Right-click it to choose what to carve; hold
/// right-click with a knife to carve it. When the carving is done the blank is gone and the
/// wooden figurine stands where it was, in the game's own ground storage - so it is picked up and
/// put down like any other figurine (Calm, 2026-10-08).
///
/// Deliberately simple (Calm: "not a detailed carving minigame"): one hold, shavings, a sound.
/// </summary>
public sealed class BlockFigurineBlank : Block
{
    /// <summary>Seconds of carving. From the block's attributes so it can be tuned in JSON.</summary>
    private float CarveTime => Attributes?["carveSeconds"].AsFloat(4) ?? 4;

    private const string Animation = "knifecut";
    private static readonly AssetLocation ScrapeSound = new("game:sounds/player/scrape");
    private static readonly AssetLocation DoneSound = new("game:sounds/block/planks");

    private WorldInteraction[] interactions;

    /// <summary>Client only, one local player: when the last scrape played, in seconds of this hold.</summary>
    private float lastScrape = -1;
    /// <summary>
    /// The scrape playing now, so it can be STOPPED (Calm, 2026-10-09: "The carving sound goes too long"). The game's scrape
    /// is 4.96 s long and a fresh one started every half second played to its end - up to eight at once, the last running on
    /// about 4.5 s after the figurine was done. Bookends' carving already worked this way.
    /// </summary>
    private ILoadedSound scrape;

    private static bool HoldsKnife(IPlayer player) =>
        player?.InventoryManager.ActiveHotbarSlot?.Itemstack?.Collectible.Tool == EnumTool.Knife;

    public override bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
    {
        if (world.BlockAccessor.GetBlockEntity(blockSel.Position) is not BEFigurineBlank be) return false;
        if (!world.Claims.TryAccess(byPlayer, blockSel.Position, EnumBlockAccessFlags.Use)) return false;

        // Choosing: no knife, nothing chosen yet, or sneaking to change the choice.
        if (!HoldsKnife(byPlayer) || !be.HasChoice || byPlayer.Entity.Controls.Sneak)
        {
            if (world.Api is ICoreClientAPI capi) new GuiDialogCarve(capi, blockSel.Position).TryOpen();
            return false;
        }

        // Carving.
        if (world.Api is ICoreClientAPI client)
        {
            byPlayer.Entity.StartAnimation(Animation);
            StopAnimationWhenDone(client, byPlayer, blockSel.Position.Copy());
        }
        return true;
    }

    public override bool OnBlockInteractStep(float secondsUsed, IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
    {
        if (!HoldsKnife(byPlayer)) return false;
        if (world.Side == EnumAppSide.Client)
        {
            // Shavings every step, a scrape twice a second.
            world.SpawnCubeParticles(blockSel.Position.ToVec3d().Add(0.5, 0.35, 0.5), new ItemStack(this), 0.25f, 1, 0.5f, null);
            if (secondsUsed < lastScrape) lastScrape = -1;
            if (secondsUsed - lastScrape >= 0.5f && world.Api is ICoreClientAPI capi)
            {
                lastScrape = secondsUsed;
                StopScrape();
                scrape = capi.World.LoadSound(new SoundParams
                {
                    Location = ScrapeSound,
                    Position = blockSel.Position.ToVec3f().Add(0.5f, 0.35f, 0.5f),
                    Range = 16,
                    Volume = 0.6f,
                    Pitch = 0.9f + (float)world.Rand.NextDouble() * 0.2f
                });
                scrape?.Start();
            }
        }
        return secondsUsed < CarveTime;
    }

    public override void OnBlockInteractStop(float secondsUsed, IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
    {
        if (world.Side == EnumAppSide.Client) { byPlayer.Entity.StopAnimation(Animation); StopScrape(); }
        if (world.Side != EnumAppSide.Server || secondsUsed < CarveTime - 0.05f || !HoldsKnife(byPlayer)) return;
        if (world.BlockAccessor.GetBlockEntity(blockSel.Position) is not BEFigurineBlank { HasChoice: true } be) return;

        var stack = ItemFigurine.Make(world, new FigurineData { Creature = be.Creature, Pose = be.Pose, Material = "wood" });
        if (stack == null) return;

        var knife = byPlayer.InventoryManager.ActiveHotbarSlot;
        knife.Itemstack.Collectible.DamageItem(world, byPlayer.Entity, knife, 1);
        world.PlaySoundAt(DoneSound, blockSel.Position, 0, byPlayer);

        if (!StandOnTheGround(world, byPlayer, blockSel.Position, stack))
        {
            world.BlockAccessor.SetBlock(0, blockSel.Position);
            if (!byPlayer.InventoryManager.TryGiveItemstack(stack))
                world.SpawnItemEntity(stack, blockSel.Position.ToVec3d().Add(0.5, 0.2, 0.5));
        }
    }

    public override bool OnBlockInteractCancel(float secondsUsed, IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel, EnumItemUseCancelReason cancelReason)
    {
        if (world.Side == EnumAppSide.Client) { byPlayer.Entity.StopAnimation(Animation); StopScrape(); }
        return true;
    }

    private void StopScrape()
    {
        scrape?.Stop();
        scrape?.Dispose();
        scrape = null;
    }

    /// <summary>
    /// Replace the blank with the game's ground storage holding the new figurine, centred where the
    /// blank stood and facing the carver. Centred (single-centre layout) rather than in a corner
    /// quadrant, so it does not jump sideways when the blank becomes the figurine.
    /// </summary>
    private static bool StandOnTheGround(IWorldAccessor world, IPlayer byPlayer, BlockPos pos, ItemStack stack)
    {
        var storage = world.GetBlock(new AssetLocation("game:groundstorage"));
        var props = stack.Collectible.GetBehavior<CollectibleBehaviorGroundStorable>()?.StorageProps?.Clone();
        if (storage == null || props == null) return false;

        world.BlockAccessor.SetBlock(storage.BlockId, pos);
        if (world.BlockAccessor.GetBlockEntity(pos) is not BlockEntityGroundStorage ground)
        {
            world.BlockAccessor.SetBlock(0, pos);
            return false;
        }

        props.Layout = EnumGroundStorageLayout.SingleCenter;
        ground.ForceStorageProps(props);
        ground.Inventory[0].Itemstack = stack;
        ground.Inventory[0].MarkDirty();
        ground.DetermineStorageProperties(null);

        double dx = byPlayer.Entity.Pos.X - (pos.X + 0.5), dz = byPlayer.Entity.Pos.Z - (pos.Z + 0.5);
        ground.MeshAngle = (float)Math.Round(Math.Atan2(dx, dz) / GameMath.PIHALF) * GameMath.PIHALF;
        ground.MarkDirty(true);
        return true;
    }

    /// <summary>
    /// The block turns into ground storage under the player's hands, and the client's Stop may then
    /// never arrive - the knife would keep cutting (Taxidermy found the same). So the client stops
    /// the animation itself once the blank is gone or the time is well past.
    /// </summary>
    private void StopAnimationWhenDone(ICoreClientAPI capi, IPlayer byPlayer, BlockPos pos)
    {
        long since = capi.World.ElapsedMilliseconds, id = 0;
        id = capi.Event.RegisterGameTickListener(_ =>
        {
            bool gone = capi.World.BlockAccessor.GetBlock(pos) is not BlockFigurineBlank;
            if (!gone && capi.World.ElapsedMilliseconds - since < (CarveTime + 1) * 1000) return;
            byPlayer.Entity.StopAnimation(Animation);
            StopScrape();
            capi.Event.UnregisterGameTickListener(id);
        }, 100);
    }

    public override WorldInteraction[] GetPlacedBlockInteractionHelp(IWorldAccessor world, BlockSelection selection, IPlayer forPlayer)
    {
        interactions ??=
        [
            new WorldInteraction { ActionLangCode = "figurines:blank-help-choose", MouseButton = EnumMouseButton.Right },
            new WorldInteraction
            {
                ActionLangCode = "figurines:blank-help-carve",
                MouseButton = EnumMouseButton.Right,
                Itemstacks = world.Collectibles.Where(c => c.Tool == EnumTool.Knife).Select(c => new ItemStack(c)).ToArray()
            }
        ];
        return interactions.Concat(base.GetPlacedBlockInteractionHelp(world, selection, forPlayer)).ToArray();
    }
}
