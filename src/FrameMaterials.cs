using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using DeskPictureFrame.Handbook;
using Vintagestory.API.Config;

namespace DeskPictureFrame
{
    /// <summary>One material a frame can be made of. From config/deskpictureframe/materials.json.</summary>
    public sealed class FrameMaterialDefinition
    {
        /// <summary>
        /// What a frame's stack carries in <c>types/metal</c> ("copper"). <c>*</c> answers every code not
        /// listed, with <c>{code}</c> in its texture filled in.
        /// </summary>
        public string Code { get; set; }

        /// <summary>The texture on the frame's <c>material</c> faces. Left out: the <c>*</c> entry's, with this code filled in.</summary>
        public string Texture { get; set; }

        /// <summary>
        /// The mod the metal comes from, when its plate is in the game's domain but the game does not have it (Pewter's
        /// game:metalplate-pewter). Left out = the game's. Decides the creative tab (one stack per mod) and the handbook's
        /// "Can be made with" line.
        /// </summary>
        public string Source { get; set; }

        /// <summary>
        /// true = on the handbook page but not in the creative tab - the game's other metal plates (Calm, 2026-10-16: "DPF to
        /// use any metal plates"), so the tab keeps its size. Crafting takes every plate either way.
        /// </summary>
        public bool HandbookOnly { get; set; }

        /// <summary>The mod id this material counts as: <see cref="Source"/>, else the game.</summary>
        public string SourceMod => string.IsNullOrEmpty(Source) ? "game" : Source;
    }

    /// <summary>
    /// The frame materials, as data - the same way Figurines, Bookends and Universal Display Lib do it
    /// (Calm, 2026-10-09: "do that so it all matches"). Every mod's
    /// <c>assets/&lt;domain&gt;/config/deskpictureframe/materials.json</c> is read, this mod's first; a later
    /// file changes an entry by repeating its code. A new material is one entry; a frame design from
    /// another mod can use any material that has one.
    ///
    /// LISTED materials (every entry but <c>*</c>) are the creative tab, in file order - whatever order
    /// the file has is the order it shows. <c>*</c> never shows; it covers every other code a recipe can
    /// make (any game metal plate).
    /// </summary>
    public static class FrameMaterials
    {
        private const string Path = "config/deskpictureframe/materials";

        private static Dictionary<string, FrameMaterialDefinition> byCode = new();
        private static List<string> listed = new();

        public static IReadOnlyDictionary<string, FrameMaterialDefinition> All => byCode;

        /// <summary>The materials shown in the creative tab, in file order.</summary>
        public static IReadOnlyList<string> Listed => listed;

        public static void Load(ICoreAPI api)
        {
            var found = new Dictionary<string, FrameMaterialDefinition>();
            var order = new List<string>();
            var files = api.Assets.GetMany(Path)
                .OrderBy(a => a.Location.Domain == "deskpictureframe" ? 0 : 1)
                .ThenBy(a => a.Location.ToString(), StringComparer.Ordinal);
            foreach (var asset in files)
            {
                try
                {
                    foreach (var m in asset.ToObject<FrameMaterialDefinition[]>() ?? [])
                    {
                        if (string.IsNullOrWhiteSpace(m.Code)) continue;
                        if (!found.ContainsKey(m.Code)) order.Add(m.Code);
                        found[m.Code] = m;
                    }
                }
                catch (Exception e) { api.Logger.Error("[DeskPictureFrame] Cannot read {0}: {1}", asset.Location, e.Message); }
            }
            byCode = found;
            listed = order.Where(c => c != "*").ToList();
            api.Logger.Notification("[DeskPictureFrame] {0} frame material(s) listed", listed.Count);
        }

        /// <summary>The texture for a material code - its own entry's, else <c>*</c> with the code filled in, else null (the shape's own).</summary>
        public static string TextureOf(string code)
        {
            if (string.IsNullOrEmpty(code)) return null;
            if (byCode.TryGetValue(code, out var own) && !string.IsNullOrWhiteSpace(own.Texture)) return own.Texture;
            return byCode.TryGetValue("*", out var any) && !string.IsNullOrWhiteSpace(any.Texture)
                ? any.Texture.Replace("{code}", code)
                : null;
        }

        /// <summary>
        /// The creative tab, built from the materials file - as Figurines, Bookends and Taxidermy build theirs.
        /// Every frame that has a creative list (the hidden old numbered ones have none) gets one stack per
        /// listed GAME material, in file order, then the first listed material of each other mod (Calm, 2026-10-15:
        /// vanilla plus one per mod - every plate still crafts, the tab just does not list them all), on the tabs its
        /// blocktype names. So a frame design or a material another mod adds shows up with nothing written per metal.
        /// Each such frame also gets ONE handbook page cycling its metals, and the "Can be made with" line.
        /// </summary>
        public static void FillCreativeTabs(ICoreAPI api)
        {
            var shown = listed.Where(c => byCode[c].SourceMod == "game" && !byCode[c].HandbookOnly)
                .Concat(listed.Where(c => byCode[c].SourceMod != "game").GroupBy(c => byCode[c].SourceMod).Select(g => g.First())).ToList();
            // every metal the recipes take - each game:metalplate-* item, so a mod's plate in the game's domain (Pewter's)
            // counts by itself - listed ones first, in file order
            var plates = api.World.Items.Where(i => i?.Code != null && i.Code.Domain == "game" && i.Code.Path.StartsWith("metalplate-"))
                .Select(i => i.Code.Path.Substring("metalplate-".Length)).ToList();
            var metals = listed.Where(plates.Contains).Concat(plates.Where(p => !listed.Contains(p)).OrderBy(p => p, StringComparer.Ordinal)).ToList();
            int frames = 0;
            foreach (var block in api.World.Blocks)
            {
                if (block is not BlockDeskPictureFrame || block.Code == null || block.CreativeInventoryStacks == null) continue;
                var lists = new List<CreativeTabAndStackList>();
                foreach (var list in block.CreativeInventoryStacks)
                {
                    if (list?.Tabs == null || list.Tabs.Length == 0) continue;
                    lists.Add(new CreativeTabAndStackList { Tabs = list.Tabs, Stacks = shown.Select(code => Stack(block, code)).ToArray() });
                }
                if (lists.Count == 0) continue;
                block.CreativeInventoryStacks = lists.ToArray();
                // one page per frame style, cycling every metal; the page names the metals and links the guide (Calm,
                // 2026-10-16: "just want say what metals and link to the guide")
                var frame = block;
                HandbookGroups.Register(block, extraStacks: metals.Where(m => !shown.Contains(m)).Select(m => Stack(frame, m).ResolvedItemstack));
                HandbookGroups.AddSection(block, "deskpictureframe:handbook-metals-title", capi => string.Join(", ", metals.Select(MetalName).Distinct()));
                HandbookGroups.AddSection(block, "deskpictureframe:handbook-guide-title", capi => Lang.Get("deskpictureframe:handbook-guide-text"));
                frames++;
            }
            api.Logger.Notification("[DeskPictureFrame] Creative tab: {0} frame(s) x {1} material(s)", frames, shown.Count);
        }

        /// <summary>"Copper": lang deskpictureframe:material-&lt;code&gt;, else the game's material-&lt;code&gt;, else the code with a capital.</summary>
        public static string MetalName(string code)
        {
            if (Lang.HasTranslation("deskpictureframe:material-" + code)) return Lang.Get("deskpictureframe:material-" + code);
            if (Lang.HasTranslation("game:material-" + code)) return Lang.Get("game:material-" + code);
            return string.IsNullOrEmpty(code) ? code : char.ToUpperInvariant(code[0]) + code.Substring(1);
        }

        private static JsonItemStack Stack(Block block, string material)
        {
            var tree = new TreeAttribute();
            FrameMesher.WriteMetal(tree, material);
            var js = new JsonItemStack { Type = EnumItemClass.Block, Code = block.Code, StackSize = 1, Attributes = new JsonObject(JToken.Parse(tree.ToJsonToken())) };
            js.ResolvedItemstack = new ItemStack(block);
            FrameMesher.WriteMetal(js.ResolvedItemstack.Attributes, material);
            return js;
        }
    }
}
