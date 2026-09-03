using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using SkiaSharp;

namespace moddingSuite.BL.TGV
{
    // Async-decoded thumbnails for .tgv files exposed to WPF tile views.
    // Two-level cache:
    //   1. In-memory LRU of decoded BitmapImage (Freeze()d, safe for cross-thread)
    //   2. On-disk PNG cache under %LOCALAPPDATA%\WarnoModdingSuite\thumbs\
    // Decode work is gated by a SemaphoreSlim so a fast scroll doesn't spawn
    // hundreds of parallel BC-decompresses.
    public sealed class ZzThumbnailCache
    {
        public static ZzThumbnailCache Instance { get; } = new ZzThumbnailCache();

        public const int ThumbSize = 96;
        private const int MaxMemoryEntries = 600;
        private const int MaxConcurrentDecodes = 2;

        private readonly string _diskCacheDir;
        private readonly object _memLock = new object();
        private readonly LinkedList<string> _lruOrder = new LinkedList<string>();
        private readonly Dictionary<string, LinkedListNode<string>> _lruIndex =
            new Dictionary<string, LinkedListNode<string>>(StringComparer.Ordinal);
        private readonly Dictionary<string, BitmapImage> _memCache =
            new Dictionary<string, BitmapImage>(StringComparer.Ordinal);
        private readonly Dictionary<string, Task<BitmapImage>> _inFlight =
            new Dictionary<string, Task<BitmapImage>>(StringComparer.Ordinal);
        private readonly SemaphoreSlim _decodeGate =
            new SemaphoreSlim(MaxConcurrentDecodes, MaxConcurrentDecodes);

        private ZzThumbnailCache()
        {
            _diskCacheDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WarnoModdingSuite", "thumbs");
            try { Directory.CreateDirectory(_diskCacheDir); }
            catch { /* best-effort */ }
        }

        public BitmapImage TryGetFromMemory(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            lock (_memLock)
            {
                if (_memCache.TryGetValue(key, out BitmapImage cached))
                {
                    TouchLru(key);
                    return cached;
                }
            }
            return null;
        }

        // Loads thumbnail asynchronously. The `readBytes` callback runs on the
        // background thread and should return the raw .tgv bytes for the entry.
        public Task<BitmapImage> GetOrLoadAsync(string key, Func<byte[]> readBytes, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(key)) return Task.FromResult<BitmapImage>(null);
            if (readBytes == null)        return Task.FromResult<BitmapImage>(null);

            BitmapImage cached = TryGetFromMemory(key);
            if (cached != null) return Task.FromResult(cached);

            Task<BitmapImage> task;
            lock (_inFlight)
            {
                if (_inFlight.TryGetValue(key, out task))
                    return task;
                task = Task.Run(() => LoadCore(key, readBytes, ct), ct);
                _inFlight[key] = task;
                task.ContinueWith(_ =>
                {
                    lock (_inFlight) _inFlight.Remove(key);
                }, TaskScheduler.Default);
            }
            return task;
        }

        private BitmapImage LoadCore(string key, Func<byte[]> readBytes, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            string diskPath = Path.Combine(_diskCacheDir, HashKey(key) + ".png");

            // Disk cache hit
            if (File.Exists(diskPath))
            {
                try
                {
                    BitmapImage img = LoadFrozenBitmap(diskPath);
                    if (img != null) { AddToMemory(key, img); return img; }
                }
                catch { /* fall through to re-decode */ }
            }

            _decodeGate.Wait(ct);
            try
            {
                ct.ThrowIfCancellationRequested();
                byte[] tgvBytes = readBytes();
                if (tgvBytes == null || tgvBytes.Length == 0) return null;

                using SKBitmap full = TgvDecoder.DecodeFullRes(tgvBytes);
                using SKBitmap thumb = ResizeToFit(full, ThumbSize, ThumbSize);
                TgvDecoder.SavePng(thumb, diskPath);
                BitmapImage img = LoadFrozenBitmap(diskPath);
                if (img != null) AddToMemory(key, img);
                return img;
            }
            finally
            {
                _decodeGate.Release();
            }
        }

        private static SKBitmap ResizeToFit(SKBitmap src, int maxW, int maxH)
        {
            if (src == null) return null;
            int srcW = src.Width;
            int srcH = src.Height;
            if (srcW <= 0 || srcH <= 0) return null;

            double scale = Math.Min((double)maxW / srcW, (double)maxH / srcH);
            if (scale >= 1.0)
            {
                // Already fits — just copy so callers can dispose freely.
                return src.Copy();
            }
            int dstW = Math.Max(1, (int)Math.Round(srcW * scale));
            int dstH = Math.Max(1, (int)Math.Round(srcH * scale));
            return src.Resize(new SKImageInfo(dstW, dstH, src.ColorType, src.AlphaType), SKFilterQuality.Medium);
        }

        private static BitmapImage LoadFrozenBitmap(string path)
        {
            using FileStream fs = File.OpenRead(path);
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.StreamSource = fs;
            img.EndInit();
            img.Freeze();
            return img;
        }

        private void AddToMemory(string key, BitmapImage img)
        {
            if (img == null) return;
            lock (_memLock)
            {
                if (_memCache.ContainsKey(key))
                {
                    _memCache[key] = img;
                    TouchLru(key);
                    return;
                }
                LinkedListNode<string> node = _lruOrder.AddFirst(key);
                _lruIndex[key] = node;
                _memCache[key] = img;
                EvictIfFull();
            }
        }

        private void TouchLru(string key)
        {
            if (_lruIndex.TryGetValue(key, out LinkedListNode<string> node))
            {
                _lruOrder.Remove(node);
                _lruOrder.AddFirst(node);
            }
        }

        private void EvictIfFull()
        {
            while (_memCache.Count > MaxMemoryEntries && _lruOrder.Last != null)
            {
                string oldest = _lruOrder.Last.Value;
                _lruOrder.RemoveLast();
                _lruIndex.Remove(oldest);
                _memCache.Remove(oldest);
            }
        }

        private static string HashKey(string key)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(key);
            byte[] hash = SHA1.HashData(bytes);
            var sb = new StringBuilder(hash.Length * 2);
            for (int i = 0; i < hash.Length; i++) sb.Append(hash[i].ToString("x2"));
            return sb.ToString();
        }
    }
}
