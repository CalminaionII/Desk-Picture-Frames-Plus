using System;
using System.IO;
using System.Text.RegularExpressions;

namespace DeskPictureFrame
{
    /// <summary>
    /// Pure validation and helper logic extracted for testability.
    /// Used by DeskPictureFrameNetworkManager for sanitising network packets.
    /// </summary>
    public static class DeskPictureFrameValidation
    {
        public static readonly string[] ValidFolders =
        {
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

        public const int MaxImageSizeBytes = 1_000_000;
        public const int MaxImagesPerPlayer = 80;

        /// <summary>
        /// Validates an image key for path traversal and null checks.
        /// </summary>
        public static bool IsImageKeyPathSafe(string imageKey)
        {
            if (string.IsNullOrEmpty(imageKey))
                return false;
            if (imageKey.Contains(".."))
                return false;
            if (Path.IsPathRooted(imageKey))
                return false;
            return true;
        }

        /// <summary>
        /// Validates that the filename portion of an image key is a number 1-5.
        /// </summary>
        public static bool IsValidImageFileName(string imageKey)
        {
            if (string.IsNullOrEmpty(imageKey))
                return false;

            string fileName = Path.GetFileName(imageKey);
            return Regex.IsMatch(fileName, @"^[1-5]$");
        }

        /// <summary>
        /// Validates that the folder portion of an image key is in the allowed list.
        /// </summary>
        public static bool IsValidImageFolder(string imageKey)
        {
            if (string.IsNullOrEmpty(imageKey))
                return false;

            int lastSlash = imageKey.LastIndexOf('/');
            if (lastSlash < 0)
                return false;

            string folder = imageKey.Substring(0, lastSlash);

            foreach (var validFolder in ValidFolders)
            {
                if (folder == validFolder)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Validates PNG header bytes (first 4 bytes: 0x89, 0x50, 0x4E, 0x47).
        /// </summary>
        public static bool HasValidPngHeader(byte[] data)
        {
            if (data == null || data.Length < 8)
                return false;

            return data[0] == 0x89
                && data[1] == 0x50
                && data[2] == 0x4E
                && data[3] == 0x47;
        }

        /// <summary>
        /// Validates image data size is within the allowed limit.
        /// </summary>
        public static bool IsWithinSizeLimit(byte[] data)
        {
            if (data == null)
                return false;
            return data.Length <= MaxImageSizeBytes;
        }

        /// <summary>
        /// Performs all upload validation checks. Returns null if valid,
        /// or a rejection reason string if invalid.
        /// </summary>
        public static string ValidateUploadPacket(string imageKey, byte[] imageData)
        {
            if (!IsImageKeyPathSafe(imageKey))
                return "suspicious image key (path traversal or null)";

            if (!IsValidImageFileName(imageKey))
                return "invalid image file name (must be 1-5)";

            if (!IsValidImageFolder(imageKey))
                return "unknown folder";

            if (!HasValidPngHeader(imageData))
                return "non-PNG data";

            if (!IsWithinSizeLimit(imageData))
                return "oversized image (exceeds 1MB)";

            return null;
        }

        /// <summary>
        /// Parses a deskframebox code path to determine the collection name.
        /// Returns null if the code path doesn't match the expected prefix.
        /// </summary>
        public static string ParseBoxCollection(string codePath)
        {
            if (string.IsNullOrEmpty(codePath))
                return null;

            const string prefix = "deskframebox-";
            if (!codePath.StartsWith(prefix))
                return null;

            return codePath.Substring(prefix.Length);
        }

        /// <summary>
        /// Builds the expected frame asset code for a given collection, index, and metal.
        /// Returns null if the collection is not recognized.
        /// </summary>
        public static string GetFrameAssetCode(string collection, int index)
        {
            switch (collection)
            {
                case "portraitsingle":
                    return $"deskpictureframe:deskportraitsingle-{index}";
                case "landscapesingle":
                    return $"deskpictureframe:desklandscapesingle-{index}";
                case "portraitwall":
                    return $"deskpictureframe:wall-portrait-{index}-north";
                case "landscapewall":
                    return $"deskpictureframe:wall-landscape-{index}-north";
                default:
                    return null;
            }
        }
    }
}
