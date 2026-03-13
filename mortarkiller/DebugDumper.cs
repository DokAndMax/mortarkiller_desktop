using Emgu.CV;
using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace mortarkiller;

public sealed class DebugDumper : IDisposable
{
    public bool Enabled { get; }
    public string RootDir { get; }
    public string SessionDir { get; }
    private int _seq = 0;

    // ▼▼▼ Асинхронна черга запису ▼▼▼
    private readonly BlockingCollection<Action> _writeQueue = new(boundedCapacity: 200);
    private readonly Task _writerTask;
    private readonly CancellationTokenSource _cts = new();
    // ▲▲▲

    public DebugDumper(string rootDir, bool enabled = true, string sessionSuffix = null)
    {
        Enabled = enabled;
        RootDir = rootDir ?? throw new ArgumentNullException(nameof(rootDir));
        Directory.CreateDirectory(RootDir);

        string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string folderName = string.IsNullOrWhiteSpace(sessionSuffix)
            ? ts : $"{ts}_{Sanitize(sessionSuffix)}";
        SessionDir = Path.Combine(RootDir, folderName);
        Directory.CreateDirectory(SessionDir);

        // ▼▼▼ Запуск фонового потоку запису ▼▼▼
        _writerTask = Task.Factory.StartNew(
            WriterLoop,
            _cts.Token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        // ▲▲▲

        SaveText("session",
            $"Session started: {DateTime.Now:O}\nMachine: {Environment.MachineName}\n");
    }

    // ▼▼▼ Фоновий цикл запису — один потік, послідовно ▼▼▼
    private void WriterLoop()
    {
        try
        {
            foreach (var action in _writeQueue.GetConsumingEnumerable(_cts.Token))
            {
                try { action(); }
                catch { /* проковтнути помилки запису */ }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Додає дію в чергу. Якщо черга переповнена — пропускає (не блокує).
    /// </summary>
    private void Enqueue(Action writeAction)
    {
        if (!Enabled) return;
        // TryAdd — неблокуючий, якщо черга повна → просто скіпаємо
        _writeQueue.TryAdd(writeAction);
    }
    // ▲▲▲

    private static string Sanitize(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "noname";
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
        return safe.Replace(' ', '-');
    }

    private string EnsureDir(string category)
    {
        string dir = string.IsNullOrWhiteSpace(category)
            ? SessionDir : Path.Combine(SessionDir, category);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private string BuildPath(string nameNoExt, string ext, string category = null)
    {
        var idx = Interlocked.Increment(ref _seq);
        var safeName = string.Join("_",
            (nameNoExt ?? "noname").Split(
                Path.GetInvalidFileNameChars(),
                StringSplitOptions.RemoveEmptyEntries));
        string dir = EnsureDir(category);
        return Path.Combine(dir, $"{idx:000}_{safeName}{ext}");
    }

    // ▼▼▼ ЗМІНЕНО: Збереження через чергу, копія даних для потокобезпеки ▼▼▼

    public string SaveBitmap(Bitmap bmp, string nameNoExt, string category = null)
    {
        if (!Enabled || bmp == null) return string.Empty;
        string path = BuildPath(nameNoExt, ".png", category);

        // Клонуємо bitmap щоб оригінал можна було dispose в основному потоці
        var clone = (Bitmap)bmp.Clone();
        Enqueue(() =>
        {
            try { clone.Save(path, ImageFormat.Png); }
            finally { clone.Dispose(); }
        });

        return path;
    }

    public string SaveMat(Mat mat, string nameNoExt, string category = null)
    {
        if (!Enabled || mat == null || mat.IsEmpty) return string.Empty;
        string path = BuildPath(nameNoExt, ".png", category);

        // Клонуємо Mat щоб оригінал можна було dispose
        var clone = mat.Clone();
        Enqueue(() =>
        {
            try { CvInvoke.Imwrite(path, clone); }
            finally { clone.Dispose(); }
        });

        return path;
    }

    public string SaveText(string nameNoExt, string content, string category = null)
    {
        if (!Enabled) return string.Empty;
        string path = BuildPath(nameNoExt, ".txt", category);

        // Рядок — immutable, копія не потрібна
        Enqueue(() => File.WriteAllText(path, content ?? ""));

        return path;
    }
    // ▲▲▲

    public string CreateSubDir(string name)
    {
        string d = Path.Combine(SessionDir, name);
        Directory.CreateDirectory(d);
        return d;
    }

    // ▼▼▼ ЗМІНЕНО: Коректне завершення з дочікуванням черги ▼▼▼
    public void Dispose()
    {
        _writeQueue.CompleteAdding();

        // Даємо до 5 секунд на допис залишку черги
        try { _writerTask.Wait(TimeSpan.FromSeconds(5)); }
        catch { }

        try { _cts.Cancel(); } catch { }
        _cts.Dispose();
        _writeQueue.Dispose();
    }
    // ▲▲▲
}