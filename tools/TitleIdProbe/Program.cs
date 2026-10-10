using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using EdenLaunchBoxIntegration;

// Usage: TitleIdProbe <eden.exe path> <rom folder or file>...
// Translates Windows drive paths (F:/...) to WSL mounts so it can run against the real install from Linux.
EdenLayout.PathMapper = p => Regex.Replace(p.Replace('\\', '/'), @"^([A-Za-z]):/", m => $"/mnt/{char.ToLowerInvariant(m.Groups[1].Value[0])}/");

EdenLayout? layout = EdenLayout.Resolve(args[0]);
if (layout == null) { Console.WriteLine("Could not resolve Eden layout"); return 1; }
Console.WriteLine($"User dir:  {layout.UserDirectory}\nNAND dir:  {layout.NandDirectory}\nSave root: {layout.SaveRoot}");
byte[]? key = layout.LoadHeaderKey();
Console.WriteLine($"header_key loaded: {key != null}");
foreach (var p in layout.GetSaveProfiles()) Console.WriteLine($"Profile: {p.DisplayName} -> {p.FolderName}");

var files = args.Skip(1).SelectMany(a => Directory.Exists(a) ? Directory.EnumerateFiles(a) : new[] { a })
    .Where(f => SwitchTitleIdReader.SupportedExtensions.Contains(Path.GetExtension(f))).OrderBy(f => f);
int mismatches = 0;
foreach (string f in files)
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    TitleIdResult r = SwitchTitleIdReader.GetTitleId(f, key);
    string? nameId = Regex.Match(Path.GetFileName(f), @"\[(01[0-9A-Fa-f]{14})\]") is { Success: true } m ? m.Groups[1].Value.ToUpperInvariant() : null;
    string check = nameId == null ? "" : nameId == r.TitleId ? "  (matches name)" : $"  ** MISMATCH name={nameId}";
    if (check.Contains("MISMATCH")) mismatches++;
    var saves = r.TitleId == null ? "" : string.Join(",", layout.GetSaveProfiles().Where(p => Directory.Exists(layout.GetSaveDirectory(p.FolderName, r.TitleId))).Select(p => p.DisplayName));
    Console.WriteLine($"{r.TitleId ?? "----------------",-16} {r.Source,-9} {sw.ElapsedMilliseconds,5}ms  saves=[{saves}]  {Path.GetFileName(f)}{check}{(r.Detail != null ? "  " + r.Detail : "")}");
}
Console.WriteLine($"Mismatches: {mismatches}");
return 0;
