using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace EdenLaunchBoxIntegration;

public enum TitleIdSource
{
    None,
    NcaHeader,
    Ticket,
    FileName
}

public readonly record struct TitleIdResult(string? TitleId, TitleIdSource Source, string? Detail)
{
    public bool Success => TitleId != null;
}

/// <summary>
/// Reads the base application title ID out of Switch game files (XCI/XCZ/NSP/NSZ/NCA).
///
/// Primary method: walk the container to its NCAs and decrypt each NCA header (AES-128-XTS with the
/// console header_key from prod.keys) to read the program ID, the same way the emulator does.
/// Fallbacks: ticket file names inside NSPs (rights ID = title ID + key generation), then a
/// [0100XXXXXXXXXXXX] style ID in the file name.
/// </summary>
public static class SwitchTitleIdReader
{
    private const int NcaHeaderReadLength = 0x400;
    private const int XtsSectorSize = 0x200;
    private const int MaxNcasToInspect = 1024;

    // Application IDs are aligned to 0x2000; updates are base | 0x800 and add-ons live above base + 0x1000.
    private const ulong BaseTitleIdMask = ~0x1FFFUL;

    private const byte ContentTypeProgram = 0;
    private const byte ContentTypeControl = 2;

    private static readonly Regex FileNameTitleIdRegex = new(@"(?<![0-9A-Fa-f])(01[0-9A-Fa-f]{14})(?![0-9A-Fa-f])", RegexOptions.Compiled);
    private static readonly Regex TicketNameRegex = new(@"^([0-9A-Fa-f]{16})[0-9A-Fa-f]{16}\.tik$", RegexOptions.Compiled);

    private static readonly ConcurrentDictionary<string, TitleIdResult> cache = new(StringComparer.OrdinalIgnoreCase);

    public static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase) { ".xci", ".xcz", ".nsp", ".nsz", ".nca" };

    public static TitleIdResult GetTitleId(string romPath, byte[]? headerKey)
    {
        string cacheKey;
        try
        {
            var info = new FileInfo(romPath);
            if (!info.Exists)
            {
                return TryFromFileName(romPath) ?? new TitleIdResult(null, TitleIdSource.None, "File not found");
            }
            cacheKey = $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{(headerKey != null ? 1 : 0)}";
        }
        catch (Exception ex)
        {
            return TryFromFileName(romPath) ?? new TitleIdResult(null, TitleIdSource.None, ex.Message);
        }

        return cache.GetOrAdd(cacheKey, _ => ReadUncached(romPath, headerKey));
    }

    private static TitleIdResult ReadUncached(string romPath, byte[]? headerKey)
    {
        string? containerError = null;
        try
        {
            using var stream = new FileStream(romPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess);
            List<ContainerEntry> entries = ListContentEntries(stream, Path.GetExtension(romPath));

            if (headerKey != null)
            {
                string? fromNca = TryFromNcaHeaders(stream, entries, headerKey);
                if (fromNca != null)
                {
                    return new TitleIdResult(fromNca, TitleIdSource.NcaHeader, null);
                }
            }

            string? fromTicket = TryFromTickets(entries);
            if (fromTicket != null)
            {
                return new TitleIdResult(fromTicket, TitleIdSource.Ticket, null);
            }
        }
        catch (Exception ex)
        {
            containerError = ex.Message;
        }

        return TryFromFileName(romPath)
            ?? new TitleIdResult(null, TitleIdSource.None, containerError ?? (headerKey == null ? "No header_key available and no title ID in file name" : "No title ID found"));
    }

    private static TitleIdResult? TryFromFileName(string romPath)
    {
        Match match = FileNameTitleIdRegex.Match(Path.GetFileNameWithoutExtension(romPath));
        if (!match.Success)
        {
            return null;
        }
        ulong id = Convert.ToUInt64(match.Groups[1].Value, 16) & BaseTitleIdMask;
        return new TitleIdResult(FormatTitleId(id), TitleIdSource.FileName, null);
    }

    private static string? TryFromTickets(List<ContainerEntry> entries)
    {
        foreach (ContainerEntry entry in entries)
        {
            Match match = TicketNameRegex.Match(entry.Name);
            if (match.Success)
            {
                ulong id = Convert.ToUInt64(match.Groups[1].Value, 16);
                if ((id & BaseTitleIdMask) == id)
                {
                    return FormatTitleId(id);
                }
            }
        }
        return null;
    }

    private static string? TryFromNcaHeaders(Stream stream, List<ContainerEntry> entries, byte[] headerKey)
    {
        var votes = new Dictionary<ulong, int>();
        var exactBaseMatches = new HashSet<ulong>();
        byte[] buffer = new byte[NcaHeaderReadLength];

        foreach (ContainerEntry entry in entries.Where(IsNcaEntry).Take(MaxNcasToInspect))
        {
            if (entry.Size < NcaHeaderReadLength)
            {
                continue;
            }
            stream.Position = entry.Offset;
            if (!ReadExactly(stream, buffer))
            {
                continue;
            }
            DecryptXts(buffer, headerKey);
            if (!IsNcaMagic(buffer, 0x200))
            {
                continue;
            }
            byte contentType = buffer[0x205];
            if (contentType != ContentTypeProgram && contentType != ContentTypeControl)
            {
                continue;
            }
            ulong programId = BitConverter.ToUInt64(buffer, 0x210);
            ulong baseId = programId & BaseTitleIdMask;
            votes[baseId] = votes.TryGetValue(baseId, out int count) ? count + 1 : 1;
            if (programId == baseId)
            {
                // The base game's own Program NCA is unambiguous; no need to look through updates and add-ons.
                if (contentType == ContentTypeProgram)
                {
                    return FormatTitleId(baseId);
                }
                exactBaseMatches.Add(baseId);
            }
        }

        if (votes.Count == 0)
        {
            return null;
        }
        ulong best = votes
            .OrderByDescending(v => exactBaseMatches.Contains(v.Key))
            .ThenByDescending(v => v.Value)
            .First().Key;
        return FormatTitleId(best);
    }

    private static bool IsNcaEntry(ContainerEntry entry)
    {
        return entry.Name.EndsWith(".nca", StringComparison.OrdinalIgnoreCase) || entry.Name.EndsWith(".ncz", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNcaMagic(byte[] buffer, int offset)
    {
        return buffer[offset] == 'N' && buffer[offset + 1] == 'C' && buffer[offset + 2] == 'A' && buffer[offset + 3] is (byte)'0' or (byte)'2' or (byte)'3';
    }

    private static string FormatTitleId(ulong id) => id.ToString("X16");

    #region Containers

    private readonly record struct ContainerEntry(string Name, long Offset, long Size);

    private static List<ContainerEntry> ListContentEntries(Stream stream, string extension)
    {
        switch (extension.ToLowerInvariant())
        {
            case ".nsp":
            case ".nsz":
                return ReadPartitionFs(stream, 0, "PFS0", 0x18);
            case ".xci":
            case ".xcz":
                return ReadXciSecurePartition(stream);
            case ".nca":
                return new List<ContainerEntry> { new("program.nca", 0, stream.Length) };
            default:
                return new List<ContainerEntry>();
        }
    }

    private static List<ContainerEntry> ReadXciSecurePartition(Stream stream)
    {
        // Standard dumps start with the cart header signature; some include a 0x1000 key area in front.
        foreach (long baseOffset in new long[] { 0, 0x1000 })
        {
            byte[] header = new byte[0x200];
            stream.Position = baseOffset;
            if (!ReadExactly(stream, header) || Encoding.ASCII.GetString(header, 0x100, 4) != "HEAD")
            {
                continue;
            }
            long rootOffset = baseOffset + BitConverter.ToInt64(header, 0x130);
            List<ContainerEntry> root = ReadPartitionFs(stream, rootOffset, "HFS0", 0x40);
            int secureIndex = root.FindIndex(e => e.Name.Equals("secure", StringComparison.OrdinalIgnoreCase));
            if (secureIndex < 0 || root[secureIndex].Size == 0)
            {
                throw new InvalidDataException("XCI has no secure partition");
            }
            return ReadPartitionFs(stream, root[secureIndex].Offset, "HFS0", 0x40);
        }
        throw new InvalidDataException("Not a valid XCI file");
    }

    /// <summary>Reads a PFS0 (entry size 0x18) or HFS0 (entry size 0x40) file table. Returned offsets are absolute.</summary>
    private static List<ContainerEntry> ReadPartitionFs(Stream stream, long offset, string magic, int entrySize)
    {
        byte[] header = new byte[0x10];
        stream.Position = offset;
        if (!ReadExactly(stream, header) || Encoding.ASCII.GetString(header, 0, 4) != magic)
        {
            throw new InvalidDataException($"Expected {magic} at 0x{offset:X}");
        }
        int fileCount = BitConverter.ToInt32(header, 4);
        int stringTableSize = BitConverter.ToInt32(header, 8);
        if (fileCount < 0 || fileCount > 4096 || stringTableSize < 0 || stringTableSize > 0x100000)
        {
            throw new InvalidDataException($"Implausible {magic} header at 0x{offset:X}");
        }

        byte[] table = new byte[fileCount * entrySize + stringTableSize];
        if (!ReadExactly(stream, table))
        {
            throw new InvalidDataException($"Truncated {magic} table at 0x{offset:X}");
        }
        long dataStart = offset + header.Length + table.Length;
        int stringTableStart = fileCount * entrySize;

        var entries = new List<ContainerEntry>(fileCount);
        for (int i = 0; i < fileCount; i++)
        {
            int e = i * entrySize;
            long fileOffset = BitConverter.ToInt64(table, e);
            long fileSize = BitConverter.ToInt64(table, e + 8);
            int nameOffset = BitConverter.ToInt32(table, e + 0x10);
            int nameStart = stringTableStart + nameOffset;
            if (nameStart < stringTableStart || nameStart >= table.Length)
            {
                continue;
            }
            int nameEnd = Array.IndexOf(table, (byte)0, nameStart);
            if (nameEnd < 0)
            {
                nameEnd = table.Length;
            }
            entries.Add(new ContainerEntry(Encoding.UTF8.GetString(table, nameStart, nameEnd - nameStart), dataStart + fileOffset, fileSize));
        }
        return entries;
    }

    private static bool ReadExactly(Stream stream, byte[] buffer)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = stream.Read(buffer, read, buffer.Length - read);
            if (n <= 0)
            {
                return false;
            }
            read += n;
        }
        return true;
    }

    #endregion

    #region Crypto

    /// <summary>
    /// AES-128-XTS decrypt in place using Nintendo's variant: 0x200 byte sectors and a big-endian sector number tweak.
    /// </summary>
    private static void DecryptXts(byte[] data, byte[] key)
    {
        using Aes dataAes = Aes.Create();
        dataAes.Key = key.AsSpan(0, 16).ToArray();
        using Aes tweakAes = Aes.Create();
        tweakAes.Key = key.AsSpan(16, 16).ToArray();

        byte[] tweak = new byte[16];
        byte[] block = new byte[16];
        for (int sector = 0; sector * XtsSectorSize < data.Length; sector++)
        {
            Array.Clear(tweak);
            for (int i = 0; i < 8; i++)
            {
                tweak[15 - i] = (byte)((ulong)sector >> (8 * i));
            }
            tweak = tweakAes.EncryptEcb(tweak, PaddingMode.None);

            int sectorStart = sector * XtsSectorSize;
            int sectorEnd = Math.Min(sectorStart + XtsSectorSize, data.Length);
            for (int pos = sectorStart; pos + 16 <= sectorEnd; pos += 16)
            {
                for (int i = 0; i < 16; i++)
                {
                    block[i] = (byte)(data[pos + i] ^ tweak[i]);
                }
                byte[] plain = dataAes.DecryptEcb(block, PaddingMode.None);
                for (int i = 0; i < 16; i++)
                {
                    data[pos + i] = (byte)(plain[i] ^ tweak[i]);
                }
                MultiplyTweakByAlpha(tweak);
            }
        }
    }

    private static void MultiplyTweakByAlpha(byte[] tweak)
    {
        int carry = tweak[15] >> 7;
        for (int i = 15; i > 0; i--)
        {
            tweak[i] = (byte)((tweak[i] << 1) | (tweak[i - 1] >> 7));
        }
        tweak[0] = (byte)(tweak[0] << 1);
        if (carry != 0)
        {
            tweak[0] ^= 0x87;
        }
    }

    #endregion
}
