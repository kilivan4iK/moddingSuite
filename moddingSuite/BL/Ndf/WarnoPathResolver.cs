using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace moddingSuite.BL.Ndf
{
    internal static class WarnoPathResolver
    {
        public static IReadOnlyList<string> EnumerateRoots(string configuredPath)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            AddIfExists(result, seen, configuredPath);

            string[] suffixes =
            {
                @"SteamLibrary\steamapps\common\WARNO",
                @"Program Files (x86)\Steam\steamapps\common\WARNO"
            };

            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
                    continue;

                foreach (string suffix in suffixes)
                {
                    string candidate = Path.Combine(drive.RootDirectory.FullName, suffix);
                    AddIfExists(result, seen, candidate);
                }
            }

            return result;
        }

        private static void AddIfExists(ICollection<string> result, ISet<string> seen, string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            string full;
            try
            {
                full = Path.GetFullPath(path);
            }
            catch
            {
                return;
            }

            if (!Directory.Exists(full))
                return;

            if (seen.Add(full))
                result.Add(full);
        }
    }
}
