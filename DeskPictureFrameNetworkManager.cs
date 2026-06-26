using System;
using System.Collections.Concurrent;
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
        private const int MaxImagesPerPlayer = 80;

        // Server side
        private IServerNetworkChannel serverChannel;
        private ConcurrentDictionary<string, ConcurrentDictionary<string, byte[]>> serverImageCache = new();
        private string cacheFolder;
        private ICoreServerAPI serverApi;

        // Client side
        private IClientNetworkChannel clientChannel;
        private ICoreClientAPI clientApi;
        private ConcurrentDictionary<string, ConcurrentDictionary<string, byte[]>> receivedTextures = new();
        private ConcurrentDictionary<string, byte> pendingRequests = new();
        private readonly object callbackLock = new();
        private Dictionary<string, List<Action>> pendingCallbacks = new();

        public static DeskPictureFrameNetworkManager Instance { get; set; }

        public void InitServer(ICoreServerAPI sapi)
        {
            cacheFolder = DeskPictureFrameConstants.ServerCacheFolder;
            Directory.CreateDirectory(cacheFolder);

            LoadCacheFromDisk(sapi);

            serverChannel = sapi.Network.RegisterChannel(ChannelName)
                .RegisterMessageType<ImageUploadPacket>()
                .RegisterMessageType<ImageRequestPacket>()
                .RegisterMessageType<ImageResponsePacket>()
                .RegisterMessageType<ImageTransferCompletePacket>()
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
                .RegisterMessageType<ImageTransferCompletePacket>()
                .SetMessageHandler<ImageResponsePacket>(OnClientReceiveImage)
                .SetMessageHandler<ImageTransferCompletePacket>(OnClientTransferComplete);

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
            string playerUid = player.PlayerUID;

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

            // Enforce max images per player
            var playerImages = serverImageCache.GetOrAdd(playerUid, _ => new ConcurrentDictionary<string, byte[]>());

            if (playerImages.Count >= MaxImagesPerPlayer && !playerImages.ContainsKey(packet.ImageKey))
            {
                serverApi.Logger.Warning($"[DeskPictureFrame] Player {player.PlayerName} exceeded {MaxImagesPerPlayer} image limit.");
                return;
            }

            playerImages[packet.ImageKey] = packet.ImageData;
            SaveImageToDisk(playerUid, packet.ImageKey, packet.ImageData);
        }

        // SERVER: Handle request from a client for another player's image
        private void OnServerReceiveRequest(IServerPlayer requestingPlayer, ImageRequestPacket packet)
        {
            int imageCount = 0;

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
                    imageCount++;
                }
            }

            serverChannel.SendPacket(new ImageTransferCompletePacket
            {
                OwnerUid = packet.OwnerUid,
                ImageCount = imageCount
            }, requestingPlayer);
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

            var playerTextures = receivedTextures.GetOrAdd(packet.OwnerUid, _ => new ConcurrentDictionary<string, byte[]>());
            playerTextures[packet.ImageKey] = packet.ImageData;

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
        }

        // CLIENT: Handle transfer-complete sentinel from server
        private void OnClientTransferComplete(ImageTransferCompletePacket packet)
        {
            pendingRequests.TryRemove(packet.OwnerUid, out _);

            lock (callbackLock)
            {
                if (pendingCallbacks.TryGetValue(packet.OwnerUid, out var callbacks))
                {
                    foreach (var cb in callbacks)
                        cb?.Invoke();
                    pendingCallbacks.Remove(packet.OwnerUid);
                }
            }

            clientApi?.Logger.Notification($"[DeskPictureFrame] Transfer complete for {packet.OwnerUid}: {packet.ImageCount} images.");
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

            // Store callback to fire when transfer completes
            if (onReceived != null)
            {
                lock (callbackLock)
                {
                    if (!pendingCallbacks.ContainsKey(ownerUid))
                        pendingCallbacks[ownerUid] = new List<Action>();
                    pendingCallbacks[ownerUid].Add(onReceived);
                }
            }

            if (!pendingRequests.TryAdd(ownerUid, 0)) return;

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
                serverApi?.Logger.Error($"[DeskPictureFrame] Failed to save image to disk: {ex.Message}");
            }
        }

        // DISK: Load all cached images on server start
        private void LoadCacheFromDisk(ICoreServerAPI sapi)
        {
            if (!Directory.Exists(cacheFolder)) return;

            foreach (string playerFolder in Directory.GetDirectories(cacheFolder))
            {
                string playerUid = Path.GetFileName(playerFolder);
                var playerImages = new ConcurrentDictionary<string, byte[]>();

                foreach (string file in Directory.GetFiles(playerFolder, "*.png"))
                {
                    string imageKey = Path.GetFileNameWithoutExtension(file).Replace("_", "/");
                    playerImages[imageKey] = File.ReadAllBytes(file);
                }

                serverImageCache[playerUid] = playerImages;
                sapi.Logger.Notification($"[DeskPictureFrame] Loaded cached images for player: {playerUid}");
            }
        }
    }
}
