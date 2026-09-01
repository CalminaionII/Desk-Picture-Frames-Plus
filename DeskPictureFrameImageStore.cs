using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace DeskPictureFrame
{
    /// <summary>
    /// Content-addressed storage for the pictures players put in frames.
    ///
    /// AN IMAGE IS IDENTIFIED BY ITS CONTENT, not by who owns it. That is the entire point of
    /// this class, and it is what makes the rest of the mod possible:
    ///
    /// * **A texture path needs no identity.** The client cache can therefore be built in
    ///   `StartPre`, BEFORE assets load - and an asset origin is only ever scanned at load.
    ///   Keying on the player could never satisfy that, because a player's UID is not known
    ///   until they connect, which is long after assets have loaded. That single fact is why
    ///   the previous design kept almost working.
    /// * **A frame keeps the picture it was placed with**, rather than following whatever the
    ///   placer happens to have in that slot today.
    /// * **Spoofing is structurally impossible.** The server recomputes the hash of anything it
    ///   is sent and drops a mismatch, so nobody can claim an identity without the bytes to
    ///   back it. No "trust the sender" check is needed because there is nothing to trust.
    /// * **Identical pictures are stored once** for the whole server.
    /// </summary>
    public static class DeskPictureFrameImageStore
    {
        /// <summary>Hex characters in an image id. 16 = 64 bits, which is far more than enough
        /// to keep pictures apart by accident. It is NOT a security boundary - the server
        /// verifies bytes against the claimed id, so a collision cannot be used to substitute
        /// one picture for another.</summary>
        public const int IdLength = 16;

        /// <summary>Most pictures indexed from the player's own folder.
        ///
        /// The old 16-folders-of-5 structure capped this at 80 by accident; flattening removed
        /// the ceiling without replacing it. Every picture is hashed and copied at startup, so
        /// an unbounded folder means a slow launch - and every one also lands in a dropdown.
        /// Generous, but finite.</summary>
        public const int MaxPictures = 250;

        /// <summary>Most images one server will hold, across every player. Content-addressed
        /// and deduplicated, so this counts distinct pictures rather than uploads.</summary>
        public const int MaxServerImages = 5000;

        /// <summary>Largest a picture may be, in pixels: 480x320 lying down or 320x480 standing
        /// up. A CEILING, not a required size - anything smaller is fine and is not stretched.
        ///
        /// Dimensions matter more than file size here. Every picture becomes a real texture in
        /// the block atlas, so a phone photo costs atlas space and video memory for everyone who
        /// can see the frame, not just the player who supplied it. 1 MB of PNG can easily be
        /// 6000x4000, which is why the byte cap alone was not enough.</summary>
        public const int MaxLongSide = 480;
        public const int MaxShortSide = 320;

        /// <summary>True if a picture is within the cap in either orientation. A square must fit
        /// the short side, since it has to satisfy whichever way round it is read.</summary>
        public static bool WithinSizeLimit(int width, int height) =>
            (width <= MaxLongSide && height <= MaxShortSide) ||
            (width <= MaxShortSide && height <= MaxLongSide);

        /// <summary>
        /// Reads a PNG's dimensions from its header, without decoding the image.
        ///
        /// The IHDR chunk is fixed: 8 byte signature, 4 byte length, "IHDR", then width and
        /// height as big-endian 32 bit. So 24 bytes is enough to police the limit - no image
        /// library, nothing that only exists on the client, and nothing that has to allocate a
        /// bitmap for a file that is about to be refused.
        /// </summary>
        public static bool TryReadPngSize(byte[] data, out int width, out int height)
        {
            width = height = 0;
            if (data == null || data.Length < 24) return false;
            if (data[0] != 0x89 || data[1] != 'P' || data[2] != 'N' || data[3] != 'G') return false;
            if (data[12] != 'I' || data[13] != 'H' || data[14] != 'D' || data[15] != 'R') return false;

            width  = (data[16] << 24) | (data[17] << 16) | (data[18] << 8) | data[19];
            height = (data[20] << 24) | (data[21] << 16) | (data[22] << 8) | data[23];
            return width > 0 && height > 0;
        }

        /// <summary>Same, straight off disk, reading only the header.</summary>
        public static bool TryReadPngSize(string path, out int width, out int height)
        {
            width = height = 0;
            try
            {
                using var stream = File.OpenRead(path);
                var head = new byte[24];
                return stream.Read(head, 0, 24) == 24 && TryReadPngSize(head, out width, out height);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Image ids are lowercase hex so they are always legal in an AssetLocation.
        /// The path segment they replaced - a raw player UID - is base64, containing '+' and
        /// mixed case, and silently resolved to nothing.</summary>
        public static string IdOf(byte[] data)
        {
            if (data == null || data.Length == 0) return null;

            using var sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(data);

            var sb = new StringBuilder(IdLength);
            for (int i = 0; i < IdLength / 2; i++) sb.Append(hash[i].ToString("x2"));
            return sb.ToString();
        }

        public static bool Verify(string id, byte[] data) => id != null && id == IdOf(data);

        public static bool IsWellFormedId(string id)
        {
            if (id == null || id.Length != IdLength) return false;
            foreach (char c in id)
            {
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            }
            return true;
        }

        // ---- client cache -----------------------------------------------------------------
        //
        // Registered as an asset origin in StartPre, so "deskpictureframe:img/<id>" resolves to
        // <root>/textures/img/<id>.png. Everything under it is derived and can be deleted at
        // any time - it rebuilds from the player's own folder and from downloads.

        public static string ClientCacheRoot() =>
            Path.Combine(GamePaths.DataPath, "ModData", "deskpictureframe", "imagecache");

        public static string ClientImagePath(string id) =>
            Path.Combine(ClientCacheRoot(), "textures", "img", id + ".png");

        public static bool ClientHas(string id) =>
            IsWellFormedId(id) && File.Exists(ClientImagePath(id));

        /// <summary>The asset path a picture resolves through - "deskpictureframe:img/&lt;id&gt;",
        /// matching the texture entries in the blocktypes.</summary>
        public static AssetLocation AssetLocationOf(string id) =>
            new AssetLocation("deskpictureframe", "textures/img/" + id + ".png");

        /// <summary>
        /// Makes a picture usable THIS session, not after a restart.
        ///
        /// Files written into an origin's folder after assets have loaded are invisible - the
        /// origin was scanned once, at load. Registering a runtime asset sidesteps that, and
        /// because it is a real asset both render paths resolve it the ordinary way through
        /// ARL, which tests Assets.Exists before it will look at the atlas at all.
        /// </summary>
        public static void RegisterRuntime(ICoreClientAPI capi, string id, byte[] data)
        {
            if (capi == null || !IsWellFormedId(id) || data == null) return;

            try
            {
                var loc = AssetLocationOf(id);
                capi.Assets.Add(loc, new RuntimeImageAsset(loc, data));
            }
            catch (Exception ex)
            {
                capi.Logger.Warning($"[DeskPictureFrame] Could not register image {id} for this session: {ex.Message}");
            }
        }

        public static bool WriteClient(string id, byte[] data)
        {
            if (!IsWellFormedId(id) || data == null) return false;
            try
            {
                string path = ClientImagePath(id);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                if (!File.Exists(path)) File.WriteAllBytes(path, data);
                return true;
            }
            catch (Exception) { return false; }
        }

        // ---- server store -----------------------------------------------------------------

        public static string ServerStoreRoot() =>
            Path.Combine(GamePaths.DataPath, "ModData", "deskpictureframe", "images");

        public static string ServerImagePath(string id) =>
            Path.Combine(ServerStoreRoot(), id + ".png");

        public static bool ServerHas(string id) =>
            IsWellFormedId(id) && File.Exists(ServerImagePath(id));

        /// <summary>Stores bytes under the id they actually hash to, ignoring whatever the
        /// sender claimed. Returns the real id, or null if the data was rejected.</summary>
        /// <summary>Distinct images this server holds. Counted once and then kept, rather than
        /// listing the directory on every upload.</summary>
        private static int serverCount = -1;

        public static bool ServerIsFull()
        {
            if (serverCount < 0)
            {
                try
                {
                    serverCount = Directory.Exists(ServerStoreRoot())
                        ? Directory.GetFiles(ServerStoreRoot(), "*.png").Length : 0;
                }
                catch (Exception) { serverCount = 0; }
            }
            return serverCount >= MaxServerImages;
        }

        public static string WriteServer(byte[] data, string claimedId)
        {
            if (data == null) return null;

            string realId = IdOf(data);
            if (claimedId != null && realId != claimedId) return null;

            try
            {
                string path = ServerImagePath(realId);
                if (File.Exists(path)) return realId;      // already held; costs nothing
                if (ServerIsFull()) return null;

                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(path, data);
                serverCount++;
                return realId;
            }
            catch (Exception) { return null; }
        }

        public static byte[] ReadServer(string id)
        {
            if (!ServerHas(id)) return null;
            try { return File.ReadAllBytes(ServerImagePath(id)); }
            catch (Exception) { return null; }
        }

        // ---- the player's own pictures ----------------------------------------------------

        /// <summary>
        /// Where the player's own pictures live. Under ModData, not ModConfig - these are the
        /// player's DATA, not settings, and it keeps everything this mod owns together.
        /// </summary>
        public static string UserImagesRoot() =>
            Path.Combine(GamePaths.DataPath, "ModData", "deskpictureframe", "userimages");

        /// <summary>The "textures" level is required by the asset origin layout.</summary>
        public static string LocalRoot() => Path.Combine(UserImagesRoot(), "textures");

        /// <summary>
        /// The one folder players drop pictures into, with any filenames they like.
        ///
        /// This replaced 16 folders of 5 numbered files. The numbering only ever existed
        /// because a frame picked its picture by SLOT; now a frame stores the picture itself
        /// and the slot is chosen in game, so the structure had nothing left to do.
        /// </summary>
        public static string PicturesRoot() => Path.Combine(LocalRoot(), "pictures");

        /// <summary>
        /// Re-reads ONE of the player's pictures from disk and returns its current id.
        ///
        /// LocalIds is built once in StartPre, so a file edited mid-session is not noticed and
        /// a re-capture would quietly reuse the old id - which is why sneak-clicking only took
        /// effect after a restart. Re-hashing on demand fixes that, and registering the result
        /// as a runtime asset makes the new picture usable immediately rather than at next
        /// launch (the origin was scanned at load and will not see the new bytes).
        /// </summary>
        public static string RefreshLocalImage(ICoreClientAPI capi, string imageKey)
        {
            if (imageKey == null) return null;

            try
            {
                string file = Path.Combine(PicturesRoot(),
                    imageKey.Replace("/", Path.DirectorySeparatorChar.ToString()) + ".png");
                if (!File.Exists(file)) return LocalIdFor(imageKey);

                byte[] data = File.ReadAllBytes(file);
                string id = IdOf(data);
                if (id == null) return LocalIdFor(imageKey);

                LocalIds[imageKey] = id;
                WriteClient(id, data);
                RegisterRuntime(capi, id, data);
                return id;
            }
            catch (Exception ex)
            {
                capi?.Logger.Warning($"[DeskPictureFrame] Could not re-read {imageKey}: {ex.Message}");
                return LocalIdFor(imageKey);
            }
        }

        /// <summary>
        /// Image key -> id, for every picture in this player's own folder. The key is the same
        /// "&lt;folder&gt;/&lt;slot&gt;" form used everywhere else, e.g.
        /// "desk-landscape-images/image1/3".
        ///
        /// Rebuilt from disk on every launch, so editing a file in the folder is picked up
        /// with no extra step - which is the whole appeal of the folder for players.
        /// </summary>
        public static Dictionary<string, string> LocalIds { get; private set; } = new();

        /// <summary>Files in the pictures folder that are not PNG, from the last index. Shown
        /// in the picker so a dropped JPG does not just silently fail to appear.</summary>
        public static int IgnoredFileCount { get; private set; }

        /// <summary>PNGs refused for being over the pixel cap, from the last index.</summary>
        public static int OversizedCount { get; private set; }

        /// <summary>Image id -> its pixel size, for everything indexed. The picker shows this so
        /// a player can see what they are choosing without leaving the game.</summary>
        public static Dictionary<string, (int Width, int Height)> LocalSizes { get; private set; } = new();

        /// <summary>
        /// Hashes everything in the player's own folder and copies it into the client cache.
        ///
        /// MUST RUN IN StartPre. The cache is an asset origin, and an origin is scanned when
        /// assets load - once, at game start, after StartPre and before StartClientSide.
        /// Anything not present by then does not resolve, and nothing logs it.
        /// </summary>
        public static void BuildClientCacheFromLocal(ICoreAPI api, string picturesRoot)
        {
            var ids = new Dictionary<string, string>();
            var sizes = new Dictionary<string, (int Width, int Height)>();
            var oversized = new List<string>();

            try
            {
                Directory.CreateDirectory(Path.Combine(ClientCacheRoot(), "textures", "img"));
                Directory.CreateDirectory(picturesRoot);

                int added = 0;
                bool capped = false;
                foreach (string file in Directory.GetFiles(picturesRoot, "*.png", SearchOption.AllDirectories))
                {
                    if (ids.Count >= MaxPictures) { capped = true; break; }

                    try
                    {
                        // Header first, so an oversized file is refused without reading or
                        // hashing all of it.
                        if (!TryReadPngSize(file, out int w, out int h) || !WithinSizeLimit(w, h))
                        {
                            oversized.Add(Path.GetFileName(file) + "  " + w + "x" + h);
                            continue;
                        }

                        byte[] data = File.ReadAllBytes(file);
                        string id = IdOf(data);
                        if (id == null) continue;

                        sizes[id] = (w, h);

                        // Keyed by the file's own name - there is no folder scheme any more.
                        string key = Path.ChangeExtension(Path.GetRelativePath(picturesRoot, file), null)
                                         .Replace("\\", "/");
                        ids[key] = id;

                        if (!ClientHas(id) && WriteClient(id, data)) added++;
                    }
                    catch (Exception ex)
                    {
                        api.Logger.Warning($"[DeskPictureFrame] Could not read {file}: {ex.Message}");
                    }
                }

                api.Logger.Notification(
                    $"[DeskPictureFrame] Indexed {ids.Count} picture(s), {added} new to the cache.");

                // A JPG dropped in here used to vanish without trace: not indexed, not in the
                // picker, nothing in the log. Say so, and say it in the picker too.
                IgnoredFileCount = 0;
                foreach (string file in Directory.GetFiles(picturesRoot, "*", SearchOption.AllDirectories))
                {
                    if (!file.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) IgnoredFileCount++;
                }
                if (IgnoredFileCount > 0)
                {
                    api.Logger.Warning(
                        $"[DeskPictureFrame] Ignored {IgnoredFileCount} file(s) in {picturesRoot} - " +
                        "pictures must be PNG files of 1 MB or less.");
                }

                // Name and measure each one: "too big" is useless without saying by how much.
                OversizedCount = oversized.Count;
                if (OversizedCount > 0)
                {
                    api.Logger.Warning(
                        $"[DeskPictureFrame] {OversizedCount} picture(s) are larger than " +
                        $"{MaxLongSide}x{MaxShortSide} (or {MaxShortSide}x{MaxLongSide} upright) " +
                        $"and were not loaded: {string.Join(", ", oversized)}");
                }

                if (capped)
                {
                    api.Logger.Warning(
                        $"[DeskPictureFrame] Only the first {MaxPictures} pictures were loaded - " +
                        $"there are more than that in {picturesRoot}. Remove some if the one you " +
                        "want is missing.");
                }
            }
            catch (Exception ex)
            {
                api.Logger.Error($"[DeskPictureFrame] Could not build the image cache: {ex.Message}");
            }

            LocalIds = ids;
            LocalSizes = sizes;
        }

        /// <summary>
        /// Old numbered-slot key ("desk-landscape-images/image1/3") -> the id of the picture
        /// that used to live there.
        ///
        /// PURELY FOR COMPATIBILITY. A frame placed before pictures had ids stores none, and
        /// its picture was "slot 3 of my folder" - a mapping that flattening would otherwise
        /// destroy. The migration records where each numbered file ended up, so those frames
        /// keep showing the right picture and get a real id the first time anyone re-captures
        /// them. Empty on a fresh install.
        /// </summary>
        public static Dictionary<string, string> LegacyIds { get; private set; } = new();

        public static void SetLegacyIds(Dictionary<string, string> map) => LegacyIds = map ?? new();

        public static string LegacyIdFor(string oldKey) =>
            oldKey != null && LegacyIds.TryGetValue(oldKey, out string id) ? id : null;

        /// <summary>
        /// Re-reads the pictures folder and registers anything new for immediate use.
        ///
        /// Same path a downloaded picture takes: hash, cache, register as a runtime asset. The
        /// registration is what makes it usable without a restart, since the asset origin was
        /// scanned once when the game loaded.
        /// </summary>
        public static void Reindex(ICoreClientAPI capi)
        {
            var before = new Dictionary<string, string>(LocalIds);
            BuildClientCacheFromLocal(capi, PicturesRoot());

            foreach (var pair in LocalIds)
            {
                if (before.TryGetValue(pair.Key, out string old) && old == pair.Value) continue;

                try
                {
                    RegisterRuntime(capi, pair.Value, File.ReadAllBytes(ClientImagePath(pair.Value)));
                }
                catch (Exception ex)
                {
                    capi?.Logger.Warning($"[DeskPictureFrame] Could not register {pair.Key}: {ex.Message}");
                }
            }
        }

        /// <summary>The id of this player's own picture for an image key, or null if absent.</summary>
        public static string LocalIdFor(string imageKey)
        {
            return imageKey != null && LocalIds.TryGetValue(imageKey, out string id) ? id : null;
        }
    }
}
