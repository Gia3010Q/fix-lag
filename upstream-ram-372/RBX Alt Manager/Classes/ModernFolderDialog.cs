using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace RBX_Alt_Manager.Classes
{
    // Windows Explorer's Common Item Dialog, with an address bar and editable Folder field.
    internal static class ModernFolderDialog
    {
        private const uint FosPickFolders = 0x20;
        private const uint FosForceFileSystem = 0x40;
        private const uint FosPathMustExist = 0x800;
        private const uint SigdnFileSysPath = 0x80058000;
        private const int CancelledHResult = unchecked((int)0x800704C7);

        [ComImport, Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]
        private class FileOpenDialogClass { }

        [ComImport, Guid("42F85136-DB7E-439C-85F1-E4075D135FC8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileDialog
        {
            [PreserveSig] int Show(IntPtr owner);
            void SetFileTypes(uint count, IntPtr filterSpec);
            void SetFileTypeIndex(uint index);
            void GetFileTypeIndex(out uint index);
            void Advise(IntPtr events, out uint cookie);
            void Unadvise(uint cookie);
            void SetOptions(uint options);
            void GetOptions(out uint options);
            void SetDefaultFolder(IShellItem folder);
            void SetFolder(IShellItem folder);
            void GetFolder(out IShellItem folder);
            void GetCurrentSelection(out IShellItem item);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetFileName(out IntPtr name);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
            void GetResult(out IShellItem item);
            void AddPlace(IShellItem item, uint location);
            void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
            void Close(int result);
            void SetClientGuid(ref Guid guid);
            void ClearClientData();
            void SetFilter(IntPtr filter);
        }

        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler(IntPtr context, ref Guid handler, ref Guid requestedInterface, out IntPtr result);
            void GetParent(out IShellItem parent);
            void GetDisplayName(uint format, out IntPtr name);
            void GetAttributes(uint mask, out uint attributes);
            void Compare(IShellItem item, uint hint, out int order);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        private static extern int SHCreateItemFromParsingName(string path, IntPtr context,
            ref Guid interfaceId, [MarshalAs(UnmanagedType.Interface)] out IShellItem item);

        internal static bool TryChoose(IWin32Window owner, string initialDirectory, out string selectedDirectory)
        {
            selectedDirectory = null;
            IFileDialog dialog = null;
            IShellItem initialItem = null;
            IShellItem selectedItem = null;
            IntPtr name = IntPtr.Zero;
            try
            {
                dialog = (IFileDialog)new FileOpenDialogClass();
                dialog.GetOptions(out uint options);
                dialog.SetOptions(options | FosPickFolders | FosForceFileSystem | FosPathMustExist);
                dialog.SetTitle("Select Notes Folder");
                dialog.SetOkButtonLabel("Select Folder");

                if (Directory.Exists(initialDirectory))
                {
                    Guid shellItemId = typeof(IShellItem).GUID;
                    if (SHCreateItemFromParsingName(initialDirectory, IntPtr.Zero, ref shellItemId, out initialItem) == 0)
                        dialog.SetDefaultFolder(initialItem);
                }

                int result = dialog.Show(owner?.Handle ?? IntPtr.Zero);
                if (result == CancelledHResult) return false;
                if (result != 0) Marshal.ThrowExceptionForHR(result);

                dialog.GetResult(out selectedItem);
                selectedItem.GetDisplayName(SigdnFileSysPath, out name);
                selectedDirectory = Marshal.PtrToStringUni(name);
                return !string.IsNullOrWhiteSpace(selectedDirectory);
            }
            finally
            {
                if (name != IntPtr.Zero) Marshal.FreeCoTaskMem(name);
                if (selectedItem != null) Marshal.ReleaseComObject(selectedItem);
                if (initialItem != null) Marshal.ReleaseComObject(initialItem);
                if (dialog != null) Marshal.ReleaseComObject(dialog);
            }
        }
    }
}
