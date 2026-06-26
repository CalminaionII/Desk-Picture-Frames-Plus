using System;
using System.IO;
using System.Reflection;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace DeskPictureFrame
{
    public class DeskPictureFrame : ModSystem
    {
        private string configFolder;

        public override double ExecuteOrder() => 0.0;

        public override void StartPre(ICoreAPI api)
        {
            DeskPictureFrameModConfig.Load(api);

            if (!DeskPictureFrameModConfig.Current.EnableTraderSpawn)
            {
                api.Assets.TryGet("deskpictureframe:patches/dpftraderspawn.json")?.Data =
                    System.Text.Encoding.UTF8.GetBytes("[]");
            }

            if (api.Side == EnumAppSide.Client)
            {
                configFolder = Path.Combine(GamePaths.ModConfig, "deskpictureframe");
                try
                {
                    Directory.CreateDirectory(configFolder);
                }
                catch (Exception ex)
                {
                    api.Logger.Error($"[DeskPictureFrame] Failed to create config folder {configFolder}: {ex.Message}");
                    return;
                }
                api.Assets.AddModOrigin("deskpictureframe", configFolder);
                CreateFolderStructure(api);
                PopulateDefaults(api);
                api.Logger.Notification($"[DeskPictureFrame] Custom images folder ready: {configFolder}");
            }
        }
        private void CreateFolderStructure(ICoreAPI api)
        {
            string[] folders =
            {
                "textures/desk-landscape-images/image1",
                "textures/desk-landscape-images/image2",
                "textures/desk-landscape-single-images/image1",
                "textures/desk-portrait-images/image1",
                "textures/desk-portrait-images/image2",
                "textures/desk-portrait-images/image3",
                "textures/desk-portrait-single-images/image1",
                "textures/grouped-landscape-images/image1",
                "textures/grouped-landscape-images/image2",
                "textures/grouped-landscape-images/image3",
                "textures/grouped-portrait-images/image1",
                "textures/grouped-portrait-images/image2",
                "textures/grouped-portrait-images/image3",
                "textures/grouped-portrait-images/image4",
                "textures/wall-landscape-image/image",
                "textures/wall-portrait-image/image"
            };

            foreach (var relFolder in folders)
            {
                string fullPath = Path.Combine(configFolder, relFolder);
                if (!Directory.Exists(fullPath))
                {
                    try
                    {
                        Directory.CreateDirectory(fullPath);
                        api.Logger.Notification($"[DeskPictureFrame] Created folder: {relFolder}");
                    }
                    catch (Exception ex)
                    {
                        api.Logger.Error($"[DeskPictureFrame] Failed to create folder {relFolder}: {ex.Message}");
                    }
                }
            }
        }

        private void PopulateDefaults(ICoreAPI api)
        {
            var assembly = Assembly.GetExecutingAssembly();

            (string RelFolder, int Count)[] folderImageCounts = new[]
            {
                ("textures/desk-landscape-images/image1", 5),
                ("textures/desk-landscape-images/image2", 5),
                ("textures/desk-landscape-single-images/image1", 5),
                ("textures/desk-portrait-images/image1", 5),
                ("textures/desk-portrait-images/image2", 5),
                ("textures/desk-portrait-images/image3", 5),
                ("textures/desk-portrait-single-images/image1", 5),
                ("textures/grouped-landscape-images/image1", 5),
                ("textures/grouped-landscape-images/image2", 5),
                ("textures/grouped-landscape-images/image3", 5),
                ("textures/grouped-portrait-images/image1", 5),
                ("textures/grouped-portrait-images/image2", 5),
                ("textures/grouped-portrait-images/image3", 5),
                ("textures/grouped-portrait-images/image4", 5),
                ("textures/wall-landscape-image/image", 5),
                ("textures/wall-portrait-image/image", 5)
            };

            int totalCopied = 0;

            foreach (var item in folderImageCounts)
            {
                string targetDir = Path.Combine(configFolder, item.RelFolder);
                Directory.CreateDirectory(targetDir);

                for (int i = 1; i <= item.Count; i++)
                {
                    string destFile = Path.Combine(targetDir, $"{i}.png");
                    if (File.Exists(destFile)) continue;

                    string resourceName = $"DeskPictureFrame.Defaults.{i}.png";

                    try
                    {
                        using var stream = assembly.GetManifestResourceStream(resourceName);
                        if (stream == null)
                        {
                            api.Logger.Debug($"[DeskPictureFrame] Embedded resource not found: {resourceName}");
                            continue;
                        }

                        using var fileStream = File.Create(destFile);
                        stream.CopyTo(fileStream);
                        totalCopied++;
                    }
                    catch (Exception ex)
                    {
                        api.Logger.Error($"[DeskPictureFrame] Failed to extract image {i}: {ex.Message}");
                    }
                }
            }

            if (totalCopied > 0)
            {
                api.Logger.Notification($"[DeskPictureFrame] Extracted {totalCopied} default placeholder images.");
            }
        }

        private DeskPictureFrameNetworkManager networkManager;

        public override void Start(ICoreAPI api)
        {
            base.Start(api);
            api.RegisterBlockClass("BlockDeskPictureFrame", typeof(BlockDeskPictureFrame));
            api.RegisterBlockEntityClass("BEDeskPictureFrame", typeof(BEDeskPictureFrame));

            if (networkManager == null)
            {
                networkManager = new DeskPictureFrameNetworkManager();
                DeskPictureFrameNetworkManager.Instance = networkManager;
            }
        }

        public override void StartServerSide(ICoreServerAPI api)
        {
            networkManager.InitServer(api);
        }

        public override void StartClientSide(ICoreClientAPI api)
        {
            string remotePlayersFolder = Path.Combine(GamePaths.DataPath, "ModData", "deskpictureframe", "remoteplayers");
            try
            {
                Directory.CreateDirectory(remotePlayersFolder);
            }
            catch (Exception ex)
            {
                api.Logger.Error($"[DeskPictureFrame] Failed to create remote players folder {remotePlayersFolder}: {ex.Message}");
                return;
            }
            api.Assets.AddModOrigin("deskpictureframe", remotePlayersFolder);
            api.Logger.Notification($"[DeskPictureFrame] Remote players cache folder ready: {remotePlayersFolder}");

            networkManager.InitClient(api);
        }
    }
}