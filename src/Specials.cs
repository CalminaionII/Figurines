using System.Collections.Concurrent;
using System.Text;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace Figurines;

/// <summary>
/// THE HAND-MADE SETS KEPT FROM 0.3.4 (Calm, 2026-10-09: "I want the Trader ones for each trader still", "keep the chess
/// pieces and DAVE"; "each type has own code, like the trader has thiers the chess has thiers"). Calm's own models, not
/// the posed creatures: <c>figurines:traderfigurine</c> (one per vanilla trader, sold and bought by that trader),
/// <c>figurines:chesspiece</c> (carved in hand - <see cref="HandCarve"/>) and <c>figurines:dave</c> (rare loot). New codes:
/// the 0.3.4 items do not carry over. Which design a stack is lives in its <c>design</c> attribute; the designs come from
/// <c>config/figurines/specials/*.json</c> in any mod, so a set can grow without code.
/// </summary>
public sealed class SpecialDefinition
{
    /// <summary>The design, unique within its item: "agriculture", "white-king", "dave". Saved on every stack.</summary>
    public string Code { get; set; }

    /// <summary>Which item it is a design of - "traderfigurine", "chesspiece", "dave" (figurines domain unless it names one).</summary>
    public string Item { get; set; }

    /// <summary>The model, e.g. "figurines:item/chess/chess-piece-king".</summary>
    public string Shape { get; set; }

    /// <summary>The model's texture codes and what fills them.</summary>
    public Dictionary<string, string> Textures { get; set; } = new();

    /// <summary>Lang key for the name. Optional: figurines:&lt;item&gt;-&lt;design&gt; by default.</summary>
    public string Name { get; set; }

    /// <summary>Lang key for the tooltip line. Optional: figurines:&lt;item&gt;desc-&lt;design&gt; if it exists.</summary>
    public string Description { get; set; }

    /// <summary>Size against the model as made (the 0.3.4 displays drew these at 1.2; Dave at 0.69).</summary>
    public float Scale { get; set; } = 1.2f;

    /// <summary>Turn about the upright, degrees - the 0.3.4 display turn, so they face the way they used to.</summary>
    public float RotateY { get; set; } = 90;

    /// <summary>Carved in hand from this item in the LEFT hand (wildcards allowed), e.g. "game:bone". Optional.</summary>
    public string CarvedFrom { get; set; }

    /// <summary>What it is, for choosing while carving - "king"; designs carved from different items share it (white and black king).</summary>
    public string Piece { get; set; }

    public AssetLocation ItemCode => new(Item.Contains(':') ? Item : "figurines:" + Item);

    public string Key => ItemCode + "|" + Code;
}

/// <summary>The models of the hand-made sets: built once, on the main thread, at their own size, feet down, centred.</summary>
public static class SpecialMesher
{
    private static readonly ConcurrentDictionary<string, MeshData> meshes = new();
    private static readonly Dictionary<string, MultiTextureMeshRef> refs = new();

    public static void Clear()
    {
        meshes.Clear();
        foreach (var r in refs.Values) r?.Dispose();
        refs.Clear();
    }

    public static bool IsDone(SpecialDefinition def) => def != null && meshes.ContainsKey(def.Key);

    /// <summary>Any thread: the model if it is built. Built for every design at load, so a display never waits.</summary>
    public static MeshData TryGet(SpecialDefinition def) => def != null && meshes.TryGetValue(def.Key, out var m) ? m : null;

    /// <summary>Main thread: build every design (a couple of dozen small models; textures go into the block atlas).</summary>
    public static void BuildAll(ICoreClientAPI capi, IEnumerable<SpecialDefinition> defs)
    {
        int built = 0;
        foreach (var def in defs)
        {
            if (meshes.ContainsKey(def.Key)) continue;
            MeshData mesh = null;
            try { mesh = Build(capi, def); }
            catch (Exception e) { capi.Logger.Error("[Figurines] Cannot build {0}: {1}", def.Key, e.Message); }
            meshes[def.Key] = mesh;
            if (mesh != null) built++;
        }
        capi.Logger.Event("[Figurines] {0} trader / chess / Dave model(s) built", built);
    }

    public static MultiTextureMeshRef GetItemRef(ICoreClientAPI capi, SpecialDefinition def)
    {
        if (def == null) return null;
        if (refs.TryGetValue(def.Key, out var r)) return r;
        var mesh = TryGet(def);
        if (mesh == null) return null;
        r = capi.Render.UploadMultiTextureMesh(FigurineMesher.FitToBlock(mesh.Clone()));
        refs[def.Key] = r;
        return r;
    }

    private static MeshData Build(ICoreClientAPI capi, SpecialDefinition def)
    {
        var loc = new AssetLocation(def.Shape).WithPathPrefixOnce("shapes/").WithPathAppendixOnce(".json");
        var shape = Shape.TryGet(capi, loc);
        if (shape == null)
        {
            capi.Logger.Warning("[Figurines] Model {0} for {1} not found", loc, def.Key);
            return null;
        }
        capi.Tesselator.TesselateShape("figurines " + def.Key, shape, out MeshData mesh, new Textures(capi, def, shape));
        if (mesh == null || mesh.VerticesCount == 0) return null;

        mesh.Rotate(new Vec3f(0.5f, 0, 0.5f), 0, def.RotateY * GameMath.DEG2RAD, 0);
        FigurineMesher.Bounds(mesh, out float minX, out float minY, out float minZ, out float maxX, out _, out float maxZ);
        mesh.Translate(-(minX + maxX) / 2, -minY, -(minZ + maxZ) / 2);
        mesh.Scale(new Vec3f(0, 0, 0), def.Scale, def.Scale, def.Scale);
        mesh.Translate(0.5f, 0, 0.5f);
        mesh.RenderPassesAndExtraBits.Fill((short)EnumChunkRenderPass.OpaqueNoCull);
        return mesh;
    }

    /// <summary>The design's textures, then the model's own; put into the block atlas (main thread).</summary>
    private sealed class Textures : ITexPositionSource
    {
        private readonly ICoreClientAPI capi;
        private readonly SpecialDefinition def;
        private readonly Shape shape;

        public Textures(ICoreClientAPI capi, SpecialDefinition def, Shape shape)
        {
            this.capi = capi;
            this.def = def;
            this.shape = shape;
        }

        public Size2i AtlasSize => capi.BlockTextureAtlas.Size;

        public TextureAtlasPosition this[string code]
        {
            get
            {
                AssetLocation loc = null;
                if (code != null && def.Textures.TryGetValue(code, out var path)) loc = new AssetLocation(path);
                else if (code != null && shape.Textures != null && shape.Textures.TryGetValue(code, out var own)) loc = own;
                if (loc == null) return capi.BlockTextureAtlas.UnknownTexturePosition;
                capi.BlockTextureAtlas.GetOrInsertTexture(new CompositeTexture(loc), out _, out var pos);
                return pos ?? capi.BlockTextureAtlas.UnknownTexturePosition;
            }
        }
    }
}

/// <summary>
/// The item class of all three sets. Like the figurines it goes on the ground, on shelves, in display cases and on Window
/// Display sills through the game's own blocks, which keep the stack - and so the design - as it was.
/// </summary>
public sealed class ItemFigurineSpecial : Item, IContainedMeshSource, IDisplayableProps
{
    public const string DesignKey = "design";

    public static ItemStack Make(IWorldAccessor world, SpecialDefinition def, int quantity = 1)
    {
        var item = world.GetItem(def.ItemCode);
        if (item == null) return null;
        var stack = new ItemStack(item, quantity);
        stack.Attributes.SetString(DesignKey, def.Code);
        stack.Attributes.SetString(FigurineData.TypeAttribute, def.Code);
        return stack;
    }

    /// <summary>The stack's design; a stack with none (the handbook's plain page) draws as the item's first design.</summary>
    public SpecialDefinition Def(ItemStack stack)
    {
        var system = FigurinesModSystem.Instance;
        if (system == null) return null;
        string design = stack?.Attributes?.GetString(DesignKey);
        return (design != null ? system.Special(Code, design) : null) ?? system.SpecialsOf(Code).FirstOrDefault();
    }

    public override string GetHeldItemName(ItemStack itemStack)
    {
        var def = Def(itemStack);
        if (def == null) return base.GetHeldItemName(itemStack);
        string key = def.Name ?? Code.Domain + ":" + Code.Path + "-" + def.Code;
        return Lang.HasTranslation(key) ? Lang.Get(key) : base.GetHeldItemName(itemStack);
    }

    public override void GetHeldItemInfo(ItemSlot inSlot, StringBuilder dsc, IWorldAccessor world, bool withDebugInfo)
    {
        base.GetHeldItemInfo(inSlot, dsc, world, withDebugInfo);
        var def = Def(inSlot.Itemstack);
        if (def == null) return;
        string key = def.Description ?? Code.Domain + ":" + Code.Path + "desc-" + def.Code;
        if (Lang.HasTranslation(key)) dsc.AppendLine(Lang.Get(key));
    }

    public override void OnBeforeRender(ICoreClientAPI capi, ItemStack itemstack, EnumItemRenderTarget target, ref ItemRenderInfo renderinfo)
    {
        if (SpecialMesher.GetItemRef(capi, Def(itemstack)) is { } meshRef) renderinfo.ModelRef = meshRef;
        base.OnBeforeRender(capi, itemstack, target, ref renderinfo);
    }

    /// <summary>Ground storage, shelves, display cases and Universal Display ask here. Built at load, so it is ready.</summary>
    public MeshData GenMesh(ItemSlot slot, ITextureAtlasAPI targetAtlas, BlockPos atBlockPos) => SpecialMesher.TryGet(Def(slot.Itemstack))?.Clone();

    public string GetMeshCacheKey(ItemSlot slot)
    {
        var def = Def(slot.Itemstack);
        string key = Code + "|" + (def?.Code ?? "");
        return SpecialMesher.IsDone(def) ? key : key + "|wait";
    }

    /// <summary>Nothing of its own - implemented so Universal Display keys each design's measured size on its <c>type</c>.</summary>
    public DisplayableAttributes GetDisplayableProps(ItemSlot inSlot, string displayType) => null;
}

/// <summary>
/// CARVING IN HAND (Calm, 2026-10-09: "do the in hand carving for the chess pieces so we can see it in game as they are
/// meant to be carved"; "knife working animation should be enough, need to enable the bone and the charcoal into the
/// left hand slot"). Knife in the right hand, bone (white) or charcoal (black) in the left: sneak + right-click picks the
/// piece, holding right-click carves it in 4 seconds with the knife animation; one bone or charcoal used, the knife worn
/// a little, the piece into the inventory. Any design with <c>carvedFrom</c> can be carved this way.
///
/// THE ONLY HARMONY IN THE MOD, and why: vssurvivalmod ItemKnife.OnHeldInteractStep / Stop never call base, so a
/// collectible behaviour on the knife is never asked to keep a hold going. Prefixes on the knife's four held-interact
/// methods, doing nothing unless the left hand holds something a design is carved from (or a carve is under way).
/// Applied once per process (singleplayer runs both sides in one).
/// </summary>
public static class HandCarve
{
    private const string HarmonyId = "figurines.handcarve";
    private const string Carving = "figurinesHandCarving";
    private const string Choice = "figurinesHandCarve";
    private const string Animation = "knifecut";
    private const float Seconds = 4f;
    private static readonly AssetLocation ScrapeSound = new("game:sounds/player/scrape");
    private static Harmony harmony;
    private static ILoadedSound scrape;

    public static void Apply(ICoreAPI api)
    {
        if (harmony != null) return;
        harmony = new Harmony(HarmonyId);
        var t = typeof(ItemKnife);
        void Patch(string method, string prefix)
        {
            var target = AccessTools.Method(t, method);
            if (target == null) { api.Logger.Error("[Figurines] ItemKnife.{0} not found - chess pieces cannot be carved in hand", method); return; }
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(HandCarve), prefix));
        }
        Patch(nameof(ItemKnife.OnHeldInteractStart), nameof(Start));
        Patch(nameof(ItemKnife.OnHeldInteractStep), nameof(Step));
        Patch(nameof(ItemKnife.OnHeldInteractStop), nameof(Stop));
        Patch(nameof(ItemKnife.OnHeldInteractCancel), nameof(Cancel));
    }

    public static void Remove()
    {
        harmony?.UnpatchAll(HarmonyId);
        harmony = null;
    }

    /// <summary>The designs the item in the left hand can become, in file order.</summary>
    private static List<SpecialDefinition> Options(EntityAgent byEntity)
    {
        var code = byEntity?.LeftHandItemSlot?.Itemstack?.Collectible?.Code;
        var system = FigurinesModSystem.Instance;
        if (code == null || system == null) return [];
        return system.Specials.Where(d => d.CarvedFrom != null && WildcardUtil.Match(new AssetLocation(d.CarvedFrom), code)).ToList();
    }

    private static SpecialDefinition Chosen(EntityAgent byEntity, List<SpecialDefinition> options)
    {
        string piece = byEntity.WatchedAttributes.GetString(Choice);
        return options.FirstOrDefault(d => (d.Piece ?? d.Code) == piece);
    }

    private static string NameOf(IWorldAccessor world, SpecialDefinition def) =>
        ItemFigurineSpecial.Make(world, def)?.GetName() ?? def.Code;

    public static bool Start(ItemSlot slot, EntityAgent byEntity, EntitySelection entitySel, bool firstEvent, ref EnumHandHandling handling)
    {
        if (!firstEvent || entitySel != null || byEntity is not EntityPlayer) return true;
        var options = Options(byEntity);
        if (options.Count == 0) return true;

        var chosen = Chosen(byEntity, options);
        if (byEntity.Controls.ShiftKey || chosen == null)
        {
            // pick the next piece (or the first): both sides work it out the same way, the server's copy is kept
            int at = chosen == null ? -1 : options.IndexOf(chosen);
            var next = byEntity.Controls.ShiftKey ? options[(at + 1) % options.Count] : options[0];
            if (byEntity.World.Side == EnumAppSide.Server)
            {
                byEntity.WatchedAttributes.SetString(Choice, next.Piece ?? next.Code);
                byEntity.WatchedAttributes.MarkPathDirty(Choice);
            }
            else (byEntity.Api as ICoreClientAPI)?.ShowChatMessage(Lang.Get("figurines:handcarve-chosen", NameOf(byEntity.World, next)));
            if (byEntity.Controls.ShiftKey) { handling = EnumHandHandling.PreventDefault; return false; }
            chosen = next;
        }

        byEntity.Attributes.SetBool(Carving, true);
        if (byEntity.World.Side == EnumAppSide.Client)
        {
            byEntity.StartAnimation(Animation);
            StopScrape();
            scrape = (byEntity.Api as ICoreClientAPI)?.World.LoadSound(new SoundParams
            {
                Location = ScrapeSound, ShouldLoop = false, Position = byEntity.Pos.XYZ.ToVec3f(),
                DisposeOnFinish = false, Volume = 0.6f, Pitch = 0.9f + (float)byEntity.World.Rand.NextDouble() * 0.2f
            });
            scrape?.Start();
        }
        handling = EnumHandHandling.PreventDefault;
        return false;
    }

    public static bool Step(float secondsUsed, ItemSlot slot, EntityAgent byEntity, ref bool __result)
    {
        if (byEntity == null || !byEntity.Attributes.GetBool(Carving)) return true;
        var options = Options(byEntity);
        var chosen = options.Count == 0 ? null : Chosen(byEntity, options) ?? options[0];
        if (chosen == null) { __result = false; return false; }
        if (secondsUsed < Seconds) { __result = true; return false; }

        if (byEntity.World.Side == EnumAppSide.Server && byEntity.Attributes.GetBool(Carving))
        {
            byEntity.Attributes.SetBool(Carving, false);
            var stack = ItemFigurineSpecial.Make(byEntity.World, chosen);
            if (stack != null)
            {
                byEntity.LeftHandItemSlot.TakeOut(1);
                byEntity.LeftHandItemSlot.MarkDirty();
                slot.Itemstack?.Collectible.DamageItem(byEntity.World, byEntity, slot, 1);
                if (!byEntity.TryGiveItemStack(stack)) byEntity.World.SpawnItemEntity(stack, byEntity.Pos.XYZ);
                ((byEntity as EntityPlayer)?.Player as IServerPlayer)?.BroadcastPlayerData();
            }
        }
        __result = false;
        return false;
    }

    public static bool Stop(EntityAgent byEntity) => !End(byEntity);

    public static bool Cancel(EntityAgent byEntity, ref bool __result)
    {
        if (!End(byEntity)) return true;
        __result = true;
        return false;
    }

    /// <summary>True if a carve was under way (and is now over).</summary>
    private static bool End(EntityAgent byEntity)
    {
        if (byEntity == null || !byEntity.Attributes.HasAttribute(Carving)) return false;
        bool was = byEntity.Attributes.GetBool(Carving) || byEntity.World.Side == EnumAppSide.Server;
        byEntity.Attributes.RemoveAttribute(Carving);
        if (byEntity.World.Side == EnumAppSide.Client) { byEntity.StopAnimation(Animation); StopScrape(); }
        return was;
    }

    private static void StopScrape()
    {
        scrape?.Stop();
        scrape?.Dispose();
        scrape = null;
    }
}
