using System;
using System.Collections.Generic;
using System.IO;

namespace DeskPictureFrame
{
    /// <summary>
    /// How much of the shared picture store each player is using.
    ///
    /// **THE FOLDER IS THE COUNT. THERE IS NO LEDGER.** A player's pictures are the files in
    /// `images/&lt;their name&gt;/`, and their usage is how many there are. Nothing is remembered,
    /// so nothing can disagree with what is on disk: delete a file from that folder - by hand,
    /// from the picker, or with an admin command - and they have room for another immediately,
    /// with nothing to run and nothing to reconcile.
    ///
    /// **That rule cost a rewrite to learn.** The first version kept a record of who supplied
    /// what, let several players claim one image, and had an admin command that handed back
    /// allowance without deleting anything. All three let the count move while the folder did
    /// not. Calm, 2026-09-04: "if the button gives an allowance back but not remove the image
    /// that idea doesn't work". Anything that frees a slot now deletes a file, and nothing else
    /// frees a slot.
    ///
    /// **Each player keeps their OWN COPY of a picture**, even one another player already sent.
    /// It costs at most a megabyte, in a case that needs byte-identical files, and it buys the
    /// thing that matters: your pictures are yours to delete, and deleting one cannot blank
    /// somebody else's frame. That was the last exception to the folder being the truth, and his
    /// question - "the same image file, if it is in the separate folders would that matter?" -
    /// is what closed it.
    ///
    /// Two consequences, taken deliberately:
    /// * **Pictures from before this layout sit loose in the store root** and count against
    ///   nobody, because nothing knows who sent them. They still resolve and still render, and
    ///   the next player to send one adopts it into their folder.
    /// * **A renamed player gets a new folder.** Their old pictures keep working and stop being
    ///   counted. The alternative is folders named after UIDs, which defeats the point of having
    ///   folders at all.
    /// </summary>
    public static class DeskPictureFrameQuota
    {
        /// <summary>Every picture in a player's folder: id, readable label, when the file was
        /// written, how big it is, and where. Read fresh every time - a directory listing is
        /// cheap, and being right matters more here than being fast.</summary>
        public static List<(string Id, string Label, DateTime When, long Bytes, string Path)>
            PicturesOf(string playerName)
        {
            var list = new List<(string, string, DateTime, long, string)>();

            string folder = DeskPictureFrameImageStore.PlayerFolder(playerName);
            if (folder == null || !Directory.Exists(folder)) return list;

            try
            {
                foreach (string file in Directory.GetFiles(folder, "*.png"))
                {
                    string id = DeskPictureFrameImageStore.IdOfFileName(file);
                    if (id == null) continue;      // not one of ours; leave it alone

                    var info = new FileInfo(file);
                    list.Add((id, DeskPictureFrameImageStore.LabelOfFileName(file),
                              info.LastWriteTimeUtc, info.Length, file));
                }
            }
            catch (Exception) { }

            list.Sort((a, b) => string.Compare(a.Item2 ?? a.Item1, b.Item2 ?? b.Item1,
                                               StringComparison.OrdinalIgnoreCase));
            return list;
        }

        public static int CountFor(string playerName) => PicturesOf(playerName).Count;

        public static bool IsFull(string playerName) =>
            CountFor(playerName) >= DeskPictureFrame.Config.Server.MaxImagesPerPlayer;

        /// <summary>
        /// Deletes one of a player's pictures.
        ///
        /// **Only from their own folder, and that check is the whole security model.** A client
        /// asking to remove a picture can only ever remove one of its own, because anything
        /// outside its folder is simply not found. No ownership record is consulted, and none is
        /// needed.
        /// </summary>
        public static bool Remove(string playerName, string id)
        {
            if (id == null) return false;

            foreach (var row in PicturesOf(playerName))
            {
                if (row.Id != id) continue;
                return DeskPictureFrameImageStore.DeleteFile(row.Path, row.Id);
            }
            return false;
        }

        public static (int Removed, long Bytes) RemoveAll(string playerName)
        {
            int removed = 0;
            long bytes = 0;

            foreach (var row in PicturesOf(playerName))
            {
                if (!DeskPictureFrameImageStore.DeleteFile(row.Path, row.Id)) continue;
                removed++;
                bytes += row.Bytes;
            }
            return (removed, bytes);
        }

        /// <summary>Everyone with a folder, biggest first. Folder names ARE player names, so
        /// this is a directory listing and nothing more.</summary>
        public static List<(string Name, int Count, long Bytes)> Usage()
        {
            var list = new List<(string, int, long)>();

            try
            {
                string root = DeskPictureFrameImageStore.ServerStoreRoot();
                if (!Directory.Exists(root)) return list;

                foreach (string dir in Directory.GetDirectories(root))
                {
                    string name = Path.GetFileName(dir);
                    var pics = PicturesOf(name);
                    if (pics.Count == 0) continue;

                    long bytes = 0;
                    foreach (var row in pics) bytes += row.Bytes;
                    list.Add((name, pics.Count, bytes));
                }
            }
            catch (Exception) { }

            list.Sort((a, b) => b.Item2.CompareTo(a.Item2));
            return list;
        }

        /// <summary>
        /// Finds one of a player's pictures by what an admin would actually type: the label it
        /// was sent under, with or without ".png", or the id from the listing.
        ///
        /// Returns every match rather than the first. Two pictures CAN share a label - they came
        /// from different folders on the sender's machine - and silently deleting whichever came
        /// first is the worst thing an irreversible command could do. The caller shows the
        /// ambiguity and asks for an id instead.
        /// </summary>
        public static List<(string Id, string Label, DateTime When, long Bytes, string Path)> Find(
            string playerName, string needle)
        {
            var hits = new List<(string, string, DateTime, long, string)>();
            if (string.IsNullOrWhiteSpace(needle)) return hits;

            needle = needle.Trim();
            string bare = needle.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                ? needle.Substring(0, needle.Length - 4) : needle;

            foreach (var row in PicturesOf(playerName))
            {
                if (string.Equals(row.Id, needle, StringComparison.OrdinalIgnoreCase))
                {
                    hits.Clear();
                    hits.Add(row);
                    return hits;                 // an id is exact; nothing else competes
                }

                if (row.Label != null
                    && string.Equals(row.Label, bare, StringComparison.OrdinalIgnoreCase))
                {
                    hits.Add(row);
                }
            }
            return hits;
        }
    }
}
