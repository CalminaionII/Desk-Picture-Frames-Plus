using System;
using System.Collections.Generic;
using System.IO;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
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

        /// <summary>The most bytes one picture may be, and NOT A SETTING.
        ///
        /// This and <see cref="LooksLikePng"/> are the boundary of what this mod will carry
        /// between players at all. The pixel caps and the store size are configurable because
        /// they are about cost - how much atlas space a picture takes, how much disk a server
        /// gives up. This is about what the bytes ARE. A server owner turning it up would not
        /// be tuning their own server, they would be deciding on behalf of everyone who
        /// connects that this channel now carries something larger and less examined than a
        /// small PNG. Calm's call, 2026-09-04: the file size and the PNG rule stay fixed "so
        /// that no malicious content can be sent to others".
        ///
        /// Leave it a const. A config property here is a doorway, not a dial.
        ///
        /// MOVED to DeskPictureFrameImageStore 2026-09-04 so the INDEX applies it too, and
        /// aliased here rather than duplicated - two copies of a security limit is one copy
        /// waiting to be raised on its own.</summary>
        private const int MaxImageBytes = DeskPictureFrameImageStore.MaxImageBytes;

        /// <summary>Most images one player may upload in a session. NOT A SETTING either, for
        /// the same reason - it is the only thing bounding a client that has decided to be a
        /// nuisance.
        ///
        /// Nothing else bounds a client: it can announce any ids it likes and the server will
        /// ask for them. Content addressing stops anyone overwriting someone else's picture,
        /// but not from filling the disk. Well above any honest use of the mod.</summary>
        private const int MaxUploadsPerPlayer = 300;

        /// <summary>Per session deliberately - it resets on restart, and a player who genuinely
        /// reaches it is doing something no normal use of the mod requires.</summary>
        private readonly Dictionary<string, int> uploadsThisSession = new();

        /// <summary>Most picture slots one frame packet may set. The biggest frame has four; this is
        /// room to grow, and a bound on what a modified client can make every viewer download.</summary>
        private const int MaxSlotsPerFrame = 8;

        /// <summary>How soon the same player may be sent the same picture again. An honest client
        /// asks once and then has it; without this a modified one could ask for the same megabyte
        /// over and over and have the server read and send it every time.</summary>
        private const long RepeatRequestMs = 10_000;

        /// <summary>"playeruid|id" -> when it was last served (server milliseconds).</summary>
        private readonly Dictionary<string, long> lastServed = new();

        private IServerNetworkChannel serverChannel;
        private ICoreServerAPI serverApi;


        private IClientNetworkChannel clientChannel;
        private ICoreClientAPI clientApi;

        /// <summary>Ids we have asked the server for and not yet heard back about, with the
        /// callbacks waiting on each. Keyed so several frames sharing a picture make one request.</summary>
        private readonly Dictionary<string, List<Action>> pendingRequests = new();

        /// <summary>Ids the server has told us it does not have either, so we stop asking.</summary>
        private readonly HashSet<string> unavailable = new();

        // ---- what the picker needs to be honest about the limit -------------------------
        //
        // All three are CLIENT side and all three are told to us by the server. Defaults say
        // "no limit known", which is the right reading in singleplayer and before a join: the
        // picker marks nothing until a server has actually said otherwise.

        /// <summary>How many pictures we are counted as using here, and the allowance. Max 0
        /// means we have not been told, so nothing is marked.</summary>
        public int QuotaUsed { get; private set; }
        public int QuotaMax { get; private set; }

        public bool QuotaKnown => QuotaMax > 0;
        public bool QuotaFull => QuotaKnown && QuotaUsed >= QuotaMax;

        /// <summary>Which of OUR OWN pictures the server already holds. Choosing one of these
        /// is free even at the limit, which is exactly why the picker has to know.</summary>
        private readonly HashSet<string> serverHolds = new();

        /// <summary>Everything this server holds for us, with the name each was sent under.
        ///
        /// TOLD TO US OUTRIGHT, not worked out from the pictures we still have. The picker used
        /// to ask "which of my current pictures do you count?", which meant a picture deleted
        /// locally became impossible to remove from the server - the one case where you most
        /// want to.</summary>
        public List<(string Id, string Name)> MyServerPictures { get; private set; } = new();

        public bool CanRelease(string id)
        {
            if (id == null) return false;
            foreach (var row in MyServerPictures)
            {
                if (row.Id == id) return true;
            }
            return false;
        }

        /// <summary>Ask the server to stop counting a picture against us. From the picker's
        /// button and nowhere else.</summary>
        public void ReleaseImage(string id)
        {
            if (!CanRelease(id)) return;
            clientChannel?.SendPacket(new ImageReleasePacket { Id = id });

            // Tidy our own copy too, so the cache does not keep serving a picture this server
            // has just been told to delete. If it is still in our pictures folder it comes back
            // at the next launch, which is right - it is still our picture, just not here.
            DeskPictureFrameImageStore.DeleteClient(id);
            DeskPictureFrameImageStore.MarkReleased(id);
        }

        /// <summary>True if choosing this picture would cost a slot we do not have.
        ///
        /// **EMPTY MEANS "NO PICTURE", AND CLEARING A FRAME IS ALWAYS ALLOWED.** The test used to
        /// be `id != null`, and the picker passes "" rather than null for the no-picture row - so
        /// at the limit it was treated as a picture the server did not hold and refused. Being
        /// full stopped you emptying a frame, which is the one thing that should never need
        /// room.</summary>
        public bool WouldBeRefused(string id) =>
            QuotaFull && !string.IsNullOrEmpty(id) && !serverHolds.Contains(id);

        /// <summary>Called when the picker opens. Cheap enough to ask every time rather than
        /// cache and risk being wrong after someone else uploads the same file.</summary>
        public void AskWhatServerHas()
        {
            if (clientChannel == null || !QuotaKnown) return;

            var ids = new List<string>();
            foreach (var pair in DeskPictureFrameImageStore.LocalIds)
            {
                if (!ids.Contains(pair.Value)) ids.Add(pair.Value);
            }
            if (ids.Count == 0) return;

            clientChannel.SendPacket(new ImageHaveQueryPacket { Ids = ids.ToArray() });
        }

        /// <summary>Runs when a fresh answer arrives, so the picker can redraw itself.</summary>
        public Action OnQuotaChanged { get; set; }

        private void OnClientQuotaState(QuotaStatePacket packet)
        {
            if (packet == null) return;
            QuotaUsed = packet.Used;
            QuotaMax = packet.Max;

            var mine = new List<(string, string)>();
            if (packet.MineIds != null)
            {
                for (int i = 0; i < packet.MineIds.Length; i++)
                {
                    string label = packet.MineNames != null && i < packet.MineNames.Length
                        ? packet.MineNames[i] : null;
                    mine.Add((packet.MineIds[i], label));
                }
            }
            MyServerPictures = mine;

            OnQuotaChanged?.Invoke();
        }

        private void OnClientHaveReply(ImageHaveReplyPacket packet)
        {
            serverHolds.Clear();
            if (packet?.Ids != null)
            {
                foreach (string id in packet.Ids) serverHolds.Add(id);
            }
            if (packet?.Claimed != null)
            {
                // Kept for the marking logic; removal now comes from MyServerPictures.
                foreach (string id in packet.Claimed) serverHolds.Add(id);
            }
            OnQuotaChanged?.Invoke();
        }

        /// <summary>SERVER: answer which of these we hold. READ ONLY - it stores nothing, asks
        /// for nothing, and cannot be used to make the server want an upload.</summary>
        private void OnServerHaveQuery(IServerPlayer player, ImageHaveQueryPacket packet)
        {
            if (packet?.Ids == null) return;

            var have = new List<string>();
            foreach (string id in packet.Ids)
            {
                if (DeskPictureFrameImageStore.IsWellFormedId(id)
                    && DeskPictureFrameImageStore.ServerHas(id)) have.Add(id);
            }

            serverChannel.SendPacket(new ImageHaveReplyPacket
            {
                Ids = have.ToArray(),
                Claimed = have.ToArray()
            }, player);

            SendQuotaState(player);
        }

        /// <summary>
        /// SERVER: a player letting go of one of their own pictures.
        ///
        /// Their claim goes; the file goes only if nobody else holds it. Refusing a picture they
        /// do not hold is silent on purpose - there is nothing to tell an honest client, and
        /// nothing to give a dishonest one.
        /// </summary>
        private void OnServerRelease(IServerPlayer player, ImageReleasePacket packet)
        {
            if (packet?.Id == null || !DeskPictureFrameImageStore.IsWellFormedId(packet.Id)) return;

            // THE FILE GOES. Freeing a slot without deleting was the whole mistake this rewrite
            // undid - the count is the folder, so nothing may move one without the other. Only
            // their own folder is searched, which is also what stops a client removing anyone
            // else's picture.
            if (!DeskPictureFrameQuota.Remove(player.PlayerName, packet.Id)) return;

            serverApi.Logger.Notification(
                $"[DeskPictureFrame] {player.PlayerName} removed {packet.Id} from the store.");

            SendQuotaState(player);

            // Tell them, since a frame elsewhere in the world may have just gone blank and they
            // will not be standing next to it.
            serverApi.SendMessage(player, GlobalConstants.GeneralChatGroup,
                Lang.Get("deskpictureframe:released-deleted"), EnumChatType.Notification);
        }

        /// <summary>SERVER: tell one player where they stand. Sent on join and after anything
        /// that moves the number, so the picker is never quoting a stale figure.</summary>
        public void SendQuotaState(IServerPlayer player)
        {
            if (serverChannel == null || player == null) return;

            var mine = DeskPictureFrameQuota.PicturesOf(player.PlayerName);
            var ids = new string[mine.Count];
            var names = new string[mine.Count];
            for (int i = 0; i < mine.Count; i++)
            {
                ids[i] = mine[i].Id;
                names[i] = mine[i].Label;
            }

            serverChannel.SendPacket(new QuotaStatePacket
            {
                Used = DeskPictureFrameQuota.CountFor(player.PlayerName),
                Max = DeskPictureFrame.Config.Server.MaxImagesPerPlayer,
                MineIds = ids,
                MineNames = names
            }, player);
        }

        /// <summary>SERVER: tell everyone, after a config reload or an admin reset.</summary>
        public void BroadcastQuotaState()
        {
            if (serverApi == null) return;
            foreach (IServerPlayer p in serverApi.World.AllOnlinePlayers) SendQuotaState(p);
        }

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

            // Read the store off its filenames. There is nothing else to load - a player's
            // usage is the files in their folder, counted when asked.
            DeskPictureFrameImageStore.BuildServerIndex(sapi);

            serverChannel = sapi.Network.RegisterChannel(ChannelName)
                .RegisterMessageType<ImageAnnouncePacket>()
                .RegisterMessageType<ImageNeedPacket>()
                .RegisterMessageType<ImageUploadPacket>()
                .RegisterMessageType<ImageRequestPacket>()
                .RegisterMessageType<ImageResponsePacket>()
                .RegisterMessageType<FrameImagesPacket>()
                .RegisterMessageType<ServerLimitsPacket>()
                .RegisterMessageType<QuotaStatePacket>()
                .RegisterMessageType<ImageHaveQueryPacket>()
                .RegisterMessageType<ImageHaveReplyPacket>()
                .RegisterMessageType<ImageReleasePacket>()
                .SetMessageHandler<ImageAnnouncePacket>(OnServerAnnounce)
                .SetMessageHandler<ImageUploadPacket>(OnServerUpload)
                .SetMessageHandler<ImageRequestPacket>(OnServerRequest)
                .SetMessageHandler<FrameImagesPacket>(OnServerFrameImages)
                .SetMessageHandler<ImageHaveQueryPacket>(OnServerHaveQuery)
                .SetMessageHandler<ImageReleasePacket>(OnServerRelease);

            // EVERY PACKET TYPE MUST BE REGISTERED IN THE SAME ORDER ON BOTH SIDES - the
            // channel identifies a message by its position in this chain, not by its name, so
            // adding one to only one side (or in a different place) silently misroutes
            // everything after it. ServerLimitsPacket therefore sits last in both.

            sapi.Event.PlayerJoin += OnPlayerJoin;

            sapi.Logger.Notification("[DeskPictureFrame] Server image store: "
                                     + DeskPictureFrameImageStore.ServerStoreRoot());
        }

        /// <summary>
        /// SERVER: tell a joining player what this server will actually accept.
        ///
        /// Sent to everyone, not just to players whose config differs - the server has no idea
        /// what is in anyone's file, and two integers on join costs nothing.
        /// </summary>
        private void OnPlayerJoin(IServerPlayer player)
        {
            try
            {
                serverChannel?.SendPacket(new ServerLimitsPacket
                {
                    MaxLongSide = DeskPictureFrameImageStore.MaxLongSide,
                    MaxShortSide = DeskPictureFrameImageStore.MaxShortSide
                }, player);

                SendQuotaState(player);
            }
            catch (Exception ex)
            {
                serverApi?.Logger.Warning(
                    $"[DeskPictureFrame] Could not send the picture limits to {player?.PlayerName}: {ex.Message}");
            }
        }

        /// <summary>
        /// SERVER: tell everyone currently connected what the caps are now. Sent after
        /// `/dpf config reload`, so a size change reaches players without them reconnecting -
        /// which is the whole point of the command.
        /// </summary>
        public void BroadcastLimits()
        {
            serverChannel?.BroadcastPacket(new ServerLimitsPacket
            {
                MaxLongSide = DeskPictureFrameImageStore.MaxLongSide,
                MaxShortSide = DeskPictureFrameImageStore.MaxShortSide
            });
        }

        /// <summary>
        /// CLIENT: adopt the server's caps, and rebuild the picture list if they moved.
        ///
        /// The re-index is the point of the packet. On a STRICTER server, pictures indexed at
        /// launch would otherwise sit in the picker and be refused on upload with nothing on
        /// screen saying why; on a MORE GENEROUS one, pictures skipped at launch would stay
        /// invisible even though this server would have taken them.
        ///
        /// Nothing happens at all when the numbers match, which is every singleplayer world and
        /// every server whose owner left the defaults alone.
        /// </summary>
        private void OnClientLimits(ServerLimitsPacket packet)
        {
            if (packet == null) return;

            DeskPictureFrameImageStore.ApplyServerLimits(packet.MaxLongSide, packet.MaxShortSide);

            // THE TEST IS "does the picture list still match the caps", not "did this packet
            // change anything". They differ in singleplayer after `/dpf config reload`: the
            // integrated server has already applied the new caps to the statics both sides
            // share, so the packet arrives carrying what this side already believes - and a
            // test on the packet would skip the re-index that the reload exists to cause.
            if (!DeskPictureFrameImageStore.IndexIsStale) return;

            clientApi?.Logger.Notification(
                $"[DeskPictureFrame] Pictures may now be up to {packet.MaxLongSide}x{packet.MaxShortSide}; " +
                "re-reading your pictures folder to match.");

            // Re-hashes every file, so off the network thread and onto the main one.
            clientApi?.Event.EnqueueMainThreadTask(
                () => DeskPictureFrameImageStore.Reindex(clientApi), "dpfreindexlimits");
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
                .RegisterMessageType<ServerLimitsPacket>()
                .RegisterMessageType<QuotaStatePacket>()
                .RegisterMessageType<ImageHaveQueryPacket>()
                .RegisterMessageType<ImageHaveReplyPacket>()
                .RegisterMessageType<ImageReleasePacket>()
                .SetMessageHandler<ImageNeedPacket>(OnClientNeed)
                .SetMessageHandler<ImageResponsePacket>(OnClientImage)
                .SetMessageHandler<ServerLimitsPacket>(OnClientLimits)
                .SetMessageHandler<QuotaStatePacket>(OnClientQuotaState)
                .SetMessageHandler<ImageHaveReplyPacket>(OnClientHaveReply);

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
            // AN EMPTY MAP IS A REAL INSTRUCTION: "this frame shows nothing now". It used to be
            // treated as nothing to say, so clearing the LAST picture on a frame never left this
            // machine - the block entity kept the old id, and re-opening the picker showed the
            // picture you had just taken off. It broke "no picture" in the same way, on any
            // single-slot frame.
            if (clientChannel == null || pos == null || ids == null) return;

            var codes = new List<string>(ids.Count);
            var values = new List<string>(ids.Count);
            foreach (var pair in ids)
            {
                codes.Add(pair.Key);
                values.Add(pair.Value);
            }

            // CHOOSING A PICTURE UNDOES REMOVING IT. Without this, a picture taken off the
            // server could never be put back: the client would keep refusing to draw it and
            // refusing to send it, and the frame would sit on the placeholder for good.
            foreach (string id in values) DeskPictureFrameImageStore.UnmarkReleased(id);

            clientChannel.SendPacket(new FrameImagesPacket
            {
                X = pos.X, Y = pos.Y, Z = pos.Z,
                Codes = codes.ToArray(),
                Ids = values.ToArray()
            });

            // Nothing to announce when the frame has been emptied.
            if (values.Count > 0)
            {
                clientChannel.SendPacket(new ImageAnnouncePacket { Ids = values.ToArray() });
            }
        }

        private void OnServerFrameImages(IServerPlayer player, FrameImagesPacket packet)
        {
            if (packet == null) return;

            // NULL IS AN EMPTY FRAME, NOT A BAD PACKET. protobuf writes nothing at all for an
            // empty array, so "clear this frame" arrives with both arrays null - and rejecting
            // that meant a frame could never be emptied over the network.
            string[] codes = packet.Codes ?? Array.Empty<string>();
            string[] sentIds = packet.Ids ?? Array.Empty<string>();
            if (codes.Length != sentIds.Length || codes.Length > MaxSlotsPerFrame) return;

            var pos = new BlockPos(packet.X, packet.Y, packet.Z);

            // THE permission gate. The picker is only a window - what actually changes a frame
            // is this packet, and a client can send it without ever opening one. So the claim
            // is tested here, on the server, against the player who really sent it.
            // TryAccess also tells the player why nothing happened.
            if (!serverApi.World.Claims.TryAccess(player, pos, EnumBlockAccessFlags.Use)) return;

            if (serverApi.World.BlockAccessor.GetBlockEntity(pos) is not BEDeskPictureFrame be) return;

            // A boxed set never takes a picture - the gate a modified client cannot skip.
            if ((be.Block as BlockDeskPictureFrame)?.IsCollection == true) return;

            // THE ID AND THE BYTES TRAVEL SEPARATELY, AND THAT IS WHY THIS GATE IS HERE.
            //
            // This packet sets the frame's picture. The bytes arrive later, in their own
            // exchange, which the quota can refuse - so accepting the id now would leave the
            // frame pointing at an image the server will never hold. The placer would keep
            // seeing it from their own cache while everyone else got the placeholder for good,
            // and no amount of messaging fixes a frame that LOOKS right to the one person who
            // could act on it. Calm, testing on 2026-09-04: "having it show the placeholder to
            // the client as well ... there is no confusion as to why that is happening."
            //
            // Refusing here instead means the block entity never takes the id, the server syncs
            // that back, and FromTreeAttributes rebuilds the placer's mesh from it - so they see
            // exactly what everyone else sees, alongside the message saying why.
            //
            // Only images the server DOES NOT ALREADY HOLD are gated. One it has costs nothing
            // to point another frame at, whoever placed it.
            bool quotaFull = DeskPictureFrameQuota.IsFull(player.PlayerName);
            bool storeFull = DeskPictureFrameImageStore.ServerIsFull();

            var ids = new Dictionary<string, string>();
            bool refused = false;
            for (int i = 0; i < codes.Length; i++)
            {
                string id = sentIds[i];
                if (!IsSlotCode(codes[i]) || !DeskPictureFrameImageStore.IsWellFormedId(id)) continue;

                if ((quotaFull || storeFull)
                    && !DeskPictureFrameImageStore.PlayerHasImage(player.PlayerName, id))
                {
                    refused = true;
                    continue;
                }

                ids[codes[i]] = id;
            }

            if (refused) TellThemWhy(player, quotaFull);

            be.SetImageIds(ids);
        }

        /// <summary>
        /// A picture slot's texture code: "image", "image1", "image2"... The ids were always checked;
        /// the codes were not, so a modified client could store any number of made-up keys on a
        /// frame - saved with the world and sent to everyone who loads it. The server cannot list a
        /// frame's real slots (Block.Textures is client only), so this checks the form instead.
        /// </summary>
        private static bool IsSlotCode(string code)
        {
            if (code == null || !code.StartsWith("image") || code.Length > 13) return false;
            for (int i = "image".Length; i < code.Length; i++)
            {
                char c = code[i];
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'))) return false;
            }
            return true;
        }

        /// <summary>Says which of the two full things happened, once. Quota first: it is the
        /// one the player can do something about.</summary>
        private void TellThemWhy(IServerPlayer player, bool quotaFull)
        {
            string msg = quotaFull
                ? Lang.Get("deskpictureframe:quota-full", DeskPictureFrame.Config.Server.MaxImagesPerPlayer)
                : Lang.Get("deskpictureframe:store-full");

            serverApi.SendMessage(player, GlobalConstants.GeneralChatGroup, msg, EnumChatType.Notification);
            serverApi.Logger.Notification(
                $"[DeskPictureFrame] {player.PlayerName} could not add a new picture - " +
                (quotaFull ? "at their limit." : "the store is full."));
        }

        private void OnServerAnnounce(IServerPlayer player, ImageAnnouncePacket packet)
        {
            if (packet?.Ids == null) return;

            // Never ask for bytes we have already decided not to keep. Without this the client
            // would upload a megabyte for the server to throw away, and the player would be told
            // twice - once by the gate above, once by the upload being refused.
            bool cannotAccept = DeskPictureFrameQuota.IsFull(player.PlayerName)
                                || DeskPictureFrameImageStore.ServerIsFull();

            var wanted = new List<string>();
            foreach (string id in packet.Ids)
            {
                if (!DeskPictureFrameImageStore.IsWellFormedId(id)) continue;
                if (DeskPictureFrameImageStore.PlayerHasImage(player.PlayerName, id)) continue;
                if (cannotAccept) continue;
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
                    // THE SERVER ASKING FOR IT MEANS SOMEBODY CHOSE IT, so a picture we had
                    // previously taken off this server is wanted again. Clearing the mark here
                    // as well as in SendFrameImages covers the case where the frame was placed
                    // by another route.
                    DeskPictureFrameImageStore.UnmarkReleased(id);

                    // MATERIALISE FROM OUR OWN FOLDER IF NEED BE. This used to read the cache
                    // and give up if the file was not there - which broke the moment the cache
                    // became lazy, and again after a removal deleted the cached copy. The
                    // picture was still sitting in the pictures folder the whole time.
                    DeskPictureFrameImageStore.EnsureCached(clientApi, id);

                    string path = DeskPictureFrameImageStore.ClientImagePath(id);
                    if (!File.Exists(path))
                    {
                        clientApi?.Logger.Warning(
                            $"[DeskPictureFrame] The server asked for {id} and we have no file " +
                            "for it - it is not in the cache and not in the pictures folder.");
                        continue;
                    }

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

                    clientChannel.SendPacket(new ImageUploadPacket
                    {
                        Id = id,
                        Data = data,
                        Name = DeskPictureFrameImageStore.LocalNameFor(id)
                    });
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

            // THE QUOTA IS CHECKED ONLY FOR AN IMAGE THE SERVER DOES NOT ALREADY HAVE.
            // Content addressing means an identical picture is one file however many people
            // have it, so a second person supplying the SAME FILE costs the server nothing and
            // must not count against them. Nobody can pick another player's picture - the
            // picker reads only their own folder - so this is two people happening to hold the
            // same image, not sharing. Asked before writing, because after the write it is
            // indistinguishable from a picture that was always there.
            // NEW TO THIS PLAYER, not to the server. Each keeps their own copy, so a picture
            // another player already has still costs this one a slot - and either can delete
            // theirs without touching the other's.
            bool isNew = !DeskPictureFrameImageStore.PlayerHasImage(
                player.PlayerName, DeskPictureFrameImageStore.IdOf(packet.Data));

            // BACKSTOP ONLY. An honest client never reaches this: the frame gate refuses the
            // id, and the announce handler never asks for the bytes. It stays because a
            // modified client can upload unprompted, and the quota has to hold against that
            // too. No message here - on the honest path the player has already been told, and
            // on the dishonest one there is nobody to inform.
            if (isNew && DeskPictureFrameQuota.IsFull(player.PlayerName))
            {
                serverApi.Logger.Notification(
                    $"[DeskPictureFrame] Ignoring an unrequested upload from {player.PlayerName} - " +
                    $"at their limit of {DeskPictureFrame.Config.Server.MaxImagesPerPlayer}.");
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

            string pictureName = SafeName(packet.Name);
            string stored = DeskPictureFrameImageStore.WriteServer(
                packet.Data, packet.Id, player.PlayerName, pictureName);
            if (stored == null)
            {
                serverApi.Logger.Warning(
                    $"[DeskPictureFrame] Rejected image from {player.PlayerName}: content does " +
                    $"not match id {packet.Id}, or the store is full.");
                return;
            }

            uploadsThisSession[player.PlayerUID] = sent + 1;
            if (isNew) SendQuotaState(player);   // the picker's count must follow the upload
            serverApi.Logger.Notification($"[DeskPictureFrame] Stored image {stored}.");
        }

        // ---- viewing -----------------------------------------------------------------------

        private void OnServerRequest(IServerPlayer player, ImageRequestPacket packet)
        {
            if (packet?.Id == null || !DeskPictureFrameImageStore.IsWellFormedId(packet.Id)) return;

            // The same picture again, this soon: already sent, so not read and sent again.
            long now = serverApi.World.ElapsedMilliseconds;
            string key = player.PlayerUID + "|" + packet.Id;
            if (lastServed.TryGetValue(key, out long when) && now - when < RepeatRequestMs) return;
            if (lastServed.Count > 2000)
            {
                var expired = new List<string>();
                foreach (var pair in lastServed) if (now - pair.Value >= RepeatRequestMs) expired.Add(pair.Key);
                foreach (string old in expired) lastServed.Remove(old);
            }
            lastServed[key] = now;

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
                // Written to the cache - the frames waiting for it are redrawn below and put it
                // in the atlas from there, this session (FrameMesher).
                ok = DeskPictureFrameImageStore.WriteClient(packet.Id, packet.Data);

                // Downloads are what make the cache grow, so this is where it is kept in check.
                if (ok) DeskPictureFrameImageStore.TrimCache(clientApi);
                if (ok) clientApi?.Logger.Notification($"[DeskPictureFrame] Received image {packet.Id}.");
            }

            if (pendingRequests.TryGetValue(packet.Id, out var waiting))
            {
                pendingRequests.Remove(packet.Id);
                if (ok) foreach (var cb in waiting) cb?.Invoke();
            }
        }

        /// <summary>
        /// Reduces a client-supplied picture name to something safe to STORE AND PRINT.
        ///
        /// It is never used as a path - the file on disk is named by its hash - but it does end
        /// up in a JSON file and in a chat window, so it is treated as hostile input anyway:
        /// directory parts thrown away, control characters dropped, length capped. A name that
        /// survives none of that becomes null and the listing falls back to the hash.
        /// </summary>
        private static string SafeName(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;

            // The client sends its folder-relative key ("Landscape/sunset"), and the last part
            // is the only bit worth showing.
            int cut = raw.LastIndexOfAny(new[] { '/', '\\' });
            if (cut >= 0) raw = raw.Substring(cut + 1);

            var sb = new System.Text.StringBuilder(raw.Length);
            foreach (char c in raw)
            {
                if (!char.IsControl(c)) sb.Append(c);
                if (sb.Length >= 64) break;
            }

            string name = sb.ToString().Trim();
            return name.Length == 0 ? null : name;
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
