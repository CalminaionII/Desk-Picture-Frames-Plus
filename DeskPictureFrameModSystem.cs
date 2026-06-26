using System;
using System.IO;
using System.Reflection;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace DeskPictureFrame
{
    public class DeskPictureFrame : ModSystem
    {
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
                string configFolder = DeskPictureFrameConstants.ConfigFolder;
                Directory.CreateDirectory(configFolder);
                api.Assets.AddModOrigin(DeskPictureFrameConstants.ModId, configFolder);
                CreateFolderStructure(api, configFolder);
                PopulateDefaults(api, configFolder);
                api.Logger.Notification($"[DeskPictureFrame] Custom images folder ready: {configFolder}");
            }
        }

        private void CreateFolderStructure(ICoreAPI api, string configFolder)
        {
            foreach (var folder in DeskPictureFrameConstants.ImageFolders)
            {
                string relFolder = Path.Combine("textures", folder);
                string fullPath = Path.Combine(configFolder, relFolder);
                if (!Directory.Exists(fullPath))
                {
                    Directory.CreateDirectory(fullPath);
                    api.Logger.Notification($"[DeskPictureFrame] Created folder: {relFolder}");
                }
            }
        }

        private void PopulateDefaults(ICoreAPI api, string configFolder)
        {
            var assembly = Assembly.GetExecutingAssembly();
            int totalCopied = 0;

            foreach (var folder in DeskPictureFrameConstants.ImageFolders)
            {
                string targetDir = Path.Combine(configFolder, "textures", folder);
                Directory.CreateDirectory(targetDir);

                for (int i = 1; i <= DeskPictureFrameConstants.DefaultImageCount; i++)
                {
                    string destFile = Path.Combine(targetDir, $"{i}.png");
                    if (File.Exists(destFile)) continue;

                    string resourceName = $"DeskPictureFrame.Defaults.{i}.png";

                    try
                    {
                        using var stream = assembly.GetManifestResourceStream(resourceName);
                        if (stream == null) continue;

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
            string remotePlayersFolder = DeskPictureFrameConstants.RemotePlayersRoot;
            Directory.CreateDirectory(remotePlayersFolder);
            api.Assets.AddModOrigin(DeskPictureFrameConstants.ModId, remotePlayersFolder);
            api.Logger.Notification($"[DeskPictureFrame] Remote players cache folder ready: {remotePlayersFolder}");

            networkManager.InitClient(api);
        }
    }
}
