# Figurines - guide for modders

The full modder reference for Figurines. Questions and reports: the Figurines page on mods.vintagestory.at.

## For Modders

Add your own creatures and metals with JSON - no code, no patches for a creature, and no dependency entry.

### Quick Start

**1.** **Create the file**

Make a file at `assets/<yourmod>/config/figurines/creatures/<anything>.json` - any file name will do. Figurines reads every mod's files when the game loads.

**2.** **Copy the example below into it**

Change **code** and **entity** to your creature's, and the poses to animations your model has.

**3.** **Load a world**

Your creature is in the carving window at the figurine blank and in the creative tab, in every material. If something in your file is wrong, the log names the file and the problem (`[Figurines] Creature in ... skipped: ...`).

**Minimal example** - a lynx from your own mod, two carvable poses and the three collector poses:

```json
[
  {
    "code": "lynx",
    "entity": "yourmod:lynx-adult-male",
    "poses": [
      { "code": "normal", "carvable": true },
      { "code": "walking", "carvable": true, "animation": "walk", "frame": 8 },
      { "code": "silver", "collectorMaterial": "iron", "animation": "sit", "frame": 15 },
      { "code": "gold", "collectorMaterial": "platinum", "animation": "attack", "frame": 12 },
      { "code": "platinum", "collectorMaterial": "uranium", "animation": "sleep", "frame": 20 }
    ]
  }
]
```

**Nothing else to make.** The figurine is your creature's own model, posed and painted in the material it is made of - no shapes, textures or item codes of your own. Its size, its name and its place in the carving menu all follow from the file.

#### Show the full example with every field

Only **code**, **entity** and **poses** are required. Each field is explained in the reference below.

```json
[
  {
    "code": "lynx",
    "entity": "yourmod:lynx-adult-male",
    "poses": [
      { "code": "normal", "carvable": true },
      { "code": "walking", "carvable": true, "animation": "walk", "frame": 8 },
      { "code": "silver", "collectorMaterial": "iron", "animation": "sit", "frame": 15 },
      { "code": "gold", "collectorMaterial": "platinum", "animation": "attack", "frame": 12 },
      { "code": "platinum", "collectorMaterial": "uranium", "animation": "sleep", "frame": 20 }
    ],
    "attachments": [ "yourmod:ear-tufts" ],
    "removeElements": [ "eye", "lid", "sleep", "whiskers" ],
    "size": 0.25,
    "name": "yourmod:creature-lynx",
    "group": "cat",
    "category": "animal",
    "enabled": true
  }
]
```

### Field Reference

| **Required** |  |
|---|---|
| `code` | Your creature's id. It is saved in every figurine, so **never change it after release.** Codes are shared by every mod - if two files use the same code, the second is skipped and the log says so - so pick one that will not clash. |
| `entity` | The game entity whose model is used, domain included, for example `game:bear-brown-adult-male`. Its textures come with it. |
| `poses` | A list of poses - see *Poses* below. At least one. |
| **Looks - all optional** |  |
| `attachments` | Items the creature wears, the way the living animal does - antlers, horns, tusks (for example `game:antler-whitetail-06`). Without them a stag's figurine has a bare head. |
| `removeElements` | Parts of the model left off, by name - any element whose name contains one of these. Default: `eye`, `lid`, `sleep`, because eyes and eyelids cannot show in one material and flicker against the face. Setting this replaces the default, so list those three too if your model has them. |
| `size` | The figurine's longest side - length or height - in its normal pose, in blocks. Left out, it is worked out true to scale from the entity's own size (see *Figurine Sizes*), which keeps a bear bigger than a hare. Every pose uses the same scale. |
| **Listing - all optional** |  |
| `name` | Lang key for its name. Left out: `figurines:creature-<code>` if that exists, else the game's own name for the entity - so a modded animal needs no lang file. |
| `group` | The family it is listed under in the carving menu, and the handbook page it shares - every creature of a family is on one page. Default: the code up to its first dash, so `bear-panda` is a Bear and `lynx` a Lynx. A mod adding a bear of its own sets `"bear"` here to list it with the game's. The page is named after the creature whose code is the family; the lang key `figurines:creaturegroup-<family>` names it instead. |
| `category` | `animal` (the default) or `hostile` - which filter tab of the carving window it is under. |
| `enabled` | False hides a creature. A patch onto our file can turn off one of ours this way. |

### Poses

| `code` | `normal` or any name of your own for a pose that can be carved, or `silver` / `gold` / `platinum` for a collector pose. |
|---|---|
| `carvable` | True: offered at the figurine blank. Collector poses never are. |
| `collectorMaterial` | The metal a collector pose comes in - `iron` / `platinum` from the Treasure Hunter's blind box, `uranium` from the weathered box in ruins. Left out: the pose code is the metal. Change this, never the pose code - figurines already made keep the pose code they were saved with. |
| `animation` | An animation code from the entity's shape. Left out: the model's rest pose. |
| `frame` | The zero-based frame of that animation to freeze at. A frame past the end is held at the last one. |

- Give a creature a `normal` pose: it is the standing figurine, and the one its size is measured in.

- Pick frames where the creature is clearly doing something - a sit, a mid-stride step, a sleep. Browse your model's animations in Model Creator to find them.

### Adding A Metal

Materials live in `config/figurines/materials.json`. Each has:

| `code` | The material's code, saved on every figurine. For a castable metal it must be the metal's own, as in its ingot (`ingot-<code>`). |
|---|---|
| `texture` | The texture painted on every face of the model. |
| `castable` | True lets the fired mold take this metal. |
| `source` | Optional. The mod the metal belongs to, when its texture is in the game's domain (Pewter's is) - it then counts as that mod's: one figurine of it per creature in the creative menu. Left out: the texture's domain. |

The name comes from `figurines:material-<code>`, else the game's own `material-<code>` (every vanilla metal has one), else the code. Add a material with a patch onto ours - this is the pewter patch Figurines ships:

```json
[
  {
    "op": "addeach",
    "path": "/-",
    "file": "figurines:config/figurines/materials.json",
    "dependsOn": [ { "modid": "pewter" } ],
    "value": [
      { "code": "pewter", "texture": "game:block/metal/plate/pewter", "castable": true, "source": "pewter" }
    ]
  }
]
```

### Figurine Sizes

`figurines:config/figurines/sizes.json` sets how big figurines are. Each creature's longest side is its real size times **scale**, kept between **smallest** and **largest** (in blocks). Patch it to make every figurine bigger or smaller:

```json
{
  "scale": 0.12,
  "smallest": 0.12,
  "largest": 0.4
}
```

A creature's own `size` wins over this.

### Shipping It With Your Mod

**Do not add Figurines to your dependencies.** Ship the creature file unconditionally. The game never reads a config file nothing asks for, so for anyone who does not have Figurines your file is inert and silent - no warnings, no errors. A dependency entry would do the opposite and stop your mod loading without it.

**Patches onto Figurines' files are the exception** (a new metal, sizes, turning off one of our creatures): they name Figurines' own files, so give each one `"dependsOn": [ { "modid": "figurines" } ]`. The patch then only applies when Figurines is installed, and is skipped silently when it is not.
