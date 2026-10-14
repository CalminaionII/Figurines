using System.Collections.Concurrent;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace Figurines;

/// <summary>
/// Turns a figurine into a mesh: the game's own creature model, frozen at one frame of one of its
/// own animations, every face painted with the material's texture, shrunk to figurine size.
///
/// The posing is Taxidermy's MountMesher (Calm's own mod, seen working on every vanilla animal):
/// resolve joints, run a ClientAnimator to the wanted frame, multiply every vertex through its
/// joint's matrix - what entityanimated.vsh does on the GPU, done once on the CPU.
///
/// The material: every texture code the model uses points at ONE atlas position, so the creature's
/// own UV layout is laid over the wood or the metal plate. Nothing to model or texture per animal.
///
/// Built on the main thread only (atlas inserts are GPU work). Displays ask from the tesselation
/// thread, which is why the caches are concurrent and ItemFigurine uses a wait key.
/// </summary>
public static class FigurineMesher
{
    private const string MeshCacheKey = "figurines-meshes";
    private const string RefCacheKey = "figurines-item-refs";
    private const string ScaleCacheKey = "figurines-creature-scales";

    /// <summary>Main-thread result of a build: the mesh, or null if it could not be built (still counts as done).</summary>
    private static ConcurrentDictionary<string, MeshData> Meshes(ICoreClientAPI capi) =>
        ObjectCacheUtil.GetOrCreate(capi, MeshCacheKey, () => new ConcurrentDictionary<string, MeshData>());

    /// <summary>True once this figurine has been built, or has failed to build - either way, nothing left to wait for.</summary>
    public static bool IsDone(ICoreClientAPI capi, FigurineData data) => Meshes(capi).ContainsKey(data.MeshKey);

    /// <summary>The figurine at its own size, feet on the floor, centred on the block, facing +Z. Null if not built yet or unbuildable.</summary>
    public static MeshData TryGet(ICoreClientAPI capi, FigurineData data) =>
        Meshes(capi).TryGetValue(data.MeshKey, out var m) ? m : null;

    /// <summary>
    /// Any thread. Off the main thread it can only use textures already in the block atlas (the
    /// shipped materials are all vanilla block textures, so normally they are); if one is not, it
    /// returns null WITHOUT caching, and the caller queues a main-thread build.
    /// </summary>
    public static MeshData Get(ICoreClientAPI capi, FigurineData data)
    {
        var cache = Meshes(capi);
        if (cache.TryGetValue(data.MeshKey, out var cached)) return cached;
        MeshData mesh = null;
        try { mesh = Build(capi, data); }
        catch (NotReadyException) { return null; }
        catch (Exception e) { capi.Logger.Error("[Figurines] Cannot build the figurine {0}: {1}", data.MeshKey, e); }
        if (mesh is { VerticesCount: 0 }) mesh = null;
        cache[data.MeshKey] = mesh;
        return mesh;
    }

    /// <summary>The same figurine uploaded for inventory, hand and dropped item: fitted into one block, centred.</summary>
    public static MultiTextureMeshRef GetItemRef(ICoreClientAPI capi, FigurineData data)
    {
        var refs = ObjectCacheUtil.GetOrCreate(capi, RefCacheKey, () => new Dictionary<string, MultiTextureMeshRef>());
        if (refs.TryGetValue(data.MeshKey, out var cached)) return cached;
        var source = Get(capi, data);
        if (source == null) return null;
        var meshRef = capi.Render.UploadMultiTextureMesh(FitToBlock(source.Clone()));
        refs[data.MeshKey] = meshRef;
        return meshRef;
    }

    public static void Clear(ICoreClientAPI capi)
    {
        var refs = ObjectCacheUtil.TryGet<Dictionary<string, MultiTextureMeshRef>>(capi, RefCacheKey);
        if (refs != null)
        {
            foreach (var r in refs.Values) r?.Dispose();
            refs.Clear();
        }
        ObjectCacheUtil.TryGet<ConcurrentDictionary<string, MeshData>>(capi, MeshCacheKey)?.Clear();
        ObjectCacheUtil.TryGet<ConcurrentDictionary<string, float>>(capi, ScaleCacheKey)?.Clear();
        Warned.Clear();
        ModelWaitSince.Clear();
    }

    /// <summary>When each entity's model was first found missing (see Posed) - per session.</summary>
    private static readonly ConcurrentDictionary<string, long> ModelWaitSince = new();

    /// <summary>Things already reported this session - a display asks again every frame.</summary>
    private static readonly ConcurrentDictionary<string, bool> Warned = new();

    private static void WarnOnce(ICoreClientAPI capi, string key, string format, params object[] args)
    {
        if (Warned.TryAdd(key, true)) capi.Logger.Warning("[Figurines] " + format, args);
    }

    private static MeshData Build(ICoreClientAPI capi, FigurineData data)
    {
        var system = capi.ModLoader.GetModSystem<FigurinesModSystem>();
        var def = system.Creature(data.Creature);
        if (def == null) { WarnOnce(capi, "creature|" + data.Creature, "No creature '{0}' - is the mod that adds it installed?", data.Creature); return null; }
        var pose = def.PoseByCode(data.Pose) ?? def.Poses[0];
        var material = system.Material(data.Material) ?? system.Material("wood");
        if (material == null) { WarnOnce(capi, "material|" + data.Material, "No material '{0}' and no wood to fall back on", data.Material); return null; }

        var mesh = Posed(capi, def, pose, material.Texture, true);
        if (mesh == null) return null;
        Place(mesh, CreatureScale(capi, def));
        return mesh;
    }

    /// <summary>
    /// The creature in a pose, painted with one texture, facing +Z - at the model's own size. Null if
    /// its entity is not loaded (the mod that adds it is missing).
    /// </summary>
    private static MeshData Posed(ICoreClientAPI capi, CreatureDefinition def, FigurinePose pose, string texture, bool withAttachments)
    {
        var entityCode = new AssetLocation(def.Entity);
        var props = capi.World.GetEntityType(entityCode);
        if (props == null) { WarnOnce(capi, "entity|" + entityCode, "No entity {0} (creature {1}) - is the mod that adds it installed?", entityCode, def.Code); return null; }
        var loaded = props.Client?.LoadedShape;
        if (loaded == null)
        {
            // NOT READY, NOT FAILED (2026-10-10): the entity exists but its model is not loaded yet - asked again later
            // rather than cached as a blank for the session. After 30 s it counts as missing, so it cannot redraw forever.
            long first = ModelWaitSince.GetOrAdd(entityCode.ToString(), _ => Environment.TickCount64);
            if (Environment.TickCount64 - first < 30000) throw new NotReadyException();
            WarnOnce(capi, "entity|" + entityCode, "No loaded model for {0} (creature {1})", entityCode, def.Code);
            return null;
        }
        string logName = "figurines:" + def.Code;

        var shape = loaded.Clone();

        // Antlers, horns, tusks: step-parented onto the model the way the living animal wears them
        // (Taxidermy's MountMesher, from vsessentialsmod EntityBehaviorContainer.addGearToShape).
        // That inserts the attachment's own textures into the atlas - GPU work - so off the main
        // thread an animal with attachments waits for the main-thread build.
        bool attached = false;
        if (withAttachments && def.Attachments is { Length: > 0 })
        {
            if (Environment.CurrentManagedThreadId != RuntimeEnv.MainThreadId) throw new NotReadyException();
            var textures = new Dictionary<string, CompositeTexture>();
            foreach (var kv in props.Client.Textures) textures[kv.Key] = kv.Value.Clone();
            foreach (var code in def.Attachments)
            {
                var item = capi.World.GetItem(new AssetLocation(code));
                var iatta = item == null ? null : IAttachableToEntity.FromCollectible(item);
                if (iatta == null) { WarnOnce(capi, "attach|" + code, "Attachment {0} on {1} is not an attachable item - left off", code, def.Code); continue; }
                string[] deleted = [];
                shape = EntityBehaviorContainer.addGearToShape(capi, null, capi.BlockTextureAtlas, shape, new ItemStack(item), iatta, "default", logName, ref deleted, textures);
                attached = true;
            }
        }

        float[] matrices = null;
        bool jointed = false;
        if (!string.IsNullOrEmpty(pose.Animation))
        {
            try { shape.InitForAnimations(capi.Logger, logName); jointed = true; }
            catch (Exception e) { WarnOnce(capi, "joints|" + def.Code, "No joints for {0}: {1} - using the rest pose", entityCode, e.Message); }
            if (jointed) matrices = Evaluate(capi, shape, pose, entityCode, attached);
        }
        // After the animator has resolved its elements, so removing one never upsets an animation.
        if (def.RemoveElements is { Length: > 0 }) shape.Elements = Strip(shape.Elements, def.RemoveElements);

        capi.Tesselator.TesselateShape(new TesselationMetaData
        {
            TypeForLogging = logName,
            TexSource = new MaterialSource(capi, texture),
            WithJointIds = jointed && matrices != null,
        }, shape, out var mesh);
        if (matrices != null) ApplyJoints(mesh, matrices);
        mesh.CustomInts = null;

        // The game's animals face -X at rest; a quarter turn makes them face +Z, which is what
        // ground storage turns towards the player (as Taxidermy's heads on the ground).
        mesh.Rotate(new Vec3f(0.5f, 0, 0.5f), 0, GameMath.PIHALF, 0);
        return mesh;
    }

    /// <summary>
    /// ONE scale per creature, used for every pose (Calm, 2026-10-08: each pose came out a different
    /// size when each was fitted to itself), so a rearing bear is taller and a sleeping one lower, as
    /// the animal really is. Measured on the NORMAL pose without attachments: its longest side -
    /// length or height, whichever is bigger - at the creature's in-game size (the entity's client
    /// <c>size</c>) is scaled TRUE TO SCALE and clamped (<see cref="SizeSettings"/>), or set to the
    /// definition's own <c>size</c> when it gives one.
    /// </summary>
    private static float CreatureScale(ICoreClientAPI capi, CreatureDefinition def)
    {
        var cache = ObjectCacheUtil.GetOrCreate(capi, ScaleCacheKey, () => new ConcurrentDictionary<string, float>());
        if (cache.TryGetValue(def.Code, out var cached)) return cached;

        var normal = def.PoseByCode("normal") ?? def.Poses[0];
        var rest = Posed(capi, def, normal, "game:block/wood/debarked/oak", false);
        float scale = 1;
        if (rest is { VerticesCount: > 0 })
        {
            Bounds(rest, out float minX, out float minY, out float minZ, out float maxX, out float maxY, out float maxZ);
            float longest = Math.Max(Math.Max(maxX - minX, maxZ - minZ), maxY - minY);
            if (longest > 0)
            {
                var sizes = FigurinesModSystem.Instance?.Sizes ?? new SizeSettings();
                float real = longest * (capi.World.GetEntityType(new AssetLocation(def.Entity))?.Client?.Size ?? 1);
                float target = def.Size ?? Math.Clamp(real * sizes.Scale, sizes.Smallest, sizes.Largest);
                scale = target / longest;
            }
        }
        cache[def.Code] = scale;
        return scale;
    }

    private static ShapeElement[] Strip(ShapeElement[] elements, string[] words)
    {
        if (elements == null) return null;
        var kept = elements.Where(e => e.Name == null || !words.Any(w => e.Name.Contains(w, StringComparison.OrdinalIgnoreCase))).ToArray();
        foreach (var e in kept) e.Children = Strip(e.Children, words);
        return kept;
    }

    /// <summary>Shrink by the creature's scale, feet at y 0, centred on the block.</summary>
    private static void Place(MeshData mesh, float scale)
    {
        if (mesh.VerticesCount == 0) return;
        Bounds(mesh, out float minX, out float minY, out float minZ, out float maxX, out _, out float maxZ);
        mesh.Translate(-(minX + maxX) / 2, -minY, -(minZ + maxZ) / 2);
        mesh.Scale(new Vec3f(0, 0, 0), scale, scale, scale);
        mesh.Translate(0.5f, 0, 0.5f);
    }

    /// <summary>Centre on the block and scale so the longest side is one block: the item-slot version.</summary>
    internal static MeshData FitToBlock(MeshData mesh)
    {
        Bounds(mesh, out float minX, out float minY, out float minZ, out float maxX, out float maxY, out float maxZ);
        float extent = Math.Max(maxX - minX, Math.Max(maxY - minY, maxZ - minZ));
        mesh.Translate(0.5f - (minX + maxX) / 2, 0.5f - (minY + maxY) / 2, 0.5f - (minZ + maxZ) / 2);
        if (extent > 0) mesh.Scale(new Vec3f(0.5f, 0.5f, 0.5f), 1 / extent, 1 / extent, 1 / extent);
        return mesh;
    }

    internal static void Bounds(MeshData mesh, out float minX, out float minY, out float minZ, out float maxX, out float maxY, out float maxZ)
    {
        minX = minY = minZ = float.MaxValue;
        maxX = maxY = maxZ = float.MinValue;
        for (int i = 0; i < mesh.VerticesCount; i++)
        {
            float x = mesh.xyz[i * 3], y = mesh.xyz[i * 3 + 1], z = mesh.xyz[i * 3 + 2];
            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            minZ = Math.Min(minZ, z); maxZ = Math.Max(maxZ, z);
        }
    }

    /// <summary>
    /// The joint matrices for one frame (Taxidermy's MountMesher.Evaluate). Activate the animation
    /// at the wanted frame, force the easing to 1, evaluate with dt = 0 - so the frame neither moves
    /// nor blends.
    /// </summary>
    private static float[] Evaluate(ICoreClientAPI capi, Shape shape, FigurinePose pose, AssetLocation entityCode, bool hasAttachments)
    {
        var anims = shape.Animations ?? [];
        Animation Find(string code) => anims.FirstOrDefault(a => string.Equals(a.Code, code, StringComparison.OrdinalIgnoreCase) && a.KeyFrames?.Length > 0 && a.QuantityFrames > 0);
        // Deer and the like carry "-antlers" versions of their animations, played when the antlers are on.
        var anim = (hasAttachments ? Find(pose.Animation + "-antlers") : null) ?? Find(pose.Animation);
        if (anim == null)
        {
            WarnOnce(capi, "anim|" + entityCode + "|" + pose.Animation, "{0} has no animation '{1}' for pose '{2}' - using the rest pose", entityCode, pose.Animation, pose.Code);
            return null;
        }

        var animator = new ClientAnimator(() => 1.0, anims, shape.Elements, shape.JointsById);
        float frame = GameMath.Clamp(pose.Frame, 0, anim.QuantityFrames - 1);
        var meta = new AnimationMetaData
        {
            Code = anim.Code,
            Animation = anim.Code,
            Weight = 1,
            AnimationSpeed = 1,
            EaseInSpeed = 1,
            EaseOutSpeed = 1,
            BlendMode = EnumAnimationBlendMode.Average,
            StartFrameOnce = frame,
        }.Init();
        var active = new Dictionary<string, AnimationMetaData> { [anim.Code] = meta };

        animator.OnFrame(active, 0);
        var state = animator.GetAnimationState(anim.Code);
        state.CurrentFrame = frame;
        state.EasingFactor = 1;
        animator.OnFrame(active, 0);
        return animator.Matrices;
    }

    /// <summary>Column-major 4x4 per joint, 16 floats each, as Mat4f lays them out.</summary>
    private static void ApplyJoints(MeshData mesh, float[] matrices)
    {
        var ids = mesh.CustomInts?.Values;
        if (ids == null) return;
        var n = new float[3];
        for (int i = 0; i < mesh.VerticesCount; i++)
        {
            int m = ids[i] * 16;
            if (m + 15 >= matrices.Length) continue;
            float x = mesh.xyz[i * 3], y = mesh.xyz[i * 3 + 1], z = mesh.xyz[i * 3 + 2];
            mesh.xyz[i * 3]     = matrices[m] * x + matrices[m + 4] * y + matrices[m + 8] * z + matrices[m + 12];
            mesh.xyz[i * 3 + 1] = matrices[m + 1] * x + matrices[m + 5] * y + matrices[m + 9] * z + matrices[m + 13];
            mesh.xyz[i * 3 + 2] = matrices[m + 2] * x + matrices[m + 6] * y + matrices[m + 10] * z + matrices[m + 14];

            VertexFlags.UnpackNormal(mesh.Flags[i], n);
            float nx = matrices[m] * n[0] + matrices[m + 4] * n[1] + matrices[m + 8] * n[2];
            float ny = matrices[m + 1] * n[0] + matrices[m + 5] * n[1] + matrices[m + 9] * n[2];
            float nz = matrices[m + 2] * n[0] + matrices[m + 6] * n[1] + matrices[m + 10] * n[2];
            float len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
            if (len > 0.0001f)
                mesh.Flags[i] = (mesh.Flags[i] & VertexFlags.ClearNormalBitMask) | VertexFlags.PackNormal(nx / len, ny / len, nz / len);
        }
    }

    /// <summary>Main thread: put these textures in the block atlas now, so later builds need no insert.</summary>
    public static void PrepareTextures(ICoreClientAPI capi, IEnumerable<string> textures)
    {
        foreach (var texture in textures.Distinct()) _ = new MaterialSource(capi, texture);
    }

    /// <summary>Thrown off the main thread when a texture is not in the atlas yet - build on the main thread instead.</summary>
    private sealed class NotReadyException : Exception { }

    /// <summary>Every texture code the model asks for gets the material's one texture.</summary>
    private sealed class MaterialSource : ITexPositionSource
    {
        private readonly ICoreClientAPI capi;
        private readonly TextureAtlasPosition position;

        public Size2i AtlasSize => capi.BlockTextureAtlas.Size;

        public MaterialSource(ICoreClientAPI capi, string texture)
        {
            this.capi = capi;
            var loc = new AssetLocation(texture);
            // Already in the atlas (vanilla block textures are): usable from any thread.
            position = capi.BlockTextureAtlas[loc]
                ?? capi.BlockTextureAtlas[loc.Clone().WithPathPrefixOnce("textures/").WithPathAppendixOnce(".png")];
            if (position != null) return;
            // Inserting is GPU work - main thread only.
            if (Environment.CurrentManagedThreadId != RuntimeEnv.MainThreadId) throw new NotReadyException();
            capi.BlockTextureAtlas.GetOrInsertTexture(new CompositeTexture(loc), out _, out position);
            if (position == null) WarnOnce(capi, "texture|" + texture, "Texture {0} could not be put in the block atlas", texture);
        }

        public TextureAtlasPosition this[string textureCode] => position ?? capi.BlockTextureAtlas.UnknownTexturePosition;
    }
}
