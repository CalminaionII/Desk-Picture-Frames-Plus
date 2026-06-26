using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
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

        private static readonly Regex SafeUidRegex = new Regex(@"^[a-zA-Z0-9_-]+$", RegexOptions.Compiled);

        private static readonly string[] ValidImageFolders = {
            "desk-landscape-images/image1",
            "desk-landscape-images/image2",
            "desk-landscape-single-images/image1",
            "desk-portrait-images/image1",
            "desk-portrait-images/image2",
            "desk-portrait-images/image3",
            "desk-portrait-single-images/image1",
            "grouped-landscape-images/image1",
            "grouped-landscape-images/image2",
            "grouped-landscape-images/image3",
            "grouped-portrait-images/image1",
            "grouped-portrait-images/image2",
            "grouped-portrait-images/image3",
            "grouped-portrait-images/image4",
            "wall-landscape-image/image",
            "wall-portrait-image/image"
        };

        private static bool IsValidUid(string uid)
        {
            return !string.IsNullOrEmpty(uid) && SafeUidRegex.IsMatch(uid);
        }

        private static bool IsValidImageKey(string imageKey)
        {
            if (string.IsNullOrEmpty(imageKey) || imageKey.Contains("..") || Path.IsPathRooted(imageKey))
                return false;

            string fileName = Path.GetFileName(imageKey);
            if (!Regex.IsMatch(fileName, @"^[1-5]$"))
                return false;

            int lastSlash = imageKey.LastIndexOf('/');
            if (lastSlash < 0) return false;
            string folder = imageKey.Substring(0, lastSlash);

            foreach (var f in ValidImageFolders)
                if (folder == f) return true;

            return false;
        }

        public void InitServer(ICoreServerAPI sapi)
        {
            cacheFolder = Path.Combine(GamePaths.DataPath, "ModData", "deskpictureframe", "playercache");
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
            string localFolder = Path.Combine(GamePaths.ModConfig, "deskpictureframe", "textures");
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

                    if (data.Length > 1_000_000)
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

            if (!IsValidImageKey(packet.ImageKey))
            {
                serverApi.Logger.Warning($"[DeskPictureFrame] Rejected invalid image key from {player.PlayerName}: {packet.ImageKey}");
                return;
            }

            if (packet.ImageData == null || packet.ImageData.Length < 8 ||
                packet.ImageData[0] != 0x89 || packet.ImageData[1] != 0x50 ||
                packet.ImageData[2] != 0x4E || packet.ImageData[3] != 0x47)
            {
                serverApi.Logger.Warning($"[DeskPictureFrame] Rejected non-PNG data from {player.PlayerName}: {packet.ImageKey}");
                return;
            }

            if (packet.ImageData.Length > 1_000_000)
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
            if (!IsValidUid(packet.OwnerUid))
            {
                serverApi.Logger.Warning($"[DeskPictureFrame] Rejected invalid owner UID request from {requestingPlayer.PlayerName}");
                return;
            }

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
            if (!IsValidUid(packet.OwnerUid))
            {
                clientApi?.Logger.Warning($"[DeskPictureFrame] Rejected invalid owner UID in response.");
                return;
            }

            if (!IsValidImageKey(packet.ImageKey))
            {
                clientApi?.Logger.Warning($"[DeskPictureFrame] Rejected invalid image key: {packet.ImageKey}");
                return;
            }

            if (packet.ImageData == null || packet.ImageData.Length < 8 ||
                packet.ImageData[0] != 0x89 || packet.ImageData[1] != 0x50 ||
                packet.ImageData[2] != 0x4E || packet.ImageData[3] != 0x47)
            {
                clientApi?.Logger.Warning($"[DeskPictureFrame] Rejected invalid image data for: {packet.OwnerUid}/{packet.ImageKey}");
                return;
            }

            if (packet.ImageData.Length > 1_000_000)
            {
                clientApi?.Logger.Warning($"[DeskPictureFrame] Rejected oversized image from server: {packet.OwnerUid}/{packet.ImageKey}");
                return;
            }

            var playerTextures = receivedTextures.GetOrAdd(packet.OwnerUid, _ => new ConcurrentDictionary<string, byte[]>());
            playerTextures[packet.ImageKey] = packet.ImageData;

            // Write to ModData
            try
            {
                string remoteFolder = Path.Combine(GamePaths.DataPath, "ModData", "deskpictureframe", "remoteplayers", packet.OwnerUid, "textures");
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
            if (!IsValidUid(ownerUid))
            {
                clientApi?.Logger.Warning($"[DeskPictureFrame] Rejected invalid owner UID in texture request.");
                return;
            }

            if (receivedTextures.ContainsKey(ownerUid))
            {
                onReceived?.Invoke();
                return;
            }

            string remoteFolder = Path.Combine(GamePaths.DataPath, "ModData", "deskpictureframe", "remoteplayers", ownerUid, "textures");
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
            if (!IsValidUid(playerUid) || !IsValidImageKey(imageKey)) return;

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
                if (!IsValidUid(playerUid)) continue;

                var playerImages = new ConcurrentDictionary<string, byte[]>();

                foreach (string file in Directory.GetFiles(playerFolder, "*.png"))
                {
                    string imageKey = Path.GetFileNameWithoutExtension(file).Replace("_", "/");
                    if (!IsValidImageKey(imageKey)) continue;

                    playerImages[imageKey] = File.ReadAllBytes(file);
                }

                serverImageCache[playerUid] = playerImages;
                sapi.Logger.Notification($"[DeskPictureFrame] Loaded cached images for player: {playerUid}");
            }
        }
    }
}
