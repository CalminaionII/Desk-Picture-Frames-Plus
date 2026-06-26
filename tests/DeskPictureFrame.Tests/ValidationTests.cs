using Xunit;
using DeskPictureFrame;

namespace DeskPictureFrame.Tests
{
    public class ImageKeyPathSafetyTests
    {
        [Fact]
        public void NullKey_ReturnsFalse()
        {
            Assert.False(DeskPictureFrameValidation.IsImageKeyPathSafe(null));
        }

        [Fact]
        public void EmptyKey_ReturnsFalse()
        {
            Assert.False(DeskPictureFrameValidation.IsImageKeyPathSafe(""));
        }

        [Fact]
        public void KeyWithPathTraversal_ReturnsFalse()
        {
            Assert.False(DeskPictureFrameValidation.IsImageKeyPathSafe("../etc/passwd"));
        }

        [Fact]
        public void KeyWithDoubleDotsInMiddle_ReturnsFalse()
        {
            Assert.False(DeskPictureFrameValidation.IsImageKeyPathSafe("folder/../secret"));
        }

        [Fact]
        public void RootedPath_ReturnsFalse()
        {
            Assert.False(DeskPictureFrameValidation.IsImageKeyPathSafe("/absolute/path/file"));
        }

        [Theory]
        [InlineData("desk-landscape-images/image1/1")]
        [InlineData("wall-portrait-image/image/5")]
        [InlineData("grouped-landscape-images/image3/3")]
        public void ValidKeys_ReturnsTrue(string key)
        {
            Assert.True(DeskPictureFrameValidation.IsImageKeyPathSafe(key));
        }
    }

    public class ImageFileNameTests
    {
        [Fact]
        public void NullKey_ReturnsFalse()
        {
            Assert.False(DeskPictureFrameValidation.IsValidImageFileName(null));
        }

        [Fact]
        public void EmptyKey_ReturnsFalse()
        {
            Assert.False(DeskPictureFrameValidation.IsValidImageFileName(""));
        }

        [Theory]
        [InlineData("desk-landscape-images/image1/1")]
        [InlineData("desk-landscape-images/image1/2")]
        [InlineData("desk-landscape-images/image1/3")]
        [InlineData("desk-landscape-images/image1/4")]
        [InlineData("desk-landscape-images/image1/5")]
        public void ValidFileNames1Through5_ReturnsTrue(string key)
        {
            Assert.True(DeskPictureFrameValidation.IsValidImageFileName(key));
        }

        [Theory]
        [InlineData("desk-landscape-images/image1/0")]
        [InlineData("desk-landscape-images/image1/6")]
        [InlineData("desk-landscape-images/image1/10")]
        [InlineData("desk-landscape-images/image1/abc")]
        [InlineData("desk-landscape-images/image1/")]
        public void InvalidFileNames_ReturnsFalse(string key)
        {
            Assert.False(DeskPictureFrameValidation.IsValidImageFileName(key));
        }

        [Fact]
        public void NegativeNumber_ReturnsFalse()
        {
            Assert.False(DeskPictureFrameValidation.IsValidImageFileName("folder/-1"));
        }
    }

    public class ImageFolderTests
    {
        [Fact]
        public void NullKey_ReturnsFalse()
        {
            Assert.False(DeskPictureFrameValidation.IsValidImageFolder(null));
        }

        [Fact]
        public void EmptyKey_ReturnsFalse()
        {
            Assert.False(DeskPictureFrameValidation.IsValidImageFolder(""));
        }

        [Fact]
        public void NoSlash_ReturnsFalse()
        {
            Assert.False(DeskPictureFrameValidation.IsValidImageFolder("noslash"));
        }

        [Theory]
        [InlineData("desk-landscape-images/image1/1")]
        [InlineData("desk-landscape-images/image2/3")]
        [InlineData("desk-landscape-single-images/image1/5")]
        [InlineData("desk-portrait-images/image1/2")]
        [InlineData("desk-portrait-images/image2/4")]
        [InlineData("desk-portrait-images/image3/1")]
        [InlineData("desk-portrait-single-images/image1/3")]
        [InlineData("grouped-landscape-images/image1/2")]
        [InlineData("grouped-landscape-images/image2/4")]
        [InlineData("grouped-landscape-images/image3/5")]
        [InlineData("grouped-portrait-images/image1/1")]
        [InlineData("grouped-portrait-images/image2/2")]
        [InlineData("grouped-portrait-images/image3/3")]
        [InlineData("grouped-portrait-images/image4/4")]
        [InlineData("wall-landscape-image/image/5")]
        [InlineData("wall-portrait-image/image/1")]
        public void AllValidFolders_ReturnsTrue(string key)
        {
            Assert.True(DeskPictureFrameValidation.IsValidImageFolder(key));
        }

        [Theory]
        [InlineData("invalid-folder/image1/1")]
        [InlineData("desk-landscape-images/image9/1")]
        [InlineData("custom-folder/1")]
        [InlineData("../secret/1")]
        public void InvalidFolders_ReturnsFalse(string key)
        {
            Assert.False(DeskPictureFrameValidation.IsValidImageFolder(key));
        }
    }

    public class PngHeaderTests
    {
        private static byte[] MakeValidPngHeader()
        {
            return new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        }

        [Fact]
        public void NullData_ReturnsFalse()
        {
            Assert.False(DeskPictureFrameValidation.HasValidPngHeader(null));
        }

        [Fact]
        public void EmptyData_ReturnsFalse()
        {
            Assert.False(DeskPictureFrameValidation.HasValidPngHeader(new byte[0]));
        }

        [Fact]
        public void TooShort_ReturnsFalse()
        {
            Assert.False(DeskPictureFrameValidation.HasValidPngHeader(new byte[] { 0x89, 0x50, 0x4E }));
        }

        [Fact]
        public void ValidPngHeader_ReturnsTrue()
        {
            Assert.True(DeskPictureFrameValidation.HasValidPngHeader(MakeValidPngHeader()));
        }

        [Fact]
        public void JpegHeader_ReturnsFalse()
        {
            // JPEG starts with FF D8 FF
            var jpegData = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46 };
            Assert.False(DeskPictureFrameValidation.HasValidPngHeader(jpegData));
        }

        [Fact]
        public void GifHeader_ReturnsFalse()
        {
            // GIF89a
            var gifData = new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0x00, 0x00 };
            Assert.False(DeskPictureFrameValidation.HasValidPngHeader(gifData));
        }

        [Fact]
        public void AllZeros_ReturnsFalse()
        {
            Assert.False(DeskPictureFrameValidation.HasValidPngHeader(new byte[8]));
        }

        [Fact]
        public void LargeValidPng_ReturnsTrue()
        {
            var data = new byte[1000];
            data[0] = 0x89;
            data[1] = 0x50;
            data[2] = 0x4E;
            data[3] = 0x47;
            Assert.True(DeskPictureFrameValidation.HasValidPngHeader(data));
        }
    }

    public class SizeLimitTests
    {
        [Fact]
        public void NullData_ReturnsFalse()
        {
            Assert.False(DeskPictureFrameValidation.IsWithinSizeLimit(null));
        }

        [Fact]
        public void EmptyData_ReturnsTrue()
        {
            Assert.True(DeskPictureFrameValidation.IsWithinSizeLimit(new byte[0]));
        }

        [Fact]
        public void ExactlyAtLimit_ReturnsTrue()
        {
            var data = new byte[1_000_000];
            Assert.True(DeskPictureFrameValidation.IsWithinSizeLimit(data));
        }

        [Fact]
        public void OneByteOverLimit_ReturnsFalse()
        {
            var data = new byte[1_000_001];
            Assert.False(DeskPictureFrameValidation.IsWithinSizeLimit(data));
        }

        [Fact]
        public void SmallData_ReturnsTrue()
        {
            Assert.True(DeskPictureFrameValidation.IsWithinSizeLimit(new byte[100]));
        }
    }

    public class ValidateUploadPacketTests
    {
        private static byte[] MakeValidPngData(int size = 100)
        {
            var data = new byte[size];
            data[0] = 0x89;
            data[1] = 0x50;
            data[2] = 0x4E;
            data[3] = 0x47;
            data[4] = 0x0D;
            data[5] = 0x0A;
            data[6] = 0x1A;
            data[7] = 0x0A;
            return data;
        }

        [Fact]
        public void ValidPacket_ReturnsNull()
        {
            string result = DeskPictureFrameValidation.ValidateUploadPacket(
                "desk-landscape-images/image1/1",
                MakeValidPngData());
            Assert.Null(result);
        }

        [Fact]
        public void NullImageKey_ReturnsPathTraversalReason()
        {
            string result = DeskPictureFrameValidation.ValidateUploadPacket(null, MakeValidPngData());
            Assert.Contains("path traversal", result);
        }

        [Fact]
        public void PathTraversalKey_ReturnsPathTraversalReason()
        {
            string result = DeskPictureFrameValidation.ValidateUploadPacket(
                "../etc/passwd",
                MakeValidPngData());
            Assert.Contains("path traversal", result);
        }

        [Fact]
        public void InvalidFileName_ReturnsInvalidNameReason()
        {
            string result = DeskPictureFrameValidation.ValidateUploadPacket(
                "desk-landscape-images/image1/99",
                MakeValidPngData());
            Assert.Contains("invalid image file name", result);
        }

        [Fact]
        public void UnknownFolder_ReturnsUnknownFolderReason()
        {
            string result = DeskPictureFrameValidation.ValidateUploadPacket(
                "unknown-folder/image1/1",
                MakeValidPngData());
            Assert.Contains("unknown folder", result);
        }

        [Fact]
        public void NonPngData_ReturnsNonPngReason()
        {
            string result = DeskPictureFrameValidation.ValidateUploadPacket(
                "desk-landscape-images/image1/1",
                new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46 });
            Assert.Contains("non-PNG", result);
        }

        [Fact]
        public void OversizedImage_ReturnsOversizedReason()
        {
            string result = DeskPictureFrameValidation.ValidateUploadPacket(
                "desk-landscape-images/image1/1",
                MakeValidPngData(1_000_001));
            Assert.Contains("oversized", result);
        }

        [Theory]
        [InlineData("desk-landscape-images/image1/1")]
        [InlineData("desk-portrait-images/image3/5")]
        [InlineData("grouped-portrait-images/image4/4")]
        [InlineData("wall-landscape-image/image/3")]
        [InlineData("wall-portrait-image/image/2")]
        public void AllValidCombinations_ReturnNull(string key)
        {
            string result = DeskPictureFrameValidation.ValidateUploadPacket(key, MakeValidPngData());
            Assert.Null(result);
        }
    }
}
