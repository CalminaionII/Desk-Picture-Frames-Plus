using System.IO;
using Vintagestory.API.Config;

namespace DeskPictureFrame
{
    internal static class DeskPictureFrameConstants
    {
        public const string ModId = "deskpictureframe";
        public const int MaxImageSizeBytes = 1_000_000;
        public const int DefaultImageCount = 5;

        /// <summary>
        /// Canonical list of image sub-folders (relative to the textures/ root).
        /// Used for folder creation, default population, and server-side validation.
        /// </summary>
        public static readonly string[] ImageFolders =
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

        // ── Path helpers ──

        public static string ConfigFolder =>
            Path.Combine(GamePaths.ModConfig, ModId);

        public static string RemotePlayersRoot =>
            Path.Combine(GamePaths.DataPath, "ModData", ModId, "remoteplayers");

        public static string ServerCacheFolder =>
            Path.Combine(GamePaths.DataPath, "ModData", ModId, "playercache");

        public static string RemotePlayerFolder(string playerUid) =>
            Path.Combine(RemotePlayersRoot, playerUid);

        public static string RemotePlayerTexturesFolder(string playerUid) =>
            Path.Combine(RemotePlayersRoot, playerUid, "textures");

        // ── Validation helpers ──

        public static bool IsValidPngData(byte[] data) =>
            data != null && data.Length >= 8 &&
            data[0] == 0x89 && data[1] == 0x50 &&
            data[2] == 0x4E && data[3] == 0x47;

        public static bool IsValidImageKey(string imageKey) =>
            imageKey != null && !imageKey.Contains("..") && !Path.IsPathRooted(imageKey);

        public static bool IsValidImageFolder(string folder)
        {
            foreach (var f in ImageFolders)
                if (folder == f) return true;
            return false;
        }
    }
}
