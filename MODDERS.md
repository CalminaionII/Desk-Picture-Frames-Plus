# Desk Picture Frame Plus - Modder Guide

Add your own frame designs and frame metals. Desk Picture Frame Plus 1.2.0 and later; what is marked (1.2.1) needs 1.2.1. A new frame design is JSON only - no C#, and nothing of
this mod's to patch. Your frame gets everything the mod's own frames have: the picture picker,
pictures shared with everyone on a server, the server's picture limits, keeping its pictures when
picked up, shelves, display cases, ground piles and Window Display, outlines measured from its
model, and (1.2.1) one handbook page of its own that flips through every metal plate and links to the guide.

Your mod's `modinfo.json` depends on it:

```json
"dependencies": { "deskpictureframe": "1.2.1" }
```

## 1. The model

Make the shape as usual. Two kinds of texture code matter:

| Texture code | What goes there |
| --- | --- |
| `material` | the frame itself - drawn in the frame's material (copper, brass, oak...) |
| `image1`, `image2`, ... | one per picture. A frame with only one picture may call it `image` |

The picture codes must start with `image` exactly - lower case, no space. `Image1` or `image 1`
is not found, and that slot never gets a picture. The picker does not show these names: it calls the
slots "Picture 1", "Picture 2"... in the order of `slotOrder` (below).

Anything else (`coal` for the backing, say) is drawn from the blocktype's own `textures` as normal.

## 2. The blocktype

`assets/<yourmod>/blocktypes/<anything>.json`. One code, no variants:

```json
{
  "code": "myframe",
  "class": "BlockDeskPictureFrame",
  "entityClass": "BEDeskPictureFrame",
  "behaviors": [ { "name": "RightClickPickup" } ],
  "shape": { "base": "<yourmod>:block/myframe" },
  "textures": {
    "image1": { "base": "deskpictureframe:img/placeholder" },
    "image2": { "base": "deskpictureframe:img/placeholder" }
  },
  "attributes": {
    "slotOrder": [ "image1", "image2" ],
    "slotOrientation": { "image1": "landscape", "image2": "portrait" },
    "shelvable": true,
    "displaycaseable": true,
    "groundstorable": true
  },
  "creativeinventoryStacks": [
    {
      "tabs": [ "general", "decorative" ],
      "stacks": [
        { "type": "block", "code": "myframe", "attributes": { "types": { "metal": "copper" } } }
      ]
    }
  ]
}
```

The creative list only has to name its **tabs** - the mod fills it from the materials (section 4),
so the one stack above is just a placeholder. Leave `creativeinventoryStacks`
out and the frame is not in the creative tab at all.

Add the usual block properties as you would for any block - `blockmaterial`, `sidesolid`,
`sounds`, the hand / ground / gui transforms, and the display transforms
(`onshelfTransformByType` and friends). The mod's own blocktypes are a good starting point.

* **Every picture slot needs its placeholder** in `textures` - the mod finds a frame's slots there.
* **`slotOrder`** is the order the picker numbers them ("Picture 1", "Picture 2"...).
* **`slotOrientation`** is `landscape` or `portrait` per slot - the picker only offers pictures
  that way round (squares suit both).

### A frame that hangs on a wall

Add `"wallMounted": true` to `attributes`. That is all - no side variants, no extra behaviour. The
frame can then only be placed against the side of a block, faces out from it, and drops if that
block is removed.

## 3. The recipe

The material travels as `types/metal` on the frame - for any material, not only metals (the name
is kept from older versions). Fill it from the ingredient with a placeholder:

```json
{
  "ingredientPattern": "A,C",
  "width": 2, "height": 1,
  "ingredients": {
    "A": { "type": "item", "code": "game:metalplate-*", "name": "metal" },
    "C": { "type": "item", "code": "game:cloth-*" }
  },
  "output": { "type": "block", "code": "<yourmod>:myframe", "attributes": { "types": { "metal": "{metal}" } } }
}
```

## 4. Materials

Every game metal plate already works in recipes. The mod's own `materials.json` lists the game's
metals - nine shown in the creative tab, the rest `handbookOnly` - plus a `*` entry that gives any
other metal plate its texture. (1.2.1) Each frame's handbook page flips through EVERY `game:metalplate-*`
item in the game, a mod's plate in the game's domain (Pewter's) included, and lists them.

For anything else, add a materials file to **your** mod -
`assets/<yourmod>/config/deskpictureframe/materials.json`:

```json
[
  { "code": "oak", "texture": "game:block/wood/planks/oak1" }
]
```

| Field | What it does |
| --- | --- |
| `code` | The material code, saved on every frame as `types/metal`. `*` answers every code not listed, with `{code}` filled into its texture. |
| `texture` | The texture the `material` faces get. Left out: the `*` entry's, with the code filled in. |
| `source` | (1.2.1) The mod the material belongs to, when its plate is in the game's domain - Pewter's `game:metalplate-pewter` is Pewter's. Left out: the game's. |
| `handbookOnly` | (1.2.1) `true` keeps it out of the creative tab; it is still on the handbook pages and still crafts. |

A later file changes an entry by repeating its code. **The creative tab shows, on every frame, each
listed material of the game's in file order (this mod's file first), then the first listed material of
each other mod** - so a mod adding ten metals shows one, and the handbook page shows them all.

A metal from a mod whose plate is in the game's own domain needs only a patch onto ours - this is the
Pewter patch the mod ships:

```json
[
  {
    "op": "addeach",
    "path": "/-",
    "file": "deskpictureframe:config/deskpictureframe/materials.json",
    "dependsOn": [ { "modid": "pewter" } ],
    "value": [ { "code": "pewter", "source": "pewter" } ]
  }
]
```

A wooden frame's recipe takes planks named `wood` and writes `"types": { "metal": "{wood}" }`, as section 3 does for metal.


## Things to avoid

* **Do not use the mod's own codes** (`frame-desk-*`, `frame-wall-*`, `frame-combo-*`).
