# ForFlagrum

A personal fork of [Flagrum](https://github.com/Kizari/Flagrum) 1.6.5 by Kizari, tuned for modding Forspoken
(in particular, making many outfit variants of Frey with the FSK Blender add-on).

Changes from Flagrum 1.6.5 include:

- A Vortex-style mod list with Enabled/Disabled buttons, categories, sorting, filters and a details panel
- Importing FSK add-on builds (folders or zips), with change detection and one-click re-sync
- A Forspoken play button in the sidebar
- Fixes for crashes when mod files are in use, model preview crashes and out-of-date file indexes
- No automatic updates or upstream version checks, so upstream releases can't replace or disable this fork

## The FSK Blender add-on

Outfit builds are made in Blender with the FSK (Forspoken Kit) add-on, which is included in [`addon/`](addon/).
See [addon/README.md](addon/README.md) for how to install and use it.

## Installing

You need the [.NET SDK](https://dotnet.microsoft.com/download) (8 or newer) on Windows.

1. Download this repository as a zip (green **Code** button, **Download ZIP**) and unzip it.
2. Double-click `Install-ForFlagrum.bat`. If Windows asks whether to run it, choose **Run**.
3. Start ForFlagrum from the new shortcut on your desktop or in the Start menu.

To update, download and unzip the new version and run `Install-ForFlagrum.bat` again. The app is installed to
`%LOCALAPPDATA%\ForFlagrum\app`. Mods and settings stay in `%LOCALAPPDATA%\Flagrum`, shared with an installed
copy of the original Flagrum.

## License

Flagrum is licensed under the GPLv3, and so is this fork. See [LICENSE](LICENSE) and [ATTRIBUTION.md](ATTRIBUTION.md).
