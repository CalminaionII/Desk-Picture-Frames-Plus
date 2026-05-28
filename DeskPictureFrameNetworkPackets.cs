
using ProtoBuf;

namespace DeskPictureFrame
{
    [ProtoContract]
    public class ImageUploadPacket
    {
        [ProtoMember(1)]
        public string PlayerUid { get; set; }

        [ProtoMember(2)]
        public string ImageKey { get; set; } // e.g. "desk-landscape-images/image1/1"

        [ProtoMember(3)]
        public byte[] ImageData { get; set; }
    }

    [ProtoContract]
    public class ImageRequestPacket
    {
        [ProtoMember(1)]
        public string OwnerUid { get; set; }
    }

    [ProtoContract]
    public class ImageResponsePacket
    {
        [ProtoMember(1)]
        public string OwnerUid { get; set; }

        [ProtoMember(2)]
        public string ImageKey { get; set; }

        [ProtoMember(3)]
        public byte[] ImageData { get; set; }
    }
}