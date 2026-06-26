using Xunit;
using DeskPictureFrame;

namespace DeskPictureFrame.Tests
{
    public class ValidationConstantsTests
    {
        [Fact]
        public void MaxImageSizeBytes_IsOneMegabyte()
        {
            Assert.Equal(1_000_000, DeskPictureFrameValidation.MaxImageSizeBytes);
        }

        [Fact]
        public void MaxImagesPerPlayer_Is80()
        {
            Assert.Equal(80, DeskPictureFrameValidation.MaxImagesPerPlayer);
        }

        [Fact]
        public void ValidFolders_Contains16Entries()
        {
            Assert.Equal(16, DeskPictureFrameValidation.ValidFolders.Length);
        }

        [Fact]
        public void ValidFolders_ContainsDeskLandscapeImage1()
        {
            Assert.Contains("desk-landscape-images/image1", DeskPictureFrameValidation.ValidFolders);
        }

        [Fact]
        public void ValidFolders_ContainsDeskLandscapeImage2()
        {
            Assert.Contains("desk-landscape-images/image2", DeskPictureFrameValidation.ValidFolders);
        }

        [Fact]
        public void ValidFolders_ContainsDeskLandscapeSingleImage1()
        {
            Assert.Contains("desk-landscape-single-images/image1", DeskPictureFrameValidation.ValidFolders);
        }

        [Fact]
        public void ValidFolders_ContainsDeskPortraitImage1()
        {
            Assert.Contains("desk-portrait-images/image1", DeskPictureFrameValidation.ValidFolders);
        }

        [Fact]
        public void ValidFolders_ContainsDeskPortraitImage2()
        {
            Assert.Contains("desk-portrait-images/image2", DeskPictureFrameValidation.ValidFolders);
        }

        [Fact]
        public void ValidFolders_ContainsDeskPortraitImage3()
        {
            Assert.Contains("desk-portrait-images/image3", DeskPictureFrameValidation.ValidFolders);
        }

        [Fact]
        public void ValidFolders_ContainsDeskPortraitSingleImage1()
        {
            Assert.Contains("desk-portrait-single-images/image1", DeskPictureFrameValidation.ValidFolders);
        }

        [Fact]
        public void ValidFolders_ContainsGroupedLandscapeImage1()
        {
            Assert.Contains("grouped-landscape-images/image1", DeskPictureFrameValidation.ValidFolders);
        }

        [Fact]
        public void ValidFolders_ContainsGroupedLandscapeImage2()
        {
            Assert.Contains("grouped-landscape-images/image2", DeskPictureFrameValidation.ValidFolders);
        }

        [Fact]
        public void ValidFolders_ContainsGroupedLandscapeImage3()
        {
            Assert.Contains("grouped-landscape-images/image3", DeskPictureFrameValidation.ValidFolders);
        }

        [Fact]
        public void ValidFolders_ContainsGroupedPortraitImage1()
        {
            Assert.Contains("grouped-portrait-images/image1", DeskPictureFrameValidation.ValidFolders);
        }

        [Fact]
        public void ValidFolders_ContainsGroupedPortraitImage2()
        {
            Assert.Contains("grouped-portrait-images/image2", DeskPictureFrameValidation.ValidFolders);
        }

        [Fact]
        public void ValidFolders_ContainsGroupedPortraitImage3()
        {
            Assert.Contains("grouped-portrait-images/image3", DeskPictureFrameValidation.ValidFolders);
        }

        [Fact]
        public void ValidFolders_ContainsGroupedPortraitImage4()
        {
            Assert.Contains("grouped-portrait-images/image4", DeskPictureFrameValidation.ValidFolders);
        }

        [Fact]
        public void ValidFolders_ContainsWallLandscapeImage()
        {
            Assert.Contains("wall-landscape-image/image", DeskPictureFrameValidation.ValidFolders);
        }

        [Fact]
        public void ValidFolders_ContainsWallPortraitImage()
        {
            Assert.Contains("wall-portrait-image/image", DeskPictureFrameValidation.ValidFolders);
        }

        [Fact]
        public void ValidFolders_DoesNotContainInvalidEntries()
        {
            foreach (var folder in DeskPictureFrameValidation.ValidFolders)
            {
                Assert.DoesNotContain("..", folder);
                Assert.False(System.IO.Path.IsPathRooted(folder));
            }
        }
    }

    public class EdgeCaseValidationTests
    {
        [Fact]
        public void WindowsPathSeparator_FailsPathTraversal()
        {
            // Backslashes should not bypass validation on Linux
            Assert.True(DeskPictureFrameValidation.IsImageKeyPathSafe("folder\\file"));
        }

        [Fact]
        public void SingleDot_DoesNotTriggerTraversal()
        {
            // Single dot is not path traversal
            Assert.True(DeskPictureFrameValidation.IsImageKeyPathSafe("folder/./file"));
        }

        [Fact]
        public void DoubleDotInName_TriggersTraversal()
        {
            // Even if part of a name, double dots are blocked
            Assert.False(DeskPictureFrameValidation.IsImageKeyPathSafe("folder..name/file"));
        }

        [Fact]
        public void VeryLongKey_IsPathSafe()
        {
            string longKey = new string('a', 1000) + "/file";
            Assert.True(DeskPictureFrameValidation.IsImageKeyPathSafe(longKey));
        }

        [Fact]
        public void ValidateUploadPacket_NullImageData_ReturnsNonPng()
        {
            string result = DeskPictureFrameValidation.ValidateUploadPacket(
                "desk-landscape-images/image1/1",
                null);
            Assert.Contains("non-PNG", result);
        }

        [Fact]
        public void ValidateUploadPacket_EmptyImageData_ReturnsNonPng()
        {
            string result = DeskPictureFrameValidation.ValidateUploadPacket(
                "desk-landscape-images/image1/1",
                new byte[0]);
            Assert.Contains("non-PNG", result);
        }
    }
}
