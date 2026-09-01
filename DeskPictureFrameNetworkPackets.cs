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
