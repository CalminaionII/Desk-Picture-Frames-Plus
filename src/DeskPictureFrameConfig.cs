using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Vintagestory.API.Common;

namespace DeskPictureFrame
{
    /// <summary>
    /// The mod's settings, saved to ModConfig/DeskPictureFrameConfig.json.
    ///
    /// **SPLIT INTO "Client" AND "Server" SECTIONS**, so it is obvious at a glance which settings
    /// do anything on the machine reading them. A flat list of six numbers gave a server owner no
    /// way to tell that two of them were only ever about their own game.
    ///
    /// **EVERY SETTING IS PRECEDED BY A `...Comment` LINE.** JSON has no comment syntax, so the
    /// explanation has to be a value like any other - the same approach Universal Display uses,
    /// and the reason a player can open this file and understand it without the documentation.
    /// The comments are REWRITTEN FROM THE CODE on every load, so they always describe the build
    /// actually running rather than whatever an older version wrote there.
    ///
    /// A NEW FILE, deliberately not the old `deskpictureframeconfig.json`. That one held a single
    /// `EnableTraderSpawn` flag which never worked and was removed in 1.0.2 - see the note in
    /// <see cref="DeskPictureFrame.StartPre"/>. Orphaned copies are still on existing installs,
    /// so reusing the name would mean reading a file written by a version that meant something
    /// else by it.
    ///
    /// RENAME A PROPERTY, NEVER RETYPE ONE. Changing an int to a bool (or the reverse) throws on
    /// deserialise, LoadOrCreate catches the throw and hands back a fresh default object, and
    /// **every other setting the player had is silently wiped**. If the meaning of a setting has
    /// to change, add a new property under a new name and leave the old one alone.
    ///
    /// WHAT IS NOT HERE IS AS DELIBERATE AS WHAT IS. The 1 MB byte cap and the PNG-only rule are
    /// not settings and must not become settings: they are what stops one player handing
    /// arbitrary bytes to everyone else who can see the frame. A server owner may decide how many
    /// pictures to hold and how big they may be on screen; nobody decides that this mod will
    /// carry something other than a small PNG. See <see cref="DeskPictureFrameNetworkManager"/>.
    /// </summary>
    public class DeskPictureFrameConfig
    {
        public ClientSettings Client { get; set; } = new ClientSettings();
        public ServerSettings Server { get; set; } = new ServerSettings();

        // ---- the comment text, in one place ------------------------------------------------
        //
        // Held as constants and written into the file on every load, so improving the wording
        // here updates every existing config rather than only new ones.

        public const string ClientAbout =
            "These only affect YOUR game. No server reads them and no server changes them.";

        public const string MaxPicturesAbout =
            "How many of your own pictures are loaded from your pictures folder (1 to 5000). " +
            "Every one is read and copied at startup, so a big number means a slower launch and " +
            "a longer list in the picker.";

        public const string MaxCachedImagesAbout =
            "How many pictures to keep in the local cache (50 to 100000). The cache holds a copy " +
            "of your own pictures plus everything downloaded from servers, and downloads would " +
            "otherwise build up forever. Over the limit the oldest downloads go first; your own " +
            "pictures are never removed. Anything removed comes back when it is next needed.";

        public const string ServerAbout =
            "These shape a SERVER. They apply to your own singleplayer worlds too, since the " +
            "game hosts one - but on a server you join, that server's values are the ones used.";

        public const string MaxImagesPerPlayerAbout =
            "How many pictures one player may have on this server (1 or more). It is simply how " +
            "many files are in their folder under ModData/deskpictureframe/images, so deleting " +
            "one gives them room for another straight away. Nothing else gives room back.";

        public const string MaxServerImagesAbout =
            "How many pictures this server will hold in total, across everybody (1 or more).";

        public const string PictureSizeAbout =
            "Largest picture allowed, in pixels: LongSide 16 to 1920, ShortSide 16 to 1080. " +
            "A ceiling, not a required size - smaller is fine and is not stretched, and it works " +
            "either way round, so 480 x 320 also allows 320 x 480. Pictures must ALSO be PNG and " +
            "1 MB or less, which cannot be changed. Raising this costs memory for every player " +
            "who can see the frame, not just whoever added the picture.";

        /// <summary>Settings that only ever affect the machine they are on.</summary>
        public class ClientSettings
        {
            [Category("Client")]
            [Description(ClientAbout)]
            public string About { get; set; } = ClientAbout;

            [Category("Client")]
            [Description(MaxPicturesAbout)]
            public string MaxPicturesComment { get; set; } = MaxPicturesAbout;

            /// <summary>Most pictures loaded from your own pictures folder. Bounds your dropdown
            /// and your launch time, and nothing else.</summary>
            [Category("Client")]
            [Display(Name = "Max pictures", Description = MaxPicturesAbout)]
            [Range(1, 5000)]
            public int MaxPictures { get; set; } = 250;

            [Category("Client")]
            [Description(MaxCachedImagesAbout)]
            public string MaxCachedImagesComment { get; set; } = MaxCachedImagesAbout;

            /// <summary>Most pictures kept in the local image cache.
            ///
            /// **THE ONLY FOLDER IN THE MOD THAT WOULD OTHERWISE GROW FOREVER.** Evicting is
            /// always safe: the cache is derived, so anything dropped is rebuilt from your own
            /// pictures folder at the next launch or fetched again from the server the next time
            /// you look at a frame using it. Your own pictures are never evicted.</summary>
            [Category("Client")]
            [Display(Name = "Max cached images", Description = MaxCachedImagesAbout)]
            [Range(50, 100000)]
            public int MaxCachedImages { get; set; } = 2000;
        }

        /// <summary>Settings that shape a server.</summary>
        public class ServerSettings
        {
            [Category("Server")]
            [Description(ServerAbout)]
            public string About { get; set; } = ServerAbout;

            [Category("Server")]
            [Description(MaxImagesPerPlayerAbout)]
            public string MaxImagesPerPlayerComment { get; set; } = MaxImagesPerPlayerAbout;

            /// <summary>Most pictures ONE PLAYER may have here. Not a remembered number: it is how
            /// many files are in that player's folder, counted when asked. Room comes from
            /// removing a picture or from raising this - nothing else, so the folder and the count
            /// can never disagree.</summary>
            [Category("Server")]
            [Display(Name = "Max images per player", Description = MaxImagesPerPlayerAbout)]
            [Range(1, 1000000)]
            public int MaxImagesPerPlayer { get; set; } = 100;

            [Category("Server")]
            [Description(MaxServerImagesAbout)]
            public string MaxServerImagesComment { get; set; } = MaxServerImagesAbout;

            [Category("Server")]
            [Display(Name = "Max server images", Description = MaxServerImagesAbout)]
            [Range(1, 1000000)]
            public int MaxServerImages { get; set; } = 5000;

            [Category("Server")]
            [Description(PictureSizeAbout)]
            public string PictureSizeComment { get; set; } = PictureSizeAbout;

            /// <summary>Largest a picture may be, in pixels.
            ///
            /// **THE SERVER'S VALUES WIN.** On joining, the server's caps replace the client's for
            /// that session, so the picker warns with the figure that will actually be enforced.
            /// A client's own values apply to indexing its own folder until then.
            ///
            /// RAISING THIS COSTS EVERY PLAYER who can see the frame, not just whoever supplied
            /// the picture: each one becomes a real texture in the block atlas. The 1 MB byte cap
            /// does not save you - 1 MB of PNG can easily be 6000x4000.</summary>
            [Category("Server")]
            [Display(Name = "Max long side", Description = PictureSizeAbout)]
            [Range(16, 1920)]
            public int MaxLongSide { get; set; } = 480;

            [Category("Server")]
            [Display(Name = "Max short side", Description = PictureSizeAbout)]
            [Range(16, 1080)]
            public int MaxShortSide { get; set; } = 320;
        }

        public const string FileName = "DeskPictureFrameConfig.json";

        /// <summary>Hard bounds on what may be typed into the file. A setting is a dial, not a way
        /// to reach a state the mod cannot survive, and zero pictures would look like the mod is
        /// broken. Out-of-range values are pulled back and the change is logged, rather than
        /// refused silently or allowed through.
        ///
        /// **THE PIXEL CEILING IS 1920x1080.** A picture becomes a texture in the block atlas,
        /// whose PAGE SIZE varies by machine and graphics settings (4096x2048 on Calm's). A cap
        /// that some players' hardware silently refuses is worse than a slightly low one, because
        /// the failure looks like a picture that will not show with nothing to explain it.
        /// 1920x1080 is a size everyone recognises, fits the common page height of 2048, and is
        /// far larger than a frame ever renders at.
        ///
        /// It is also close to academic: at 1 MB per file, only unusually compressible pictures
        /// reach anywhere near it. Raise it only with evidence from more than one machine.</summary>
        private const int MinPictures = 1, MaxPicturesLimit = 5000;
        private const int MinCached = 50, MaxCachedLimit = 100_000;
        private const int MinServerImages = 1, MaxServerImagesLimit = 1_000_000;
        private const int MinPerPlayer = 1, MaxPerPlayerLimit = 1_000_000;
        private const int MinSide = 16, MaxLongSideLimit = 1920, MaxShortSideLimit = 1080;

        /// <summary>
        /// Load, falling back to defaults, and always write the file back so a player can see
        /// every setting that exists rather than having to guess the property names.
        /// </summary>
        public static DeskPictureFrameConfig LoadOrCreate(ICoreAPI api)
        {
            DeskPictureFrameConfig config = null;

            try
            {
                config = api.LoadModConfig<DeskPictureFrameConfig>(FileName);
            }
            catch (Exception e)
            {
                // A malformed file must not stop the mod loading. Say so loudly, though - the
                // player has just silently lost their settings and the next save will overwrite
                // the file they were trying to edit.
                api.Logger.Error("[DeskPictureFrame] Could not read {0}, using defaults: {1}", FileName, e);
            }

            if (config == null) config = new DeskPictureFrameConfig();

            // A file written before the sections existed deserialises with them missing rather
            // than failing, so fill them in instead of dereferencing null.
            if (config.Client == null) config.Client = new ClientSettings();
            if (config.Server == null) config.Server = new ServerSettings();

            config.RefreshComments();
            config.Clamp(api);
            api.StoreModConfig(config, FileName);

            return config;
        }

        /// <summary>Puts the current explanations back, overwriting whatever the file held.
        ///
        /// The comments are documentation that happens to live in a data file. Loading them like
        /// settings would freeze an old build's wording into every existing config - and a player
        /// editing one has changed nothing but their own copy of the manual.</summary>
        private void RefreshComments()
        {
            Client.About = ClientAbout;
            Client.MaxPicturesComment = MaxPicturesAbout;
            Client.MaxCachedImagesComment = MaxCachedImagesAbout;

            Server.About = ServerAbout;
            Server.MaxImagesPerPlayerComment = MaxImagesPerPlayerAbout;
            Server.MaxServerImagesComment = MaxServerImagesAbout;
            Server.PictureSizeComment = PictureSizeAbout;
        }

        /// <summary>True if the most recent load had to correct at least one value. Read by the
        /// config-manager reload path, which then asks the manager to re-read the file so its
        /// panel shows the corrected number rather than what was typed.</summary>
        public bool WasClamped { get; private set; }

        private void Clamp(ICoreAPI api)
        {
            var before = (Client.MaxPictures, Client.MaxCachedImages, Server.MaxImagesPerPlayer,
                          Server.MaxServerImages, Server.MaxLongSide, Server.MaxShortSide);

            Client.MaxPictures = Bound(api, "Client.MaxPictures", Client.MaxPictures,
                                       MinPictures, MaxPicturesLimit);
            Client.MaxCachedImages = Bound(api, "Client.MaxCachedImages", Client.MaxCachedImages,
                                           MinCached, MaxCachedLimit);

            Server.MaxImagesPerPlayer = Bound(api, "Server.MaxImagesPerPlayer",
                                              Server.MaxImagesPerPlayer, MinPerPlayer, MaxPerPlayerLimit);
            Server.MaxServerImages = Bound(api, "Server.MaxServerImages", Server.MaxServerImages,
                                           MinServerImages, MaxServerImagesLimit);
            Server.MaxLongSide = Bound(api, "Server.MaxLongSide", Server.MaxLongSide,
                                       MinSide, MaxLongSideLimit);
            Server.MaxShortSide = Bound(api, "Server.MaxShortSide", Server.MaxShortSide,
                                        MinSide, MaxShortSideLimit);

            // Written the wrong way round is a typo, not an instruction. Swapping is right because
            // the test accepts a picture in EITHER orientation, so "480 short, 320 long" would
            // otherwise quietly mean a 480x480 square passes - the opposite of what someone
            // tightening the cap intended.
            if (Server.MaxShortSide > Server.MaxLongSide)
            {
                api.Logger.Warning(
                    "[DeskPictureFrame] {0} has Server.MaxShortSide ({1}) larger than " +
                    "Server.MaxLongSide ({2}); using them the other way round.",
                    FileName, Server.MaxShortSide, Server.MaxLongSide);
                (Server.MaxLongSide, Server.MaxShortSide) = (Server.MaxShortSide, Server.MaxLongSide);
            }

            WasClamped = before != (Client.MaxPictures, Client.MaxCachedImages, Server.MaxImagesPerPlayer,
                                    Server.MaxServerImages, Server.MaxLongSide, Server.MaxShortSide);
        }

        private static int Bound(ICoreAPI api, string name, int value, int min, int max)
        {
            if (value >= min && value <= max) return value;

            int fixedUp = value < min ? min : max;
            api.Logger.Warning(
                "[DeskPictureFrame] {0} in {1} must be between {2} and {3}; using {4} instead of {5}.",
                name, FileName, min, max, fixedUp, value);
            return fixedUp;
        }
    }
}
