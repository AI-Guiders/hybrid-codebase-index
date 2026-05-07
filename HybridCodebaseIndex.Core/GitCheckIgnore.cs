using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace HybridCodebaseIndex.Core;

internal static class GitCheckIgnore
{
    internal static HashSet<string> GetIgnoredRelativePathsOrEmpty(string workspaceRoot, IReadOnlyList<string> relativePaths)
    {
        if (relativePaths.Count == 0)
            return [];

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "check-ignore -z --stdin",
                WorkingDirectory = workspaceRoot,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardInputEncoding = Encoding.UTF8,
                StandardOutputEncoding = Encoding.UTF8,
            };

            using var p = Process.Start(psi);
            if (p is null)
                return [];

            // For -z: NUL-separated input.
            using (var stdin = p.StandardInput)
            {
                for (var i = 0; i < relativePaths.Count; i++)
                {
                    if (i > 0) stdin.Write('\0');
                    stdin.Write(relativePaths[i]);
                }
            }

            var stdout = p.StandardOutput.ReadToEnd();
            _ = p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0 && stdout.Length == 0)
                return [];

            var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var parts = stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                var s = part.Trim();
                if (s.Length == 0)
                    continue;
                ignored.Add(s.Replace('\\', '/'));
            }

            return ignored;
        }
        catch (Win32Exception)
        {
            // git not installed / not on PATH
            return [];
        }
        catch
        {
            return [];
        }
    }
}
