using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;

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
        /// listed material, in file order, on the tabs its blocktype names. So a frame design or a material
        /// another mod adds shows up with nothing written per metal.
        /// </summary>
        public static void FillCreativeTabs(ICoreAPI api)
        {
            int frames = 0;
            foreach (var block in api.World.Blocks)
            {
                if (block is not BlockDeskPictureFrame || block.Code == null || block.CreativeInventoryStacks == null) continue;
                var lists = new List<CreativeTabAndStackList>();
                foreach (var list in block.CreativeInventoryStacks)
                {
                    if (list?.Tabs == null || list.Tabs.Length == 0) continue;
                    lists.Add(new CreativeTabAndStackList { Tabs = list.Tabs, Stacks = listed.Select(code => Stack(block, code)).ToArray() });
                }
                if (lists.Count == 0) continue;
                block.CreativeInventoryStacks = lists.ToArray();
                frames++;
            }
            api.Logger.Notification("[DeskPictureFrame] Creative tab: {0} frame(s) x {1} material(s)", frames, listed.Count);
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
