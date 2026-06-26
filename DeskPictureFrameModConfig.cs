using System;
using Vintagestory.API.Common;

namespace DeskPictureFrame
{
    internal class DeskPictureFrameModConfig
    {
        public bool EnableTraderSpawn { get; set; } = true;
        public string EnableTraderSpawnComment { get; set; } = "Enable or disable the Desk Picture Frame trader spawning in world generation";

        public static DeskPictureFrameModConfig Current { get; private set; }

        public static void Load(ICoreAPI api)
        {
            try
            {
                Current = api.LoadModConfig<DeskPictureFrameModConfig>("deskpictureframeconfig.json");
                if (Current == null)
                {
                    Current = new DeskPictureFrameModConfig();
                }
                api.StoreModConfig(Current, "deskpictureframeconfig.json");
            }
            catch (Exception e)
            {
                api.Logger.Error("[DeskPictureFrame] Failed to load config: {0}", e.Message);
                Current = new DeskPictureFrameModConfig();
            }
        }
    }
}
