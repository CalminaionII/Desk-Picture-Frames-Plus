using ProtoBuf;

namespace DeskPictureFrame
{
    // EVERY PACKET CLASS NEEDS ITS OWN [ProtoContract], broadcasts included. protobuf-net
    // cannot infer one and throws "Type is not expected, and no contract can be inferred",
    // which on a dedicated server surfaces as "Player X got removed. Threw an exception at
    // the server" - a kick, not an error line. Singleplayer never exercises this path
    // (shared reference sect.8c).

    /// <summary>CLIENT -> SERVER: "I just placed a frame using these images."
    /// The server replies with <see cref="ImageNeedPacket"/> naming the ones it lacks.</summary>
    [ProtoContract]
    public class ImageAnnouncePacket
    {
        [ProtoMember(1)]
        public string[] Ids { get; set; }
    }

    /// <summary>SERVER -> CLIENT: "send me these, I do not have them."
    /// Only ever a subset of what was announced, so a picture is uploaded once ever rather
    /// than 80 files on every join.</summary>
    [ProtoContract]
    public class ImageNeedPacket
    {
        [ProtoMember(1)]
        public string[] Ids { get; set; }
    }

    /// <summary>CLIENT -> SERVER: the bytes of one image.
    ///
    /// There is no player field, and that is deliberate. The server recomputes the id from
    /// the bytes and stores it under that, so a client cannot claim an identity it does not
    /// have the data for. The old packet carried a PlayerUid the server trusted, which let
    /// anyone overwrite anyone else's pictures.</summary>
    [ProtoContract]
    public class ImageUploadPacket
    {
        [ProtoMember(1)]
        public string Id { get; set; }

        [ProtoMember(2)]
        public byte[] Data { get; set; }

        /// <summary>The name the picture has in the sender's own folder, carried ONLY so an
        /// admin listing the store sees "sunset.png" rather than a hash. Added 2026-09-04.
        ///
        /// **IT IS NEVER A PATH AND NEVER TOUCHES THE FILESYSTEM.** The file on the server is
        /// still named by its hash - this is a label, stored as text in the ledger and printed
        /// in a command. It is client-supplied and therefore untrusted: the server strips it to
        /// a bare filename and caps its length before keeping it.</summary>
        [ProtoMember(3)]
        public string Name { get; set; }
    }

    /// <summary>CLIENT -> SERVER: "I can see a frame using this image and do not have it."</summary>
    [ProtoContract]
    public class ImageRequestPacket
    {
        [ProtoMember(1)]
        public string Id { get; set; }
    }

    /// <summary>SERVER -> CLIENT: the bytes for a requested image.
    /// <see cref="Data"/> is null when the server does not have it either, so the client can
    /// stop waiting instead of sitting on the placeholder forever.</summary>
    [ProtoContract]
    public class ImageResponsePacket
    {
        [ProtoMember(1)]
        public string Id { get; set; }

        [ProtoMember(2)]
        public byte[] Data { get; set; }
    }

    /// <summary>SERVER -> CLIENT, once on join: the pixel caps this server enforces.
    ///
    /// Both sides have a `MaxLongSide`/`MaxShortSide` in their own config file, but only the
    /// server's is ever applied - it is the one refusing uploads. Without this packet the
    /// picker would warn with the local file's numbers, so a player on a stricter server would
    /// be told 480x320 and then watch the upload be rejected with nothing on screen to explain
    /// it. In singleplayer both sides read the same file and this changes nothing.
    ///
    /// The byte cap is not here on purpose: it is fixed at 1 MB everywhere and there is
    /// nothing to negotiate.</summary>
    [ProtoContract]
    public class ServerLimitsPacket
    {
        [ProtoMember(1)]
        public int MaxLongSide { get; set; }

        [ProtoMember(2)]
        public int MaxShortSide { get; set; }
    }

    /// <summary>SERVER -> CLIENT: how much of this server's picture allowance you have used.
    ///
    /// Sent on join and whenever it moves - after an upload is recorded, after
    /// a removal, after `/dpf config reload`. It exists so the PICKER can show the
    /// limit before you hit it, rather than the limit being something you discover by placing
    /// a frame and being told no.</summary>
    [ProtoContract]
    public class QuotaStatePacket
    {
        [ProtoMember(1)]
        public int Used { get; set; }

        [ProtoMember(2)]
        public int Max { get; set; }

        /// <summary>Every picture this server holds for you, and what each was called. Sent
        /// whether or not you still have the file - so a picture you have deleted locally can
        /// still be taken off the server, which is the case that matters most.</summary>
        [ProtoMember(3)]
        public string[] MineIds { get; set; }

        [ProtoMember(4)]
        public string[] MineNames { get; set; }
    }

    /// <summary>CLIENT -> SERVER: "which of my pictures do you already have?"
    ///
    /// Asked when the picker opens. NOT the same as <see cref="ImageAnnouncePacket"/>, which
    /// means "I have placed these" and makes the server ask for the bytes - this one only
    /// enquires, and nothing is uploaded on the back of it.
    ///
    /// The picker needs the answer to mark rows honestly: at the limit you can still point a
    /// frame at a picture the server ALREADY holds, because that costs it nothing. Without
    /// this the picker would have to block everything, including pictures that would work.</summary>
    [ProtoContract]
    public class ImageHaveQueryPacket
    {
        [ProtoMember(1)]
        public string[] Ids { get; set; }
    }

    /// <summary>SERVER -> CLIENT: of the ids you asked about, these are the ones I hold.
    /// About 4 KB for a full folder of 250, so it is asked outright rather than cached
    /// cleverly.</summary>
    [ProtoContract]
    public class ImageHaveReplyPacket
    {
        [ProtoMember(1)]
        public string[] Ids { get; set; }

        /// <summary>Of those, the ones this player is counted for. The picker offers "remove
        /// from the server" on these and on nothing else - you cannot let go of a picture that
        /// was never charged to you.</summary>
        [ProtoMember(2)]
        public string[] Claimed { get; set; }
    }

    /// <summary>CLIENT -> SERVER: "stop counting this picture against me."
    ///
    /// Sent only from the picker's remove button, after a confirmation. **Never inferred from a
    /// picture going missing** - a pictures folder empties itself through reinstalls, moved
    /// folders and antivirus, and treating that as intent would blank frames across a server.
    ///
    /// The server drops this player's claim and deletes the file only if nobody else holds it.
    /// A client naming a picture it does not hold changes nothing, so lying gains nothing.</summary>
    [ProtoContract]
    public class ImageReleasePacket
    {
        [ProtoMember(1)]
        public string Id { get; set; }
    }

    /// <summary>CLIENT -> SERVER: the images a frame was placed or re-captured with.
    ///
    /// Placement runs on both sides, but only the CLIENT can read the placing player's own
    /// image folder, so the ids have to travel to the server for it to persist them into the
    /// block entity and sync them to everyone else.</summary>
    [ProtoContract]
    public class FrameImagesPacket
    {
        [ProtoMember(1)]
        public int X { get; set; }

        [ProtoMember(2)]
        public int Y { get; set; }

        [ProtoMember(3)]
        public int Z { get; set; }

        /// <summary>Texture codes - "image1", "image2", "image" - parallel to <see cref="Ids"/>.</summary>
        [ProtoMember(4)]
        public string[] Codes { get; set; }

        [ProtoMember(5)]
        public string[] Ids { get; set; }
    }
}
