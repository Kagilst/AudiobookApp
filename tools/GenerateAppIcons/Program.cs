using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

static string RepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "AudiobookApp.sln"))
            || Directory.Exists(Path.Combine(dir.FullName, "AudiobookApp")))
            return dir.FullName;
        dir = dir.Parent;
    }
    return Directory.GetCurrentDirectory();
}

var repo = args.Length > 0 ? Path.GetFullPath(args[0]) : RepoRoot();
var source = Path.Combine(repo, "AudiobookApp", "Assets", "Brand", "book-icon-source.jpg");
var assets = Path.Combine(repo, "AudiobookApp", "Assets");
if (!File.Exists(source))
    throw new FileNotFoundException("Missing brand source", source);

Directory.CreateDirectory(assets);
using var src = Image.FromFile(source);
var black = Color.Black;

SavePng(Square(src, 50, black), Path.Combine(assets, "StoreLogo.png"));
SavePng(Square(src, 88, black), Path.Combine(assets, "Square44x44Logo.scale-200.png"));
SavePng(Square(src, 24, black), Path.Combine(assets, "Square44x44Logo.targetsize-24_altform-unplated.png"));
SavePng(Square(src, 300, black), Path.Combine(assets, "Square150x150Logo.scale-200.png"));
SavePng(Letterbox(src, 620, 300, black), Path.Combine(assets, "Wide310x150Logo.scale-200.png"));
SavePng(Letterbox(src, 1240, 600, black), Path.Combine(assets, "SplashScreen.scale-200.png"));
SavePng(Square(src, 48, black), Path.Combine(assets, "LockScreenLogo.scale-200.png"));

var icoPath = Path.Combine(assets, "App.ico");
WriteIcon(src, icoPath, black, 16, 24, 32, 48, 64, 128, 256);
Console.WriteLine($"Wrote icons under {assets}");
Console.WriteLine($"App.ico bytes: {new FileInfo(icoPath).Length}");

static Bitmap Square(Image src, int size, Color bg)
{
    var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    using var g = Graphics.FromImage(bmp);
    g.Clear(bg);
    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
    g.SmoothingMode = SmoothingMode.HighQuality;
    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    g.CompositingQuality = CompositingQuality.HighQuality;
    var scale = Math.Min(size / (double)src.Width, size / (double)src.Height);
    var w = (int)Math.Round(src.Width * scale);
    var h = (int)Math.Round(src.Height * scale);
    g.DrawImage(src, (size - w) / 2f, (size - h) / 2f, w, h);
    return bmp;
}

static Bitmap Letterbox(Image src, int width, int height, Color bg)
{
    var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
    using var g = Graphics.FromImage(bmp);
    g.Clear(bg);
    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
    g.SmoothingMode = SmoothingMode.HighQuality;
    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    g.CompositingQuality = CompositingQuality.HighQuality;
    var scale = Math.Min(width / (double)src.Width, height / (double)src.Height);
    var w = (int)Math.Round(src.Width * scale);
    var h = (int)Math.Round(src.Height * scale);
    g.DrawImage(src, (width - w) / 2f, (height - h) / 2f, w, h);
    return bmp;
}

static void SavePng(Bitmap bmp, string path)
{
    bmp.Save(path, ImageFormat.Png);
    bmp.Dispose();
}

static void WriteIcon(Image src, string path, Color bg, params int[] sizes)
{
    var frames = new List<Bitmap>();
    var dibs = new List<byte[]>();
    foreach (var size in sizes)
    {
        var frame = Square(src, size, bg);
        frames.Add(frame);
        dibs.Add(ToIconDib(frame));
    }

    using var ms = new MemoryStream();
    using (var bw = new BinaryWriter(ms))
    {
        bw.Write((ushort)0);
        bw.Write((ushort)1);
        bw.Write((ushort)frames.Count);
        var offset = 6 + 16 * frames.Count;
        for (var i = 0; i < frames.Count; i++)
        {
            bw.Write((byte)(frames[i].Width >= 256 ? 0 : frames[i].Width));
            bw.Write((byte)(frames[i].Height >= 256 ? 0 : frames[i].Height));
            bw.Write((byte)0);
            bw.Write((byte)0);
            bw.Write((ushort)1);
            bw.Write((ushort)32);
            bw.Write(dibs[i].Length);
            bw.Write(offset);
            offset += dibs[i].Length;
        }

        foreach (var dib in dibs)
            bw.Write(dib);
    }

    File.WriteAllBytes(path, ms.ToArray());
    foreach (var frame in frames)
        frame.Dispose();
}

static byte[] ToIconDib(Bitmap bmp)
{
    var w = bmp.Width;
    var h = bmp.Height;
    var xorStride = w * 4;
    var andStride = ((w + 31) / 32) * 4;
    var xorSize = xorStride * h;
    var andSize = andStride * h;

    using var ms = new MemoryStream();
    using (var bw = new BinaryWriter(ms))
    {
        bw.Write(40);
        bw.Write(w);
        bw.Write(h * 2);
        bw.Write((short)1);
        bw.Write((short)32);
        bw.Write(0);
        bw.Write(xorSize + andSize);
        bw.Write(0);
        bw.Write(0);
        bw.Write(0);
        bw.Write(0);

        for (var y = h - 1; y >= 0; y--)
        {
            for (var x = 0; x < w; x++)
            {
                var c = bmp.GetPixel(x, y);
                bw.Write(c.B);
                bw.Write(c.G);
                bw.Write(c.R);
                bw.Write(c.A);
            }
        }

        bw.Write(new byte[andSize]);
    }

    return ms.ToArray();
}
