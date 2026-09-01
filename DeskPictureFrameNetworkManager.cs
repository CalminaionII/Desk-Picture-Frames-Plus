using System;
using System.Collections.Generic;
using System.IO;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace DeskPictureFrame
{
    /// <summary>
    /// Moves pictures between clients, keyed by CONTENT rather than by player.
    ///
    /// The exchange is deliberately lazy in both directions:
    ///
    /// * A client announces an image only when it actually PLACES a frame using it, and the
    ///   server replies with just the ids it lacks. So a picture is uploaded once ever, by
    ///   whoever first used it. The old design re-read and re-sent all 80 files every time any
    ///   player spawned into view.
    /// * A client requests an image only when it can SEE a frame using it.
    ///
    /// There is no "who owns this" anywhere in the protocol, and that is what removes a whole
    /// class of bug: the server stores bytes under the id it computes for them itself, so a
    /// client cannot claim an identity it does not have the data for.
    /// </summary>
    public class DeskPictureFrameNetworkManager
    {
        private const string ChannelName = "deskpictureframe";
        private const int MaxImageBytes = 1_000_000;

        /// <summary>Most images one player may upload in a session.
        ///
        /// Nothing else bounds a client: it can announce any ids it likes and the server will
        /// ask for them. Content addressing stops anyone overwriting someone else's picture,
        /// but not from filling the disk. Well above any honest use of the mod.</summary>
        private const int MaxUploadsPerPlayer = 300;

        /// <summary>Per session deliberately - it resets on restart, and a player who genuinely
        /// reaches it is doing something no normal use of the mod requires.</summary>
        private readonly Dictionary<string, int> uploadsThisSession = new();

        private IServerNetworkChannel serverChannel;
        private ICoreServerAPI serverApi;

        private IClientNetworkChannel clientChannel;
        private ICoreClientAPI clientApi;

        /// <summary>Ids we have asked the server for and not yet heard back about, with the
        /// callbacks waiting on each. Keyed so several frames sharing a picture make one request.</summary>
        private readonly Dictionary<string, List<Action>> pendingRequests = new();

        /// <summary>Ids the server has told us it does not have either, so we stop asking.</summary>
        private readonly HashSet<string> unavailable = new();

        // SIDE-SPECIFIC, and cleared at the start of every session. A single static assigned in
        // the constructor was wrong twice over: both sides construct one and in singleplayer
        // they share the process, so the last one won; and nothing ever cleared it, so a second
        // world inherited the first world's channels (shared reference sect.10c).
        public static DeskPictureFrameNetworkManager ClientInstance { get; private set; }
        public static DeskPictureFrameNetworkManager ServerInstance { get; private set; }

        public static void ClearClient() => ClientInstance = null;
        public static void ClearServer() => ServerInstance = null;

        public void InitServer(ICoreServerAPI sapi)
        {
            serverApi = sapi;
            ServerInstance = this;

            Directory.CreateDirectory(DeskPictureFrameImageStore.ServerStoreRoot());

            serverChannel = sapi.Network.RegisterChannel(ChannelName)
                .RegisterMessageType<ImageAnnouncePacket>()
                .RegisterMessageType<ImageNeedPacket>()
                .RegisterMessageType<ImageUploadPacket>()
                .RegisterMessageType<ImageRequestPacket>()
                .RegisterMessageType<ImageResponsePacket>()
                .RegisterMessageType<FrameImagesPacket>()
                .SetMessageHandler<ImageAnnouncePacket>(OnServerAnnounce)
                .SetMessageHandler<ImageUploadPacket>(OnServerUpload)
                .SetMessageHandler<ImageRequestPacket>(OnServerRequest)
                .SetMessageHandler<FrameImagesPacket>(OnServerFrameImages);

            sapi.Logger.Notification("[DeskPictureFrame] Server image store: "
                                     + DeskPictureFrameImageStore.ServerStoreRoot());
        }

        public void InitClient(ICoreClientAPI capi)
        {
            clientApi = capi;
            ClientInstance = this;

            clientChannel = capi.Network.RegisterChannel(ChannelName)
                .RegisterMessageType<ImageAnnouncePacket>()
                .RegisterMessageType<ImageNeedPacket>()
                .RegisterMessageType<ImageUploadPacket>()
                .RegisterMessageType<ImageRequestPacket>()
                .RegisterMessageType<ImageResponsePacket>()
                .RegisterMessageType<FrameImagesPacket>()
                .SetMessageHandler<ImageNeedPacket>(OnClientNeed)
                .SetMessageHandler<ImageResponsePacket>(OnClientImage);

            capi.Logger.Notification("[DeskPictureFrame] Client network channel registered.");
        }

        // ---- placing and re-capturing ------------------------------------------------------

        /// <summary>
        /// CLIENT: tells the server which pictures a frame was just placed or re-captured with.
        ///
        /// Capture can only happen here, because only this client can read its own image
        /// folder. The server persists the ids into the block entity and syncs them onward.
        /// </summary>
        public void SendFrameImages(BlockPos pos, Dictionary<string, string> ids)
        {
            if (clientChannel == null || pos == null || ids == null || ids.Count == 0) return;

            var codes = new List<string>(ids.Count);
            var values = new List<string>(ids.Count);
            foreach (var pair in ids)
            {
                codes.Add(pair.Key);
                values.Add(pair.Value);
            }

            clientChannel.SendPacket(new FrameImagesPacket
            {
                X = pos.X, Y = pos.Y, Z = pos.Z,
                Codes = codes.ToArray(),
                Ids = values.ToArray()
            });

            clientChannel.SendPacket(new ImageAnnouncePacket { Ids = values.ToArray() });
        }

        private void OnServerFrameImages(IServerPlayer player, FrameImagesPacket packet)
        {
            if (packet?.Codes == null || packet.Ids == null
                || packet.Codes.Length != packet.Ids.Length) return;

            var pos = new BlockPos(packet.X, packet.Y, packet.Z);

            // THE permission gate. The picker is only a window - what actually changes a frame
            // is this packet, and a client can send it without ever opening one. So the claim
            // is tested here, on the server, against the player who really sent it.
            // TryAccess also tells the player why nothing happened.
            if (!serverApi.World.Claims.TryAccess(player, pos, EnumBlockAccessFlags.Use)) return;

            if (serverApi.World.BlockAccessor.GetBlockEntity(pos) is not BEDeskPictureFrame be) return;

            var ids = new Dictionary<string, string>();
            for (int i = 0; i < packet.Codes.Length; i++)
            {
                if (DeskPictureFrameImageStore.IsWellFormedId(packet.Ids[i]))
                    ids[packet.Codes[i]] = packet.Ids[i];
            }

            be.SetImageIds(ids);
        }

        private void OnServerAnnounce(IServerPlayer player, ImageAnnouncePacket packet)
        {
            if (packet?.Ids == null) return;

            var wanted = new List<string>();
            foreach (string id in packet.Ids)
            {
                if (!DeskPictureFrameImageStore.IsWellFormedId(id)) continue;
                if (DeskPictureFrameImageStore.ServerHas(id)) continue;
                if (!wanted.Contains(id)) wanted.Add(id);
            }

            if (wanted.Count == 0) return;

            serverChannel.SendPacket(new ImageNeedPacket { Ids = wanted.ToArray() }, player);
        }

        /// <summary>CLIENT: the server asked for images it does not have. Send only those.</summary>
        private void OnClientNeed(ImageNeedPacket packet)
        {
            if (packet?.Ids == null) return;

            foreach (string id in packet.Ids)
            {
                if (!DeskPictureFrameImageStore.IsWellFormedId(id)) continue;

                try
                {
                    string path = DeskPictureFrameImageStore.ClientImagePath(id);
                    if (!File.Exists(path)) continue;

                    // Dimensions BEFORE the bytes go anywhere: reading 24 bytes of header beats
                    // sending a megabyte the server is only going to refuse.
                    if (!DeskPictureFrameImageStore.TryReadPngSize(path, out int w, out int h)
                        || !DeskPictureFrameImageStore.WithinSizeLimit(w, h))
                    {
                        clientApi?.Logger.Warning(
                            $"[DeskPictureFrame] Not sending {id} - {w}x{h}, over the " +
                            $"{DeskPictureFrameImageStore.MaxLongSide}x{DeskPictureFrameImageStore.MaxShortSide} limit.");
                        continue;
                    }

                    byte[] data = File.ReadAllBytes(path);
                    if (data.Length > MaxImageBytes)
                    {
                        clientApi?.Logger.Warning(
                            $"[DeskPictureFrame] Not sending {id} - over the 1MB limit.");
                        continue;
                    }

                    clientChannel.SendPacket(new ImageUploadPacket { Id = id, Data = data });
                }
                catch (Exception ex)
                {
                    clientApi?.Logger.Error($"[DeskPictureFrame] Could not send image {id}: {ex.Message}");
                }
            }
        }

        private void OnServerUpload(IServerPlayer player, ImageUploadPacket packet)
        {
            if (packet?.Data == null) return;

            uploadsThisSession.TryGetValue(player.PlayerUID, out int sent);
            if (sent >= MaxUploadsPerPlayer)
            {
                serverApi.Logger.Warning(
                    $"[DeskPictureFrame] {player.PlayerName} has uploaded {sent} images this " +
                    "session; ignoring further ones.");
                return;
            }

            // Backstop. The client checks dimensions before sending, but a modified one need
            // not - and an oversized picture costs every player in view, not just its owner.
            if (!DeskPictureFrameImageStore.TryReadPngSize(packet.Data, out int width, out int height)
                || !DeskPictureFrameImageStore.WithinSizeLimit(width, height))
            {
                serverApi.Logger.Warning(
                    $"[DeskPictureFrame] Rejected an image from {player.PlayerName}: {width}x{height}, max " +
                    $"{DeskPictureFrameImageStore.MaxLongSide}x{DeskPictureFrameImageStore.MaxShortSide} " +
                    $"(or {DeskPictureFrameImageStore.MaxShortSide}x{DeskPictureFrameImageStore.MaxLongSide} upright).");
                return;
            }

            if (packet.Data.Length > MaxImageBytes)
            {
                serverApi.Logger.Warning(
                    $"[DeskPictureFrame] Rejected oversized image from {player.PlayerName}.");
                return;
            }

            if (!LooksLikePng(packet.Data))
            {
                serverApi.Logger.Warning(
                    $"[DeskPictureFrame] Rejected non-PNG data from {player.PlayerName}.");
                return;
            }

            // Stored under the id the BYTES hash to, not the one claimed. A mismatch is
            // rejected outright, so an identity cannot be asserted without the data behind it -
            // which is why this protocol needs no notion of who owns what.
            if (DeskPictureFrameImageStore.ServerIsFull())
            {
                serverApi.Logger.Warning(
                    "[DeskPictureFrame] Image store is full (" +
                    DeskPictureFrameImageStore.MaxServerImages + " images); not storing more.");
                return;
            }

            string stored = DeskPictureFrameImageStore.WriteServer(packet.Data, packet.Id);
            if (stored == null)
            {
                serverApi.Logger.Warning(
                    $"[DeskPictureFrame] Rejected image from {player.PlayerName}: content does " +
                    $"not match id {packet.Id}, or the store is full.");
                return;
            }

            uploadsThisSession[player.PlayerUID] = sent + 1;
            serverApi.Logger.Notification($"[DeskPictureFrame] Stored image {stored}.");
        }

        // ---- viewing -----------------------------------------------------------------------

        private void OnServerRequest(IServerPlayer player, ImageRequestPacket packet)
        {
            if (packet?.Id == null || !DeskPictureFrameImageStore.IsWellFormedId(packet.Id)) return;

            // Always answer, even with nothing, so a waiting client stops waiting instead of
            // sitting on the placeholder for the rest of the session.
            serverChannel.SendPacket(new ImageResponsePacket
            {
                Id = packet.Id,
                Data = DeskPictureFrameImageStore.ReadServer(packet.Id)
            }, player);
        }

        /// <summary>
        /// CLIENT: ask for a picture we can see but do not have. The callback runs once the
        /// bytes are on disk - or immediately, if we already have them.
        /// </summary>
        public void RequestImage(string id, Action onArrived)
        {
            if (!DeskPictureFrameImageStore.IsWellFormedId(id)) return;

            if (DeskPictureFrameImageStore.ClientHas(id))
            {
                onArrived?.Invoke();
                return;
            }

            if (unavailable.Contains(id)) return;

            if (!pendingRequests.TryGetValue(id, out var waiting))
            {
                pendingRequests[id] = waiting = new List<Action>();
                clientChannel?.SendPacket(new ImageRequestPacket { Id = id });
            }

            if (onArrived != null) waiting.Add(onArrived);
        }

        private void OnClientImage(ImageResponsePacket packet)
        {
            if (packet?.Id == null) return;

            bool ok = false;

            if (packet.Data == null)
            {
                unavailable.Add(packet.Id);
                clientApi?.Logger.Notification(
                    $"[DeskPictureFrame] Server has no image {packet.Id}; frames using it stay on the placeholder.");
            }
            else if (!LooksLikePng(packet.Data) || !DeskPictureFrameImageStore.Verify(packet.Id, packet.Data))
            {
                // The id is a hash of the content, so this is checkable rather than trusted.
                clientApi?.Logger.Warning(
                    $"[DeskPictureFrame] Discarded image {packet.Id} - content does not match its id.");
            }
            else
            {
                ok = DeskPictureFrameImageStore.WriteClient(packet.Id, packet.Data);

                // Written to disk for NEXT session; registered as a runtime asset so it is
                // usable in THIS one. Without the second step the picture would not appear
                // until the game was restarted, which is the whole point of doing it here.
                DeskPictureFrameImageStore.RegisterRuntime(clientApi, packet.Id, packet.Data);

                if (ok) clientApi?.Logger.Notification($"[DeskPictureFrame] Received image {packet.Id}.");
            }

            if (pendingRequests.TryGetValue(packet.Id, out var waiting))
            {
                pendingRequests.Remove(packet.Id);
                if (ok) foreach (var cb in waiting) cb?.Invoke();
            }
        }

        private static bool LooksLikePng(byte[] data)
        {
            return data != null && data.Length >= 8
                && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47;
        }

        /// <summary>The folders a player's own pictures live in. Used by the mod system to
        /// create the folder structure and extract the default images.</summary>
        public static readonly string[] ValidFolders = {
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
    }
}
