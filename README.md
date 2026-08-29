# Prince Of Persia: The Lost Crown - Archipelago Item Tracker
A program for building a list of every item and location in the game. For eventual use in the making of an Archipelago randomizer. This is not an item tracker for the randomizer itself.

## How to Install:
1. **Install BepInEx.**
   This has proven very finicky in the past, as it doesn't seem to play nice with most versions of the game. A guaranteed working installation is included with https://github.com/Lyall/PoPTLCFix/releases/tag/v0.8.6. Make sure to remove the included plugin (will likely cause a crash) and let BepInEx generate the necessary files. This may take a bit of time. Let the game sit on the main menu for a minute or two after first launch.

2. **Fork this repo, then clone your fork**
   - Edit the `<GameDir>` property near the top of `PoPTracker.csproj` to point at your own game installation folder.
   - The repo includes a `nuget.config` that points at BepInEx's own package feed. You shouldn't need to touch it, but if `dotnet restore`/`dotnet build` can't find `BepInEx.Unity.IL2CPP`, confirm this file is present in the same folder as the `.csproj`.
   - Build the plugin with `dotnet build`. The build automatically copies the compiled plugin into your BepInEx `plugins` folder.

3. **Start the game.**
   A log file called `PoPTracker.log` will be created in the plugin's folder inside `BepInEx\plugins\`. `locations.csv` and `items.csv` are written directly into your cloned repo folder (not the game folder), so they're ready to commit once you've played.

## How to Use:
Play through the game as normal. Do not manually touch the item/location CSVs, except to leave a note (if needed). The plugin will automatically fill the CSVs with new items/locations as you play.

If you want to confirm a specific pickup was actually recorded, check `PoPTracker.log`. Every tracked pickup logs a line when it fires.

Once you're done with a session, push your changes to your fork and request a merge. The CSV files will need manual review, to ensure data isn't duplicated or otherwise incorrect.

## Known Limitations
- **Shops**: purchases are tracked correctly, but this tool does not (yet) support reading every shop's full catalog automatically for every visit. Some shop items may need to be manually confirmed, and a shop may need to be checked multiple times for new items or upgrades when they become available.
- **Boss/miniboss drops**: some enemies drop loot at a variable position depending on where they die. The tracker attempts to account for this, but occasionally the same drop may be logged twice with slightly different coordinates. If you spot this, it's safe to manually merge the duplicate rows.
- **"Safe ground" collectibles** (tokens that follow you until you reach solid footing): these can land in a wide range of positions depending on player movement options, so the tracker uses a fuzzy position match for known cases. In rare cases, two genuinely different nearby collectibles could be merged into one entry. Flag this in an issue if you notice it.
- **Non-item grants**: Rarely, the tracker might record an item that doesn't exist or you already have. I've so far noticed it with the 'map', which will sometimes become disabled and re-enabled, triggering an item grant. I may remove this from the tracking entirely (as there's not really any use for randomizing the map). 

## NOTE:
If you notice an item *isn't* tracked, make an issue with the item type, general game location, and how you acquired it. If you need any help or have any questions, ask it in the Archipelago Discord server's "Prince of Persia: The Lost Crown" thread.