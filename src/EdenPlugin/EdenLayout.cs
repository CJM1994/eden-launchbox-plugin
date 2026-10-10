using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace EdenLaunchBoxIntegration;

public sealed record EdenProfile(string FolderName, string DisplayName);

/// <summary>
/// Resolves where an Eden (or other yuzu-family) install keeps its data: portable "user" folder next to the
/// executable, or %APPDATA%\&lt;name&gt;, honouring a custom NAND directory from qt-config.ini.
/// </summary>
public sealed class EdenLayout
{
    // Device-wide saves live under an all-zero user ID.
    public const string DeviceSaveFolder = "00000000000000000000000000000000";

    private const string ProfilesDatRelativePath = "system/save/8000000000000010/su/avators/profiles.dat";
    private const int ProfilesHeaderSize = 0x10;
    private const int ProfileEntrySize = 0xC8;
    private const int ProfileUsernameOffset = 0x28;
    private const int ProfileUsernameSize = 0x20;
    private const int MaxProfiles = 8;

    /// <summary>Lets the test harness translate Windows paths (e.g. F:/...) when running outside Windows.</summary>
    public static Func<string, string> PathMapper { get; set; } = p => p;

    public string UserDirectory { get; }
    public string NandDirectory { get; }
    public string SaveRoot => Path.Combine(NandDirectory, "user", "save", "0000000000000000");
    public string ProdKeysPath => Path.Combine(UserDirectory, "keys", "prod.keys");

    private EdenLayout(string userDirectory, string nandDirectory)
    {
        UserDirectory = userDirectory;
        NandDirectory = nandDirectory;
    }

    public static EdenLayout? Resolve(string? emulatorApplicationPath)
    {
        if (string.IsNullOrWhiteSpace(emulatorApplicationPath))
        {
            return null;
        }
        string exePath = Path.GetFullPath(PathMapper(emulatorApplicationPath));
        string? exeDirectory = Path.GetDirectoryName(exePath);
        if (exeDirectory == null)
        {
            return null;
        }

        string userDirectory = Path.Combine(exeDirectory, "user");
        if (!Directory.Exists(userDirectory))
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            userDirectory = Path.Combine(appData, Path.GetFileNameWithoutExtension(exePath).ToLowerInvariant());
            if (!Directory.Exists(userDirectory))
            {
                return null;
            }
        }

        string nandDirectory = ReadConfiguredNandDirectory(userDirectory) ?? Path.Combine(userDirectory, "nand");
        return new EdenLayout(userDirectory, nandDirectory);
    }

    private static string? ReadConfiguredNandDirectory(string userDirectory)
    {
        string configPath = Path.Combine(userDirectory, "config", "qt-config.ini");
        if (!File.Exists(configPath))
        {
            return null;
        }
        try
        {
            bool inDataStorage = false;
            foreach (string rawLine in File.ReadLines(configPath))
            {
                string line = rawLine.Trim();
                if (line.StartsWith('['))
                {
                    inDataStorage = line.Equals("[Data%20Storage]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                if (!inDataStorage || !line.StartsWith("nand_directory=", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                string value = line.Substring("nand_directory=".Length).Trim().Trim('"');
                if (value.Length == 0)
                {
                    return null;
                }
                string mapped = PathMapper(value);
                if (!Path.IsPathRooted(mapped))
                {
                    mapped = Path.Combine(userDirectory, mapped);
                }
                return Directory.Exists(mapped) ? Path.GetFullPath(mapped) : null;
            }
        }
        catch
        {
        }
        return null;
    }

    public byte[]? LoadHeaderKey()
    {
        try
        {
            if (!File.Exists(ProdKeysPath))
            {
                return null;
            }
            foreach (string rawLine in File.ReadLines(ProdKeysPath))
            {
                int eq = rawLine.IndexOf('=');
                if (eq < 0 || !rawLine.Substring(0, eq).Trim().Equals("header_key", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                byte[] key = Convert.FromHexString(rawLine.Substring(eq + 1).Trim());
                return key.Length == 32 ? key : null;
            }
        }
        catch
        {
        }
        return null;
    }

    /// <summary>
    /// Profiles registered in the emulator, in the order they appear in profiles.dat (first is the default user).
    /// Returns an empty list when profiles.dat can't be read.
    /// </summary>
    public List<EdenProfile> LoadProfiles()
    {
        var profiles = new List<EdenProfile>();
        string path = Path.Combine(NandDirectory, ProfilesDatRelativePath.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            if (!File.Exists(path))
            {
                return profiles;
            }
            byte[] data = File.ReadAllBytes(path);
            for (int i = 0; i < MaxProfiles; i++)
            {
                int entry = ProfilesHeaderSize + i * ProfileEntrySize;
                if (entry + ProfileEntrySize > data.Length)
                {
                    break;
                }
                ReadOnlySpan<byte> uuid = data.AsSpan(entry, 16);
                if (uuid.IndexOfAnyExcept((byte)0) < 0)
                {
                    continue;
                }
                // Save folders are the UUID's two little-endian u64 halves printed high half first.
                string folder = BitConverter.ToUInt64(data, entry + 8).ToString("X16") + BitConverter.ToUInt64(data, entry).ToString("X16");
                ReadOnlySpan<byte> nameBytes = data.AsSpan(entry + ProfileUsernameOffset, ProfileUsernameSize);
                int nul = nameBytes.IndexOf((byte)0);
                string name = Encoding.UTF8.GetString(nul >= 0 ? nameBytes.Slice(0, nul) : nameBytes).Trim();
                profiles.Add(new EdenProfile(folder, name.Length > 0 ? name : folder));
            }
        }
        catch
        {
        }
        return profiles;
    }

    /// <summary>
    /// Save folders to consider: every registered profile plus device saves. Folders left behind by deleted
    /// profiles or old emulator versions are ignored so backups don't pick up stale data. If profiles.dat
    /// can't be read, every folder under the save root is used.
    /// </summary>
    public List<EdenProfile> GetSaveProfiles()
    {
        List<EdenProfile> profiles = LoadProfiles();
        if (profiles.Count == 0 && Directory.Exists(SaveRoot))
        {
            profiles = Directory.EnumerateDirectories(SaveRoot)
                .Select(Path.GetFileName)
                .Where(name => name != null && name.Length == 32 && name != DeviceSaveFolder)
                .Select(name => new EdenProfile(name!, name!))
                .ToList();
        }
        profiles.Add(new EdenProfile(DeviceSaveFolder, "Device"));
        return profiles;
    }

    public string GetSaveDirectory(string profileFolder, string titleId)
    {
        return Path.Combine(SaveRoot, profileFolder, titleId);
    }
}
