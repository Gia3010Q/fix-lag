using Newtonsoft.Json;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace RBX_Alt_Manager
{
    public partial class AccountManager
    {
        private static readonly object notesExportLock = new object();
        private static Timer notesExportTimer;

        public static string GetNotesOutputPath(string selectedDirectory)
        {
            if (string.IsNullOrWhiteSpace(selectedDirectory) || !Path.IsPathRooted(selectedDirectory))
                throw new ArgumentException("Choose an absolute notes folder path.");
            string fullPath = Path.GetFullPath(selectedDirectory);
            string root = Path.GetPathRoot(fullPath);
            if (!string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase))
                fullPath = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string directory = string.Equals(Path.GetFileName(fullPath), "mizuautonote", StringComparison.OrdinalIgnoreCase)
                ? fullPath : Path.Combine(fullPath, "mizuautonote");
            return Path.Combine(directory, "datauser.js");
        }

        public static string ExportNotes(string parentDirectory)
        {
            if (AccountsList == null)
                throw new InvalidOperationException("Accounts have not loaded yet.");

            // Only public labels and user-written notes are exported. Cookies and
            // passwords remain in AccountData and never enter this JavaScript file.
            var notes = AccountsList.Select(account => new
            {
                username = account.Username ?? string.Empty,
                alias = account.Alias ?? string.Empty,
                description = account.Description ?? string.Empty
            }).ToArray();
            string javascript = "const datauser = " +
                JsonConvert.SerializeObject(notes, Formatting.Indented) + ";" + Environment.NewLine;
            string outputPath = GetNotesOutputPath(parentDirectory);
            string outputDirectory = Path.GetDirectoryName(outputPath);

            lock (notesExportLock)
            {
                Directory.CreateDirectory(outputDirectory);
                string temporary = Path.Combine(outputDirectory, "datauser." + Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    File.WriteAllText(temporary, javascript, new UTF8Encoding(false));
                    File.Copy(temporary, outputPath, true);
                }
                finally
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
            }
            return outputPath;
        }

        private static void ScheduleNotesExport()
        {
            if (General == null || string.IsNullOrWhiteSpace(General.Get("NotesExportDirectory"))) return;
            lock (notesExportLock)
            {
                if (notesExportTimer == null)
                    notesExportTimer = new Timer(state =>
                    {
                        try { ExportNotes(General.Get("NotesExportDirectory")); }
                        catch (Exception exception) { Program.Logger.Error($"Note export failed: {exception.Message}"); }
                    });
                notesExportTimer.Change(700, Timeout.Infinite);
            }
        }
    }
}
