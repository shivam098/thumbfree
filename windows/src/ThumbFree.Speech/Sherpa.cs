using System.Reflection;
using System.Runtime.InteropServices;

namespace ThumbFree.Speech;

/// <summary>Small guards around the sherpa-onnx C# wrapper, whose failures would otherwise crash the process later.</summary>
internal static class Sherpa
{
    /// <summary>Full paths of <paramref name="names"/> in <paramref name="directory"/>; throws if one is missing (as Android does).</summary>
    public static string[] RequireFiles(string directory, params string[] names) =>
        names.Select(name =>
        {
            var path = Path.Combine(directory, name);
            return File.Exists(path) ? NativePath(path) : throw new FileNotFoundException($"Missing {path}", path);
        }).ToArray();

    /// <summary>
    /// The wrapper marshals paths as ANSI strings, so a folder name outside the system code page (a Hindi user name, say)
    /// would reach sherpa-onnx as "?". Such paths get their 8.3 short form, which is plain ASCII when the volume keeps
    /// short names; anything else is passed unchanged.
    /// </summary>
    public static string NativePath(string path)
    {
        var full = Path.GetFullPath(path);
        if (full.All(char.IsAscii) || !OperatingSystem.IsWindows())
        {
            return full;
        }

        var buffer = new char[1024];
        var length = GetShortPathNameW(full, buffer, (uint)buffer.Length);
        var shortPath = length > 0 && length < buffer.Length ? new string(buffer, 0, (int)length) : null;
        return shortPath is not null && shortPath.All(char.IsAscii) ? shortPath : full;
    }

    /// <summary>
    /// sherpa-onnx returns a null object for a configuration it rejects (and logs to a console nobody sees); the wrapper
    /// keeps it, and the first call through it would crash the process. Throws instead.
    /// </summary>
    public static T Created<T>(T wrapper, string what)
        where T : class, IDisposable
    {
        var field = typeof(T).GetField("_handle", BindingFlags.Instance | BindingFlags.NonPublic);
        if (field?.GetValue(wrapper) is HandleRef { Handle: 0 })
        {
            wrapper.Dispose();
            throw new InvalidDataException($"sherpa-onnx couldn't load the {what}.");
        }

        return wrapper;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathNameW(string longPath, char[] shortPath, uint length);
}
