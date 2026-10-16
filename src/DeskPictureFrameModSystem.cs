using System;
using System.IO;
using DPF;
using DeskPictureFrame.Handbook;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

namespace DeskPictureFrame
{
    public class DeskPictureFrame : ModSystem
    {
        private DeskPictureFrameNetworkManager networkManager;

        /// <summary>This machine's settings, from ModConfig/DeskPictureFrameConfig.json.
        /// Loaded in StartPre and not re-read afterwards - a limit that changed halfway through
        /// a session would leave an index built to the old one.</summary>
        public static DeskPictureFrameConfig Config { get; private set; } = new DeskPictureFrameConfig();

        public override double ExecuteOrder() => 0.0;

        // The EnableTraderSpawn config was removed in 1.0.2. It never worked: it looked up
        // "patches/dpftraderspawn.json" when the file is at "patches/dpf/dpftraderspawn.json",
        // and StartPre runs before assets are readable at all (VintagestoryAPI.xml says of
        // AssetsLoaded: "Asset files are now available to load using api.Assets.TryGet()").
        // TryGet returned null, the null-conditional assignment skipped silently, and the
        // trader spawned anyway with nothing in the log. Suppressing the caravan is now the
        // dependsOn gate on Calm's Collectibles in patches/dpf/dpftraderspawn.json, which does
        // work. An orphaned deskpictureframeconfig.json on an existing install is harmless.
        //
        // THAT IS NOT AN ARGUMENT AGAINST CONFIG IN StartPre, and 1.1.1 adds one. What failed
        // there was an ASSET lookup, which genuinely cannot happen this early. LoadModConfig is
        // plain file I/O and works fine - it has to be here, because the client index below
        // reads the limits it sets. The new file is DeskPictureFrameConfig.json, deliberately
        // not the old name.
        // The materials are read in AssetsFinalize, NOT AssetsLoaded: this mod system runs at ExecuteOrder 0.0, before the
        // game's JSON patch loader (0.05), so a patch on materials.json (Pewter's, patches/pewter.json) was never in the
        // file yet when it was read (Calm's log 2026-10-15: "9 frame material(s) listed" with Pewter installed). Nothing
        // uses the materials before the creative tab is built here; the client draws with them from LevelFinalize.
        public override void AssetsFinalize(ICoreAPI api)
        {
            base.AssetsFinalize(api);
            FrameMaterials.Load(api);
            FrameMaterials.FillCreativeTabs(api);
        }

        public override void Dispose()
        {
            HandbookGroups.Remove();
            base.Dispose();
        }

        public override void StartPre(ICoreAPI api)
        {
            // BOTH SIDES, and before anything reads a limit. In singleplayer they share the
            // process and read the same file, so the two loads agree by construction.
            Config = DeskPictureFrameConfig.LoadOrCreate(api);
            DeskPictureFrameImageStore.ApplyConfig(Config);

            if (api.Side == EnumAppSide.Client)
            {
                Directory.CreateDirectory(DeskPictureFrameImageStore.PicturesRoot());
                MigrateToFlatPictures(api);
                PopulateDefaults(api);
                api.Logger.Notification($"[DeskPictureFrame] Your pictures folder: {DeskPictureFrameImageStore.PicturesRoot()}");

                // THE PICTURE FOLDERS ARE NO LONGER ASSET ORIGINS (1.2.0). Until then the cache had
                // to be registered here, before assets load, or nothing in it resolved (proved
                // 2026-08-11) - because ARL drew the frames and only used pictures that were game
                // assets. The frames draw themselves now and put a picture's bytes straight into
                // the block atlas when one is first drawn (FrameMesher), so the cache is plain files
                // and nothing is scanned at load. The index is still built here: it reads the
                // limits set just above.
                DeskPictureFrameImageStore.BuildClientCacheFromLocal(api, DeskPictureFrameImageStore.PicturesRoot());
                DeskPictureFrameImageStore.TrimCache(api);
                api.Logger.Notification(
                    $"[DeskPictureFrame] Image cache ready: {DeskPictureFrameImageStore.ClientCacheRoot()}");
            }
        }

        public override void Start(ICoreAPI api)
        {
            base.Start(api);
            api.RegisterBlockClass("BlockDeskPictureFrame", typeof(BlockDeskPictureFrame));
            api.RegisterBlockEntityClass("BEDeskPictureFrame", typeof(BEDeskPictureFrame));
            api.RegisterBlockClass("DPFAutoBoxesFiller", typeof(BlockAutoBoxesFiller));
            api.RegisterBlockEntityClass("DPFAutoBoxesFiller", typeof(BEAutoBoxesFiller));


            ListenForConfigReloads(api);
        }

        /// <summary>
        /// Pick up config changes made in game through a config manager, without a restart.
        /// Integrated Mod Manager raises `imm.<modid>` after it rewrites the file; ConfigKit and
        /// ConfigLib raise `configkit:<modid>:config-saved` / `configlib:<modid>:config-saved`.
        /// All vanilla event-bus API, and an event nobody raises never fires, so this costs
        /// nothing when no manager is installed. Copied from Universal Display, where it was
        /// worked out the hard way on 2026-09-12.
        ///
        /// THE SERVER RELOAD IS DEFERRED 50 ms AND RE-ARMED ON EVERY EVENT. ConfigKit and
        /// ConfigLib forward the client's config-saved to the server BEFORE the server has
        /// written its own file, then raise it again after the write. Reloading on the first
        /// copy reads the old file; re-arming means the reload runs after the LAST event, which
        /// is the post-write one. Never fold the events into one pending reload - that swallowed
        /// the post-write copy and read stale values.
        ///
        /// The server side is the same code as `/dpf reload`, so a manager and the command
        /// cannot drift apart.
        /// </summary>
        private void ListenForConfigReloads(ICoreAPI api)
        {
            long pendingReload = 0;
            EventBusListenerDelegate onConfigChanged =
                delegate (string eventName, ref EnumHandling handling, IAttribute data)
                {
                    if (api is ICoreClientAPI capi) { ApplyClientConfigReload(capi); return; }
                    if (pendingReload != 0) api.Event.UnregisterCallback(pendingReload);
                    pendingReload = api.Event.RegisterCallback(
                        dt => { pendingReload = 0; ApplyServerConfigReload((ICoreServerAPI)api); }, 50);
                };
            api.Event.RegisterEventBusListener(onConfigChanged, 0.5, "imm.deskpictureframe");
            api.Event.RegisterEventBusListener(onConfigChanged, 0.5, "configkit:deskpictureframe:config-saved");
            api.Event.RegisterEventBusListener(onConfigChanged, 0.5, "configlib:deskpictureframe:config-saved");
        }

        /// <summary>
        /// After a reload that had to correct a value, ask the config manager on THIS side to
        /// re-read the file, so its panel shows the corrected number rather than the one typed.
        /// ConfigKit and ConfigLib each listen for their own `<manager>:config-reload` event
        /// carrying the domain and call ReadFromFile; IMM has no such event. Re-reading raises
        /// no config-saved, so this cannot loop back into our listener.
        ///
        /// WHAT THIS CANNOT REACH: on a dedicated server the admin's panel was filled from the
        /// client's copy of the server settings, and neither manager has a way to push a fresh
        /// copy to one client. The server, the file and every other player are right; the admin
        /// sees the corrected number when they reopen the panel or rejoin.
        /// </summary>
        private static void AskManagersToReread(ICoreAPI api)
        {
            if (!Config.WasClamped) return;

            var tree = new TreeAttribute();
            tree.SetString("domain", "deskpictureframe");
            api.Event.PushEvent("configkit:config-reload", tree);
            api.Event.PushEvent("configlib:config-reload", tree);
        }

        /// <summary>
        /// CLIENT: re-read the file and apply the two client settings and this machine's own
        /// caps, leaving a server's imposed caps alone. Re-indexes the picture list when the
        /// caps or MaxPictures moved, so the picker matches what will now be accepted.
        /// </summary>
        private static void ApplyClientConfigReload(ICoreClientAPI capi)
        {
            Config = DeskPictureFrameConfig.LoadOrCreate(capi);
            AskManagersToReread(capi);
            bool reindex = DeskPictureFrameImageStore.ApplyClientReload(Config);
            if (reindex) DeskPictureFrameImageStore.Reindex(capi);

            capi.Logger.Notification(
                "[DeskPictureFrame] Client: settings reloaded. MaxPictures={0} MaxCachedImages={1}, own caps {2}x{3}{4}",
                DeskPictureFrameImageStore.MaxPictures, DeskPictureFrameImageStore.MaxCachedImages,
                Config.Server.MaxLongSide, Config.Server.MaxShortSide,
                reindex ? " - picture list re-indexed" : "");
        }

        /// <summary>
        /// SERVER: re-read, clamp, apply, and tell everyone connected the new caps and quota.
        /// Shared by `/dpf reload` and the config-manager listener.
        /// </summary>
        private static void ApplyServerConfigReload(ICoreServerAPI api)
        {
            var was = (Config.Server.MaxLongSide, Config.Server.MaxShortSide,
                       Config.Server.MaxServerImages, Config.Server.MaxImagesPerPlayer);

            // LoadOrCreate clamps and writes back, so a nonsense value is corrected in the
            // file as well as in memory - the same path a restart would have taken.
            Config = DeskPictureFrameConfig.LoadOrCreate(api);
            AskManagersToReread(api);
            DeskPictureFrameImageStore.ApplyConfig(Config);

            var now = (Config.Server.MaxLongSide, Config.Server.MaxShortSide,
                       Config.Server.MaxServerImages, Config.Server.MaxImagesPerPlayer);

            // NOTHING TO SAY IF NOTHING CHANGED. The config managers forward a client's save to
            // the server whenever an ADMIN saves - their own client settings included - and the
            // server re-reads its file each time. Announcing "sent to everyone connected" for a
            // file that did not move read as the server settings changing when they had not.
            if (now == was)
            {
                api.Logger.VerboseDebug("[DeskPictureFrame] Server: config re-read, nothing changed.");
                return;
            }

            DeskPictureFrameNetworkManager.ServerInstance?.BroadcastLimits();
            DeskPictureFrameNetworkManager.ServerInstance?.BroadcastQuotaState();

            api.Logger.Notification(
                $"[DeskPictureFrame] Server: settings reloaded. Picture size {DeskPictureFrameImageStore.MaxLongSide}x" +
                $"{DeskPictureFrameImageStore.MaxShortSide}, store {DeskPictureFrameImageStore.MaxServerImages}, " +
                $"per player {Config.Server.MaxImagesPerPlayer} - sent to everyone connected.");
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

                    // ALL DIRECTORIES. Pictures live in a folder per player since 1.1.1, so a
                    // top-level listing would report an almost empty store.
                    string[] files = Directory.GetFiles(root, "*.png", SearchOption.AllDirectories);
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
                    var top = DeskPictureFrameQuota.Usage();
                    if (top.Count > 0)
                    {
                        msg += "\nMost of it from: ";
                        for (int i = 0; i < top.Count && i < 3; i++)
                        {
                            msg += (i > 0 ? ", " : "") + $"{top[i].Name} {top[i].Count}";
                        }
                        msg += "   (`/dpf quota` for all of them)";
                    }

                    msg += $"\n{root}" +
                           "\nPictures are shared: one file serves every frame using it, on any player's build. " +
                           "Deleting one blanks those frames, so remove files only if you know what they were.";

                    return TextCommandResult.Success(msg);
                })
                .EndSubCommand()

                .BeginSubCommand("quota")
                .WithDescription("Who is using the picture store, and how much of it")
                .HandleWith(args => QuotaReport(api))
                .BeginSubCommand("list")
                .WithDescription("The pictures one player has on this server")
                .WithArgs(api.ChatCommands.Parsers.Word("player"))
                .HandleWith(args => QuotaList(api, args[0] as string))
                .EndSubCommand()
                .BeginSubCommand("remove")
                .WithDescription("DELETE one picture by name, or 'all' - add 'confirm' to go through with it")
                .WithArgs(api.ChatCommands.Parsers.Word("player"),
                          api.ChatCommands.Parsers.Word("picture"),
                          api.ChatCommands.Parsers.OptionalWord("confirm"))
                .HandleWith(args => QuotaRemove(api, args[0] as string, args[1] as string,
                                                args[2] as string))
                .EndSubCommand()
                .EndSubCommand()

                .BeginSubCommand("config")
                .WithDescription("Settings from DeskPictureFrameConfig.json")
                .BeginSubCommand("reload")
                .WithDescription("Re-read the settings file and apply it without restarting")
                .HandleWith(args => ReloadConfig(api))
                .EndSubCommand()
                .EndSubCommand();
        }

        /// <summary>`/dpf quota` - who is using the store. A listing of the folders, which is
        /// exactly what the limit is enforced against, so what an admin sees cannot disagree
        /// with what the server does.</summary>
        private TextCommandResult QuotaReport(ICoreServerAPI api)
        {
            var usage = DeskPictureFrameQuota.Usage();
            int max = Config.Server.MaxImagesPerPlayer;

            if (usage.Count == 0) return TextCommandResult.Success("Nobody has added a picture yet.");

            var msg = new System.Text.StringBuilder($"Allowance {max} each:");
            foreach (var row in usage)
            {
                msg.Append($"\n  {row.Name}  {row.Count}/{max}   {row.Bytes / 1024f / 1024f:0.0} MB");
            }
            msg.Append("\nA player's pictures are the files in their own folder under " +
                       "ModData/deskpictureframe/images - delete one and they have room for another.");
            return TextCommandResult.Success(msg.ToString());
        }

        /// <summary>`/dpf quota list &lt;player&gt;` - what one player has. Names come from the
        /// sender's own folder and are a LABEL only; the id on the end of each file is the
        /// picture's identity, and anything sent before 1.1.1 has no label to show.</summary>
        private TextCommandResult QuotaList(ICoreServerAPI api, string name)
        {
            var rows = DeskPictureFrameQuota.PicturesOf(name);
            if (rows.Count == 0)
            {
                return TextCommandResult.Success(
                    $"{name} has no pictures on this server. Folder names are player names, and " +
                    "`/dpf quota` lists them.");
            }

            long total = 0;
            var msg = new System.Text.StringBuilder($"{name}: {rows.Count} picture(s)");
            foreach (var row in rows)
            {
                total += row.Bytes;
                msg.Append($"\n  {row.Label ?? row.Id}   {row.Bytes / 1024f:0} KB   " +
                           $"{row.When.ToLocalTime():yyyy-MM-dd}   {row.Id}");
            }
            msg.Append($"\n{total / 1024f / 1024f:0.0} MB in total.");
            return TextCommandResult.Success(msg.ToString());
        }

        /// <summary>
        /// `/dpf quota remove &lt;player&gt; &lt;picture|all&gt; confirm` - THE ONLY COMMAND THAT DELETES
        /// A PICTURE.
        ///
        /// **Named, because that is what actually gets asked for.** Calm, 2026-09-04: "someone
        /// could ask can you delete, say sunset, sky and moon images, so I can upload them".
        /// Three names, three commands - not "wipe everything they have ever sent". `all` is
        /// spelled out so it cannot be reached by leaving an argument off.
        ///
        /// A player can do this themselves from the picker now, so this is for the cases they
        /// cannot: someone who has left, or who has asked an admin to sort it out.
        ///
        /// Deleting is the ONLY way to give someone room, by design - the count is the folder,
        /// so nothing can hand allowance back without a file going. What it costs is real: every
        /// frame showing that picture goes blank, for everyone, for good.
        /// </summary>
        private TextCommandResult QuotaRemove(ICoreServerAPI api, string name, string picture,
                                              string confirm)
        {
            bool confirmed = string.Equals(confirm, "confirm", StringComparison.OrdinalIgnoreCase);
            bool all = string.Equals(picture, "all", StringComparison.OrdinalIgnoreCase);

            var rows = all ? DeskPictureFrameQuota.PicturesOf(name)
                           : DeskPictureFrameQuota.Find(name, picture);

            if (rows.Count == 0)
            {
                return TextCommandResult.Error(all
                    ? $"{name} has no pictures to remove."
                    : $"No picture called '{picture}' from {name}. `/dpf quota list {name}` " +
                      "shows what they have - anything sent before 1.1.1 has no name, so use " +
                      "its id from that listing.");
            }

            // Two pictures can share a label. Deleting whichever came first, silently, is the
            // worst thing an irreversible command could do - so it stops and asks for an id.
            if (!all && rows.Count > 1)
            {
                var amb = new System.Text.StringBuilder(
                    $"{rows.Count} of {name}'s pictures are called '{picture}'. Name the one you " +
                    "mean by its id:");
                foreach (var row in rows)
                {
                    amb.Append($"\n  {row.Id}   {row.Bytes / 1024f:0} KB   {row.When.ToLocalTime():yyyy-MM-dd}");
                }
                return TextCommandResult.Error(amb.ToString());
            }

            if (!confirmed)
            {
                long size = 0;
                foreach (var row in rows) size += row.Bytes;

                string what = all
                    ? $"ALL {rows.Count} of {name}'s pictures, {size / 1024f / 1024f:0.0} MB"
                    : $"'{rows[0].Label ?? rows[0].Id}' from {name}, {size / 1024f:0} KB";

                return TextCommandResult.Success(
                    $"This would DELETE {what}." +
                    "\nEvery frame using " + (all ? "them" : "it") +
                    " goes blank for everyone, permanently, and that cannot be undone." +
                    $"\nTo go ahead: `/dpf quota remove {name} {picture} confirm`");
            }

            int removed = 0;
            long bytes = 0;
            if (all)
            {
                (removed, bytes) = DeskPictureFrameQuota.RemoveAll(name);
            }
            else if (DeskPictureFrameQuota.Remove(name, rows[0].Id))
            {
                removed = 1;
                bytes = rows[0].Bytes;
            }

            // Their picker must stop saying they are full the moment they stop being full.
            DeskPictureFrameNetworkManager.ServerInstance?.BroadcastQuotaState();

            api.Logger.Notification(
                $"[DeskPictureFrame] Removed {removed} picture(s) from {name} " +
                $"({bytes / 1024f / 1024f:0.0} MB).");

            return TextCommandResult.Success(
                $"Deleted {removed} picture(s) from {name}, freeing " +
                (bytes >= 1024 * 1024 ? $"{bytes / 1024f / 1024f:0.0} MB" : $"{bytes / 1024f:0} KB") +
                $". They now have {DeskPictureFrameQuota.CountFor(name)} of {Config.Server.MaxImagesPerPlayer}.");
        }

        /// <summary>
        /// `/dpf config reload` — pick up an edited settings file on a running server.
        ///
        /// **The file stays the only place settings are written.** There is no command that
        /// sets a value, deliberately: two ways to change a setting means two things to keep
        /// in step, and the one that loses is always the file nobody looked at. Edit, reload.
        ///
        /// A file watcher was the other option and was not taken - it fires halfway through a
        /// save, reads a truncated file, and reports the failure to nobody. This says what it
        /// applied, to the person who asked for it.
        ///
        /// The picture size reaches connected players immediately, because clients already
        /// know how to be told (it is the join path). What CANNOT be applied live is
        /// MaxPictures: it decides an index built during StartPre, and this is a server-side
        /// command besides. It is reported rather than silently ignored.
        /// </summary>
        private TextCommandResult ReloadConfig(ICoreServerAPI api)
        {
            int wasLong = DeskPictureFrameImageStore.MaxLongSide;
            int wasShort = DeskPictureFrameImageStore.MaxShortSide;
            int wasStore = DeskPictureFrameImageStore.MaxServerImages;
            int wasPictures = DeskPictureFrameImageStore.MaxPictures;

            ApplyServerConfigReload(api);

            var msg = new System.Text.StringBuilder();
            msg.Append($"Picture size: {DeskPictureFrameImageStore.MaxLongSide}x{DeskPictureFrameImageStore.MaxShortSide}");
            if (wasLong != DeskPictureFrameImageStore.MaxLongSide
                || wasShort != DeskPictureFrameImageStore.MaxShortSide)
            {
                msg.Append($" (was {wasLong}x{wasShort}) - sent to everyone connected");
            }
            else msg.Append(" (unchanged)");

            msg.Append($"\nStore limit: {DeskPictureFrameImageStore.MaxServerImages} pictures");
            if (wasStore != DeskPictureFrameImageStore.MaxServerImages) msg.Append($" (was {wasStore})");
            else msg.Append(" (unchanged)");

            // Named only when it actually differs, so the usual reload stays two lines.
            if (wasPictures != DeskPictureFrameImageStore.MaxPictures)
            {
                msg.Append($"\nMaxPictures is now {DeskPictureFrameImageStore.MaxPictures}, but it is a " +
                           "client setting read at launch - it does nothing here and takes effect on a restart.");
            }

            // Both of these look like bugs from the outside if they are not said out loud.
            msg.Append("\nPictures already stored are kept and keep showing - a smaller size " +
                       "applies to what is uploaded from now on, and nothing is deleted.");

            return TextCommandResult.Success(msg.ToString());
        }

        public override void StartClientSide(ICoreClientAPI api)
        {
            DeskPictureFrameNetworkManager.ClearClient();

            // Back to this machine's own caps before joining anything. A server sends its own
            // on join and they hold for that session only - without this reset, a strict
            // server's numbers would follow the player into the next world and quietly hide
            // pictures on a machine that was never told why.
            DeskPictureFrameImageStore.ForgetServerLimits();
            DeskPictureFrameImageStore.ForgetReleased();   // per server, like the limits
            if (DeskPictureFrameImageStore.IndexIsStale) DeskPictureFrameImageStore.Reindex(api);

            // NO ORIGIN IS REGISTERED HERE. Registering one at this point is too late to be
            // scanned - see the note in StartPre. The content-addressed image cache is built
            // and registered there instead.

            networkManager = new DeskPictureFrameNetworkManager();
            networkManager.InitClient(api);

            // One handbook page per frame style, plus "Can be made with" (registered in FrameMaterials.FillCreativeTabs).
            // Tools/Materials Kit/src/HandbookGroups.cs - rerun install-kit.py --handbook-only to update it.
            HandbookGroups.Apply(api, "deskpictureframe.handbookgroups");

            // Frames draw themselves (FrameMesher) - per world, since the atlas is rebuilt.
            FrameMesher.Clear(api);
            api.Event.LevelFinalize += () => FrameMesher.PrepareMaterials(api);
            api.Event.LeaveWorld += () => FrameMesher.Clear(api);
        }
    }
}
