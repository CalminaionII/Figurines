using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;

namespace Figurines;

/// <summary>
/// One creature a figurine can be made of. Loaded from every
/// <c>assets/&lt;domain&gt;/config/figurines/creatures/*.json</c> in every mod, so adding an animal
/// is one file (the same idea as Taxidermy's animal definitions).
/// </summary>
public sealed class CreatureDefinition
{
    /// <summary>Stable id, saved into every figurine. Never rename after release.</summary>
    public string Code { get; set; }

    /// <summary>The game entity whose model is used, e.g. "game:bear-brown-adult-male".</summary>
    public string Entity { get; set; }

    /// <summary>
    /// The poses. "normal" is the carved one; "silver", "gold" and "platinum" are the collector
    /// poses, one per metal, never offered at the blank.
    /// </summary>
    public FigurinePose[] Poses { get; set; } = [new()];

    /// <summary>
    /// Optional: the figurine's longest side - length or height - IN ITS NORMAL POSE, in blocks.
    /// Left out, the creature is sized TRUE TO SCALE by config/figurines/sizes.json (Calm,
    /// 2026-10-08), which is what keeps a bear bigger than a drifter. The same scale is used for
    /// every pose, so the poses keep the animal's real proportions.
    /// </summary>
    public float? Size { get; set; }

    /// <summary>
    /// Parts of the model left off the figurine, by name (case-insensitive "contains"). The game
    /// draws eyes as a bar through the head with a see-through texture; in wood or metal that bar
    /// would fight the head's own faces, so eyes go by default. So do eyelids and the closed-eye pieces
    /// some models carry for sleeping ("L sleep", "LidR"...): in one material they cannot show, and
    /// awake they sit on the face and flicker against it (Calm, 2026-10-08: the panda's "off texture").
    /// </summary>
    public string[] RemoveElements { get; set; } = ["eye", "lid", "sleep"];

    /// <summary>
    /// Items attached to the model the way the living animal wears them - antlers, horns, tusks
    /// (e.g. "game:antler-whitetail-06"). Without them a stag's figurine has a bare head.
    /// </summary>
    public string[] Attachments { get; set; } = [];

    /// <summary>
    /// Lang key for the creature's name. Optional: without it the name is figurines:creature-&lt;code&gt;
    /// if that exists, else the game's own name for the entity - so a modded animal needs no lang file.
    /// </summary>
    public string Name { get; set; }

    /// <summary>
    /// The family it is listed under in the carving menu (Bear, Deer, Goat...). Optional: by default
    /// the code up to its first dash, so "bear-panda" is a Bear and another mod's "lynx" a Lynx.
    /// A mod adding a bear of its own would set "bear" here to list it with the game's.
    /// </summary>
    public string Group { get; set; }

    /// <summary>"animal" (the default) or "hostile" - which filter tab of the carving window it is under.</summary>
    public string Category { get; set; } = "animal";

    public string GroupCode => !string.IsNullOrEmpty(Group) ? Group : Code.Split('-')[0];

    public bool Enabled { get; set; } = true;

    public FigurinePose PoseByCode(string code) => Poses.FirstOrDefault(p => p.Code == code);

    public IEnumerable<FigurinePose> CarvablePoses => Poses.Where(p => p.Carvable);
}

public sealed class FigurinePose
{
    /// <summary>"normal", or the collector metal it belongs to: "silver", "gold", "platinum".</summary>
    public string Code { get; set; } = "normal";

    /// <summary>Animation code in the entity's shape, or null for the rest pose.</summary>
    public string Animation { get; set; }

    /// <summary>Zero-based frame of that animation to freeze at. Clamped to the animation's length.</summary>
    public float Frame { get; set; }

    /// <summary>Offered at the blank - the standing ("normal") pose and one more (Calm, 2026-10-08). Collector poses never are.</summary>
    public bool Carvable { get; set; }

    /// <summary>Reward metal for this collector pose. Omitted = the pose code, for existing addon definitions.
    /// Separate from Code so changing new rewards never renames a pose saved on existing figurines.</summary>
    public string CollectorMaterial { get; set; }

    public string RewardMaterial => CollectorMaterial ?? Code;
}

/// <summary>What a figurine is made of, and how it is painted. From config/figurines/materials.json.</summary>
public sealed class MaterialDefinition
{
    public string Code { get; set; }

    /// <summary>The texture painted on every face of the model.</summary>
    public string Texture { get; set; }

    /// <summary>
    /// Whether the fired mold takes this metal (Calm, 2026-10-08: other materials should be easy to
    /// add). The code must be the metal's own - the crucible's ingot-&lt;code&gt;. A mod adds a castable
    /// metal with one materials file; a file loaded later may change an entry by repeating its code.
    /// </summary>
    public bool Castable { get; set; }

    /// <summary>
    /// The mod the material comes from, when its texture is in the game's domain but the game does not have it (Pewter
    /// ships game:block/metal/plate/pewter). Left out = the game's. Decides the creative tab (one stack per mod) and the
    /// handbook's "Can be made with" line.
    /// </summary>
    public string Source { get; set; }

    /// <summary>The mod id this material counts as: <see cref="Source"/>, else the texture's domain.</summary>
    public string SourceMod => !string.IsNullOrEmpty(Source) ? Source
        : Texture != null && Texture.Contains(':') ? Texture.Substring(0, Texture.IndexOf(':')) : "game";
}

/// <summary>
/// A figurine's identity, as one <c>figurine</c> tree attribute on the stack: which creature,
/// which pose, which material. Everything the client needs to draw it, so a picked-up figurine
/// comes back exactly as it was.
/// </summary>
public sealed class FigurineData
{
    public const string Key = "figurine";

    private string creature, pose = "normal", material = "wood", meshKey;

    public string Creature { get => creature; set { creature = value; meshKey = null; } }
    public string Pose { get => pose; set { pose = value; meshKey = null; } }
    public string Material { get => material; set { material = value; meshKey = null; } }

    /// <summary>
    /// What makes two figurines look different - and the mesh cache key. Worked out once, not on every ask
    /// (Calm, 2026-10-09: performance - it is asked every frame for every figurine drawn).
    /// </summary>
    public string MeshKey => meshKey ??= Creature + "|" + Pose + "|" + Material;

    /// <summary>
    /// A mold carries only the design - creature and pose - under its own key, so a mold can never
    /// be mistaken for a figurine. The material comes from the metal poured into it.
    /// </summary>
    public const string MoldKey = "figurinemold";

    public static FigurineData Of(ItemStack stack, string key = Key) => FromTree(stack?.Attributes?.GetTreeAttribute(key));

    public static FigurineData FromTree(ITreeAttribute tree)
    {
        var creature = tree?.GetString("creature");
        if (string.IsNullOrEmpty(creature)) return null;
        return new FigurineData
        {
            Creature = creature,
            Pose = tree.GetString("pose", "normal"),
            Material = tree.GetString("material", "wood")
        };
    }

    public void WriteTo(ItemStack stack, string key = Key)
    {
        WriteTo(stack.Attributes.GetOrAddTreeAttribute(key));
        if (key == Key) stack.Attributes.SetString(TypeAttribute, DisplayType);
    }

    /// <summary>
    /// The stack's "type": what Universal Display keys its measured size on, beside the item code
    /// (vssurvivalmod IDisplayableProps - clutter's way). Every figurine is ONE item, so without it
    /// the first figurine measured gave its footprint to all of them. Material is left out - it
    /// does not change the shape.
    /// </summary>
    public const string TypeAttribute = "type";

    public string DisplayType => Creature + "-" + Pose;

    public void WriteTo(ITreeAttribute tree)
    {
        tree.SetString("creature", Creature);
        tree.SetString("pose", Pose);
        tree.SetString("material", Material);
    }

    /// <summary>The same design in another material.</summary>
    public FigurineData In(string material) => new() { Creature = Creature, Pose = Pose, Material = material };
}

/// <summary>
/// config/figurines/sizes.json - how big the figurines are. TRUE TO SCALE, compressed the way
/// real figurine ranges are (Calm, 2026-10-08): every creature is its in-game size times
/// <see cref="Scale"/>, but none smaller than <see cref="Smallest"/> (a hen would be a speck) or
/// bigger than <see cref="Largest"/>. Sizes are the longest side, length or height, in blocks.
/// </summary>
public sealed class SizeSettings
{
    public float Scale { get; set; } = 0.12f;
    public float Smallest { get; set; } = 0.12f;
    public float Largest { get; set; } = 0.4f;
}
