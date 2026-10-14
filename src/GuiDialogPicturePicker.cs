using System;
using System.Collections.Generic;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace DeskPictureFrame
{
    /// <summary>
    /// Choose which of your pictures a frame shows.
    ///
    /// A DROPDOWN RATHER THAN A GRID OF THUMBNAILS. The grid had to solve three problems at
    /// once - scrolling, scaling each image into a tile, and fitting on screen - and did none
    /// of them well. A dropdown scrolls itself, has no thumbnails to scale, and stays the same
    /// size whether you have four pictures or four hundred. One large preview underneath shows
    /// the choice, which is all the grid was really providing.
    ///
    /// The dialog deliberately does NOT grab the mouse (PrefersUngrabbedMouse is true by
    /// default), so the frame itself stays visible behind it and updates as you choose. Alt
    /// re-grabs for normal look control - documented API behaviour, not a workaround.
    /// </summary>
    public class GuiDialogPicturePicker : GuiDialog
    {
        private const int Width = 300;
        private const int PreviewSize = 160;

        private readonly BlockPos pos;
        private readonly Block block;
        private readonly List<string> slots = new();
        private readonly Dictionary<string, string> chosen = new();

        private string activeSlot;

        /// <summary>Which subfolder of the pictures folder to show, "" for all of them. Folders
        /// come from the index keys, which are paths relative to the pictures folder, so this
        /// can only ever offer folders the player actually made in there.</summary>
        private string activeFolder = "";

        /// <summary>How many pictures were left out of the list for being the wrong shape for
        /// this slot. Reported, never silently dropped.</summary>
        private int wrongWayRound;

        /// <summary>Parallel to the picture dropdown: index 0 is "none", then one per picture.</summary>
        private string[] ids = Array.Empty<string>();

        /// <summary>The picture the remove button is armed for. Removing blanks every frame
        /// using it, for everyone, so the button asks once before it does anything - two
        /// deliberate clicks rather than a dialog on top of a dialog.</summary>
        private string armedForRemoval;

        public override string ToggleKeyCombinationCode => null;

        public GuiDialogPicturePicker(ICoreClientAPI capi, BlockPos pos, Block block,
                                      Dictionary<string, string> current) : base(capi)
        {
            this.pos = pos;
            this.block = block;

            foreach (string slot in BEDeskPictureFrame.SlotsOf(block)) slots.Add(slot);
            SortSlots(block, slots);

            if (current != null)
            {
                foreach (var pair in current) chosen[pair.Key] = pair.Value;
            }

            // An older frame carries no ids, but its picture is still recoverable from the slot
            // it used to point at - so show that as the current selection rather than nothing.
            var legacyKeys = (capi.World.BlockAccessor.GetBlockEntity(pos) as BEDeskPictureFrame)?.EffectiveLegacyKeys
                             ?? BEDeskPictureFrame.LegacyKeysFor(block);
            foreach (var pair in legacyKeys)
            {
                if (chosen.ContainsKey(pair.Key)) continue;
                string id = DeskPictureFrameImageStore.LegacyIdFor(pair.Value);
                if (id != null) chosen[pair.Key] = id;
            }

            activeSlot = slots.Count > 0 ? slots[0] : null;
        }

        /// <summary>
        /// Puts the picture slots in the order they appear on the frame, LEFT TO RIGHT.
        ///
        /// The order comes from a `slotOrder` attribute on the blocktype, because only the shape
        /// knows where each texture code actually sits and the numbers in the codes have nothing
        /// to do with it. On the three-picture grouped frame `image1` is the MIDDLE one and
        /// `image3` is on the left, so sorting by name listed them in an order that matched
        /// nothing you could see. Calm, 2026-09-04: "1 is the middle ... which doesn't make
        /// sense".
        ///
        /// The dropdown numbers entries by their position in this list, so "Picture 1" is now
        /// simply the leftmost. Nothing is stored differently - this is presentation only, and a
        /// blocktype without the attribute falls back to the old order.
        /// </summary>
        private static void SortSlots(Block block, List<string> slots)
        {
            var declared = block?.Attributes?["slotOrder"]?.AsArray<string>();
            if (declared == null || declared.Length == 0)
            {
                slots.Sort(StringComparer.Ordinal);
                return;
            }

            var position = new Dictionary<string, int>();
            for (int i = 0; i < declared.Length; i++) position[declared[i]] = i;

            // Anything the attribute forgot goes last, in its own order, rather than vanishing.
            slots.Sort((a, b) =>
            {
                int ia = position.TryGetValue(a, out int pa) ? pa : int.MaxValue;
                int ib = position.TryGetValue(b, out int pb) ? pb : int.MaxValue;
                return ia != ib ? ia.CompareTo(ib) : string.CompareOrdinal(a, b);
            });
        }

        public override void OnGuiOpened()
        {
            base.OnGuiOpened();

            // RE-READ THE FOLDER EVERY TIME THIS OPENS, so a picture dropped in while the game
            // is running shows up without a restart. It is hashed and indexed; it is cached the
            // moment a frame wants to draw it.
            DeskPictureFrameImageStore.Reindex(capi);

            // Ask the server what our allowance is and which of these pictures it already
            // holds. The answer arrives after this returns, so the dialog composes once now and
            // again when it lands - the marks appear a moment later rather than not at all.
            var net = DeskPictureFrameNetworkManager.ClientInstance;
            if (net != null)
            {
                net.OnQuotaChanged = () => capi.Event.EnqueueMainThreadTask(Compose, "dpfquotarecompose");
                net.AskWhatServerHas();
            }

            Compose();
        }

        public override void OnGuiClosed()
        {
            base.OnGuiClosed();

            // Drop the callback with the dialog. A closed picker redrawing itself on a late
            // packet is a leak at best and a compose on a disposed composer at worst.
            var net = DeskPictureFrameNetworkManager.ClientInstance;
            if (net != null) net.OnQuotaChanged = null;
        }

        /// <summary>
        /// Whether a picture's shape suits the slot being edited.
        ///
        /// The slot's own orientation comes from `attributes/slotOrientation`, per slot rather
        /// than per block: `groupedportrait` is a LANDSCAPE wall frame beside portrait desk
        /// frames, so one answer for the whole block would be wrong for three of its four
        /// pictures.
        ///
        /// A SQUARE suits both, deliberately - it is the one shape that is not wrong either
        /// way, and marking it would be noise. No orientation declared means no opinion.
        /// </summary>
        private bool FitsSlot(int width, int height)
        {
            if (width == height) return true;
            if (activeSlot == null) return true;

            string want = block?.Attributes?["slotOrientation"]?[activeSlot]?.AsString(null);
            if (want == null) return true;

            return want == (width > height ? "landscape" : "portrait");
        }

        private void Compose()
        {
            var all = new List<KeyValuePair<string, string>>(DeskPictureFrameImageStore.LocalIds);
            all.Sort((a, b) => string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase));

            // A key is the file's path relative to the pictures folder, so everything before
            // the last "/" is the folder the player filed it under. No folder means loose in
            // the top of the pictures folder.
            var folders = new List<string>();
            foreach (var pair in all)
            {
                int cut = pair.Key.LastIndexOf('/');
                string folder = cut < 0 ? "" : pair.Key.Substring(0, cut);
                if (folder != "" && !folders.Contains(folder)) folders.Add(folder);
            }
            folders.Sort(StringComparer.OrdinalIgnoreCase);

            if (activeFolder != "" && !folders.Contains(activeFolder)) activeFolder = "";

            // Only pictures that suit this slot are listed: a landscape frame lists landscape
            // pictures, an upright frame upright ones, and a square suits both. Counted rather
            // than silently dropped - the note at the foot says how many are hidden and why,
            // so a picture you know you added is never just missing.
            var pictures = new List<KeyValuePair<string, string>>();
            wrongWayRound = 0;

            foreach (var pair in all)
            {
                int cut = pair.Key.LastIndexOf('/');
                string folder = cut < 0 ? "" : pair.Key.Substring(0, cut);
                if (activeFolder != "" && folder != activeFolder) continue;

                if (DeskPictureFrameImageStore.LocalSizes.TryGetValue(pair.Value, out var size)
                    && !FitsSlot(size.Width, size.Height))
                {
                    wrongWayRound++;
                    continue;
                }

                pictures.Add(pair);
            }

            // Index 0 is "no picture", so clearing a slot is just another choice in the list
            // rather than a separate button.
            var values = new List<string> { "" };
            var names = new List<string> { Lang.Get("deskpictureframe:picker-none") };
            foreach (var pair in pictures)
            {
                values.Add(pair.Value);
                // Inside one folder the prefix is on every row and only costs width.
                string name = activeFolder == "" ? pair.Key
                                                 : pair.Key.Substring(activeFolder.Length + 1);

                // Size on the row itself: which of two similar pictures is the big one is
                // exactly what you want to know while choosing, not after.
                if (DeskPictureFrameImageStore.LocalSizes.TryGetValue(pair.Value, out var size))
                {
                    name += $"   {size.Width} x {size.Height}";
                }

                // A picture the server already holds stays usable at the limit - it costs
                // nothing to point another frame at it - so only the ones that would need
                // storing are marked. Marking everything would be simpler and wrong.
                if (DeskPictureFrameNetworkManager.ClientInstance?.WouldBeRefused(pair.Value) == true)
                {
                    name = Lang.Get("deskpictureframe:picker-blocked-mark") + " " + name;
                }

                names.Add(name);
            }
            ids = values.ToArray();

            string currentId = activeSlot != null && chosen.TryGetValue(activeSlot, out string c) ? c : "";
            int selected = Math.Max(0, Array.IndexOf(ids, currentId));

            // An explicit Y cursor, not chained BelowCopy/RightCopy - two chains from different
            // bases advance by different totals and quietly overlap (shared reference sect.10e).
            int y = (int)GuiStyle.TitleBarHeight + 12;

            // The background MUST size itself from its children. ElementBounds.Fill inside an
            // autosized dialog resolves to ZERO, and Cairo blurs the dialog background at
            // compose time - blurring a zero-size surface reads out of bounds and kills the
            // process outright. That was a real crash, not a theoretical one.
            var bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
            bgBounds.BothSizing = ElementSizing.FitToChildren;

            var composer = capi.Gui
                .CreateCompo("dpfpicturepicker" + pos, ElementStdBounds.AutosizedMainDialog
                                                          .WithAlignment(EnumDialogArea.CenterMiddle))
                .AddShadedDialogBG(bgBounds)
                .AddDialogTitleBar(Lang.Get("deskpictureframe:picker-title"), () => TryClose())
                .BeginChildElements(bgBounds);

            // Only ask which picture when the frame actually holds more than one.
            if (slots.Count > 1)
            {
                var slotNames = new string[slots.Count];
                var slotValues = new string[slots.Count];
                for (int i = 0; i < slots.Count; i++)
                {
                    slotValues[i] = slots[i];
                    // Just the number. The orientation is on the "Show this picture" heading,
                    // which follows the selected slot - saying it twice only crowds the row.
                    slotNames[i] = Lang.Get("deskpictureframe:picker-slot-n", i + 1);
                }

                composer.AddStaticText(Lang.Get("deskpictureframe:picker-slot"),
                    CairoFont.WhiteSmallText(), ElementBounds.Fixed(0, y, Width, 22));
                y += 26;

                composer.AddDropDown(slotValues, slotNames, Math.Max(0, slots.IndexOf(activeSlot)),
                    (code, on) =>
                    {
                        if (!on || code == activeSlot) return;
                        activeSlot = code;
                        // Deferred: recomposing from inside an element's own handler replaces
                        // the composer currently driving it.
                        capi.Event.EnqueueMainThreadTask(Compose, "dpfrecompose");
                    },
                    ElementBounds.Fixed(0, y, Width, 30), "slotdd");
                y += 40;
            }

            // Only worth showing once the player has actually made folders in there.
            if (folders.Count > 0)
            {
                var folderValues = new List<string> { "" };
                var folderNames = new List<string> { Lang.Get("deskpictureframe:picker-folder-all") };
                foreach (string folder in folders)
                {
                    folderValues.Add(folder);
                    folderNames.Add(folder);
                }

                composer.AddStaticText(Lang.Get("deskpictureframe:picker-folder"),
                    CairoFont.WhiteSmallText(), ElementBounds.Fixed(0, y, Width, 22));
                y += 26;

                composer.AddDropDown(folderValues.ToArray(), folderNames.ToArray(),
                    Math.Max(0, folderValues.IndexOf(activeFolder)),
                    (code, on) =>
                    {
                        if (!on || code == activeFolder) return;
                        activeFolder = code;
                        // Deferred, as with the slot dropdown: recomposing from inside an
                        // element's own handler replaces the composer driving it.
                        capi.Event.EnqueueMainThreadTask(Compose, "dpfrecompose");
                    },
                    ElementBounds.Fixed(0, y, Width, 30), "folderdd");
                y += 40;
            }

            // Say what shape this slot wants, right above the list that only offers that shape.
            // On a single-picture frame there is no slot dropdown to carry it, so without this
            // the filtering has nothing anywhere explaining itself.
            string heading = Lang.Get("deskpictureframe:picker-pictures");
            string slotWant = activeSlot == null ? null
                : block?.Attributes?["slotOrientation"]?[activeSlot]?.AsString(null);
            if (slotWant != null)
            {
                heading += "  " + Lang.Get("deskpictureframe:picker-" + slotWant);
            }

            composer.AddStaticText(heading,
                CairoFont.WhiteSmallText(), ElementBounds.Fixed(0, y, Width, 22));
            y += 26;

            composer.AddDropDown(ids, names.ToArray(), selected,
                (code, on) =>
                {
                    if (!on) return;
                    Choose(code);
                },
                ElementBounds.Fixed(0, y, Width, 30), "picturedd");
            y += 42;

            // One large preview, centred. This is all the thumbnail grid was really giving,
            // and at this size a picture is actually recognisable.
            var previewBounds = ElementBounds.Fixed((Width - PreviewSize) / 2, y, PreviewSize, PreviewSize);
            composer.AddInset(previewBounds.ForkBoundingParent(4, 4, 4, 4), 4);

            // THE TEST IS "IS THERE A FILE TO DRAW", not "is it cached". Since the cache is
            // filled on demand, one of your own pictures is not in it until a frame has asked
            // for it - so a ClientHas check here showed "no picture" for everything you owned
            // until you selected it and the frame pulled it in. Your own folder first, the cache
            // second, and the preview appears either way.
            string previewFile = null;
            if (currentId != "")
            {
                if (DeskPictureFrameImageStore.LocalPaths.TryGetValue(currentId, out string own)
                    && System.IO.File.Exists(own))
                {
                    previewFile = own;
                }
                else if (DeskPictureFrameImageStore.ClientHas(currentId))
                {
                    previewFile = DeskPictureFrameImageStore.ClientImagePath(currentId);
                }
            }

            if (previewFile != null)
            {
                string file = previewFile;
                composer.AddStaticCustomDraw(previewBounds,
                    (ctx, surface, bounds) => DrawFitted(ctx, bounds, file));
            }
            else
            {
                // Centred on the dialog rather than positioned by hand. The preview box is
                // itself centred, so CenterFixed lines the two up with no arithmetic that can
                // drift if either width changes - which is exactly how it ended up off-centre.
                composer.AddStaticText(Lang.Get("deskpictureframe:picker-none"),
                    CairoFont.WhiteSmallText().WithOrientation(EnumTextOrientation.Center),
                    ElementBounds.Fixed(EnumDialogArea.CenterFixed, 0,
                                        y + PreviewSize / 2 - 12, PreviewSize, 24));
            }
            y += PreviewSize + 16;

            // An empty list has three quite different causes, and "drop some PNG files in" is
            // only right for one of them. Saying it when the player HAS pictures - just none
            // this frame will take - reads as though the mod has lost them.
            if (pictures.Count == 0)
            {
                string message;

                if (all.Count == 0)
                {
                    // From "ModData" onwards, never the whole absolute path. A launcher install
                    // buries the game under a folder name and a hex instance id, which wrapped
                    // to four lines and ran over the note underneath.
                    string full = DeskPictureFrameImageStore.PicturesRoot();
                    int cut = full.IndexOf("ModData", StringComparison.OrdinalIgnoreCase);
                    message = Lang.Get("deskpictureframe:picker-empty",
                                       cut >= 0 ? full.Substring(cut) : full);
                }
                else if (wrongWayRound > 0 && slotWant != null)
                {
                    message = Lang.Get("deskpictureframe:picker-empty-orientation",
                                       Lang.Get("deskpictureframe:word-" + slotWant));
                }
                else
                {
                    message = Lang.Get("deskpictureframe:picker-empty-folder");
                }

                composer.AddStaticText(message, CairoFont.WhiteDetailText(),
                    ElementBounds.Fixed(0, y, Width, 66));
                y += 70;
            }

            // What a picture has to BE. Says it in the one place the question comes up, rather
            // than only in a log line nobody reads.
            string note = Lang.Get("deskpictureframe:picker-format",
                                   DeskPictureFrameImageStore.MaxLongSide,
                                   DeskPictureFrameImageStore.MaxShortSide);

            // Where you stand, whenever a server has told us. Shown always rather than only
            // when full: a limit you can see coming is a limit; one you meet by surprise is a
            // bug report.
            var netState = DeskPictureFrameNetworkManager.ClientInstance;
            if (netState?.QuotaKnown == true)
            {
                composer.AddStaticText(
                    Lang.Get("deskpictureframe:picker-quota", netState.QuotaUsed, netState.QuotaMax),
                    CairoFont.WhiteDetailText(), ElementBounds.Fixed(0, y, Width, 22));
                y += 24;
            }

            // REMOVE THE PICTURE THIS FRAME IS SHOWING. No list of its own: the frame you
            // opened the picker on is the choice, which is how you got here.
            //
            // It works for a picture you have already deleted from your own folder, which is the
            // case that used to fail - the id comes from the frame and the permission comes from
            // the server, so neither depends on the file still being in your pictures folder.
            string currentPic = activeSlot != null && chosen.TryGetValue(activeSlot, out string cur)
                ? cur : null;

            if (netState?.CanRelease(currentPic) == true)
            {
                bool armed = armedForRemoval == currentPic;
                composer.AddSmallButton(
                    Lang.Get(armed ? "deskpictureframe:picker-release-confirm"
                                   : "deskpictureframe:picker-release"),
                    () => ReleaseCurrent(currentPic),
                    ElementBounds.Fixed(0, y, Width, 26));
                y += 30;

                if (armed)
                {
                    composer.AddStaticText(Lang.Get("deskpictureframe:picker-release-warning"),
                        CairoFont.WhiteDetailText(), ElementBounds.Fixed(0, y, Width, 44));
                    y += 48;
                }
            }
            else armedForRemoval = null;

            int ignored = DeskPictureFrameImageStore.IgnoredFileCount;
            int oversized = DeskPictureFrameImageStore.OversizedCount;
            int toobig = DeskPictureFrameImageStore.TooBigCount;
            // NO "n not shown" line. The heading above the list already reads "Show this
            // picture: (portrait)", so a picture missing from a portrait frame's list explains
            // itself - a warning as well is just noise on a screen that has enough text on it.
            // The count is still used to word the message when the list comes out empty.
            if (ignored > 0) note += " " + Lang.Get("deskpictureframe:picker-ignored", ignored);
            if (oversized > 0) note += " " + Lang.Get("deskpictureframe:picker-oversized", oversized);
            // Its own line, not folded into "oversized" - the two need different fixes.
            if (toobig > 0) note += " " + Lang.Get("deskpictureframe:picker-toobig", toobig);

            int lines = 2 + (ignored > 0 ? 1 : 0) + (oversized > 0 ? 1 : 0) + (toobig > 0 ? 1 : 0);
            composer.AddStaticText(note, CairoFont.WhiteDetailText(),
                ElementBounds.Fixed(0, y, Width, lines * 22));
            y += lines * 22 + 6;

            SingleComposer = composer.EndChildElements().Compose();
        }

        /// <summary>Paints a picture scaled to fit, centred, aspect preserved.
        ///
        /// Drawn rather than used as a background image: AddImageBG paints at the texture's
        /// native size and crops, so a 1024px photo showed a postage stamp of its middle.</summary>
        private static void DrawFitted(Context ctx, ElementBounds bounds, string file)
        {
            try
            {
                using var img = new ImageSurface(file);
                if (img.Width <= 0 || img.Height <= 0) return;

                double scale = Math.Min(bounds.InnerWidth / img.Width, bounds.InnerHeight / img.Height);

                ctx.Save();
                ctx.Translate(bounds.drawX + (bounds.InnerWidth - img.Width * scale) / 2,
                              bounds.drawY + (bounds.InnerHeight - img.Height * scale) / 2);
                ctx.Scale(scale, scale);
                ctx.SetSource(img, 0, 0);
                ctx.Paint();
                ctx.Restore();
            }
            catch (Exception)
            {
                // A picture that will not decode simply does not draw; the dialog is unaffected.
            }
        }

        /// <summary>First click arms and explains, second click does it. The picture is also
        /// cleared from this frame - keeping it selected would leave the slot pointing at
        /// something the server has just been told to stop holding.</summary>
        private bool ReleaseCurrent(string id)
        {
            if (armedForRemoval != id)
            {
                armedForRemoval = id;
                capi.Event.EnqueueMainThreadTask(Compose, "dpfarmrelease");
                return true;
            }

            armedForRemoval = null;
            DeskPictureFrameNetworkManager.ClientInstance?.ReleaseImage(id);

            // If this frame was showing it, clear the slot too - leaving it selected would point
            // the frame at something the server has just been told to let go of.
            if (activeSlot != null && chosen.TryGetValue(activeSlot, out string cur) && cur == id)
            {
                chosen.Remove(activeSlot);
                DeskPictureFrameNetworkManager.ClientInstance?.SendFrameImages(pos, chosen);
            }

            capi.Event.EnqueueMainThreadTask(Compose, "dpfdidrelease");
            return true;
        }

        private void Choose(string id)
        {
            if (activeSlot == null) return;

            // Caught here as well as marked in the list. The mark is the warning; this is what
            // makes it true, and it saves a round trip to be told no by the server.
            var net = DeskPictureFrameNetworkManager.ClientInstance;
            if (net?.WouldBeRefused(id) == true)
            {
                capi.TriggerIngameError(this, "dpfquotafull",
                    Lang.Get("deskpictureframe:picker-blocked", net.QuotaMax));
                capi.Event.EnqueueMainThreadTask(Compose, "dpfrecompose");
                return;
            }

            if (string.IsNullOrEmpty(id)) chosen.Remove(activeSlot);
            else chosen[activeSlot] = id;

            DeskPictureFrameNetworkManager.ClientInstance?.SendFrameImages(pos, chosen);
            capi.Event.EnqueueMainThreadTask(Compose, "dpfrecompose");
        }
    }
}
