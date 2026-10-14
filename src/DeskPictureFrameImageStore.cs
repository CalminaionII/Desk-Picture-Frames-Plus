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

        // ---- limits ------------------------------------------------------------------------
        //
        // ALL FOUR ARE SETTINGS NOW, read from ModConfig/DeskPictureFrameConfig.json - see
        // DeskPictureFrameConfig for what each one costs and who it applies to. They are
        // `static` rather than `const` for exactly that reason; nothing else about them
        // changed, and a fresh install still behaves as it did.
        //
        // The 1 MB byte cap and the PNG-only rule are NOT here and are NOT settings. They live
        // in DeskPictureFrameNetworkManager as consts, because they are the boundary that stops
        // one player handing arbitrary bytes to everyone who can see their frame.

        /// <summary>Most pictures indexed from the player's own folder.
        ///
        /// The old 16-folders-of-5 structure capped this at 80 by accident; flattening removed
        /// the ceiling without replacing it. Every picture is hashed and copied at startup, so
        /// an unbounded folder means a slow launch - and every one also lands in a dropdown.
        /// Generous, but finite.</summary>
        public static int MaxPictures { get; private set; } = 250;

        /// <summary>Most images one server will hold, across every player. Content-addressed
        /// and deduplicated, so this counts distinct pictures rather than uploads.</summary>
        public static int MaxServerImages { get; private set; } = 5000;

        /// <summary>Most pictures kept in the local cache. See the config; the cache is the one
        /// folder that would otherwise grow forever.</summary>
        public static int MaxCachedImages { get; private set; } = 2000;

        /// <summary>Largest a picture may be, in pixels: 480x320 lying down or 320x480 standing
        /// up by default. A CEILING, not a required size - anything smaller is fine and is not
        /// stretched.
        ///
        /// Dimensions matter more than file size here. Every picture becomes a real texture in
        /// the block atlas, so a phone photo costs atlas space and video memory for everyone who
        /// can see the frame, not just the player who supplied it. 1 MB of PNG can easily be
        /// 6000x4000, which is why the byte cap alone was not enough.
        ///
        /// THESE TWO ARE THE SERVER'S, once connected to one. See <see cref="ApplyServerLimits"/>.</summary>
        public static int MaxLongSide { get; private set; } = 480;
        public static int MaxShortSide { get; private set; } = 320;

        /// <summary>What this machine's own config file asked for, kept so the client can tell
        /// a server that overrode it from one that agreed with it.</summary>
        private static int localLongSide = 480, localShortSide = 320;

        /// <summary>The caps the last index actually ran under. A server can change them
        /// mid-session, and a picture list built to the old ones would be wrong in whichever
        /// direction the change went.</summary>
        private static int indexedLongSide = -1, indexedShortSide = -1;

        /// <summary>Takes this machine's settings. Called on both sides in StartPre, before
        /// anything reads a limit.</summary>
        public static void ApplyConfig(DeskPictureFrameConfig config)
        {
            if (config == null) return;

            MaxPictures = config.Client.MaxPictures;
            MaxCachedImages = config.Client.MaxCachedImages;
            MaxServerImages = config.Server.MaxServerImages;
            MaxLongSide = localLongSide = config.Server.MaxLongSide;
            MaxShortSide = localShortSide = config.Server.MaxShortSide;
        }

        /// <summary>
        /// CLIENT, on a config reload while in a world. Takes the client's own settings and this
        /// machine's own size caps, but does NOT overwrite caps a server has imposed for the
        /// session - ApplyConfig would, because it cannot tell a server's number from the
        /// file's. The rule: if the live caps still equal the OLD local caps, no server has
        /// spoken and the new local caps go live; otherwise the server's stay. Returns true if
        /// the picture list on screen needs re-indexing.
        /// </summary>
        public static bool ApplyClientReload(DeskPictureFrameConfig config)
        {
            if (config == null) return false;

            bool serverCapsInForce = MaxLongSide != localLongSide || MaxShortSide != localShortSide;
            bool picturesChanged = MaxPictures != config.Client.MaxPictures;

            MaxPictures = config.Client.MaxPictures;
            MaxCachedImages = config.Client.MaxCachedImages;
            localLongSide = config.Server.MaxLongSide;
            localShortSide = config.Server.MaxShortSide;

            if (!serverCapsInForce)
            {
                MaxLongSide = localLongSide;
                MaxShortSide = localShortSide;
            }

            return picturesChanged || IndexIsStale;
        }

        /// <summary>
        /// CLIENT: adopt the server's pixel caps for the rest of the session.
        ///
        /// The server is the only opinion that counts - it refuses an oversized upload whatever
        /// the sender believes - so a picker warning off the local file would name a number
        /// nobody enforces. Returns true if this actually changed anything, which is the signal
        /// to re-index: a stricter server means pictures already indexed must now be refused,
        /// and a more generous one means pictures skipped at launch should be offered.
        ///
        /// In singleplayer both sides read the same file, so this is always a no-op.
        /// </summary>
        public static bool ApplyServerLimits(int longSide, int shortSide)
        {
            if (longSide < 1 || shortSide < 1) return false;
            if (longSide == MaxLongSide && shortSide == MaxShortSide) return false;

            MaxLongSide = longSide;
            MaxShortSide = shortSide;
            return true;
        }

        /// <summary>CLIENT: back to this machine's own settings, on leaving a server. Without
        /// this, a strict server's caps would follow the player into the next world.</summary>
        public static void ForgetServerLimits()
        {
            MaxLongSide = localLongSide;
            MaxShortSide = localShortSide;
        }

        /// <summary>The most bytes one picture may be, and NOT A SETTING - see the note in
        /// DeskPictureFrameNetworkManager, which enforces it on the wire.
        ///
        /// IT LIVES HERE because the index has to apply it too. It used to be checked only when
        /// a picture was about to be SENT, which meant an over-size file was indexed, listed in
        /// the picker and could be chosen - and then failed silently on upload, leaving a frame
        /// on the placeholder with nothing on screen saying why. Calm found it on 2026-09-04
        /// with a 3.1 MB picture: "it should not show in there if it is over the size limit as
        /// it would just be confusing."</summary>
        public const int MaxImageBytes = 1_000_000;

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
        // Pictures by id, as <root>/textures/img/<id>.png (the layout of the asset origin it was
        // until 1.2.0, kept so existing caches still read). Plain files now: FrameMesher puts a
        // picture's bytes in the atlas when a frame first draws it. Everything under it is derived
        // and can be deleted at any time - it rebuilds from the player's own folder and downloads.

        public static string ClientCacheRoot() =>
            Path.Combine(GamePaths.DataPath, "ModData", "deskpictureframe", "imagecache");

        public static string ClientImagePath(string id) =>
            Path.Combine(ClientCacheRoot(), "textures", "img", id + ".png");

        public static bool ClientHas(string id) =>
            IsWellFormedId(id) && File.Exists(ClientImagePath(id));

        /// <summary>
        /// Drops the oldest cached pictures until the cache is within its cap.
        ///
        /// **SAFE BY CONSTRUCTION, because the cache is derived.** Everything in it is either a
        /// copy of one of your own pictures - rebuilt at the next launch - or a download, which
        /// comes back from the server the next time you look at a frame using it. Evicting can
        /// lose nothing but time.
        ///
        /// YOUR OWN PICTURES ARE NEVER EVICTED. They would only be written straight back at the
        /// next launch, and dropping one mid-session would blank a frame you are looking at.
        /// Oldest first otherwise, by write time, which is the closest thing to "least recently
        /// wanted" available without tracking every read.
        /// </summary>
        public static void TrimCache(ICoreAPI api)
        {
            try
            {
                string dir = Path.Combine(ClientCacheRoot(), "textures", "img");
                if (!Directory.Exists(dir)) return;

                string[] files = Directory.GetFiles(dir, "*.png");
                if (files.Length <= MaxCachedImages) return;

                // NOTHING IS PROTECTED ANY MORE. Cached copies of your own pictures used to be
                // exempt, because dropping one meant it was gone until the next launch. Now
                // anything evicted is either re-read from your pictures folder for nothing, or
                // fetched again from the server the next time a frame needs it.
                var candidates = new List<FileInfo>();
                foreach (string file in files) candidates.Add(new FileInfo(file));

                candidates.Sort((a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));

                int over = files.Length - MaxCachedImages;
                int removed = 0;
                foreach (var info in candidates)
                {
                    if (removed >= over) break;
                    try { info.Delete(); removed++; } catch (Exception) { }
                }

                if (removed > 0)
                {
                    api?.Logger.Notification(
                        $"[DeskPictureFrame] Image cache was over {MaxCachedImages}; removed the " +
                        $"{removed} least recently added. They come back from the server if needed.");
                }
            }
            catch (Exception ex)
            {
                api?.Logger.Warning($"[DeskPictureFrame] Could not tidy the image cache: {ex.Message}");
            }
        }

        /// <summary>Drops one picture from the local cache. Used when you remove a picture from
        /// a server, so the two agree at once rather than at the next launch. If it is still in
        /// your own pictures folder it will simply be cached again next time - correct, since it
        /// is still your picture.</summary>
        public static bool DeleteClient(string id)
        {
            if (!IsWellFormedId(id)) return false;
            FrameMesher.ForgetPicture(id);   // frames drawn from now on show the placeholder, as before 1.2.0
            try
            {
                string path = ClientImagePath(id);
                if (!File.Exists(path)) return false;
                File.Delete(path);
                return true;
            }
            catch (Exception) { return false; }
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

        /// <summary>
        /// Image id -> the file holding it. Built by reading the store's FILENAMES, never from
        /// the ledger.
        ///
        /// **THAT IS WHAT MAKES A FOLDER PER PLAYER SAFE.** Pictures live at
        /// `images/&lt;player&gt;/&lt;name&gt;_&lt;id&gt;.png`, so an admin can open a folder, see
        /// "flower.png" and delete it - and the server finds every other picture exactly as
        /// before, because the id it needs is still in the name of every file. Nothing about
        /// resolving an image depends on uploaders.json, which stays what it always was: history
        /// an admin can lose without breaking a single frame.
        ///
        /// Rebuilt on startup and after anything that moves files. A stale entry costs nothing -
        /// every use checks the file is really there.
        /// </summary>
        private static Dictionary<string, string> serverPaths = new();

        /// <summary>The 16 hex characters before ".png" are the id, whatever comes before them.
        /// So "flower_63b9...png", "63b9...png" and "holiday photo_63b9...png" all resolve, and
        /// an admin renaming the readable part breaks nothing.</summary>
        public static string IdOfFileName(string file)
        {
            string name = Path.GetFileNameWithoutExtension(file);
            if (name.Length < IdLength) return null;

            string tail = name.Substring(name.Length - IdLength);
            return IsWellFormedId(tail) ? tail : null;
        }

        /// <summary>The readable half of a stored file's name - "flower" out of
        /// "flower_63b9...png". Null when the file is just the bare id, which is everything
        /// stored before 1.1.1.</summary>
        public static string LabelOfFileName(string file)
        {
            string name = Path.GetFileNameWithoutExtension(file);
            if (name.Length <= IdLength) return null;

            string label = name.Substring(0, name.Length - IdLength);
            label = label.TrimEnd('_');
            return label.Length == 0 ? null : label;
        }

        /// <summary>Does THIS PLAYER'S folder hold this picture? Not "does the server have it
        /// anywhere" - each player keeps their own copy, so this is the question that decides
        /// whether an upload costs them a slot.</summary>
        public static bool PlayerHasImage(string playerName, string id)
        {
            string folder = PlayerFolder(playerName);
            if (folder == null || !Directory.Exists(folder) || !IsWellFormedId(id)) return false;

            try
            {
                foreach (string file in Directory.GetFiles(folder, "*.png"))
                {
                    if (IdOfFileName(file) == id) return true;
                }
            }
            catch (Exception) { }
            return false;
        }

        /// <summary>Reads the store off disk. Cheap - it looks at names and never opens a file,
        /// so a full store costs a directory walk rather than 5 GB of hashing.</summary>
        public static void BuildServerIndex(ICoreAPI api)
        {
            var found = new Dictionary<string, string>();
            try
            {
                Directory.CreateDirectory(ServerStoreRoot());
                foreach (string file in Directory.GetFiles(ServerStoreRoot(), "*.png",
                                                           SearchOption.AllDirectories))
                {
                    string id = IdOfFileName(file);
                    if (id == null)
                    {
                        api?.Logger.Warning(
                            $"[DeskPictureFrame] Ignoring {Path.GetFileName(file)} in the picture " +
                            "store - the file name does not end in an image id, so nothing can " +
                            "ask for it. Renaming it back to '<something>_<id>.png' restores it.");
                        continue;
                    }

                    // THE SAME PICTURE IN TWO FOLDERS IS EXPECTED, not a conflict. Each player
                    // keeps their own copy so that each can delete their own without blanking
                    // the other's frames - the bytes are identical, so serving either is the
                    // same answer. First one found is as good as any.
                    if (!found.ContainsKey(id)) found[id] = file;
                }
            }
            catch (Exception ex)
            {
                api?.Logger.Error($"[DeskPictureFrame] Could not read the picture store: {ex.Message}");
            }

            serverPaths = found;
            api?.Logger.Notification($"[DeskPictureFrame] Picture store holds {found.Count} image(s).");
        }

        /// <summary>Where an image actually is, or null. Verified against the filesystem every
        /// time, so a file deleted by hand stops existing here the moment it is gone - which is
        /// what gives the player their allowance back with nothing to run.</summary>
        public static string ServerImagePath(string id)
        {
            if (!IsWellFormedId(id)) return null;
            if (!serverPaths.TryGetValue(id, out string path)) return null;

            if (File.Exists(path)) return path;

            // The copy we knew about has gone. Another player may still hold one, and the bytes
            // are identical, so look before declaring the picture lost.
            string other = FindAnyCopy(id);
            if (other != null)
            {
                serverPaths[id] = other;
                return other;
            }

            serverPaths.Remove(id);
            return null;
        }

        /// <summary>A copy of this picture sitting loose in the store root - i.e. left by the
        /// flat layout used before 1.1.1, belonging to nobody.</summary>
        private static string LooseCopy(string id)
        {
            try
            {
                foreach (string file in Directory.GetFiles(ServerStoreRoot(), "*.png",
                                                           SearchOption.TopDirectoryOnly))
                {
                    if (IdOfFileName(file) == id) return file;
                }
            }
            catch (Exception) { }
            return null;
        }

        /// <summary>Any file in the store holding this picture, whoever's folder it is in.</summary>
        private static string FindAnyCopy(string id)
        {
            try
            {
                foreach (string file in Directory.GetFiles(ServerStoreRoot(), "*.png",
                                                           SearchOption.AllDirectories))
                {
                    if (IdOfFileName(file) == id) return file;
                }
            }
            catch (Exception) { }
            return null;
        }

        public static bool ServerHas(string id) => ServerImagePath(id) != null;

        /// <summary>The folder one player's pictures live in. Their name, not their UID, because
        /// the whole point is an admin recognising it - and a name that is awkward on a
        /// filesystem falls back to something that is not.</summary>
        public static string PlayerFolder(string playerName)
        {
            string safe = SafeFolderName(playerName);
            return safe == null ? ServerStoreRoot() : Path.Combine(ServerStoreRoot(), safe);
        }

        /// <summary>Player names are not filenames. Anything that cannot go in a path is
        /// dropped, and a name left empty by that goes in the store root rather than in a
        /// folder called nothing.</summary>
        private static string SafeFolderName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;

            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
            {
                if (Array.IndexOf(Path.GetInvalidFileNameChars(), c) < 0 && !char.IsControl(c))
                    sb.Append(c);
                if (sb.Length >= 48) break;
            }

            string safe = sb.ToString().Trim().TrimEnd('.');
            return safe.Length == 0 ? null : safe;
        }

        /// <summary>The readable half of a stored file's name. Same treatment as the folder:
        /// it is a label, and a label that will not go in a path is simply left off.</summary>
        private static string SafeFilePart(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;

            if (name.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - 4);

            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
            {
                if (Array.IndexOf(Path.GetInvalidFileNameChars(), c) < 0 && !char.IsControl(c))
                    sb.Append(c);
                if (sb.Length >= 48) break;
            }

            string safe = sb.ToString().Trim();
            return safe.Length == 0 ? null : safe;
        }

        /// <summary>Stores bytes under the id they actually hash to, ignoring whatever the
        /// sender claimed. Returns the real id, or null if the data was rejected.</summary>

        /// <summary>Taken from the index, which is taken from the files - so deleting pictures
        /// by hand lowers it without anything having to be told. The old cached tally could
        /// only ever go up, and a hand-deletion left the store looking full for the rest of the
        /// session.</summary>
        public static bool ServerIsFull() => serverPaths.Count >= MaxServerImages;

        public static int ServerImageCount => serverPaths.Count;

        /// <summary>Stores bytes under the id they actually hash to, ignoring whatever the
        /// sender claimed, in the supplying player's own folder under a name a person can read.
        /// Returns the real id, or null if the data was rejected.</summary>
        public static string WriteServer(byte[] data, string claimedId,
                                         string playerName = null, string pictureName = null)
        {
            if (data == null) return null;

            string realId = IdOf(data);
            if (claimedId != null && realId != claimedId) return null;

            try
            {
                // THIS PLAYER'S folder, not the server as a whole. Each keeps their own copy so
                // that each can delete their own without blanking anyone else's frames - the
                // point of the folder being the count.
                if (PlayerHasImage(playerName, realId)) return realId;
                if (ServerIsFull()) return null;

                // <player>/<picture>_<id>.png. The id is the part that matters and is always
                // there; the rest is for whoever opens the folder. A picture with no usable
                // name is still stored, just as the bare id.
                string folder = PlayerFolder(playerName);
                string label = SafeFilePart(pictureName);
                string path = Path.Combine(folder, (label == null ? "" : label + "_") + realId + ".png");

                Directory.CreateDirectory(folder);

                // A picture sitting loose in the store root came from before there were folders
                // and belongs to nobody. Move it in rather than writing a second copy beside it -
                // that is how an old store sorts itself out as people use it.
                string loose = LooseCopy(realId);
                if (loose != null) File.Move(loose, path);
                else File.WriteAllBytes(path, data);

                serverPaths[realId] = path;
                return realId;
            }
            catch (Exception) { return null; }
        }

        /// <summary>Removes one image from the server's store. Used only by the admin command
        /// that clears a player's pictures - nothing automatic ever deletes.
        ///
        /// The cached count is invalidated rather than decremented: deleting is rare, and a
        /// recount cannot drift the way an adjustment can.</summary>
        /// <summary>Deletes ONE file. Named rather than looked up by id, because copies of a
        /// picture exist in several folders and removing a player's own must not take somebody
        /// else's with it.</summary>
        public static bool DeleteFile(string path, string id)
        {
            if (path == null || !File.Exists(path)) return false;
            try
            {
                File.Delete(path);

                // If the index pointed at this copy, re-point it at a surviving one; only when
                // there is none does the picture actually stop existing.
                if (serverPaths.TryGetValue(id, out string known)
                    && string.Equals(known, path, StringComparison.OrdinalIgnoreCase))
                {
                    string other = FindAnyCopy(id);
                    if (other != null) serverPaths[id] = other;
                    else serverPaths.Remove(id);
                }
                return true;
            }
            catch (Exception) { return false; }
        }

        /// <summary>The name this picture has in the player's OWN folder, for an id. Used when
        /// uploading, so the server can label what it stores with something a human recognises.
        /// Null if the id is not one of ours.</summary>
        public static string LocalNameFor(string id)
        {
            foreach (var pair in LocalIds)
            {
                if (pair.Value == id) return pair.Key;
            }
            return null;
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
        /// effect after a restart. Re-hashing on demand fixes that; the new picture is cached under
        /// its new id and drawn from there straight away.
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

        /// <summary>Image id -> the file in YOUR pictures folder holding it. What makes lazy
        /// caching possible: a picture of ours can be produced on demand instead of being copied
        /// up front, and produced for free - it is already on this disk.</summary>
        public static Dictionary<string, string> LocalPaths { get; private set; } = new();

        /// <summary>
        /// Makes sure a picture is in the cache and usable, if it is one of OURS.
        ///
        /// Returns false for anything we do not have a file for - a picture somebody else added -
        /// which is the signal to ask the server for it instead.
        ///
        /// Once it is in the cache a frame can draw it (FrameMesher reads it from there into the
        /// atlas), this session and the next. Downloads land in the same place.
        /// </summary>
        /// <summary>
        /// Pictures we have taken off this server, so we stop drawing them.
        ///
        /// **WITHOUT THIS, REMOVING A PICTURE ONLY HALF WORKED.** The file stays in your own
        /// pictures folder - the whole point of removing it from the server rather than deleting
        /// it - so the next frame to ask for it would read it straight back out of your folder
        /// and show it. To you, and to nobody else, which is the "looks right only to you" trap
        /// again.
        ///
        /// Cleared on leaving a world, and cleared for an id the moment it is uploaded again, so
        /// choosing the picture a second time works normally.
        /// </summary>
        private static readonly HashSet<string> releasedHere = new();

        public static void MarkReleased(string id)
        {
            if (IsWellFormedId(id)) releasedHere.Add(id);
        }

        public static void UnmarkReleased(string id)
        {
            if (id != null) releasedHere.Remove(id);
        }

        public static void ForgetReleased() => releasedHere.Clear();

        public static bool EnsureCached(ICoreClientAPI capi, string id)
        {
            if (!IsWellFormedId(id)) return false;
            if (ClientHas(id)) return true;
            if (releasedHere.Contains(id)) return false;   // we asked for it to be gone
            if (!LocalPaths.TryGetValue(id, out string file) || !File.Exists(file)) return false;

            try
            {
                byte[] data = File.ReadAllBytes(file);
                if (IdOf(data) != id) return false;      // edited since we indexed it

                WriteClient(id, data);
                return true;
            }
            catch (Exception ex)
            {
                capi?.Logger.Warning($"[DeskPictureFrame] Could not cache {file}: {ex.Message}");
                return false;
            }
        }

        /// <summary>Files in the pictures folder that are not PNG, from the last index. Shown
        /// in the picker so a dropped JPG does not just silently fail to appear.</summary>
        public static int IgnoredFileCount { get; private set; }

        /// <summary>PNGs refused for being over the pixel cap, from the last index.</summary>
        public static int OversizedCount { get; private set; }

        /// <summary>PNGs refused for being over 1 MB, from the last index. Counted separately
        /// from OversizedCount because the fix is different: one needs fewer pixels, the other
        /// needs more compression, and telling someone to shrink a picture that is already
        /// small enough on screen is useless advice.</summary>
        public static int TooBigCount { get; private set; }

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
        /// <summary>
        /// Removes the folder the JPEG converter used, if one is there.
        ///
        /// **THE CONVERTER WAS BUILT AND THEN DROPPED, 2026-09-04.** It took JPEGs and oversized
        /// PNGs and shrank them to fit. It worked, but it moved a judgement onto the mod that
        /// belongs to the player - Calm's call: "that should be up to the user to do to make sure
        /// they stay within the limits and use the right file type". A picture that does not fit
        /// is named in the picker and in the log, and the player decides what to do about it,
        /// which is also the only behaviour that cannot quietly change what their picture looks
        /// like.
        ///
        /// Everything in that folder was derived, so removing it loses nothing.
        /// </summary>
        private static void RemoveConvertedFolder(ICoreAPI api)
        {
            try
            {
                string dir = Path.Combine(GamePaths.DataPath, "ModData", "deskpictureframe", "converted");
                if (!Directory.Exists(dir)) return;

                int files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Length;
                Directory.Delete(dir, true);
                api?.Logger.Notification(
                    $"[DeskPictureFrame] Removed the old 'converted' folder ({files} file(s)) - " +
                    "pictures are used exactly as you supply them.");
            }
            catch (Exception ex)
            {
                api?.Logger.Warning($"[DeskPictureFrame] Could not remove the converted folder: {ex.Message}");
            }
        }

        public static void BuildClientCacheFromLocal(ICoreAPI api, string picturesRoot)
        {
            RemoveConvertedFolder(api);

            var ids = new Dictionary<string, string>();
            var sizes = new Dictionary<string, (int Width, int Height)>();
            var paths = new Dictionary<string, string>();
            var oversized = new List<string>();
            var toobig = new List<string>();

            try
            {
                Directory.CreateDirectory(Path.Combine(ClientCacheRoot(), "textures", "img"));
                Directory.CreateDirectory(picturesRoot);

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

                        // THE PIXEL CAP DOES NOT IMPLY THIS ONE. PNG compresses flat colour
                        // brilliantly and noise hardly at all, so the two limits are independent
                        // in both directions - measured 2026-09-04 on real files, a 2560x1369
                        // game screenshot was 798 KB while 1920x1080 generated artwork was
                        // 3.1 MB. A picture inside the pixel cap can be far over this one, and
                        // it must be refused HERE rather than at upload, or it reaches the picker
                        // and fails invisibly later.
                        long bytes = new FileInfo(file).Length;
                        if (bytes > MaxImageBytes)
                        {
                            toobig.Add($"{Path.GetFileName(file)}  {bytes / 1024f / 1024f:0.0} MB");
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

                        // For a converted picture this is the converted file, not the JPEG the
                        // player dropped in - it has to be the thing whose bytes hash to the id,
                        // since EnsureCached checks exactly that before using it.
                        paths[id] = file;

                        // NOTHING IS COPIED INTO THE CACHE HERE, and that is the point.
                        //
                        // This used to write every picture you own into the cache at startup, so
                        // a folder of 300 cost 300 cached copies whether or not a single frame
                        // used one. The cache is now a WORKING SET: a picture is cached the first
                        // time something actually needs to draw it, by EnsureCached below.
                        // Calm, 2026-09-04: "ideally the cache contains images seen when looking
                        // at a frame or in the area or if I have selected it".
                        //
                        // The read above is still needed - an id IS the hash of the bytes, so
                        // there is no way to know what a picture is called without reading it.
                    }
                    catch (Exception ex)
                    {
                        api.Logger.Warning($"[DeskPictureFrame] Could not read {file}: {ex.Message}");
                    }
                }

                api.Logger.Notification($"[DeskPictureFrame] Indexed {ids.Count} picture(s).");

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

                TooBigCount = toobig.Count;
                if (TooBigCount > 0)
                {
                    api.Logger.Warning(
                        $"[DeskPictureFrame] {TooBigCount} picture(s) are over 1 MB and were not " +
                        $"loaded: {string.Join(", ", toobig)}. Save them at a lower quality or a " +
                        "smaller size - the limit is on the FILE, not on the pixels.");
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
            LocalPaths = paths;
            indexedLongSide = MaxLongSide;
            indexedShortSide = MaxShortSide;
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
        /// <summary>True if the pixel caps have moved since the last index, so the picture
        /// list on screen no longer matches what will be accepted.</summary>
        public static bool IndexIsStale =>
            indexedLongSide != MaxLongSide || indexedShortSide != MaxShortSide;

        public static void Reindex(ICoreClientAPI capi)
        {
            BuildClientCacheFromLocal(capi, PicturesRoot());

            // Nothing is cached here: EnsureCached copies a picture in the moment something wants
            // to draw it, and a re-captured one is handled by RefreshLocalImage.
        }

        /// <summary>The id of this player's own picture for an image key, or null if absent.</summary>
        public static string LocalIdFor(string imageKey)
        {
            return imageKey != null && LocalIds.TryGetValue(imageKey, out string id) ? id : null;
        }
    }
}
