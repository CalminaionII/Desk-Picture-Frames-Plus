using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace DeskPictureFrame
{
    /// <summary>
    /// A picture that arrived over the network, made visible to the asset system WITHOUT a
    /// restart.
    ///
    /// WHY THIS IS NEEDED. An asset origin is scanned once, when assets load, between
    /// `StartPre` and `StartClientSide`. A picture downloaded mid-session lands on disk far
    /// too late for that, so it did not resolve until the next launch - which is exactly the
    /// case that matters, because a stranger's pictures arrive when you first walk up to their
    /// frames.
    ///
    /// WHY NOT JUST INSERT INTO THE ATLAS. `InsertTextureCached` does work after the atlas has
    /// been generated, but ARL never gets that far: `BakeVariantTextures` tests
    /// `clientApi.Assets.Exists(...)` first and quietly rewrites the path to `game:unknown`
    /// when it fails. The ASSET has to exist, not merely the texture - so this registers one
    /// with `IAssetManager.Add`, documented as "Adds a runtime asset to the game".
    ///
    /// Doing it this way means neither render path needs changing: the placed block and the
    /// shelved itemstack both resolve the ordinary way, through ARL, with correct lighting and
    /// chunk batching. The alternative was overriding `ITexPositionSource` on both paths and
    /// reproducing ARL's own metal and overlay baking, which is far more code and depends on
    /// internals that are not ours.
    /// </summary>
    public class RuntimeImageAsset : IAsset
    {
        public RuntimeImageAsset(AssetLocation location, byte[] data)
        {
            Location = location;
            Name = location?.Path;
            Data = data;
        }

        public AssetLocation Location { get; set; }
        public string Name { get; set; }
        public byte[] Data { get; set; }
        public IAssetOrigin Origin { get; set; }
        public bool IsPatched { get; set; }

        public bool IsLoaded() => Data != null;

        public string ToText() => Data == null ? null : Encoding.UTF8.GetString(Data);

        public T ToObject<T>(Newtonsoft.Json.JsonSerializerSettings settings = null)
        {
            return Newtonsoft.Json.JsonConvert.DeserializeObject<T>(ToText(), settings);
        }

        /// <summary>The only method that actually matters here - it is what the texture atlas
        /// calls when it loads this asset.</summary>
        public BitmapRef ToBitmap(ICoreClientAPI capi) => capi.Render.BitmapCreateFromPng(Data);
    }
}
