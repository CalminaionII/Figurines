using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace Figurines;

/// <summary>
/// The blind box (Calm, 2026-10-08). Break it open and you get one figurine - which animal is the
/// surprise. The TRADER box (one vanilla trader sells it) holds silver or gold, the RUIN box (ruin
/// loot) always platinum; either way the figurine is in that metal's own collector pose. Nothing on
/// the outside says which metal is in a trader box.
///
/// What a box can hold comes from the block's <c>metals</c> attribute (metal -> weight) and the
/// creature definitions: a creature is a candidate for a metal when it has a pose of that name.
/// Nothing in here lists animals, so a new creature file is in the boxes at once.
/// </summary>
public sealed class BlockFigurineBox : Block
{
    public override bool TryPlaceBlock(IWorldAccessor world, IPlayer byPlayer, ItemStack itemstack, BlockSelection blockSel, ref string failureCode)
    {
        if (!base.TryPlaceBlock(world, byPlayer, itemstack, blockSel, ref failureCode)) return false;
        if (world.BlockAccessor.GetBlockEntity(blockSel.Position) is BEFigurineBox be)
        {
            var pos = blockSel.DidOffset ? blockSel.Position.AddCopy(blockSel.Face.Opposite) : blockSel.Position;
            double dx = byPlayer.Entity.Pos.X - (pos.X + blockSel.HitPosition.X);
            double dz = byPlayer.Entity.Pos.Z - (pos.Z + blockSel.HitPosition.Z);
            be.MeshAngleRad = (float)Math.Round(Math.Atan2(dx, dz) / GameMath.PIHALF) * GameMath.PIHALF;
            be.MarkDirty(true);
        }
        return true;
    }

    /// <summary>metal -> weight, from the block's attributes.</summary>
    private Dictionary<string, float> Metals => Attributes?["metals"].AsObject<Dictionary<string, float>>() ?? new Dictionary<string, float>();

    /// <summary>Every figurine this box could hold: each creature with a pose for one of its metals, in that metal.</summary>
    private List<FigurineData> Candidates(ICoreAPI api, string metal = null)
    {
        var system = api.ModLoader.GetModSystem<FigurinesModSystem>();
        var found = new List<FigurineData>();
        foreach (var m in metal == null ? Metals.Keys.ToArray() : new[] { metal })
        {
            if (system.Material(m) == null) continue;
            foreach (var def in system.Creatures)
                if (def.PoseByCode(m) != null) found.Add(new FigurineData { Creature = def.Code, Pose = m, Material = m });
        }
        return found;
    }

    /// <summary>
    /// Open the box: ONE figurine, rolled now - the metal by its weight, then the animal.
    /// SERVER SIDE ONLY: GetDrops runs on both, and a client rolling for itself would show a
    /// different figurine from the one the server gives.
    /// </summary>
    public override ItemStack[] GetDrops(IWorldAccessor world, BlockPos pos, IPlayer byPlayer, float dropQuantityMultiplier = 1)
    {
        if (world.Side != EnumAppSide.Server) return [];
        var metals = Metals.Where(kv => kv.Value > 0 && Candidates(world.Api, kv.Key).Count > 0).ToList();
        if (metals.Count == 0)
        {
            world.Logger.Warning("[Figurines] {0} opened but no creature has a pose for any of its metals - nothing given", Code);
            return [];
        }

        double roll = world.Rand.NextDouble() * metals.Sum(kv => kv.Value);
        string metal = metals[^1].Key;
        foreach (var (m, weight) in metals)
        {
            if ((roll -= weight) < 0) { metal = m; break; }
        }
        var candidates = Candidates(world.Api, metal);
        var stack = ItemFigurine.Make(world, candidates[world.Rand.Next(candidates.Count)]);
        return stack == null ? [] : [stack];
    }

    /// <summary>The handbook's "Drops when broken": everything this box could hold.</summary>
    public override BlockDropItemStack[] GetDropsForHandbook(ItemStack handbookStack, IPlayer forPlayer) =>
        Candidates(api).Select(d => ItemFigurine.Make(api.World, d)).Where(s => s != null).Select(s => new BlockDropItemStack(s)).ToArray();

    /// <summary>Broken open: the figurine goes straight into the player's inventory, or at their feet.</summary>
    public override void OnBlockBroken(IWorldAccessor world, BlockPos pos, IPlayer byPlayer, float dropQuantityMultiplier = 1)
    {
        if (world.Side == EnumAppSide.Server)
        {
            foreach (var drop in GetDrops(world, pos, byPlayer, dropQuantityMultiplier))
            {
                if (byPlayer == null || !byPlayer.InventoryManager.TryGiveItemstack(drop))
                    world.SpawnItemEntity(drop, pos.ToVec3d().Add(0.5, 0.5, 0.5));
            }
            world.BlockAccessor.SetBlock(0, pos);
        }
    }
}

/// <summary>A placed box: only remembers which way it faces.</summary>
public sealed class BEFigurineBox : BlockEntity
{
    public float MeshAngleRad;
    private MeshData mesh;

    public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor world)
    {
        base.FromTreeAttributes(tree, world);
        MeshAngleRad = tree.GetFloat("meshAngleRad");
        mesh = null;
    }

    public override void ToTreeAttributes(ITreeAttribute tree)
    {
        base.ToTreeAttributes(tree);
        tree.SetFloat("meshAngleRad", MeshAngleRad);
    }

    public override bool OnTesselation(ITerrainMeshPool mesher, ITesselatorAPI tessThreadTesselator)
    {
        if (mesh == null && Api is ICoreClientAPI capi)
        {
            capi.Tesselator.TesselateBlock(Block, out var m);
            mesh = m?.Rotate(new Vec3f(0.5f, 0.5f, 0.5f), 0, MeshAngleRad, 0);
        }
        if (mesh != null) mesher.AddMeshData(mesh);
        return true;
    }
}
