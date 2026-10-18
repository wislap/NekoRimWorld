# N.E.K.O RimWorld Bridge user guide

Connect your colony to N.E.K.O and ask about colonists, resources, research, quests, trade and caravan journeys. The Mod reads game information and offers advice without issuing game commands.

## Requirements

- RimWorld **1.6**. This build references 1.6.4871; other game versions have not been certified.
- [Harmony](https://github.com/pardeike/HarmonyRimWorld), enabled above Core and the mods that depend on it.
- N.E.K.O and the matching **N.E.K.O RimWorld plugin package** (`.neko-plugin`). Install the Mod ZIP and plugin separately. You do not need bench.
- An OpenAI-compatible model service configured for the plugin. Consultations send relevant game information to that service and may incur charges.

## Install the Mod

1. Exit the game and extract `NekoRimWorld-3.2.0.zip`.
2. Move the extracted `NekoRimWorld` folder into the game's `Mods` directory.
3. Check that the resulting path is `RimWorld/Mods/NekoRimWorld/About/About.xml`.
4. Start the game, enable Harmony and **N.E.K.O RimWorld Bridge**, and restart as prompted.

The ZIP includes the runtime DLLs. Players do not need the .NET SDK or a source build. Install the game, Harmony and N.E.K.O plugin separately.

## Install the plugin and pair the game

1. Use **Import** in the N.E.K.O **Plugin Center** to install the `.neko-plugin` file. Start the RimWorld plugin after installation; it does not start automatically by default.
2. Open **N.E.K.O RimWorld Bridge** in the game's Mod Settings. Choose **Generate new pairing code**, then select **Enable N.E.K.O connection**.
3. Copy the **Connection port** and **Pairing code**, then close the settings window to save and apply them.
4. Open **Settings** in the plugin panel, enter the same port and pairing code, and choose **Save and apply connection settings**.
5. Load your colony. Once the panel shows **Game connected**, use the connection check in Troubleshooting to read a colony overview. This does not call a model.

Game settings follow RimWorld's language, with English and Simplified Chinese supplied. The plugin panel follows N.E.K.O's language. The default port is `48919`, and the connection stays on this computer (`127.0.0.1`). Generating a new pairing code in the game also requires updating the plugin.

## Configure the model and ask a question

Under **Model service** in the plugin settings, enter the service URL, model name and API key. Choose **Save model settings** and wait for confirmation. This is separate from the model used by your N.E.K.O character. You can usually keep the advanced limits at their defaults.

Return to your N.E.K.O conversation and ask:

> In RimWorld, what should I deal with first?

Expand the full answer under **Latest advice**, or find earlier discussions in **History**. Saving settings, browsing history and checking the game connection do not call a model. Consultations also work while the game is paused.

## If connecting or saving does not finish

- **Connecting / reconnecting**: check that the plugin is running and has saved the same port and pairing code.
- **Checking the pairing code**: wait for the connection; if it does not complete, check both sides' settings.
- **Pairing code too short**: generate a code in the game, enable the connection again and close the window to save.
- **Save unconfirmed**: check the same save's status first. A plugin restart may remove its receipt. Review the current configuration before deciding to save again. The panel never repeats a write automatically.
- **Incomplete answer or file export failure**: read the retained text and explanation first. You can still copy an available full answer without running the consultation again.

Keep pairing codes and API keys private. Hide them in screenshots and shared logs.

## Update

Exit the game and stop the plugin. Move the previous Mod folder outside the game directory as a backup, then install the new `NekoRimWorld` folder. Keep the installation folder name consistent and check that the Mod and plugin versions match. Saved game settings and plugin history are not stored in the Mod ZIP.

## Package information

`BUILD.json` records the version, input hashes, dependencies and software smoke-test result. `THIRD-PARTY-NOTICES.md` and `Licenses/` retain dependency licenses. The ZIP's accompanying `.sha256` and `.manifest.json` files can verify the download and extracted contents.

Builds and software smoke tests do not certify every mod combination, operating system or actual consultation quality.
