using System.IO;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace ScreenTranslate.App;

/// <summary>
/// 管理者権限不要（%LOCALAPPDATA%\Programs\ScreenTranslate）のデスクトップアプリ登録・
/// デスクトップ／スタートメニューショートカット作成・カスタムモノクロアイコン生成を担うクラス。
/// </summary>
internal static class DesktopInstaller
{
    private const string AppFolderName = "ScreenTranslate";
    private const string ExeFileName = "ScreenTranslate.exe";
    private const string IconFileName = "ScreenTranslate.ico";
    private const string UninstallRegKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ScreenTranslate";

    public static string UserInstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs",
        AppFolderName);

    public static string InstalledExePath => Path.Combine(UserInstallDir, ExeFileName);

    public static bool IsInstalled()
        => File.Exists(InstalledExePath);

    public static (bool Success, string Message) InstallAndCreateShortcuts()
    {
        try
        {
            string? currentExe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(currentExe) || !File.Exists(currentExe))
                return (false, "実行ファイルのパスを取得できませんでした。");

            Directory.CreateDirectory(UserInstallDir);

            string targetExe = InstalledExePath;
            if (!string.Equals(Path.GetFullPath(currentExe), Path.GetFullPath(targetExe), StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(currentExe, targetExe, overwrite: true);
            }

            string iconPath = Path.Combine(UserInstallDir, IconFileName);
            File.WriteAllBytes(iconPath, BuildAppIconIcoBytes());

            string desktopShortcut = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                "ScreenTranslate.lnk");
            string startMenuShortcut = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                "ScreenTranslate.lnk");

            CreateWindowsShortcut(desktopShortcut, targetExe, UserInstallDir, iconPath, "ScreenTranslate リアルタイム画面翻訳");
            CreateWindowsShortcut(startMenuShortcut, targetExe, UserInstallDir, iconPath, "ScreenTranslate リアルタイム画面翻訳");

            using var key = Registry.CurrentUser.CreateSubKey(UninstallRegKey);
            if (key is not null)
            {
                key.SetValue("DisplayName", "ScreenTranslate");
                key.SetValue("DisplayVersion", "0.8.0");
                key.SetValue("Publisher", "ScreenTranslate");
                key.SetValue("DisplayIcon", iconPath);
                key.SetValue("InstallLocation", UserInstallDir);
                key.SetValue("UninstallString", $"\"{targetExe}\" --uninstall");
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }

            return (true, "デスクトップとスタートメニューに ScreenTranslate を登録しました。");
        }
        catch (Exception ex)
        {
            return (false, $"ショートカット登録エラー: {ex.Message}");
        }
    }

    public static void UninstallQuietly()
    {
        try
        {
            string desktopShortcut = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                "ScreenTranslate.lnk");
            string startMenuShortcut = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                "ScreenTranslate.lnk");

            if (File.Exists(desktopShortcut)) File.Delete(desktopShortcut);
            if (File.Exists(startMenuShortcut)) File.Delete(startMenuShortcut);
            Registry.CurrentUser.DeleteSubKeyTree(UninstallRegKey, throwOnMissingSubKey: false);
        }
        catch { }
    }

    private static void CreateWindowsShortcut(string lnkPath, string targetPath, string workingDir, string iconPath, string description)
    {
        Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null) return;

        object? shell = Activator.CreateInstance(shellType);
        if (shell is null) return;

        object? shortcut = shellType.InvokeMember(
            "CreateShortcut",
            BindingFlags.InvokeMethod,
            null,
            shell,
            [lnkPath]);
        if (shortcut is null) return;

        Type scType = shortcut.GetType();
        scType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, [targetPath]);
        scType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, [workingDir]);
        scType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, [$"{iconPath},0"]);
        scType.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, [description]);
        scType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
    }

    public static ImageSource? CreateWindowIcon()
    {
        try
        {
            var ms = new MemoryStream(BuildAppIconIcoBytes());
            var frame = BitmapFrame.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            frame.Freeze();
            return frame;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// モノクロダーク丸角背景 (#12161C) + エメラルドコーナーブラケット (#4DA892) + テキストラインの
    /// マルチサイズ (16x16 / 32x32 / 48x48) 32-bit BGRA .ico バイナリをメモリ上で生成する。
    /// </summary>
    public static byte[] BuildAppIconIcoBytes()
    {
        int[] sizes = [16, 32, 48];
        var images = sizes.Select(RenderIconBmpEntry).ToArray();

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        // ICONDIR
        bw.Write((ushort)0); // Reserved
        bw.Write((ushort)1); // Type = 1 (Icon)
        bw.Write((ushort)images.Length);

        int offset = 6 + 16 * images.Length;
        for (int i = 0; i < images.Length; i++)
        {
            int s = sizes[i];
            bw.Write((byte)(s >= 256 ? 0 : s));
            bw.Write((byte)(s >= 256 ? 0 : s));
            bw.Write((byte)0);  // Palette count
            bw.Write((byte)0);  // Reserved
            bw.Write((ushort)1); // Planes
            bw.Write((ushort)32); // BitCount
            bw.Write((uint)images[i].Length);
            bw.Write((uint)offset);
            offset += images[i].Length;
        }

        foreach (var img in images)
            bw.Write(img);

        bw.Flush();
        return ms.ToArray();
    }

    private static byte[] RenderIconBmpEntry(int size)
    {
        int andRowBytes = ((size + 31) / 32) * 4;
        int xorBytes = size * size * 4;
        int andBytes = andRowBytes * size;

        using var ms = new MemoryStream(40 + xorBytes + andBytes);
        using var bw = new BinaryWriter(ms);

        // BITMAPINFOHEADER (biHeight is doubled for XOR + AND masks in ICO)
        bw.Write((uint)40);
        bw.Write(size);
        bw.Write(size * 2);
        bw.Write((ushort)1);
        bw.Write((ushort)32);
        bw.Write((uint)0);
        bw.Write((uint)(xorBytes + andBytes));
        bw.Write(0);
        bw.Write(0);
        bw.Write((uint)0);
        bw.Write((uint)0);

        double radius = size * 0.22;
        double pad = Math.Max(1.0, size * 0.06);
        int bMargin = Math.Max(2, (int)Math.Round(size * 0.20));
        int bArm = Math.Max(3, (int)Math.Round(size * 0.20));
        int bThick = Math.Max(1, size / 18);

        // Bottom-up BGRA rows
        for (int pyBottom = 0; pyBottom < size; pyBottom++)
        {
            int y = size - 1 - pyBottom;
            for (int x = 0; x < size; x++)
            {
                double alpha = RoundedRectCoverage(x + 0.5, y + 0.5, pad, pad, size - pad * 2, size - pad * 2, radius);
                if (alpha <= 0.01)
                {
                    bw.Write((uint)0);
                    continue;
                }

                byte r = 0x14, g = 0x19, b = 0x22;

                // Subtle outer border
                double innerCov = RoundedRectCoverage(x + 0.5, y + 0.5, pad + 1.0, pad + 1.0, size - (pad + 1.0) * 2, size - (pad + 1.0) * 2, Math.Max(1.0, radius - 1.0));
                if (innerCov < 0.95)
                {
                    r = 0x32; g = 0x3A; b = 0x46;
                }

                // Emerald tactical corner brackets (#4DA892)
                bool inTop = y >= bMargin && y < bMargin + bThick;
                bool inBot = y <= size - 1 - bMargin && y > size - 1 - bMargin - bThick;
                bool inLeft = x >= bMargin && x < bMargin + bThick;
                bool inRight = x <= size - 1 - bMargin && x > size - 1 - bMargin - bThick;

                bool topArm = y >= bMargin && y < bMargin + bArm;
                bool botArm = y <= size - 1 - bMargin && y > size - 1 - bMargin - bArm;
                bool leftArm = x >= bMargin && x < bMargin + bArm;
                bool rightArm = x <= size - 1 - bMargin && x > size - 1 - bMargin - bArm;

                bool isBracket =
                    ((inTop || inBot) && (leftArm || rightArm)) ||
                    ((inLeft || inRight) && (topArm || botArm));

                // Minimalist translation lines inside brackets
                int line1Y = (int)Math.Round(size * 0.42);
                int line2Y = (int)Math.Round(size * 0.58);
                bool isLine1 = y >= line1Y && y < line1Y + bThick && x >= bMargin + bThick * 2 && x <= size - bMargin - bThick * 2;
                bool isLine2 = y >= line2Y && y < line2Y + bThick && x >= bMargin + bThick * 2 && x <= (int)(size * 0.64);

                if (isBracket)
                {
                    r = 0x4D; g = 0xA8; b = 0x92;
                }
                else if (isLine1 || isLine2)
                {
                    r = 0xE6; g = 0xEA; b = 0xF0;
                }

                byte aByte = (byte)Math.Clamp((int)Math.Round(alpha * 255.0), 0, 255);
                bw.Write(b);
                bw.Write(g);
                bw.Write(r);
                bw.Write(aByte);
            }
        }

        // AND mask (all 0 since 32-bit alpha channel controls transparency)
        bw.Write(new byte[andBytes]);
        bw.Flush();
        return ms.ToArray();
    }

    private static double RoundedRectCoverage(double px, double py, double rx, double ry, double rw, double rh, double rad)
    {
        double cx = Math.Clamp(px, rx + rad, rx + rw - rad);
        double cy = Math.Clamp(py, ry + rad, ry + rh - rad);
        double dx = px - cx;
        double dy = py - cy;
        double dist = Math.Sqrt(dx * dx + dy * dy);
        return Math.Clamp(rad + 0.5 - dist, 0.0, 1.0);
    }
}
