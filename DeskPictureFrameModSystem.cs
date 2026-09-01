using System;
using System.IO;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace DeskPictureFrame
{
    public class DeskPictureFrame : ModSystem
    {
        private DeskPictureFrameNetworkManager networkManager;

        public override double ExecuteOrder() => 0.0;

        // The EnableTraderSpawn config was removed in 1.0.2. It never worked: it looked up
        // "patches/dpftraderspawn.json" when the file is at "patches/dpf/dpftraderspawn.json",
        // and StartPre runs before assets are readable at all (VintagestoryAPI.xml says of
        // AssetsLoaded: "Asset files are now available to load using api.Assets.TryGet()").
        // TryGet returned null, the null-conditional assignment skipped silently, and the
        // trader spawned anyway with nothing in the log. Suppressing the caravan is now the
        // dependsOn gate on Calm's Collectibles in patches/dpf/dpftraderspawn.json, which does
        // work. An orphaned deskpictureframeconfig.json on an existing install is harmless.
        public override void StartPre(ICoreAPI api)
        {
            if (api.Side == EnumAppSide.Client)
            {
                Directory.CreateDirectory(DeskPictureFrameImageStore.PicturesRoot());
                MigrateToFlatPictures(api);
                api.Assets.AddModOrigin("deskpictureframe", DeskPictureFrameImageStore.UserImagesRoot());
                PopulateDefaults(api);
                api.Logger.Notification($"[DeskPictureFrame] Your pictures folder: {DeskPictureFrameImageStore.PicturesRoot()}");

                // THE IMAGE CACHE MUST BE BUILT AND REGISTERED HERE, NOT IN StartClientSide.
                //
                // An origin is scanned when assets load, and assets load once at game start -
                // after StartPre, before StartClientSide. Registering an origin any later means
                // it is never scanned, so nothing under it resolves, and a relog does not help
                // either because assets are not reloaded on a world join.
                //
                // Proved from the log, 2026-08-11: the ModConfig origin (StartPre, 04:19:37)
                // resolved; a second origin registered in StartClientSide (04:19:44) did not.
                // Same domain, same layout, seven seconds apart.
                //
                // Content addressing is what lets this happen here at all: an image id is a
                // hash of the bytes, so it needs no player identity - and a player's UID is not
                // known until they connect, far too late for this.
                DeskPictureFrameImageStore.BuildClientCacheFromLocal(api, DeskPictureFrameImageStore.PicturesRoot());
                api.Assets.AddModOrigin("deskpictureframe", DeskPictureFrameImageStore.ClientCacheRoot());
                api.Logger.Notification(
                    $"[DeskPictureFrame] Image cache ready: {DeskPictureFrameImageStore.ClientCacheRoot()}");
            }
        }

        public override void Start(ICoreAPI api)
        {
            base.Start(api);
            api.RegisterBlockClass("BlockDeskPictureFrame", typeof(BlockDeskPictureFrame));
            api.RegisterBlockEntityClass("BEDeskPictureFrame", typeof(BEDeskPictureFrame));
        }


        /// <summary>
        /// Brings an existing install's pictures into the single flat pictures folder, and
        /// records where each one went so frames placed before this still show the right thing.
        ///
        /// Handles every earlier layout:
        ///   ModConfig/deskpictureframe/textures/&lt;folder&gt;/&lt;n&gt;.png          (1.0.1 and earlier)
        ///   ModConfig/deskpictureframe/textures/local/&lt;folder&gt;/&lt;n&gt;.png    (interim only)
        ///   ModData/.../userimages/textures/&lt;folder&gt;/&lt;n&gt;.png             (interim only)
        ///
        /// A numbered file becomes "&lt;folder-with-dashes&gt;-&lt;n&gt;.png", so nothing collides and a
        /// player can still tell which picture was which. The old key is remembered against
        /// the new file's id, which is what lets an un-captured frame keep its picture.
        /// </summary>
        private void MigrateToFlatPictures(ICoreAPI api)
        {
            var legacy = new System.Collections.Generic.Dictionary<string, string>();
            string dest = DeskPictureFrameImageStore.PicturesRoot();
            Directory.CreateDirectory(dest);

            string[] roots =
            {
                Path.Combine(GamePaths.ModConfig, "deskpictureframe", "textures", "local"),
                Path.Combine(GamePaths.ModConfig, "deskpictureframe", "textures"),
                DeskPictureFrameImageStore.LocalRoot()
            };

            int moved = 0;
            foreach (string folder in DeskPictureFrameNetworkManager.ValidFolders)
            {
                string rel = folder.Replace("/", Path.DirectorySeparatorChar.ToString());
                string flatName = folder.Replace("/", "-");

                foreach (string root in roots)
                {
                    string from = Path.Combine(root, rel);
                    if (!Directory.Exists(from)) continue;

                    for (int n = 1; n <= 5; n++)
                    {
                        string src = Path.Combine(from, n + ".png");
                        if (!File.Exists(src)) continue;

                        string target = Path.Combine(dest, $"{flatName}-{n}.png");
                        try
                        {
                            if (!File.Exists(target))
                            {
                                File.Copy(src, target);
                                moved++;
                            }

                            // Remember the old slot key, so a frame that never captured an id
                            // still resolves to the picture it was placed with.
                            legacy[$"{folder}/{n}"] =
                                DeskPictureFrameImageStore.IdOf(File.ReadAllBytes(target));
                        }
                        catch (Exception ex)
                        {
                            api.Logger.Error($"[DeskPictureFrame] Could not migrate {folder}/{n}: {ex.Message}");
                        }
                    }

                    try { Directory.Delete(from, true); } catch (Exception) { }
                    break;
                }
            }

            DeskPictureFrameImageStore.SetLegacyIds(legacy);

            RemoveEmptyLegacyFolders(api, roots);
            RemoveObsoleteModDataFolders(api);

            if (moved > 0)
            {
                api.Logger.Notification(
                    $"[DeskPictureFrame] Moved {moved} picture(s) into one folder: {dest}. " +
                    "They are all together now with descriptive names - pick which one a frame " +
                    "shows in game instead of by folder and number.");
            }
        }

        /// <summary>
        /// Clears away the scaffolding a 1.0.1 install leaves behind.
        ///
        /// The migration above deletes each numbered folder as it empties it, but the tree
        /// those folders sat in survives - `ModConfig/deskpictureframe/textures`, and
        /// `ModConfig/deskpictureframe` itself - so a player who has updated is left with a
        /// directory that looks like it still matters and does not.
        ///
        /// **Only ever removes a directory that is completely empty.** Anything a player put
        /// there themselves, or any file this migration did not recognise, keeps its folder
        /// alive and nothing is touched. Deepest first, so a parent can go once its children
        /// have.
        /// </summary>
        private void RemoveEmptyLegacyFolders(ICoreAPI api, string[] roots)
        {
            var candidates = new System.Collections.Generic.List<string>();
            foreach (string root in roots) candidates.Add(root);
            candidates.Add(Path.Combine(GamePaths.ModConfig, "deskpictureframe"));

            int removed = 0;
            // Deepest path first: textures/local before textures before deskpictureframe.
            candidates.Sort((a, b) => b.Length.CompareTo(a.Length));

            foreach (string dir in candidates)
            {
                try
                {
                    if (!Directory.Exists(dir)) continue;
                    if (Directory.GetFileSystemEntries(dir).Length > 0) continue;

                    Directory.Delete(dir);
                    removed++;
                }
                catch (Exception ex)
                {
                    api.Logger.Warning($"[DeskPictureFrame] Could not remove the empty folder {dir}: {ex.Message}");
                }
            }

            if (removed > 0)
            {
                api.Logger.Notification(
                    $"[DeskPictureFrame] Tidied away {removed} empty folder(s) left over from an older version.");
            }
        }

        /// <summary>
        /// Removes the ModData folders the owner-based design left behind.
        ///
        ///   playercache/&lt;playerUid&gt;/&lt;folder&gt;_&lt;slot&gt;_&lt;n&gt;.png   images keyed by WHO owned them
        ///   remoteplayers/...                                    the same idea, registered as an origin
        ///
        /// Both are dead: pictures are keyed by a hash of their bytes now, and neither name
        /// appears anywhere else in this mod. They are also pure CACHE — every byte in them
        /// either sits in the player's own pictures folder or comes back from the server on
        /// request — so removing them cannot lose a picture, only a duplicate.
        ///
        /// Named explicitly rather than swept by pattern: a future folder that happens to be
        /// unrecognised must not be deleted by a rule written today.
        /// </summary>
        /// <summary>
        /// Makes empty "Landscape", "Portrait" and "Square" folders for the player to sort
        /// pictures into, beside the example picture.
        ///
        /// Nothing in the mod requires them. A picture works from anywhere under the pictures
        /// folder, and its shape is read from the file itself, never from where it sits - these
        /// exist because three named folders in an empty directory say what shape goes where
        /// without anyone reading a word of documentation.
        ///
        /// **Only ever on a first run**, guarded by the same "no pictures yet" test as the
        /// example picture. A player who deletes them has said what they want; recreating them
        /// every launch would be the mod arguing back.
        ///
        /// They stay invisible in the picker's folder chooser until they hold a picture, since
        /// that list is built from indexed pictures rather than from directories.
        /// </summary>
        private void CreateSuggestedFolders(ICoreAPI api)
        {
            foreach (string name in new[] { "Landscape", "Portrait", "Square" })
            {
                try
                {
                    string dir = Path.Combine(DeskPictureFrameImageStore.PicturesRoot(), name);
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                }
                catch (Exception ex)
                {
                    api.Logger.Warning($"[DeskPictureFrame] Could not create the {name} folder: {ex.Message}");
                }
            }
        }

        private void RemoveObsoleteModDataFolders(ICoreAPI api)
        {
            string root = Path.Combine(GamePaths.DataPath, "ModData", "deskpictureframe");

            foreach (string name in new[] { "playercache", "remoteplayers" })
            {
                string dir = Path.Combine(root, name);
                try
                {
                    if (!Directory.Exists(dir)) continue;

                    int files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Length;
                    Directory.Delete(dir, true);
                    api.Logger.Notification(
                        $"[DeskPictureFrame] Removed the old '{name}' folder ({files} file(s)) - " +
                        "pictures have been stored by content rather than by player since 1.1.0.");
                }
                catch (Exception ex)
                {
                    api.Logger.Warning($"[DeskPictureFrame] Could not remove {dir}: {ex.Message}");
                }
            }
        }

        /// <summary>Extracts one sample picture so the folder is not empty and players can
        /// see what belongs there. Five numbered defaults per folder made sense when the slot
        /// chose the picture; one does now.</summary>
        private void PopulateDefaults(ICoreAPI api)
        {
            // NO EXAMPLE PICTURE IS WRITTEN. A frame with nothing chosen already shows the
            // shipped placeholder, so an example file only added something for the player to
            // wonder about and delete. The folders below are the whole of the first-run setup.
            if (Directory.GetFiles(DeskPictureFrameImageStore.PicturesRoot(), "*.png",
                                   SearchOption.AllDirectories).Length > 0) return;

            CreateSuggestedFolders(api);
        }

        public override void StartServerSide(ICoreServerAPI api)
        {
            // Cleared per session, not in Dispose - a mod system's Dispose is not told which
            // side it is running for, and in singleplayer both sides share these statics
            // (shared reference sect.10c).
            DeskPictureFrameNetworkManager.ClearServer();

            networkManager = new DeskPictureFrameNetworkManager();
            networkManager.InitServer(api);

            RegisterAdminCommands(api);
        }

        /// <summary>
        /// `/dpf images` — what this mod is costing the server, in one line an admin can act on.
        ///
        /// **Read only, deliberately.** An image is only truly unused if no frame anywhere
        /// references it, and "anywhere" includes unloaded chunks, chests and stacks in
        /// inventories - none of which can be scanned reliably. So this reports and the admin
        /// decides; it never deletes. Requires the controlserver privilege.
        /// </summary>
        private void RegisterAdminCommands(ICoreServerAPI api)
        {
            api.ChatCommands
                .Create("dpf")
                .WithDescription("Desk Picture Frames Plus")
                .RequiresPrivilege(Privilege.controlserver)
                .BeginSubCommand("images")
                .WithDescription("How many pictures this server stores, and how much disk they use")
                .HandleWith(args =>
                {
                    string root = DeskPictureFrameImageStore.ServerStoreRoot();
                    if (!Directory.Exists(root))
                    {
                        return TextCommandResult.Success("No pictures are stored yet.");
                    }

                    string[] files = Directory.GetFiles(root, "*.png");
                    long total = 0;
                    long biggest = 0;
                    string biggestName = null;

                    foreach (string file in files)
                    {
                        var info = new FileInfo(file);
                        total += info.Length;
                        if (info.Length > biggest)
                        {
                            biggest = info.Length;
                            biggestName = info.Name;
                        }
                    }

                    string msg =
                        $"{files.Length} picture(s) of a possible {DeskPictureFrameImageStore.MaxServerImages}, " +
                        $"{total / 1024f / 1024f:0.0} MB total";
                    if (files.Length > 0)
                    {
                        msg += $", average {total / files.Length / 1024f:0} KB, " +
                               $"largest {biggestName} at {biggest / 1024f:0} KB";
                    }
                    msg += $"\n{root}" +
                           "\nPictures are shared: one file serves every frame using it, on any player's build. " +
                           "Deleting one blanks those frames, so remove files only if you know what they were.";

                    return TextCommandResult.Success(msg);
                })
                .EndSubCommand();
        }

        public override void StartClientSide(ICoreClientAPI api)
        {
            DeskPictureFrameNetworkManager.ClearClient();

            // NO ORIGIN IS REGISTERED HERE. Registering one at this point is too late to be
            // scanned - see the note in StartPre. The content-addressed image cache is built
            // and registered there instead.

            networkManager = new DeskPictureFrameNetworkManager();
            networkManager.InitClient(api);
        }
    }
}
