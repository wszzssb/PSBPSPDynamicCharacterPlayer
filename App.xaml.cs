using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace PSBPSPDynamicCharacterPlayer;

public partial class App : System.Windows.Application
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectory(string lpPathName);

    protected override void OnStartup(StartupEventArgs e)
    {
        // 确保 FreeMote 的 emotedriver.dll 及其 x64 依赖能从解包工具目录加载
        const string freeMoteLib = @"D:\test\galgame\解包工具\FreeMoteViewer\lib";
        var x64 = Path.Combine(freeMoteLib, "x64");
        if (Directory.Exists(x64))
        {
            SetDllDirectory(x64);
        }
        else if (Directory.Exists(freeMoteLib))
        {
            SetDllDirectory(freeMoteLib);
        }
        base.OnStartup(e);
    }
}
