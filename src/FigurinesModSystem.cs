using Newtonsoft.Json.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Config;
using Figurines.Handbook;

namespace Figurines;

public sealed class FigurinesModSystem : ModSystem
{
    public IReadOnlyList<CreatureDefinition> Creatures { get; private set; } = [];
    public IReadOnlyDictionary<string, MaterialDefinition> Materials { get; private set; } = new Dictionary<string, MaterialDefinition>();
    public SizeSettings Sizes { get; private set; } = new();

    /// <summary>The hand-made sets - trader figurines, chess pieces, Dave (Specials.cs), in file order.</summary>
    public IReadOnlyList<SpecialDefinition> Specials { get; private set; } = [];
    private Dictionary<string, SpecialDefinition> specialsByKey = new();

    /// <summary>For static helpers (names) that have no api to hand. Both sides load the same definitions.</summary>
    public static FigurinesModSystem Instance { get; private set; }

    public override void Start(ICoreAPI api)
    {
        Instance = this;
        api.RegisterItemClass("FigurinesFigurine", typeof(ItemFigurine));
        api.RegisterBlockClass("FigurinesBlank", typeof(BlockFigurineBlank));
        api.RegisterBlockEntityClass("FigurinesBlank", typeof(BEFigurineBlank));
        api.RegisterBlockClass("FigurinesBox", typeof(BlockFigurineBox));
        api.RegisterBlockEntityClass("FigurinesBox", typeof(BEFigurineBox));
        api.RegisterBlockClass("FigurinesMoldRaw", typeof(BlockFigurineMoldRaw));
        api.RegisterBlockEntityClass("FigurinesMoldRaw", typeof(BEFigurineMoldRaw));
        api.RegisterBlockClass("FigurinesMoldFired", typeof(BlockFigurineMoldFired));
        api.RegisterBlockEntityClass("FigurinesMoldFired", typeof(BEFigurineMoldFired));
        api.RegisterItemClass("FigurinesSpecial", typeof(ItemFigurineSpecial));
        HandCarve.Apply(api);
    }

    public override void Dispose()
    {
        HandCarve.Remove();
        HandbookGroups.Remove();
        base.Dispose();
    }

    public override void StartClientSide(ICoreClientAPI capi)
    {
        // One handbook page per creature, flipping through its poses and casts, instead of 400+ pages (Calm, 2026-10-15;
        // the groups are registered with the creative stacks in AssetsFinalize). Tools/Materials Kit/src/HandbookGroups.cs.
        HandbookGroups.Apply(capi, "figurines.handbookgroups");
        // Atlas positions and uploaded meshes belong to the world being left.
        capi.Event.LeaveWorld += () => { FigurineMesher.Clear(capi); SpecialMesher.Clear(); ItemFigurine.ClearPending(); };
        // No build-everything-at-load: with every vanilla animal the antlered / horned figurines alone
        // came to 493 models, 8 seconds of stutter after joining (Calm, 2026-10-08). Each figurine is
        // built the first time it is drawn instead (FigurineMesher.Get / ItemFigurine.GenMesh).
        // Their TEXTURES are put in the block atlas here though - ten of them, no models - so a figurine
        // can be built on any thread the first time a display asks, and Universal Display measures the
        // real model rather than the stand-in. The wood (rough oak) is used by no vanilla block.
        capi.Event.LevelFinalize += () => FigurineMesher.PrepareTextures(capi, Materials.Values.Select(m => m.Texture));
        // The hand-made sets are a couple of dozen small models: all built now, so a display never waits for one.
        capi.Event.LevelFinalize += () => SpecialMesher.BuildAll(capi, Specials);
    }

    /// <summary>
    /// Definitions come from assets, so this cannot happen in Start() - assets are not there yet and
    /// GetMany returns nothing, silently. The counts are logged on purpose.
    /// </summary>
    public override void AssetsLoaded(ICoreAPI api)
    {
        var creatures = new Dictionary<string, CreatureDefinition>();
        foreach (var asset in api.Assets.GetMany("config/figurines/creatures/").OrderBy(a => a.Location.ToString()))
        {
            CreatureDefinition[] defs;
            try { defs = asset.ToObject<CreatureDefinition[]>(); }
            catch (Exception e)
            {
                api.Logger.Error("[Figurines] Cannot read {0}: {1}", asset.Location, e.Message);
                continue;
            }
            foreach (var def in defs ?? [])
            {
                if (string.IsNullOrWhiteSpace(def.Code) || string.IsNullOrWhiteSpace(def.Entity) || def.Poses is not { Length: > 0 })
                {
                    api.Logger.Error("[Figurines] Creature in {0} skipped: it needs a code, an entity and at least one pose", asset.Location);
                    continue;
                }
                if (!def.Enabled) continue;
                if (!creatures.TryAdd(def.Code, def))
                    api.Logger.Error("[Figurines] Duplicate creature {0} in {1} skipped", def.Code, asset.Location);
            }
        }
        Creatures = creatures.Values.OrderBy(d => d.Code).ToArray();

        var materials = new Dictionary<string, MaterialDefinition>();
        foreach (var asset in api.Assets.GetMany("config/figurines/materials"))
        {
            try
            {
                foreach (var m in asset.ToObject<MaterialDefinition[]>() ?? [])
                    if (!string.IsNullOrWhiteSpace(m.Code) && !string.IsNullOrWhiteSpace(m.Texture)) materials[m.Code] = m;
            }
            catch (Exception e) { api.Logger.Error("[Figurines] Cannot read {0}: {1}", asset.Location, e.Message); }
        }
        Materials = materials;

        var sizes = api.Assets.TryGet(new AssetLocation("figurines:config/figurines/sizes.json"));
        try { Sizes = sizes?.ToObject<SizeSettings>() ?? new SizeSettings(); }
        catch (Exception e) { api.Logger.Error("[Figurines] Cannot read {0}: {1}", sizes.Location, e.Message); }

        var specials = new List<SpecialDefinition>();
        var byKey = new Dictionary<string, SpecialDefinition>();
        foreach (var asset in api.Assets.GetMany("config/figurines/specials/").OrderBy(a => a.Location.ToString()))
        {
            try
            {
                foreach (var def in asset.ToObject<SpecialDefinition[]>() ?? [])
                {
                    if (string.IsNullOrWhiteSpace(def?.Code) || string.IsNullOrWhiteSpace(def.Item) || string.IsNullOrWhiteSpace(def.Shape))
                    {
                        api.Logger.Error("[Figurines] A design in {0} skipped: it needs a code, an item and a shape", asset.Location);
                        continue;
                    }
                    def.Textures ??= new Dictionary<string, string>();
                    if (byKey.TryAdd(def.Key, def)) specials.Add(def);
                    else api.Logger.Error("[Figurines] Duplicate design {0} in {1} skipped", def.Key, asset.Location);
                }
            }
            catch (Exception e) { api.Logger.Error("[Figurines] Cannot read {0}: {1}", asset.Location, e.Message); }
        }
        Specials = specials;
        specialsByKey = byKey;

        api.Logger.Event("[Figurines] {0} creature(s), {1} material(s), {2} trader / chess / Dave design(s) loaded", Creatures.Count, Materials.Count, Specials.Count);

        // after every mod's patches (JsonPatchLoader runs at 0.05) - so a mod that replaces the trade lists cannot wipe ours
        if (api.Side == EnumAppSide.Server) TradeLists.AddEntries(api);
    }

    /// <summary>
    /// The creative tab, built from the creature files - so an animal another mod adds shows up with
    /// no itemtype or patch of its own (Taxidermy builds its stacks the same way). Per creature:
    /// each carvable pose in wood, then each collector pose in its own metal.
    /// </summary>
    public override void AssetsFinalize(ICoreAPI api)
    {
        var item = api.World.GetItem(new AssetLocation("figurines:figurine"));
        if (item == null) return;
        // cast figurines in the creative tab: copper for every creature, and the first castable metal of each other mod
        // (Calm, 2026-10-15: vanilla plus one per mod, so a player sees a mod's metals work without a stack per metal)
        var casts = Materials.Values.Where(m => m.Castable && m.Code == "copper")
            .Concat(Materials.Values.Where(m => m.Castable && m.SourceMod != "game").GroupBy(m => m.SourceMod).Select(g => g.First()))
            .Select(m => m.Code).ToList();
        var stacks = new List<JsonItemStack>();
        var handbookCasts = new List<ItemStack>();
        var extraMetals = Materials.Values.Where(m => m.Castable && !casts.Contains(m.Code)).ToArray();
        foreach (var def in Creatures)
        {
            foreach (var pose in def.CarvablePoses)
                stacks.Add(Stack(item, new FigurineData { Creature = def.Code, Pose = pose.Code }));
            if (def.CarvablePoses.FirstOrDefault() is { } cast)
            {
                foreach (var metal in casts)
                    stacks.Add(Stack(item, new FigurineData { Creature = def.Code, Pose = cast.Code, Material = metal }));
                foreach (var metal in extraMetals)
                    handbookCasts.Add(Stack(item, new FigurineData { Creature = def.Code, Pose = cast.Code, Material = metal.Code }).ResolvedItemstack);
            }
            foreach (var pose in def.Poses.Where(p => !p.Carvable && Material(p.RewardMaterial) != null))
                stacks.Add(Stack(item, new FigurineData { Creature = def.Code, Pose = pose.Code, Material = pose.RewardMaterial }));
        }
        item.CreativeInventoryStacks = [new CreativeTabAndStackList { Tabs = ["general", "figurines"], Stacks = stacks.ToArray() }];
        // one handbook page per FAMILY - every bear on one page, every deer on another - as Taxidermy's mounts (Calm,
        // 2026-10-15). The family is the carving menu's: the creature's "group", else its code up to the first dash.
        HandbookGroups.Register(item, stack => FamilyOf(FigurineData.Of(stack)?.Creature) ?? "",
            stack => FamilyOf(FigurineData.Of(stack)?.Creature) is { } family ? Lang.Get("figurines:figurine-group", FamilyName(family)) : null,
            handbookCasts);
        // the page says which metals it casts in (the installed ones - a mod's metal joins the list by itself) and links the
        // guide for the how; collectors, molds and boxes are explained there (Calm, 2026-10-16: "just want say what metals
        // and link to the guide")
        string MetalsText(ICoreClientAPI capi) => string.Join(", ", Materials.Values
            .Where(m => m.Castable).Select(m => ItemFigurine.MaterialName(m.Code)).Distinct().OrderBy(name => name));
        // not on a collector's page - those poses are never cast (Calm's screenshot, 2026-10-16)
        HandbookGroups.AddSection(item, "figurines:handbook-metals-title", (capi, stack) =>
            FigurineData.Of(stack) is { } data && Creature(data.Creature)?.PoseByCode(data.Pose) is { Carvable: false } ? null : MetalsText(capi));
        HandbookGroups.AddSection(item, "figurines:handbook-guide-title", capi => Lang.Get("figurines:handbook-guide-text"));
        // the blank, the molds (casting happens there) and the blind boxes (what each holds, from its own metals list) get
        // the same sections (Calm, 2026-10-16: "Do all of those now")
        foreach (var block in api.World.Blocks.Where(b => b?.Code?.Domain == "figurines"))
        {
            string path = block.Code.Path;
            if (path.StartsWith("figurinemold-")) HandbookGroups.AddSection(block, "figurines:handbook-metals-title", MetalsText);
            if (path.StartsWith("figurinebox-"))
            {
                var box = block;
                HandbookGroups.AddSection(block, "figurines:handbook-box-title", capi => BoxText(box));
            }
            if (path == "figurineblank" || path.StartsWith("figurinemold-") || path.StartsWith("figurinebox-"))
                HandbookGroups.AddSection(block, "figurines:handbook-guide-title", capi => Lang.Get("figurines:handbook-guide-text"));
        }

        // the hand-made sets: every design of each item, in file order
        foreach (var group in Specials.GroupBy(d => d.ItemCode.ToString()))
        {
            var special = api.World.GetItem(new AssetLocation(group.Key));
            if (special == null) { api.Logger.Error("[Figurines] No item {0} for its designs", group.Key); continue; }
            var list = new List<JsonItemStack>();
            foreach (var def in group)
            {
                var tree = new TreeAttribute();
                tree.SetString(ItemFigurineSpecial.DesignKey, def.Code);
                tree.SetString(FigurineData.TypeAttribute, def.Code);
                var js = new JsonItemStack { Type = EnumItemClass.Item, Code = special.Code, StackSize = 1, Attributes = new JsonObject(JToken.Parse(tree.ToJsonToken())) };
                js.ResolvedItemstack = ItemFigurineSpecial.Make(api.World, def);
                list.Add(js);
            }
            special.CreativeInventoryStacks = [new CreativeTabAndStackList { Tabs = ["general", "figurines"], Stacks = list.ToArray() }];
            HandbookGroups.Register(special);   // one page per set: the trader figurines, the chess pieces, Dave
            HandbookGroups.AddSection(special, "figurines:handbook-guide-title", capi => Lang.Get("figurines:handbook-guide-text"));
        }
    }

    private string FamilyOf(string creature) => creature == null ? null : Creature(creature)?.GroupCode ?? creature;

    /// <summary>
    /// A family's name for its handbook page, with a capital: lang figurines:creaturegroup-&lt;family&gt; if there is one, else the
    /// name of the creature whose code IS the family ("bear"), else the family code.
    /// </summary>
    private string FamilyName(string family)
    {
        string name = Lang.HasTranslation("figurines:creaturegroup-" + family) ? Lang.Get("figurines:creaturegroup-" + family)
            : Creature(family) != null ? ItemFigurine.CreatureName(family) : family.Replace('-', ' ');
        return string.IsNullOrEmpty(name) ? name : char.ToUpperInvariant(name[0]) + name.Substring(1);
    }

    /// <summary>"75% Iron, 25% Platinum" - a blind box's attributes.metals weights as shares, largest first.</summary>
    private static string BoxText(Block box)
    {
        var metals = box.Attributes?["metals"];
        if (metals == null || !metals.Exists || metals.Token is not JObject weights) return null;
        var list = weights.Properties().Select(p => (code: p.Name, weight: (double?)p.Value ?? 0)).Where(p => p.weight > 0).ToList();
        double total = list.Sum(p => p.weight);
        if (total <= 0) return null;
        return string.Join(", ", list.OrderByDescending(p => p.weight)
            .Select(p => Math.Round(p.weight * 100 / total) + "% " + ItemFigurine.MaterialName(p.code)));
    }

    private static JsonItemStack Stack(Item item, FigurineData data)
    {
        var tree = new TreeAttribute();
        data.WriteTo(tree.GetOrAddTreeAttribute(FigurineData.Key));
        tree.SetString(FigurineData.TypeAttribute, data.DisplayType);
        var js = new JsonItemStack { Type = EnumItemClass.Item, Code = item.Code, StackSize = 1, Attributes = new JsonObject(JToken.Parse(tree.ToJsonToken())) };
        js.ResolvedItemstack = new ItemStack(item);
        data.WriteTo(js.ResolvedItemstack);
        return js;
    }

    public CreatureDefinition Creature(string code) => Creatures.FirstOrDefault(c => c.Code == code);

    public SpecialDefinition Special(AssetLocation item, string design) =>
        item != null && design != null && specialsByKey.TryGetValue(item + "|" + design, out var d) ? d : null;

    public IEnumerable<SpecialDefinition> SpecialsOf(AssetLocation item) => Specials.Where(d => d.ItemCode.Equals(item));

    public MaterialDefinition Material(string code) => code != null && Materials.TryGetValue(code, out var m) ? m : null;
}
