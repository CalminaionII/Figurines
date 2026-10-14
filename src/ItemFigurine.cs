using System.Collections.Concurrent;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace Figurines;

/// <summary>
/// THE figurine - one item code for every creature, pose and material (Calm, 2026-10-08: "all in
/// one code the same as Taxidermy"). What it is lives in the stack's <c>figurine</c> attribute
/// (<see cref="FigurineData"/>), and the model is built from it (<see cref="FigurineMesher"/>).
/// It goes on the ground, in display cases and on shelves through the game's own blocks, which
/// keep the stack - and so the creature, pose and material - exactly as it was.
/// </summary>
public sealed class ItemFigurine : Item, IContainedMeshSource, IDisplayableProps
{
    public static ItemStack Make(IWorldAccessor world, FigurineData data)
    {
        var item = world.GetItem(new AssetLocation("figurines:figurine"));
        if (item == null) return null;
        var stack = new ItemStack(item);
        data.WriteTo(stack);
        return stack;
    }

    /// <summary>
    /// The material of a stack that names one but no creature - a trader's "any gold figurine" line,
    /// which matches every gold figurine because the trader only asks that its attributes be a SUBSET
    /// of the offered stack's (vssurvivalmod InventoryTrader.GetBuyingConditionsSlot -> Satisfies).
    /// </summary>
    private static string AnyMaterialOf(ItemStack stack) =>
        FigurineData.Of(stack) == null ? stack?.Attributes?.GetTreeAttribute(FigurineData.Key)?.GetString("material") : null;

    /// <summary>
    /// A figurine with no creature draws as the first creature: in wood for the handbook's plain page,
    /// or in the named material (and that metal's own pose, if it has one) for an "any animal" line.
    /// </summary>
    private FigurineData DataOrExample(ItemStack stack)
    {
        var data = FigurineData.Of(stack);
        if (data != null) return data;
        var first = api.ModLoader.GetModSystem<FigurinesModSystem>().Creatures.FirstOrDefault();
        if (first == null) return null;
        string material = AnyMaterialOf(stack) ?? "wood";
        return new FigurineData { Creature = first.Code, Material = material, Pose = first.PoseByCode(material) != null ? material : "normal" };
    }

    public override string GetHeldItemName(ItemStack itemStack)
    {
        var data = FigurineData.Of(itemStack);
        if (data == null)
        {
            string any = AnyMaterialOf(itemStack);
            return any == null ? base.GetHeldItemName(itemStack) : Lang.Get("figurines:figurine-any", MaterialName(any));
        }
        return Lang.Get("figurines:figurine-name", MaterialName(data.Material), CreatureName(data.Creature));
    }

    /// <summary>
    /// "bear", lower case for "Wooden bear figurine". The definition's own lang key, else
    /// figurines:creature-&lt;code&gt;, else the game's name for the entity with any "(male)" taken off -
    /// so an animal another mod adds is named without a lang file of ours.
    /// </summary>
    public static string CreatureName(string code)
    {
        var def = FigurinesModSystem.Instance?.Creature(code);
        if (def?.Name != null && Lang.HasTranslation(def.Name)) return Lang.Get(def.Name);
        if (Lang.HasTranslation("figurines:creature-" + code)) return Lang.Get("figurines:creature-" + code);
        if (def?.Entity != null)
        {
            var entity = new AssetLocation(def.Entity);
            string name = Lang.GetMatching(entity.Domain + ":item-creature-" + entity.Path);
            if (!string.IsNullOrEmpty(name) && !name.StartsWith("item-creature-") && !name.Contains(":item-creature-"))
                return System.Text.RegularExpressions.Regex.Replace(name, @"\s*\([^)]*\)\s*$", "").Trim().ToLowerInvariant();
        }
        return code;
    }

    public static string MaterialName(string code)
    {
        // Ours, else the game's own metal name ("material-copper"), else the code - so a material another mod adds is named.
        if (Lang.HasTranslation("figurines:material-" + code)) return Lang.Get("figurines:material-" + code);
        if (Lang.HasTranslation("game:material-" + code)) return Lang.Get("game:material-" + code);
        return string.IsNullOrEmpty(code) ? code : char.ToUpperInvariant(code[0]) + code.Substring(1);
    }

    public override void GetHeldItemInfo(ItemSlot inSlot, StringBuilder dsc, IWorldAccessor world, bool withDebugInfo)
    {
        base.GetHeldItemInfo(inSlot, dsc, world, withDebugInfo);
        var data = FigurineData.Of(inSlot.Itemstack);
        if (data == null) return;
        dsc.AppendLine(data.Material == "wood"
            ? Lang.Get("figurines:figurine-carved")
            : Lang.Get("figurines:figurine-cast", MaterialName(data.Material).ToLowerInvariant()));
        // A collector's piece is a pose nobody can carve (silver / gold / platinum).
        var def = FigurinesModSystem.Instance?.Creature(data.Creature);
        if (def?.PoseByCode(data.Pose) is { Carvable: false }) dsc.AppendLine(Lang.Get("figurines:figurine-collector"));
    }

    /// <summary>
    /// The figurine each stack's attributes describe, read once per attributes tree (Calm, 2026-10-09: performance -
    /// this runs every frame for every figurine drawn; reading it each time made garbage every frame). A changed stack
    /// gets a new attributes tree, so it is read again.
    /// </summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Vintagestory.API.Datastructures.ITreeAttribute, FigurineData> drawn = new();

    public override void OnBeforeRender(ICoreClientAPI capi, ItemStack itemstack, EnumItemRenderTarget target, ref ItemRenderInfo renderinfo)
    {
        var attributes = itemstack?.Attributes;
        FigurineData data = null;
        if (attributes != null && !drawn.TryGetValue(attributes, out data))
        {
            data = DataOrExample(itemstack);
            if (data != null) drawn.AddOrUpdate(attributes, data);
        }
        if (data != null && FigurineMesher.GetItemRef(capi, data) is { } meshRef) renderinfo.ModelRef = meshRef;
        base.OnBeforeRender(capi, itemstack, target, ref renderinfo);
    }

    /// <summary>
    /// Ground storage, display cases and shelves ask here from the TESSELATION thread. The mesh is
    /// built on the main thread (atlas inserts), so when it is not ready yet: queue the build and
    /// a redraw, and let the display show the item's default model for a moment. Taxidermy's way.
    /// </summary>
    public MeshData GenMesh(ItemSlot slot, ITextureAtlasAPI targetAtlas, BlockPos atBlockPos)
    {
        if (api is not ICoreClientAPI capi) return null;
        var data = FigurineData.Of(slot.Itemstack);
        if (data == null) return null;

        // Built here and now when its textures are already in the atlas - the display must never see
        // a stand-in, because Universal Display measures the first mesh it gets and keeps that size.
        FigurineMesher.Get(capi, data);
        if (FigurineMesher.IsDone(capi, data)) return FigurineMesher.TryGet(capi, data)?.Clone();

        // ONE QUEUED BUILD PER MODEL (2026-10-10): every display asking meanwhile only adds itself to the list, and all
        // of them are redrawn when it is done (each ask used to queue its own build + redraw).
        var waiting = Pending.GetOrAdd(data.MeshKey, _ => new ConcurrentDictionary<BlockPos, byte>());
        if (atBlockPos != null) waiting[atBlockPos.Copy()] = 0;
        if (Queued.TryAdd(data.MeshKey, 0))
        {
            string key = data.MeshKey;
            capi.Event.EnqueueMainThreadTask(() =>
            {
                FigurineMesher.Get(capi, data);
                Queued.TryRemove(key, out _);
                if (Pending.TryRemove(key, out var redraw))
                    foreach (var pos in redraw.Keys) Redraw(capi, pos);
            }, "figurines-mesh");
        }
        return null;
    }

    /// <summary>Models with a build queued, and the displays waiting on each - per session (Clear on world start).</summary>
    private static readonly ConcurrentDictionary<string, byte> Queued = new();
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<BlockPos, byte>> Pending = new();

    public static void ClearPending()
    {
        Queued.Clear();
        Pending.Clear();
    }

    /// <summary>
    /// The display caches its mesh under this key and asks for it on every lookup. While the mesh
    /// is unbuilt the key ends in "|wait", so the stand-in is cached under that, and the real mesh
    /// is asked for again once it exists - nothing of the game's is ever removed.
    /// </summary>
    public string GetMeshCacheKey(ItemSlot slot)
    {
        var data = FigurineData.Of(slot.Itemstack);
        string key = Code + "|" + (data?.MeshKey ?? "");
        if (data == null || api is not ICoreClientAPI capi) return key;
        return FigurineMesher.IsDone(capi, data) ? key : key + "|wait";
    }

    private static void Redraw(ICoreClientAPI capi, BlockPos pos)
    {
        if (pos == null) return;
        if (capi.World.BlockAccessor.GetBlockEntity(pos) is BlockEntityDisplay display) display.updateMeshes();
        capi.World.BlockAccessor.MarkBlockDirty(pos);
    }

    /// <summary>
    /// Nothing of its own - the "displayable" attributes still answer. Implemented only because
    /// Universal Display reads the stack's <c>type</c> (FigurineData.DisplayType) when the item is
    /// IDisplayableProps, which gives every creature + pose its own measured footprint.
    /// </summary>
    public DisplayableAttributes GetDisplayableProps(ItemSlot inSlot, string displayType) => null;
}
