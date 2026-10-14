using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace DeskPictureFrame
{
    /// <summary>
    /// What one frame shows: its metal, and the picture id in each slot it has a picture for.
    ///
    /// A slot with no entry falls through to the blocktype's own texture, the shipped placeholder.
    /// A picture this client does not have yet is SHOWN as the placeholder (<see cref="Shown"/>),
    /// so the key changes - and the frame is drawn again - the moment it arrives.
    /// </summary>
    public sealed class FrameLook
    {
        /// <summary>Null = no metal recorded: the shape's own texture (brass).</summary>
        public readonly string Metal;

        /// <summary>Texture code ("image1", "image") -> picture id.</summary>
        public readonly IReadOnlyDictionary<string, string> Wanted;

        /// <summary>Set once every wanted picture is here - it stands until a picture is taken away (see readyGeneration).</summary>
        private string readyKey;

        /// <summary>FrameMesher.PictureGeneration when readyKey was set: a picture taken off the server since makes it stale.</summary>
        private int readyGeneration;

        public FrameLook(string metal, IReadOnlyDictionary<string, string> wanted)
        {
            Metal = string.IsNullOrEmpty(metal) ? null : metal;
            Wanted = wanted ?? new Dictionary<string, string>();
        }

        /// <summary>The look a stack carries: metal and picture ids, both in its <c>types</c> tree.</summary>
        public static FrameLook OfStack(ItemStack stack)
        {
            var attributes = stack?.Attributes;
            if (attributes == null) return new FrameLook(null, null);
            if (attributes[FrameMesher.TypesKey] is ITreeAttribute tree && Looks.TryGetValue(tree, out var cached)) return cached;
            var look = new FrameLook(FrameMesher.MetalOf(attributes), BlockDeskPictureFrame.FrameImageIdsOf(stack));
            if (attributes[FrameMesher.TypesKey] is ITreeAttribute types) Looks.AddOrUpdate(types, look);
            return look;
        }

        /// <summary>
        /// One look per <c>types</c> tree (a display or a hand asks every frame - reading the tree each
        /// time made garbage every frame; Calm's performance rule, 2026-10-09). A changed stack gets a
        /// new tree, so it is read again.
        /// </summary>
        private static readonly ConditionalWeakTable<ITreeAttribute, FrameLook> Looks = new();

        /// <summary>The picture id actually drawn in a slot: the wanted one once it is here, else the placeholder.</summary>
        public string Shown(ICoreClientAPI capi, string textureCode)
        {
            if (!Wanted.TryGetValue(textureCode, out string id)) return null;
            return FrameMesher.PictureReady(capi, id) ? id : BEDeskPictureFrame.PlaceholderImageId;
        }

        /// <summary>The texture a shape's texture code gets, or null for the block's / shape's own.</summary>
        public string TextureFor(ICoreClientAPI capi, string textureCode)
        {
            if (textureCode == "material") return FrameMaterials.TextureOf(Metal);
            string shown = Shown(capi, textureCode);
            return shown == null ? null : FrameMesher.PictureTexture(shown);
        }

        /// <summary>Metal plus what each slot shows - two frames with the same key draw the same.</summary>
        public string Key(ICoreClientAPI capi)
        {
            // A REMOVED PICTURE MAKES IT STALE (2026-10-10): "every picture is here" was remembered for good, so a frame in a
            // hand, on a shelf or on a window kept showing a picture taken off the server until a restart.
            int generation = FrameMesher.PictureGeneration;
            if (readyKey != null && readyGeneration == generation) return readyKey;
            readyKey = null;
            var sb = new StringBuilder(Metal ?? "-");
            bool allHere = true;
            foreach (var code in Wanted.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                string shown = Shown(capi, code);
                if (shown != Wanted[code]) allHere = false;
                sb.Append('|').Append(code).Append('=').Append(shown);
            }
            string key = sb.ToString();
            if (allHere) { readyKey = key; readyGeneration = generation; }
            return key;
        }

        /// <summary>Every texture this look draws with, for the "is it all in the atlas" test.</summary>
        public IEnumerable<string> Textures(ICoreClientAPI capi)
        {
            if (FrameMaterials.TextureOf(Metal) is { } material) yield return material;
            foreach (var code in Wanted.Keys) yield return FrameMesher.PictureTexture(Shown(capi, code));
        }
    }

    /// <summary>
    /// Draws a frame in its metal and its pictures: placed, in a hand or slot, and on a shelf, in a
    /// display case, a ground pile or on a window. Attribute Rendering Library did this until 1.2.0;
    /// the mod now depends on nothing (Calm: "rely on my own mods").
    ///
    /// The same three models ARL used (public ARL source, BlockBehaviorShapeTexturesFromAttributes):
    /// placed = the block's shape with its rotation zeroed, turned by the frame's own angle afterwards;
    /// hand / slot = the inventory shape, else the block's; displays = the block's shape as it is.
    ///
    /// Textures go in the block atlas, which is GPU work and main thread only. Placed frames and
    /// displays are built on the tesselation thread, so a texture not in yet is queued for the main
    /// thread and those blocks are drawn again once it is in (the lib's MaterialTextures way). A mesh
    /// built while something was waiting is never kept.
    /// </summary>
    public static class FrameMesher
    {
        /// <summary>
        /// The tree a stack keeps its metal and picture ids in, and a placed frame its metal.
        /// Named <c>types</c> because ARL saved them there - frames from before 1.2.0 read unchanged,
        /// and 1.1.x can still read what this version saves.
        /// </summary>
        public const string TypesKey = "types";
        public const string MetalKey = "metal";

        private const string PlacedMeshesKey = "deskpictureframe-placedmeshes";
        private const string ContainedMeshesKey = "deskpictureframe-containedmeshes";
        private const string ItemRefsKey = "deskpictureframe-itemrefs";

        private const string PicturePrefix = "deskpictureframe:img/";
        public static string PictureTexture(string id) => PicturePrefix + id;

        public static string MetalOf(ITreeAttribute tree) =>
            (tree?[TypesKey] as ITreeAttribute)?.GetString(MetalKey);

        public static void WriteMetal(ITreeAttribute tree, string metal)
        {
            if (tree != null && !string.IsNullOrEmpty(metal)) tree.GetOrAddTreeAttribute(TypesKey).SetString(MetalKey, metal);
        }

        // ---------- textures ----------

        private static readonly ConcurrentDictionary<string, TextureAtlasPosition> Positions = new();
        /// <summary>Metal / shape textures whose file does not exist - drawn with the shape's own. Never a picture: those arrive later.</summary>
        private static readonly ConcurrentDictionary<string, bool> Missing = new();
        private static readonly ConcurrentDictionary<string, bool> Queued = new();
        private static readonly ConcurrentDictionary<string, ConcurrentBag<BlockPos>> RedrawWhenIn = new();
        private static readonly ConcurrentDictionary<string, bool> PicturesHere = new();

        /// <summary>Per world: the atlas is rebuilt, so every position from the last world is wrong.</summary>
        public static void Clear(ICoreClientAPI capi)
        {
            Positions.Clear();
            Missing.Clear();
            Queued.Clear();
            RedrawWhenIn.Clear();
            PicturesHere.Clear();
            OwnTried.Clear();
            DrawnAt.Clear();
            clientApi = capi;
            if (capi == null) return;
            var refs = ObjectCacheUtil.TryGet<Dictionary<string, MultiTextureMeshRef>>(capi, ItemRefsKey);
            if (refs != null)
            {
                foreach (var r in refs.Values) r?.Dispose();
                refs.Clear();
            }
            ObjectCacheUtil.TryGet<ConcurrentDictionary<string, MeshData>>(capi, PlacedMeshesKey)?.Clear();
            ObjectCacheUtil.TryGet<ConcurrentDictionary<string, MeshData>>(capi, ContainedMeshesKey)?.Clear();
        }

        /// <summary>
        /// True when this client can draw the picture: its bytes are in the picture cache. On the main
        /// thread one of the player's OWN pictures is copied into the cache first (the cache is filled on
        /// demand) - so a frame in a hand or slot shows it without a placed frame asking first.
        /// Remembered once true - a picture's bytes never change, its id is their hash.
        /// </summary>
        public static bool PictureReady(ICoreClientAPI capi, string id)
        {
            if (id == null) return false;
            if (id == BEDeskPictureFrame.PlaceholderImageId || PicturesHere.ContainsKey(id)) return true;
            bool here = DeskPictureFrameImageStore.ClientHas(id);
            if (!here && capi != null && Environment.CurrentManagedThreadId == RuntimeEnv.MainThreadId
                && OwnTried.TryAdd(id, true))
                here = DeskPictureFrameImageStore.EnsureCached(capi, id);
            if (here) PicturesHere[id] = true;
            return here;
        }

        /// <summary>
        /// A picture deleted from the cache (taken off the server): every frame showing it goes to the placeholder - in a hand
        /// or slot at once (its look's key is stale, see PictureGeneration), and placed frames and the shelves, cases, piles and
        /// windows that drew it are redrawn (2026-10-10; before, they kept it until a restart or the chunk redrew).
        /// </summary>
        public static void ForgetPicture(string id)
        {
            if (id == null) return;
            PicturesHere.TryRemove(id, out _);
            OwnTried.TryRemove(id, out _);
            System.Threading.Interlocked.Increment(ref pictureGeneration);
            var capi = clientApi;
            if (capi != null && DrawnAt.TryRemove(id, out var places))
                capi.Event.EnqueueMainThreadTask(() =>
                {
                    foreach (var pos in places.Keys) Redraw(capi, pos);
                }, "deskpictureframe-forgetpicture");
        }

        private static int pictureGeneration;

        /// <summary>Goes up whenever a picture is taken away - a look's remembered key from before it is stale.</summary>
        public static int PictureGeneration => System.Threading.Volatile.Read(ref pictureGeneration);

        /// <summary>Where each picture has been drawn (placed frames and the displays holding frames) - redrawn when it is taken away.</summary>
        private static readonly ConcurrentDictionary<string, ConcurrentDictionary<BlockPos, byte>> DrawnAt = new();

        private static ICoreClientAPI clientApi;

        private static void NoteDrawn(ICoreClientAPI capi, FrameLook look, BlockPos pos)
        {
            if (pos == null) return;
            foreach (var code in look.Wanted.Keys)
            {
                string shown = look.Shown(capi, code);
                if (shown == null || shown == BEDeskPictureFrame.PlaceholderImageId) continue;
                var places = DrawnAt.GetOrAdd(shown, _ => new ConcurrentDictionary<BlockPos, byte>());
                if (!places.ContainsKey(pos)) places[pos.Copy()] = 0;
            }
        }

        /// <summary>Own-picture copies tried this world - one try each, not one per frame drawn.</summary>
        private static readonly ConcurrentDictionary<string, bool> OwnTried = new();

        /// <summary>
        /// Main thread, at load: the texture of every listed material and of every game metal plate (what
        /// the recipes take), so frames never wait for one.
        /// </summary>
        public static void PrepareMaterials(ICoreClientAPI capi)
        {
            var codes = new HashSet<string>(FrameMaterials.All.Keys.Where(c => c != "*"));
            foreach (var item in capi.World.Items)
                if (item?.Code != null && item.Code.Domain == "game" && item.Code.Path.StartsWith("metalplate-"))
                    codes.Add(item.Code.Path.Substring("metalplate-".Length));
            int count = codes.Count(code => TryTexture(capi, FrameMaterials.TextureOf(code), null, out _));
            TryTexture(capi, PictureTexture(BEDeskPictureFrame.PlaceholderImageId), null, out _);
            capi.Logger.Notification("[DeskPictureFrame] {0} material texture(s) ready for frames", count);
        }

        /// <summary>True when a texture is in the block atlas or known not to exist - nothing to wait for. Any thread.</summary>
        private static bool Settled(ICoreClientAPI capi, string texture)
        {
            if (Positions.ContainsKey(texture) || Missing.ContainsKey(texture)) return true;
            return FromAtlas(capi, texture) != null;
        }

        private static TextureAtlasPosition FromAtlas(ICoreClientAPI capi, string texture)
        {
            var location = new AssetLocation(texture);
            var position = capi.BlockTextureAtlas[location]
                ?? capi.BlockTextureAtlas[location.Clone().WithPathPrefixOnce("textures/").WithPathAppendixOnce(".png")];
            if (position != null) Positions[texture] = position;
            return position;
        }

        /// <summary>
        /// The atlas position of a texture. False when it does not exist, or - off the main thread -
        /// when it is not in the atlas yet: then it is queued, and <paramref name="redrawAt"/> is drawn
        /// again once it is in.
        /// </summary>
        internal static bool TryTexture(ICoreClientAPI capi, string texture, BlockPos redrawAt, out TextureAtlasPosition position)
        {
            position = null;
            if (string.IsNullOrEmpty(texture)) return false;
            if (Positions.TryGetValue(texture, out position)) return true;
            if (Missing.ContainsKey(texture)) return false;
            position = FromAtlas(capi, texture);
            if (position != null) return true;

            if (Environment.CurrentManagedThreadId == RuntimeEnv.MainThreadId)
            {
                Insert(capi, texture);
                return Positions.TryGetValue(texture, out position);
            }

            if (redrawAt != null) RedrawWhenIn.GetOrAdd(texture, _ => new ConcurrentBag<BlockPos>()).Add(redrawAt.Copy());
            if (Queued.TryAdd(texture, true))
            {
                capi.Event.EnqueueMainThreadTask(() =>
                {
                    Insert(capi, texture);
                    Queued.TryRemove(texture, out _);
                    if (RedrawWhenIn.TryRemove(texture, out var redraw))
                        foreach (var pos in redraw.Distinct()) Redraw(capi, pos);
                }, "deskpictureframe-texture");
            }
            return false;
        }

        private static void Insert(ICoreClientAPI capi, string texture)
        {
            if (Positions.ContainsKey(texture) || Missing.ContainsKey(texture)) return;
            var location = new AssetLocation(texture);
            TextureAtlasPosition position;

            string id = texture.StartsWith(PicturePrefix) ? texture.Substring(PicturePrefix.Length) : null;
            if (id != null && id != BEDeskPictureFrame.PlaceholderImageId)
            {
                // A PICTURE GOES STRAIGHT FROM ITS BYTES INTO THE ATLAS - no asset, no asset origin.
                // Until 1.2.0 every picture had to be a game asset (ARL tested Assets.Exists), which is
                // why the cache was an asset origin registered in StartPre and a picture arriving
                // mid-session needed a runtime asset. Neither is needed now. Never marked missing: a
                // picture not here yet arrives later.
                byte[] data;
                try { data = File.ReadAllBytes(DeskPictureFrameImageStore.ClientImagePath(id)); }
                catch (Exception)
                {
                    // Gone from the cache before it reached the atlas (trimmed, or taken off the
                    // server): drawn as the placeholder - its key changes, so nothing waits on it.
                    PicturesHere.TryRemove(id, out _);
                    return;
                }
                try { capi.BlockTextureAtlas.InsertTextureCached(location, data, out _, out position); }
                catch (Exception) { position = null; }
                if (position != null) Positions[texture] = position;
                else
                {
                    // Bad bytes or no room: the blocktype's placeholder for the rest of this world,
                    // never asked again (a frame would otherwise redraw itself forever).
                    Missing[texture] = true;
                    capi.Logger.Warning("[DeskPictureFrame] Picture {0} could not be put in the block atlas", id);
                }
                return;
            }

            if (!capi.Assets.Exists(location.Clone().WithPathPrefixOnce("textures/").WithPathAppendixOnce(".png")))
            {
                Missing[texture] = true;
                return;
            }
            capi.BlockTextureAtlas.GetOrInsertTexture(new CompositeTexture(location), out _, out position);
            if (position != null) Positions[texture] = position;
            else
            {
                Missing[texture] = true;
                capi.Logger.Warning("[DeskPictureFrame] Texture {0} could not be put in the block atlas", texture);
            }
        }

        /// <summary>A placed frame is drawn again; a shelf, case or ground pile rebuilds its item meshes first.</summary>
        private static void Redraw(ICoreClientAPI capi, BlockPos pos)
        {
            var be = capi.World.BlockAccessor.GetBlockEntity(pos);
            if (be is BEDeskPictureFrame frame) frame.blockMesh = null;
            if (be is BlockEntityDisplay display) display.updateMeshes();
            capi.World.BlockAccessor.MarkBlockDirty(pos);
        }

        // ---------- meshes ----------

        /// <summary>
        /// A placed frame, unturned (the block entity turns it by its own angle). Cached and shared by
        /// every frame with the same look; <paramref name="waiting"/> = a texture was not in the atlas,
        /// the mesh is not kept and the frame is drawn again when it is.
        /// </summary>
        public static MeshData Placed(ICoreClientAPI capi, Block block, FrameLook look, BlockPos pos, out bool waiting)
        {
            var cache = ObjectCacheUtil.GetOrCreate(capi, PlacedMeshesKey, () => new ConcurrentDictionary<string, MeshData>());
            string key = block.Id + "|" + look.Key(capi);
            NoteDrawn(capi, look, pos);
            waiting = false;
            if (cache.TryGetValue(key, out var cached)) return cached;
            var mesh = Build(capi, block, block.Shape, look, pos, true, out waiting);
            if (mesh != null && !waiting) cache[key] = mesh;
            return mesh;
        }

        /// <summary>
        /// A frame on a shelf, in a display case, a ground pile or on a window: the block's shape as it
        /// is. The caller clones it. Off the main thread a missing texture is queued and the display at
        /// <paramref name="atBlockPos"/> redrawn - its cache key says "|wait" until then.
        /// </summary>
        public static MeshData Contained(ICoreClientAPI capi, Block block, FrameLook look, BlockPos atBlockPos)
        {
            var cache = ObjectCacheUtil.GetOrCreate(capi, ContainedMeshesKey, () => new ConcurrentDictionary<string, MeshData>());
            string key = block.Id + "|" + look.Key(capi);
            NoteDrawn(capi, look, atBlockPos);
            if (cache.TryGetValue(key, out var cached)) return cached;
            var mesh = Build(capi, block, block.Shape, look, atBlockPos, false, out bool waiting);
            if (mesh != null && !waiting) cache[key] = mesh;
            return mesh;
        }

        /// <summary>True when every texture this look needs is in the atlas (or settled as missing).</summary>
        public static bool TexturesReady(ICoreClientAPI capi, FrameLook look)
        {
            foreach (var texture in look.Textures(capi))
                if (!Settled(capi, texture)) return false;
            return true;
        }

        /// <summary>In a hand, a slot, on the ground as a dropped item, in the handbook. Main thread.</summary>
        public static MultiTextureMeshRef ItemRef(ICoreClientAPI capi, Block block, FrameLook look)
        {
            var refs = ObjectCacheUtil.GetOrCreate(capi, ItemRefsKey, () => new Dictionary<string, MultiTextureMeshRef>());
            string key = block.Id + "|" + look.Key(capi);
            if (refs.TryGetValue(key, out var meshRef)) return meshRef;
            var mesh = Build(capi, block, block.ShapeInventory ?? block.Shape, look, null, false, out _);
            meshRef = mesh == null ? null : capi.Render.UploadMultiTextureMesh(mesh);
            refs[key] = meshRef;
            return meshRef;
        }

        private static MeshData Build(ICoreClientAPI capi, Block block, CompositeShape cshape, FrameLook look, BlockPos redrawAt, bool unturned, out bool waiting)
        {
            waiting = false;
            if (cshape?.Base == null) return null;
            var shape = Shape.TryGet(capi, cshape.Base.Clone().WithPathPrefixOnce("shapes/").WithPathAppendixOnce(".json"));
            if (shape == null) return null;

            var source = new FrameTextureSource(capi, block, look, shape, redrawAt);
            capi.Tesselator.TesselateShape(new TesselationMetaData
            {
                TexSource = source,
                TypeForLogging = "deskpictureframe " + block.Code,
                Rotation = unturned ? new Vec3f() : new Vec3f(cshape.rotateX, cshape.rotateY, cshape.rotateZ),
                IgnoreElements = cshape.IgnoreElements,
                SelectiveElements = cshape.SelectiveElements,
                QuantityElements = cshape.QuantityElements
            }, shape, out var mesh);
            waiting = source.Waiting;
            return mesh;
        }

        /// <summary>
        /// Each texture code the shape asks for: <c>material</c> -> the metal plate, <c>imageN</c> -> the
        /// picture (or the placeholder), anything else -> the block's own, then the shape's own.
        /// </summary>
        private sealed class FrameTextureSource : ITexPositionSource
        {
            private readonly ICoreClientAPI capi;
            private readonly FrameLook look;
            private readonly Shape shape;
            private readonly BlockPos redrawAt;
            private readonly ITexPositionSource fallback;

            public bool Waiting { get; private set; }

            public Size2i AtlasSize => capi.BlockTextureAtlas.Size;

            public FrameTextureSource(ICoreClientAPI capi, Block block, FrameLook look, Shape shape, BlockPos redrawAt)
            {
                this.capi = capi;
                this.look = look;
                this.shape = shape;
                this.redrawAt = redrawAt;
                fallback = capi.Tesselator.GetTextureSource(block);
            }

            public TextureAtlasPosition this[string textureCode]
            {
                get
                {
                    string texture = look.TextureFor(capi, textureCode);
                    if (texture != null)
                    {
                        if (TryTexture(capi, texture, redrawAt, out var position)) return position;
                        if (!Missing.ContainsKey(texture)) Waiting = true;
                    }
                    return Fallback(textureCode);
                }
            }

            private TextureAtlasPosition Fallback(string textureCode)
            {
                var position = fallback?[textureCode];
                if (position != null && position != capi.BlockTextureAtlas.UnknownTexturePosition) return position;
                if (shape?.Textures != null && shape.Textures.TryGetValue(textureCode, out var location) && location != null)
                {
                    if (TryTexture(capi, location.ToString(), redrawAt, out var own)) return own;
                    if (!Missing.ContainsKey(location.ToString())) Waiting = true;
                }
                return position ?? capi.BlockTextureAtlas.UnknownTexturePosition;
            }
        }
    }
}
