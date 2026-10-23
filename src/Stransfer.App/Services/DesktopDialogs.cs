using System.Windows;
using Microsoft.Win32;
using Stransfer.Core;

namespace Stransfer.App.Services;

public sealed record ClipboardTransfer(string[] Sources, TransferOperation Operation, uint Sequence);

public sealed class DesktopDialogs(Window owner)
{
    public bool IsOpen { get; private set; }
    public string[] ChooseFiles()
    {
        IsOpen = true;
        try { var dialog = new OpenFileDialog { Multiselect = true, Title = "Select source files" }; return dialog.ShowDialog(owner) == true ? dialog.FileNames : []; }
        finally { IsOpen = false; }
    }
    public string[] ChooseFolders()
    {
        IsOpen = true;
        try { var dialog = new OpenFolderDialog { Multiselect = true, Title = "Select source folders" }; return dialog.ShowDialog(owner) == true ? dialog.FolderNames : []; }
        finally { IsOpen = false; }
    }
    public string? ChooseDestination()
    {
        IsOpen = true;
        try { var dialog = new OpenFolderDialog { Title = "Select destination" }; return dialog.ShowDialog(owner) == true ? dialog.FolderName : null; }
        finally { IsOpen = false; }
    }
    public bool ConfirmRemove() => MessageBox.Show(owner, "Remove this record and its partial files? Completed files and sources are kept.",
        "Remove transfer", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
    public static ClipboardTransfer? ReadClipboard()
    {
        uint sequence = ExplorerPasteService.ClipboardSequence;
        if (!Clipboard.ContainsFileDropList()) return null;
        var files = Clipboard.GetFileDropList().Cast<string>().ToArray();
        var move = false;
        var effect = Clipboard.GetData("Preferred DropEffect");
        var bytes = effect is MemoryStream stream ? stream.ToArray() : effect as byte[];
        if (bytes is not null)
        {
            move = bytes.Length >= 4 && (BitConverter.ToInt32(bytes, 0) & 2) != 0;
        }
        return files.Length == 0 || sequence != ExplorerPasteService.ClipboardSequence ? null : new(files, move ? TransferOperation.Move : TransferOperation.Copy, sequence);
    }
    public static void OpenFolder(string path) => Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { path }, UseShellExecute = true });
}
