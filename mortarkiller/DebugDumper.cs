using Emgu.CV;
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;

namespace mortarkiller;

public sealed class DebugDumper : IDisposable
{
    public bool Enabled { get; }
    public string RootDir { get; }
    public string SessionDir { get; }
    private int _seq = 0;

    public DebugDumper(string rootDir, bool enabled = true, string sessionSuffix = null)
    {
        Enabled = enabled;
        RootDir = rootDir ?? throw new ArgumentNullException(nameof(rootDir));
        Directory.CreateDirectory(RootDir);

        // Папка з часовою міткою + суфікс сесії (детекція, кольори тощо)
        string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string folderName = string.IsNullOrWhiteSpace(sessionSuffix) ? ts : $"{ts}_{Sanitize(sessionSuffix)}";
        SessionDir = Path.Combine(RootDir, folderName);
        Directory.CreateDirectory(SessionDir);

        SaveText("session", $"Session started: {DateTime.Now:O}\nMachine: {Environment.MachineName}\n");
    }

    private static string Sanitize(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "noname";
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
        return safe.Replace(' ', '-');
    }

    private string EnsureDir(string category)
    {
        string dir = string.IsNullOrWhiteSpace(category) ? SessionDir : Path.Combine(SessionDir, category);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private string BuildPath(string nameNoExt, string ext, string category = null)
    {
        var idx = System.Threading.Interlocked.Increment(ref _seq);
        var safeName = string.Join("_", (nameNoExt ?? "noname").Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        string dir = EnsureDir(category);
        return Path.Combine(dir, $"{idx:000}_{safeName}{ext}");
    }

    public string SaveBitmap(Bitmap bmp, string nameNoExt, string category = null)
    {
        if (!Enabled || bmp == null) return string.Empty;
        string path = BuildPath(nameNoExt, ".png", category);
        bmp.Save(path, ImageFormat.Png);
        return path;
    }

    public string SaveMat(Mat mat, string nameNoExt, string category = null)
    {
        if (!Enabled || mat == null || mat.IsEmpty) return string.Empty;
        string path = BuildPath(nameNoExt, ".png", category);
        CvInvoke.Imwrite(path, mat);
        return path;
    }

    public string SaveText(string nameNoExt, string content, string category = null)
    {
        if (!Enabled) return string.Empty;
        string path = BuildPath(nameNoExt, ".txt", category);
        File.WriteAllText(path, content ?? "");
        return path;
    }

    public string CreateSubDir(string name)
    {
        string d = Path.Combine(SessionDir, name);
        Directory.CreateDirectory(d);
        return d;
    }

    public void Dispose()
    {
        // тут поки нічого
    }
}