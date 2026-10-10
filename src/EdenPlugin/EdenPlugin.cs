using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Unbroken.LaunchBox.Plugins;
using Unbroken.LaunchBox.Plugins.Data;
using Unbroken.LaunchBox.Windows;

namespace EdenLaunchBoxIntegration;

/// <summary>
/// Save management for Eden (and other yuzu-family Switch emulators) in LaunchBox. Each game's save folder is
/// found at nand/user/save/0000000000000000/&lt;profile&gt;/&lt;TitleID&gt;, with the title ID read from the game file.
/// </summary>
public class EdenPlugin : EmulatorPlugin
{
    private const string SaveGroupPrefix = "eden:";
    private const string LogPrefix = "[EdenPlugin] ";

    private static readonly HashSet<string> supportedExecutables = new(StringComparer.OrdinalIgnoreCase)
    {
        "eden.exe", "citron.exe", "sudachi.exe", "suyu.exe", "yuzu.exe"
    };

    private sealed record SaveContext(IGame Game, IAdditionalApplication? AdditionalApplication)
    {
        public string? RomPath => !string.IsNullOrWhiteSpace(AdditionalApplication?.ApplicationPath) ? AdditionalApplication.ApplicationPath : Game.ApplicationPath;
    }

    public override string EmulatorName => "Eden";

    public override IEnumerable<IEmulator> GetApplicableEmulators(IEnumerable<IEmulator?>? emulators)
    {
        return emulators?.Where(e => e != null && IsSupportedExecutable(e.ApplicationPath)).Cast<IEmulator>().ToList() ?? new List<IEmulator>();
    }

    public override EmulatorSupportResponse IsPlatformSupported(string platform)
    {
        bool supported = string.Equals(platform, "Nintendo Switch", StringComparison.OrdinalIgnoreCase);
        return new EmulatorSupportResponse(supported, supported);
    }

    public override string? GetCurrentVersion(string applicationPath)
    {
        try
        {
            string? version = FileVersionInfo.GetVersionInfo(applicationPath).ProductVersion;
            return string.IsNullOrWhiteSpace(version) ? null : version;
        }
        catch
        {
            return null;
        }
    }

    public override IEnumerable<EmulatorControllerVersion>? GetInstallableVersions() => Array.Empty<EmulatorControllerVersion>();

    public override EmulatorInstallResponse InstallEmulator(InstallEmulatorArgs args)
    {
        return new EmulatorInstallResponse("Installing Eden is not supported by this plugin. Install it manually and add it as an emulator.");
    }

    public override bool IsUpdateAvailable(IEmulator? emulator, out EmulatorControllerVersion? version)
    {
        version = null;
        return false;
    }

    public override bool IsUpdateAvailable(string? emulatorAppPath, out EmulatorControllerVersion? version)
    {
        version = null;
        return false;
    }

    public override bool SupportsSaveManagement() => true;

    public override bool UseSaveGroupIdForPersistedMatch(GameSaveBase save)
    {
        return save?.SaveGroupId?.StartsWith(SaveGroupPrefix, StringComparison.OrdinalIgnoreCase) == true;
    }

    /// <summary>
    /// Content hash used by LaunchBox to match the live save against its backups. Live saves are folders and
    /// backups are 7z archives of the folder contents, so both are hashed as a manifest of relative path + SHA-256.
    /// </summary>
    public override bool TryComputeSaveSignature(GameSaveBase? save, string emulatorApplicationPath, out string? signature)
    {
        signature = null;
        if (!UseSaveGroupIdForPersistedMatch(save!) || string.IsNullOrWhiteSpace(save!.FileLocation))
        {
            return false;
        }
        string? tempFolder = null;
        try
        {
            if (!TryPrepareFolderSource(ResolveLaunchBoxPath(save.FileLocation), out string? folder, out tempFolder, out _))
            {
                return false;
            }
            signature = ComputeFolderManifest(folder!);
            return signature != null;
        }
        catch (Exception ex)
        {
            LogWarning($"Couldn't compute signature for '{save.FileLocation}': {ex.Message}");
            return false;
        }
        finally
        {
            if (tempFolder != null)
            {
                try { Directory.Delete(tempFolder, recursive: true); } catch { }
            }
        }
    }

    public override GetSavesResponse GetSaves(GetSavesArgs? args)
    {
        if (args?.Emulator == null)
        {
            return new GetSavesResponse(Array.Empty<GameSaveBase>());
        }
        EdenLayout? layout = EdenLayout.Resolve(args.Emulator.ApplicationPath);
        if (layout == null)
        {
            LogWarning("Couldn't find Eden's user folder for " + args.Emulator.ApplicationPath);
            return new GetSavesResponse(Array.Empty<GameSaveBase>());
        }

        byte[]? headerKey = layout.LoadHeaderKey();
        List<EdenProfile> profiles = layout.GetSaveProfiles();
        var saves = new List<GameSaveBase>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (SaveContext context in BuildSaveContexts(args))
        {
            string? titleId = ResolveTitleId(context.RomPath, headerKey);
            if (titleId == null)
            {
                continue;
            }
            foreach (EdenProfile profile in profiles)
            {
                string saveDirectory = layout.GetSaveDirectory(profile.FolderName, titleId);
                if (Directory.Exists(saveDirectory) && seen.Add(saveDirectory))
                {
                    saves.Add(CreateSave(context.Game.Id, context.AdditionalApplication?.Id, args.Emulator, saveDirectory, profile, titleId, profiles.Count > 2, null));
                }
            }
        }
        return new GetSavesResponse(saves);
    }

    public override AddSaveResponse AddSaveFile(AddSaveArgs? args)
    {
        GameSaveBase? save = args?.SaveToAdd;
        if (save == null)
        {
            return new AddSaveResponse("The save file doesn't exist!");
        }
        if (!TryResolveContext(save, out IGame? game, out IAdditionalApplication? additionalApplication, out IEmulator? emulator))
        {
            return new AddSaveResponse("Can't get emulator record for game/app in question!");
        }
        EdenLayout? layout = EdenLayout.Resolve(emulator!.ApplicationPath);
        if (layout == null)
        {
            return new AddSaveResponse("Couldn't find Eden's user folder.");
        }

        List<EdenProfile> profiles = layout.GetSaveProfiles();
        if (!TryParseSaveGroupId(save.SaveGroupId, out string? profileFolder, out string? titleId))
        {
            string? romPath = !string.IsNullOrWhiteSpace(additionalApplication?.ApplicationPath) ? additionalApplication.ApplicationPath : game!.ApplicationPath;
            titleId = ResolveTitleId(romPath, layout.LoadHeaderKey());
            if (titleId == null)
            {
                return new AddSaveResponse("Couldn't determine the title ID for this game.");
            }
            // Saves without Eden metadata go to the default (first) profile.
            profileFolder = profiles[0].FolderName;
        }
        EdenProfile profile = profiles.FirstOrDefault(p => p.FolderName.Equals(profileFolder, StringComparison.OrdinalIgnoreCase))
            ?? new EdenProfile(profileFolder!, profileFolder!);

        if (!TryPrepareFolderSource(ResolveLaunchBoxPath(save.FileLocation ?? string.Empty), out string? sourceFolder, out string? tempFolder, out string? error))
        {
            return new AddSaveResponse(error ?? "The save file doesn't exist!");
        }
        try
        {
            string target = layout.GetSaveDirectory(profile.FolderName, titleId!);
            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }
            CopyDirectory(sourceFolder!, target);
            LogInfo($"Restored save for {titleId} ({profile.DisplayName}) from {save.FileLocation}");
            return new AddSaveResponse(CreateSave(save.GameId, save.AdditionalApplicationId, emulator, target, profile, titleId!, profiles.Count > 2, save));
        }
        catch (Exception ex)
        {
            LogWarning("Restore failed: " + ex.Message);
            return new AddSaveResponse("Failed to restore the save: " + ex.Message);
        }
        finally
        {
            if (tempFolder != null)
            {
                try { Directory.Delete(tempFolder, recursive: true); } catch { }
            }
        }
    }

    public override PluginResponse RemoveSave(GameSaveBase? save)
    {
        if (string.IsNullOrWhiteSpace(save?.FileLocation))
        {
            return new PluginResponse(success: false, "Invalid save.");
        }
        try
        {
            if (Directory.Exists(save.FileLocation))
            {
                LogInfo("Deleting save folder: " + save.FileLocation);
                Directory.Delete(save.FileLocation, recursive: true);
                return new PluginResponse(success: true);
            }
            if (File.Exists(save.FileLocation))
            {
                File.Delete(save.FileLocation);
                return new PluginResponse(success: true);
            }
            return new PluginResponse(success: false, "Save file not found.");
        }
        catch (Exception ex)
        {
            LogWarning("Delete save failed: " + ex.Message);
            return new PluginResponse(success: false, "Failed to delete save.");
        }
    }

    private static GameSaveGame CreateSave(string? gameId, string? additionalApplicationId, IEmulator? emulator, string saveDirectory, EdenProfile profile, string titleId, bool showProfileChip, GameSaveBase? previous)
    {
        return new GameSaveGame
        {
            GameId = gameId,
            AdditionalApplicationId = additionalApplicationId,
            FileLocation = saveDirectory,
            IsDirectory = true,
            SaveGroupId = $"{SaveGroupPrefix}{gameId}:{profile.FolderName}:{titleId}",
            MatchLineageId = previous?.MatchLineageId,
            MigrationFamilyId = previous?.MigrationFamilyId,
            SaveGroupName = !string.IsNullOrWhiteSpace(previous?.SaveGroupName) ? previous.SaveGroupName : "My Save File",
            DisplayChipText = profile.FolderName == EdenLayout.DeviceSaveFolder ? "Device Save" : showProfileChip ? profile.DisplayName : null,
            EmulatorFileName = Path.GetFileName(emulator?.ApplicationPath),
            Title = previous?.Title,
            OriginalFileName = titleId,
            EmulatorCore = null
        };
    }

    private static bool TryParseSaveGroupId(string? saveGroupId, out string? profileFolder, out string? titleId)
    {
        profileFolder = null;
        titleId = null;
        if (saveGroupId == null || !saveGroupId.StartsWith(SaveGroupPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        string[] parts = saveGroupId.Split(':');
        if (parts.Length != 4 || parts[2].Length != 32 || parts[3].Length != 16 || !parts[2].All(Uri.IsHexDigit) || !parts[3].All(Uri.IsHexDigit))
        {
            return false;
        }
        profileFolder = parts[2].ToUpperInvariant();
        titleId = parts[3].ToUpperInvariant();
        return true;
    }

    private static string? ResolveTitleId(string? romPath, byte[]? headerKey)
    {
        if (string.IsNullOrWhiteSpace(romPath) || romPath.Contains("://") || !SwitchTitleIdReader.SupportedExtensions.Contains(Path.GetExtension(romPath)))
        {
            return null;
        }
        TitleIdResult result = SwitchTitleIdReader.GetTitleId(romPath, headerKey);
        if (!result.Success)
        {
            LogWarning($"Couldn't determine title ID for '{romPath}': {result.Detail}");
        }
        return result.TitleId;
    }

    private static List<SaveContext> BuildSaveContexts(GetSavesArgs args)
    {
        var contexts = new List<SaveContext>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (args.AdditionalApplications != null)
        {
            foreach (IAdditionalApplication? app in args.AdditionalApplications)
            {
                if (app == null || !app.UseEmulator || !seen.Add(app.Id))
                {
                    continue;
                }
                IGame? game = PluginHelper.DataManager.GetGameById(app.GameId);
                string emulatorId = IsUnassignedEmulatorId(app.EmulatorId) ? game?.EmulatorId ?? string.Empty : app.EmulatorId;
                if (game != null && IsSupportedEmulatorId(emulatorId))
                {
                    contexts.Add(new SaveContext(game, app));
                }
            }
        }
        if (args.Games != null)
        {
            foreach (IGame game in args.Games)
            {
                if (game != null && seen.Add(game.Id ?? string.Empty))
                {
                    contexts.Add(new SaveContext(game, null));
                }
            }
        }
        return contexts;
    }

    private static bool TryResolveContext(GameSaveBase save, out IGame? game, out IAdditionalApplication? additionalApplication, out IEmulator? emulator)
    {
        additionalApplication = null;
        emulator = null;
        game = PluginHelper.DataManager.GetGameById(save.GameId);
        if (game == null)
        {
            return false;
        }
        if (!string.IsNullOrWhiteSpace(save.AdditionalApplicationId))
        {
            additionalApplication = game.GetAllAdditionalApplications()?.FirstOrDefault(a => a.Id == save.AdditionalApplicationId);
        }
        string? emulatorId = additionalApplication != null && !IsUnassignedEmulatorId(additionalApplication.EmulatorId) ? additionalApplication.EmulatorId : game.EmulatorId;
        if (string.IsNullOrWhiteSpace(emulatorId))
        {
            return false;
        }
        emulator = PluginHelper.DataManager.GetEmulatorById(emulatorId);
        return emulator != null;
    }

    private static bool IsSupportedExecutable(string? applicationPath)
    {
        if (string.IsNullOrWhiteSpace(applicationPath))
        {
            return false;
        }
        try
        {
            return supportedExecutables.Contains(Path.GetFileName(applicationPath));
        }
        catch
        {
            return false;
        }
    }

    private static bool IsSupportedEmulatorId(string? emulatorId)
    {
        if (IsUnassignedEmulatorId(emulatorId))
        {
            return false;
        }
        try
        {
            return IsSupportedExecutable(PluginHelper.DataManager.GetEmulatorById(emulatorId!)?.ApplicationPath);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsUnassignedEmulatorId(string? emulatorId)
    {
        return string.IsNullOrWhiteSpace(emulatorId) || emulatorId.Equals(Guid.Empty.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Backups arrive either as a folder or as a 7z archive of one.</summary>
    private static bool TryPrepareFolderSource(string? path, out string? folderPath, out string? tempFolder, out string? errorMessage)
    {
        folderPath = null;
        tempFolder = null;
        errorMessage = null;
        if (Directory.Exists(path))
        {
            folderPath = path;
            return true;
        }
        if (!File.Exists(path))
        {
            errorMessage = "The save file doesn't exist!";
            return false;
        }
        if (!string.Equals(Path.GetExtension(path), ".7z", StringComparison.OrdinalIgnoreCase))
        {
            errorMessage = "This save must be a folder or 7z archive.";
            return false;
        }
        tempFolder = Path.Combine(Path.GetTempPath(), "EdenLaunchBox-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempFolder);
        if (!SevenZip.Extract(path, tempFolder))
        {
            errorMessage = "Failed to extract save archive.";
            return false;
        }
        // Unwrap an archive of the <TitleID> folder itself, but not a save whose only content is one subfolder (e.g. save_data).
        string[] directories = Directory.GetDirectories(tempFolder);
        bool isWrappedTitleFolder = directories.Length == 1 && Directory.GetFiles(tempFolder).Length == 0
            && Path.GetFileName(directories[0]) is { Length: 16 } name && name.All(Uri.IsHexDigit);
        folderPath = isWrappedTitleFolder ? directories[0] : tempFolder;
        return true;
    }

    private static string? ComputeFolderManifest(string folderPath)
    {
        string[] files = Directory.GetFiles(folderPath, "*", SearchOption.AllDirectories)
            .Where(f => !IgnoredManifestFiles.Contains(Path.GetFileName(f)))
            .OrderBy(f => Path.GetRelativePath(folderPath, f).Replace('\\', '/'), StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (files.Length == 0)
        {
            return null;
        }
        var manifest = new StringBuilder();
        foreach (string file in files)
        {
            using FileStream stream = new(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            manifest.Append(Path.GetRelativePath(folderPath, file).Replace('\\', '/').ToLowerInvariant())
                .Append('|')
                .Append(Convert.ToHexString(SHA256.HashData(stream)))
                .Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(manifest.ToString())));
    }

    private static readonly HashSet<string> IgnoredManifestFiles = new(StringComparer.OrdinalIgnoreCase) { "manifest.sha256", "desktop.ini", "thumbs.db" };

    /// <summary>Backup locations are stored relative to the LaunchBox folder (e.g. "Saves\Nintendo Switch\...").</summary>
    private static string ResolveLaunchBoxPath(string path)
    {
        if (Path.IsPathRooted(path))
        {
            return path;
        }
        // Plugins run inside LaunchBox\Core, so the install root is its parent.
        string? root = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))?.FullName;
        return root != null ? Path.Combine(root, path) : Path.GetFullPath(path);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }
        foreach (string directory in Directory.GetDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }

    private static void LogInfo(string message)
    {
        try { Root.Logging?.PublishInfo(LogPrefix + message); } catch { }
    }

    private static void LogWarning(string message)
    {
        try { Root.Logging?.PublishWarning(LogPrefix + message); } catch { }
    }
}
