using System.Globalization;
using System.Runtime.InteropServices;

namespace PerformanceBooster.Launcher;

// Windows' own windows: a message box to ask and to report, a task dialog with a progress bar for the download.
static unsafe partial class Dialogs
{
    const string Title = "Performance Booster";

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBox(IntPtr owner, string text, string caption, uint type);

    const uint MbOk = 0x0, MbYesNo = 0x4, MbIconError = 0x10, MbIconInformation = 0x40, MbSetForeground = 0x10000, MbTopMost = 0x40000;
    const int IdYes = 6, IdCancel = 2;

    public static bool Ask(string text) => MessageBox(IntPtr.Zero, text, Title, MbYesNo | MbIconInformation | MbSetForeground | MbTopMost) == IdYes;

    public static void Error(string text) => MessageBox(IntPtr.Zero, text, Title, MbOk | MbIconError | MbSetForeground | MbTopMost);

    // TASKDIALOGCONFIG (commctrl.h packs it to 1 byte).
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct TaskDialogConfig
    {
        public uint Size;
        public IntPtr Parent, Instance;
        public uint Flags, CommonButtons;
        public char* WindowTitle;
        public IntPtr MainIcon;
        public char* MainInstruction, Content;
        public uint ButtonCount;
        public IntPtr Buttons;
        public int DefaultButton;
        public uint RadioButtonCount;
        public IntPtr RadioButtons;
        public int DefaultRadioButton;
        public char* VerificationText, ExpandedInformation, ExpandedControlText, CollapsedControlText;
        public IntPtr FooterIcon;
        public char* Footer;
        public IntPtr Callback, CallbackData;
        public uint Width;
    }

    [LibraryImport("comctl32.dll")]
    private static partial int TaskDialogIndirect(TaskDialogConfig* config, int* button, int* radioButton, int* verification);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    private static partial nint SendMessage(IntPtr hwnd, uint message, nuint wParam, nint lParam);

    const uint TdfAllowDialogCancellation = 0x8, TdfShowProgressBar = 0x200, TdfCallbackTimer = 0x800, TdcbfCancelButton = 0x8;
    const uint TdnCreated = 0, TdnButtonClicked = 2, TdnTimer = 4;
    const uint WmUser = 0x400, TdmClickButton = WmUser + 102, TdmSetProgressBarRange = WmUser + 105, TdmSetProgressBarPos = WmUser + 106,
        TdmUpdateElementText = WmUser + 114;
    const nuint TdeContent = 0;

    // Shows the download until it finishes, fails or is cancelled; true when the file is complete.
    public static bool Progress(Download download)
    {
        var handle = GCHandle.Alloc(download);
        var title = Marshal.StringToHGlobalUni(Title);
        var instruction = Marshal.StringToHGlobalUni(Texts.T("Downloading the .NET 10 Desktop Runtime…"));
        var content = Marshal.StringToHGlobalUni(" ");
        try
        {
            var config = new TaskDialogConfig
            {
                Size = (uint)sizeof(TaskDialogConfig),
                Flags = TdfAllowDialogCancellation | TdfShowProgressBar | TdfCallbackTimer,
                CommonButtons = TdcbfCancelButton,
                WindowTitle = (char*)title,
                MainInstruction = (char*)instruction,
                Content = (char*)content,
                Callback = (IntPtr)(delegate* unmanaged<IntPtr, uint, nuint, nint, nint, int>)&OnNotification,
                CallbackData = GCHandle.ToIntPtr(handle),
            };
            int button;
            if (TaskDialogIndirect(&config, &button, null, null) != 0)
                download.Start(); // the window could not be shown: the download still runs
            download.Wait();
            return download.Succeeded;
        }
        finally
        {
            download.Cancel();
            download.Wait();
            Marshal.FreeHGlobal(title);
            Marshal.FreeHGlobal(instruction);
            Marshal.FreeHGlobal(content);
            handle.Free();
        }
    }

    // Starts the download when the window opens, shows its progress five times a second and closes the window when it is done.
    [UnmanagedCallersOnly]
    static int OnNotification(IntPtr hwnd, uint notification, nuint wParam, nint lParam, nint data)
    {
        if (GCHandle.FromIntPtr(data).Target is not Download download) return 0;
        switch (notification)
        {
            case TdnCreated:
                SendMessage(hwnd, TdmSetProgressBarRange, 0, 1000 << 16);
                download.Start();
                break;
            case TdnTimer:
                long total = download.Total, received = download.Received;
                if (total > 0) SendMessage(hwnd, TdmSetProgressBarPos, (nuint)(received * 1000 / total), 0);
                var text = total > 0 ? Texts.F("{0} MB of {1} MB", Mb(received), Mb(total)) : Texts.F("{0} MB", Mb(received));
                fixed (char* p = text) SendMessage(hwnd, TdmUpdateElementText, TdeContent, (nint)p);
                if (download.Done) SendMessage(hwnd, TdmClickButton, IdCancel, 0);
                break;
            case TdnButtonClicked:
                if (!download.Done) download.Cancel();
                break;
        }
        return 0;
    }

    static string Mb(long bytes) => (bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture);
}
