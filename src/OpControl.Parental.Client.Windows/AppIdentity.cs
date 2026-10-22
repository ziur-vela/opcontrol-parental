// Copyright (c) 2026 Ziur / OpControl IT Solutions
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace OpControl.Parental.Client;

public static class AppIdentity
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetPackageFamilyName(IntPtr process, ref uint packageFamilyNameLength, StringBuilder? packageFamilyName);

    public static string FromWin32Path(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        var file = Path.GetFileName(path).Trim().ToLowerInvariant();
        if (file.Length == 0 || file == "unknown") return "";
        var publisher = "";
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            publisher = NormalizePublisher(info.CompanyName);
        }
        catch { }
        return $"win32:{file}|{publisher}";
    }

    public static string FromStorePath(string path)
    {
        var marker = @"\windowsapps\";
        var index = path.ToLowerInvariant().IndexOf(marker, StringComparison.Ordinal);
        if (index < 0) return "";
        var full = path[(index + marker.Length)..];
        var slash = full.IndexOf('\\');
        if (slash >= 0) full = full[..slash];
        var parts = full.Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return "";
        var family = $"{parts[0]}_{parts[^1]}".ToLowerInvariant();
        return $"store:{family}";
    }

    public static string ForProcess(Process process, string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            var store = FromStorePath(path);
            if (store.Length > 0) return store;
        }

        try
        {
            var pfn = PackageFamilyName(process.Handle);
            if (pfn.Length > 0) return $"store:{pfn.ToLowerInvariant()}";
        }
        catch { }

        return FromWin32Path(path);
    }

    public static string NormalizePublisher(string? publisher)
    {
        if (string.IsNullOrWhiteSpace(publisher)) return "";
        var builder = new StringBuilder(publisher.Length);
        foreach (var ch in publisher.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch)) builder.Append(ch);
        }
        return builder.ToString();
    }

    public static string FromManualPath(string path)
    {
        var full = path.Trim().Trim('"').ToLowerInvariant();
        return full.Length == 0 ? "" : $"path:{full}";
    }

    private static string PackageFamilyName(IntPtr processHandle)
    {
        if (!OperatingSystem.IsWindows() || processHandle == IntPtr.Zero) return "";
        uint length = 0;
        GetPackageFamilyName(processHandle, ref length, null);
        if (length == 0) return "";
        var builder = new StringBuilder((int)length);
        return GetPackageFamilyName(processHandle, ref length, builder) ? builder.ToString() : "";
    }
}
