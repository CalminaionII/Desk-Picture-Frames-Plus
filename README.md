# Desk Picture Frame Plus

Metal frames for Vintage Story that show your own pictures, chosen in game.

Drop PNG files into one folder, then sneak and right-click any frame to choose which picture it shows. Frames come in eight
designs - on a desk, flush to a wall, and doubles, triples and combos - and in any metal plate the game has.

**Download:** [Desk Picture Frame Plus on the Vintage Story Mod DB](https://mods.vintagestory.at/deskpictureframe)

## Requirements

- Vintage Story 1.22 or later.
- Nothing else. Since 1.2.0 the mod no longer needs Attribute Rendering Library - frames draw themselves.
- Install on the server and on every client.

## Using It

- **Your pictures live here:** `VintagestoryData/ModData/deskpictureframe/userimages/textures/pictures` - name the files whatever you like.
  Subfolders become a folder chooser in the picker.
- **Pick a picture:** sneak and right-click a frame. A picture added while you play is there the next time you open the picker.
- **PNG only, up to 1 MB each,** and up to 480 x 320 pixels (320 x 480 standing up) by default - a server can change the size.
- **A frame keeps the picture you gave it** - pick it up, put it down, move it to a shelf or a display case, and it still shows it.
- **Multiplayer:** a picture is uploaded once and everyone sees the same one, whoever placed the frame. Frames respect land claims.

## Making Frames

A metal plate and a piece of cloth in the crafting grid make a frame - any metal plate, any cloth. The frame comes out in the
plate's metal. The handbook has a user guide, **Desk Picture Frame Plus: User Guide**, and each frame has one handbook page
that lists the metal plates it can be made with.

## For Server Owners

- Settings are in `ModConfig/DeskPictureFrameConfig.json` (Client and Server sections, each explained in the file), or in game
  through Integrated Mod Manager, ConfigKit or ConfigLib.
- Server settings: pictures per player (100), pictures on the server (5000), and the largest picture size (480 x 320).
- `/dpf config reload` reads the file again; `/dpf quota`, `/dpf quota list <player>` and `/dpf quota remove <player> <picture> confirm`
  show and manage what each player has added; `/dpf images` reports the whole store.
- Each player's pictures are in a folder named after them under `ModData/deskpictureframe/images`.

## For Modders

Frame designs and frame metals are JSON - no code. See [MODDERS.md](MODDERS.md).

## Updating From An Earlier Version

Nothing to do. Frames already placed are converted when the world loads, keeping their metal and their picture, and your
pictures are moved into the new folder for you. A boxed set of five from before 1.2.0 still in your world becomes an unknown
block - break it on the ground to unpack the frames first.

## Licence

MIT - see [LICENSE](LICENSE).
