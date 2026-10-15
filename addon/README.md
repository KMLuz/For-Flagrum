# FSK - Forspoken Kit (Blender add-on)

FSK imports Forspoken character models into Blender and exports edited outfits as **builds** that ForFlagrum
turns into mods. It is made for creating outfit variants of Frey (`hu/hu000`).

- Version: 0.5.1
- Requires: Blender 4.2 or newer, on Windows
- Author: Luz (the importer is based on Flagrum by Kizari)

## Installing

1. In Blender, open **Edit > Preferences > Add-ons**.
2. Choose **Install from Disk** (in the drop-down at the top right in Blender 4.2) and pick `fsk_kit.zip` from this
   folder. Don't unzip it first.
3. Tick **FSK - Forspoken Kit** to enable it, then expand it to set its preferences:
   - **Game data folder**: the extracted game data, i.e. the folder that contains `asset`.
     You can extract the character's files with ForFlagrum's Asset Explorer (right-click the folder, Export Folder).
   - **Character**: `hu/hu000` for Frey.
   - **Output folder**: where builds are written, e.g. `...\Frey\Builds`. Each build gets its own folder in here.

## Making a build

The add-on lives in the **Forspoken** tab of the 3D viewport's sidebar (press `N`), and models can be imported from
**File > Import**.

1. Pick the vanilla **outfit** the build starts from (e.g. `hu000_100`), and optionally your own **Head** and **Body**
   meshes.
2. Select your meshes and press **Refresh plan**. The plan lists which vanilla pieces are kept, replaced or removed,
   and why. Use the pin button to keep a vanilla piece as it is.
3. Check the options:
   - **Keep game data on unchanged head triangles**: unchanged head vertices keep the game's normals, wrinkle
     factors and second UV set.
   - **Auto LODs**: makes the lower detail levels for you, matching the game's proportions.
   - **Body uses the 400 skin material**: gives the outfit its own copy of the master body skin material.
   - **Write textures**, and **Shared skin in its own build**: see below.
4. Give the build a name (this becomes the mod's name in ForFlagrum, e.g. `Jacket_NoPants`) and press
   **Export to Flagrum**.

Each build folder contains the built model, materials and textures, an `entries.txt` listing which game files they
replace, and a `build_log.txt` with what was changed and a PASS/FAIL check.

### Shared skin

The face and body skin textures are used by every outfit. With **Shared skin in its own build** on (the default),
they go into a separate build, `Frey_Skin` by default, instead of into every outfit build. That way:

- import `Frey_Skin` into ForFlagrum once and keep it enabled, and
- any number of different outfits can be enabled at the same time. Only variants of the same outfit replace each
  other.

**Export textures only** updates the textures and body material without rebuilding the model.

## Using builds in ForFlagrum

1. In ForFlagrum, click **Import Builds** and choose your output folder. Every build in it is listed as New,
   Changed or Up to date.
2. Import the builds you want. Each one becomes a mod, which starts disabled.
3. Turn mods on with their Enabled/Disabled button.
4. After re-exporting from Blender, the mod shows a **Changed** badge. Click the mod, then **Re-sync** in the
   details panel. Enabled mods are re-applied straight away.

Zipped build folders can also be installed with **Install Mod**.
