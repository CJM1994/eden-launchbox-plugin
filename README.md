# Eden LaunchBox Integration

LaunchBox emulator plugin that adds save management (detection, automatic backups, restore) for the
Eden Switch emulator. Also matches `citron.exe`, `sudachi.exe`, `suyu.exe` and `yuzu.exe`, which share the layout.

## How saves are found

1. **Eden data folder**: `<exe dir>\user` if it exists (portable), otherwise `%APPDATA%\<exe name>`.
   A custom `nand_directory` in `user\config\qt-config.ini` is honoured.
2. **Title ID**: read from the game file itself. The plugin walks the XCI/NSP (also XCZ/NSZ/NCA) to its NCAs
   and decrypts each NCA header with `header_key` from `user\keys\prod.keys` to get the program ID.
   It falls back to ticket names inside NSPs, then to a `[0100XXXXXXXXXXXX]` in the file name.
3. **Profiles**: read from `nand\system\save\8000000000000010\su\avators\profiles.dat`.
   Save folders for profiles that no longer exist are ignored.
4. **Save folder**: `nand\user\save\0000000000000000\<profile>\<TitleID>`. Device saves use the
   all-zero profile folder.

## Build and install

Requires the .NET 10 SDK and a LaunchBox 14 install to compile against (defaults to `/mnt/c/LaunchBox`;
override with `-p:LaunchBoxDir=...`).

```sh
cd src/EdenPlugin
dotnet build -c Release
```

Close LaunchBox, then copy `Eden.LaunchBox.Integration.dll` and `manifest.json` from `bin/Release/net10.0/` into
`C:\LaunchBox\Local\Plugins\Eden LaunchBox Integration\` and restart LaunchBox.

LaunchBox 14 only registers emulator plugins from its managed plugin folders (`Local\Plugins`, `System\Plugins`),
and those need a `manifest.json`. A DLL dropped in the legacy `LaunchBox\Plugins` folder is loaded but never used
for save management.

## Backup matching

LaunchBox stores backups as 7z archives of the save folder's contents. The plugin implements
`TryComputeSaveSignature` (a SHA-256 manifest of relative paths + file hashes, extracting 7z backups first) so
LaunchBox can tell when the live save is already backed up. Without it, games show "No backup" even when backups exist.

## Testing title ID detection

```sh
cd tools/TitleIdProbe
dotnet run -c Release -- 'F:\emulators\Eden\eden.exe' /mnt/f/roms/switch
```
