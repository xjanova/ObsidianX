using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;

namespace ObsidianX.Client;

public partial class App : Application
{
    // Shell change-notify flags — nudges Explorer to drop cached icons for the
    // exe so the taskbar/shortcut icon updates after an icon-cache hiccup.
    private const uint SHCNE_ASSOCCHANGED = 0x08000000;
    private const uint SHCNF_IDLIST = 0x0000;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            EnsureDesktopShortcut();
            // Ask shell to refresh icons in case the cache is stale.
            SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Shortcut setup failed: {ex.Message}");
        }
    }

    // Create or self-heal a Desktop shortcut pointing at this exe. Uses the
    // WScript.Shell COM object via late binding so we don't need an extra COM
    // reference. IconLocation points at the exe's own embedded icon (index 0)
    // so the shortcut always reflects the latest Logo.ico bundled into the
    // binary. If a shortcut already exists but points at a stale path (user
    // moved the build folder, switched Debug/Release, etc.) we rewrite it.
    private static void EnsureDesktopShortcut()
    {
        var exe = Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return;

        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var shortcutPath = Path.Combine(desktop, "ObsidianX.lnk");

        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null) return;
        var shell = Activator.CreateInstance(shellType);
        if (shell == null) return;

        object? shortcut = null;
        try
        {
            shortcut = shellType.InvokeMember(
                "CreateShortcut",
                BindingFlags.InvokeMethod, null, shell,
                new object[] { shortcutPath });
            if (shortcut == null) return;
            var st = shortcut.GetType();

            // WScript.Shell.CreateShortcut returns an existing shortcut or a
            // blank one — checking TargetPath lets us skip untouched writes
            // and avoid bumping the file's LastWriteTime on every launch.
            var currentTarget = st.InvokeMember("TargetPath", BindingFlags.GetProperty,
                null, shortcut, null) as string ?? "";
            var currentIcon = st.InvokeMember("IconLocation", BindingFlags.GetProperty,
                null, shortcut, null) as string ?? "";
            var wantIcon = exe + ",0";

            if (string.Equals(currentTarget, exe, StringComparison.OrdinalIgnoreCase)
                && string.Equals(currentIcon, wantIcon, StringComparison.OrdinalIgnoreCase))
            {
                return;  // shortcut already points at us — nothing to do
            }

            st.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { exe });
            st.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut,
                new object[] { Path.GetDirectoryName(exe) ?? "" });
            st.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut,
                new object[] { wantIcon });
            st.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut,
                new object[] { "ObsidianX Neural Knowledge Network" });
            st.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
        }
        finally
        {
            if (shortcut != null) Marshal.FinalReleaseComObject(shortcut);
            Marshal.FinalReleaseComObject(shell);
        }
    }
}
