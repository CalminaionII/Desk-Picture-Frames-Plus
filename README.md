# Desk Picture Frame Plus

A Vintage Story mod that adds picture frames — desk, wall and grouped — which display **your own
images**. Drop PNG files into a folder, then choose which one each frame shows from inside the
game.

**Requires:** Attribute Rendering Library 3.1.0 or later.

---

## Adding your own pictures

1. Put PNG files in:

   ```
   VintagestoryData/ModData/deskpictureframe/userimages/textures/pictures/
   ```

   Any filenames you like, in as many subfolders as you like — the folders show up as
   categories in the picker.

2. **Sneak and right-click any frame** to open the picture picker, and choose one.

That is the whole workflow. Pictures can be added while the game is running; they appear in the
picker without a restart.

**PNG only, 1 MB or less per file.** Anything else is skipped, and the picker says how many it
ignored and why.

### Sizes

Frames are wider than they are tall, or the other way round, and an image is stretched to fit —
so match the shape or it will look squashed.

| frame | suggested image |
| --- | --- |
| desk frames | 128x64 landscape, 64x128 portrait |
| wall and grouped frames | 384x256 landscape, 256x384 portrait |

Larger images work and simply cost more memory.

---

## Multiplayer

**Everyone sees the same picture.** A picture is identified by a hash of its contents rather
than by who placed it, so when you choose one it is stored on the server and served to every
client that needs it. Someone else's frame shows *their* picture, not yours.

Frames keep their picture when picked up and put back down, and a frame sitting on a shelf or
in an inventory renders the same as a placed one.

---

## Updating from 1.0.1

**Nothing to do.** Frames placed on 1.0.1 keep their pictures — the old numbered-slot images are
migrated to the new content-based ones on first load, and the old `ModConfig/deskpictureframe`
folder is cleaned up for you.

The numbered picture slots are gone. Pictures are no longer tied to a slot number, which is why
filenames and folders are now yours to choose.

---

## Licence

MIT — see [LICENSE](LICENSE).
