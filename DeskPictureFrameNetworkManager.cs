using System;
using System.Collections.Generic;
using System.IO;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace DeskPictureFrame
{
    public class DeskPictureFrameNetworkManager
    {
        private const string ChannelName = "deskpictureframe";

        // Server side
        private IServerNetworkChannel serverChannel;
        private Dictionary<string, Dictionary<string, byte[]>> serverImageCache = new();
        private string cacheFolder;
        private ICoreServerAPI serverApi;

        // Client side
        private IClientNetworkChannel clientChannel;
        private ICoreClientAPI clientApi;
        private Dictionary<string, Dictionary<string, byte[]>> receivedTextures = new();
        private HashSet<string> pendingRequests = new();
        private Dictionary<string, List<Action>> pendingCallbacks = new();

        public static DeskPictureFrameNetworkManager Instance { get; private set; }

        public DeskPictureFrameNetworkManager()
        {
            Instance = this;
        }

        public void InitServer(ICoreServerAPI sapi)
        {
            cacheFolder = DeskPictureFrameConstants.ServerCacheFolder;
            Directory.CreateDirectory(cacheFolder);

            LoadCacheFromDisk(sapi);

            serverChannel = sapi.Network.RegisterChannel(ChannelName)
                .RegisterMessageType<ImageUploadPacket>()
                .RegisterMessageType<ImageRequestPacket>()
                .RegisterMessageType<ImageResponsePacket>()
                .SetMessageHandler<ImageUploadPacket>(OnServerReceiveUpload)
                .SetMessageHandler<ImageRequestPacket>(OnServerReceiveRequest);
            serverApi = sapi;

            sapi.Logger.Notification("[DeskPictureFrame] Server network channel registered.");
        }

        public void InitClient(ICoreClientAPI capi)
        {
            clientApi = capi;

            clientChannel = capi.Network.RegisterChannel(ChannelName)
                .RegisterMessageType<ImageUploadPacket>()
                .RegisterMessageType<ImageRequestPacket>()
                .RegisterMessageType<ImageResponsePacket>()
                .SetMessageHandler<ImageResponsePacket>(OnClientReceiveImage);

            capi.Event.PlayerEntitySpawn += (p) =>
            {
                if (p.Entity.World.Side == EnumAppSide.Client)
                    UploadLocalImagesToServer(capi);
            };

            capi.Logger.Notification("[DeskPictureFrame] Client network channel registered.");
        }

        // CLIENT: Read local images and send to server
        private void UploadLocalImagesToServer(ICoreClientAPI capi)
        {
            string localFolder = Path.Combine(DeskPictureFrameConstants.ConfigFolder, "textures");
            if (!Directory.Exists(localFolder)) return;

            string[] imageFiles = Directory.GetFiles(localFolder, "*.png", SearchOption.AllDirectories);

            foreach (string file in imageFiles)
            {
                try
                {
                    string relative = Path.GetRelativePath(localFolder, file)
                        .Replace("\\", "/")
                        .Replace(".png", "");

                    byte[] data = File.ReadAllBytes(file);

                    if (data.Length > DeskPictureFrameConstants.MaxImageSizeBytes)
                    {
                        capi.Logger.Warning($"[DeskPictureFrame] Skipping {relative} - exceeds 1MB size limit.");
                        continue;
                    }

                    clientChannel.SendPacket(new ImageUploadPacket
                    {
                        PlayerUid = capi.World.Player.PlayerUID,
                        ImageKey = relative,
                        ImageData = data
                    });

                    capi.Logger.Notification($"[DeskPictureFrame] Uploaded image: {relative}");
                }
                catch (Exception ex)
                {
                    capi.Logger.Error($"[DeskPictureFrame] Failed to upload image {file}: {ex.Message}");
                }
            }
        }

        // SERVER: Receive and cache uploaded image
        private void OnServerReceiveUpload(IServerPlayer player, ImageUploadPacket packet)
        {
            if (!DeskPictureFrameConstants.IsValidImageKey(packet.ImageKey))
            {
                serverApi.Logger.Warning($"[DeskPictureFrame] Rejected suspicious image key from {player.PlayerName}: {packet.ImageKey}");
                return;
            }

            string fileName = Path.GetFileName(packet.ImageKey);
            if (!System.Text.RegularExpressions.Regex.IsMatch(fileName, @"^[1-5]$"))
            {
                serverApi.Logger.Warning($"[DeskPictureFrame] Rejected invalid image name from {player.PlayerName}: {packet.ImageKey}");
                return;
            }

            string folder = packet.ImageKey.Substring(0, packet.ImageKey.LastIndexOf('/'));
            if (!DeskPictureFrameConstants.IsValidImageFolder(folder))
            {
                serverApi.Logger.Warning($"[DeskPictureFrame] Rejected unknown folder from {player.PlayerName}: {packet.ImageKey}");
                return;
            }

            if (!DeskPictureFrameConstants.IsValidPngData(packet.ImageData))
            {
                serverApi.Logger.Warning($"[DeskPictureFrame] Rejected non-PNG data from {player.PlayerName}: {packet.ImageKey}");
                return;
            }

            if (packet.ImageData.Length > DeskPictureFrameConstants.MaxImageSizeBytes)
            {
                serverApi.Logger.Warning($"[DeskPictureFrame] Rejected oversized image from {player.PlayerName}: {packet.ImageKey}");
                return;
            }

            // Sanitise: max 80 images per player
            if (!serverImageCache.ContainsKey(packet.PlayerUid))
                serverImageCache[packet.PlayerUid] = new Dictionary<string, byte[]>();

            serverImageCache[packet.PlayerUid][packet.ImageKey] = packet.ImageData;
            SaveImageToDisk(packet.PlayerUid, packet.ImageKey, packet.ImageData);
        }

        // SERVER: Handle request from a client for another player's image
        private void OnServerReceiveRequest(IServerPlayer requestingPlayer, ImageRequestPacket packet)
        {
            if (serverImageCache.TryGetValue(packet.OwnerUid, out var images))
            {
                foreach (var kvp in images)
                {
                    serverChannel.SendPacket(new ImageResponsePacket
                    {
                        OwnerUid = packet.OwnerUid,
                        ImageKey = kvp.Key,
                        ImageData = kvp.Value
                    }, requestingPlayer);
                }
            }

        }

        // CLIENT: Receive another player's image and write to disk
        private void OnClientReceiveImage(ImageResponsePacket packet)
        {
            if (!DeskPictureFrameConstants.IsValidPngData(packet.ImageData))
            {
                clientApi?.Logger.Warning($"[DeskPictureFrame] Rejected invalid image data for: {packet.OwnerUid}/{packet.ImageKey}");
                return;
            }

            if (!DeskPictureFrameConstants.IsValidImageKey(packet.ImageKey))
            {
                clientApi?.Logger.Warning($"[DeskPictureFrame] Rejected suspicious image key: {packet.ImageKey}");
                return;
            }

            if (!receivedTextures.ContainsKey(packet.OwnerUid))
                receivedTextures[packet.OwnerUid] = new Dictionary<string, byte[]>();

            receivedTextures[packet.OwnerUid][packet.ImageKey] = packet.ImageData;
            pendingRequests.Remove(packet.OwnerUid);

                // Write to ModData
                try
                {
                    string remoteFolder = DeskPictureFrameConstants.RemotePlayerTexturesFolder(packet.OwnerUid);
                    string filePath = Path.Combine(remoteFolder, packet.ImageKey + ".png");
                    Directory.CreateDirectory(Path.GetDirectoryName(filePath));
                    File.WriteAllBytes(filePath, packet.ImageData);
                    clientApi?.Logger.Notification($"[DeskPictureFrame] Cached remote image: {packet.OwnerUid}/{packet.ImageKey}");
                }
                catch (Exception ex)
                {
                    clientApi?.Logger.Error($"[DeskPictureFrame] Failed to write remote image: {ex.Message}");
                }

                // Fire any waiting callbacks
                if (pendingCallbacks.TryGetValue(packet.OwnerUid, out var callbacks))
                {
                    foreach (var cb in callbacks)
                        cb?.Invoke();
                    pendingCallbacks.Remove(packet.OwnerUid);
                }
            }
        // CLIENT: Request textures for a frame owner

        public void RequestTexturesForFrame(string ownerUid, Action onReceived)
        {
            if (receivedTextures.ContainsKey(ownerUid))
            {
                onReceived?.Invoke();
                return;
            }

            string remoteFolder = DeskPictureFrameConstants.RemotePlayerTexturesFolder(ownerUid);
            if (Directory.Exists(remoteFolder))
            {
                clientApi?.Logger.Notification($"[DeskPictureFrame] Found disk cache for player: {ownerUid}");
                onReceived?.Invoke();
                return;
            }

            // Store callback to fire when images arrive
            if (!pendingCallbacks.ContainsKey(ownerUid))
                pendingCallbacks[ownerUid] = new List<Action>();

            if (onReceived != null)
                pendingCallbacks[ownerUid].Add(onReceived);

            if (pendingRequests.Contains(ownerUid)) return;

            pendingRequests.Add(ownerUid);

            clientChannel?.SendPacket(new ImageRequestPacket
            {
                OwnerUid = ownerUid
            });
        }

        // CLIENT: Check if we already have a received texture cached
        public bool TryGetReceivedTexture(string ownerUid, string imageKey, out byte[] data)
        {
            data = null;
            return receivedTextures.TryGetValue(ownerUid, out var images) &&
                   images.TryGetValue(imageKey, out data);
        }

        // DISK: Save image to ModData cache
        private void SaveImageToDisk(string playerUid, string imageKey, byte[] data)
        {
            try
            {
                string playerFolder = Path.Combine(cacheFolder, playerUid);
                string filePath = Path.Combine(playerFolder, imageKey.Replace("/", "_") + ".png");
                Directory.CreateDirectory(Path.GetDirectoryName(filePath));
                File.WriteAllBytes(filePath, data);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DeskPictureFrame] Failed to save image to disk: {ex.Message}");
            }
        }

        // DISK: Load all cached images on server start
        private void LoadCacheFromDisk(ICoreServerAPI sapi)
        {
            if (!Directory.Exists(cacheFolder)) return;

            foreach (string playerFolder in Directory.GetDirectories(cacheFolder))
            {
                string playerUid = Path.GetFileName(playerFolder);
                serverImageCache[playerUid] = new Dictionary<string, byte[]>();

                foreach (string file in Directory.GetFiles(playerFolder, "*.png"))
                {
                    string imageKey = Path.GetFileNameWithoutExtension(file).Replace("_", "/");
                    serverImageCache[playerUid][imageKey] = File.ReadAllBytes(file);
                }

                sapi.Logger.Notification($"[DeskPictureFrame] Loaded cached images for player: {playerUid}");
            }
        }
    }
}