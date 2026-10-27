// Renders the app icon: a stick figure pointing to the right, carrying Clawd, who is
// programming on a small laptop, under its arm. Writes a multi-size .ico file.
//
// Usage: dotnet run --project tools/IconGen -- src/AiDevControlPanel.App/Assets/app.ico [preview.png]

using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

var output = args.Length > 0 ? args[0] : "app.ico";
int[] sizes = [16, 24, 32, 48, 64, 128, 256];

var frames = sizes.Select(size => (size, Render(size))).ToList();
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
using (var stream = File.Create(output))
{
    WriteIco(stream, frames);
}

if (args.Length > 1)
{
    var encoder = new PngBitmapEncoder();
    encoder.Frames.Add(BitmapFrame.Create(frames[^1].Item2));
    using var preview = File.Create(args[1]);
    encoder.Save(preview);
}

Console.WriteLine($"Wrote {output} ({string.Join(", ", sizes)} px).");

static BitmapSource Render(int size)
{
    var detailed = size >= 48;
    var visual = new DrawingVisual();
    using (var dc = visual.RenderOpen())
    {
        dc.PushTransform(new ScaleTransform(size / 256.0, size / 256.0));
        Draw(dc, detailed);
        dc.Pop();
    }

    var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
    bitmap.Render(visual);
    return new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
}

static void Draw(DrawingContext dc, bool detailed)
{
    var ink = Brush("#2B2B2B");
    var clawd = Brush("#D97757");
    var eyes = Brush("#1B1B1B");
    var thickness = detailed ? 13 : 24;
    var pen = new Pen(ink, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };

    // Stick figure facing right.
    var shoulder = new Point(180, 98);
    var hip = new Point(180, 172);
    dc.DrawEllipse(Brushes.White, pen, new Point(180, 46), 26, 26);
    dc.DrawLine(pen, new Point(180, 72), hip);
    dc.DrawLine(pen, hip, new Point(150, 246));
    dc.DrawLine(pen, hip, new Point(210, 246));

    // Back arm: carries Clawd and the laptop under the arm.
    var arm = new StreamGeometry();
    using (var ctx = arm.Open())
    {
        ctx.BeginFigure(shoulder, isFilled: false, isClosed: false);
        ctx.LineTo(new Point(156, 184), isStroked: true, isSmoothJoin: true);
        ctx.LineTo(new Point(26, 186), isStroked: true, isSmoothJoin: true);
    }

    dc.DrawGeometry(null, pen, arm);

    // Clawd: terracotta block with square eyes; its arms reach down to the keyboard.
    dc.DrawRectangle(clawd, null, new Rect(40, 98, 96, 56));
    dc.DrawRectangle(eyes, null, detailed ? new Rect(62, 110, 12, 16) : new Rect(58, 106, 18, 22));
    dc.DrawRectangle(eyes, null, detailed ? new Rect(102, 110, 12, 16) : new Rect(100, 106, 18, 22));
    if (detailed)
    {
        dc.DrawRectangle(clawd, null, new Rect(24, 142, 12, 32));
        dc.DrawRectangle(clawd, null, new Rect(142, 142, 12, 32));
    }

    // Laptop in front of Clawd, resting on the forearm.
    dc.DrawRoundedRectangle(Brush("#2E3440"), null, new Rect(34, 134, 110, 40), 4, 4);
    if (detailed)
    {
        dc.DrawRectangle(Brush("#A3BE8C"), null, new Rect(44, 142, 36, 5));
        dc.DrawRectangle(Brush("#D8DEE9"), null, new Rect(52, 151, 52, 5));
        dc.DrawRectangle(Brush("#D08770"), null, new Rect(44, 160, 28, 5));
    }

    dc.DrawRoundedRectangle(Brush("#9AA0A6"), null, new Rect(22, 172, 136, 9), 3, 3);

    // Pointing arm, drawn last so it stays in front.
    dc.DrawLine(pen, shoulder, new Point(246, 58));
}

static SolidColorBrush Brush(string color)
{
    var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
    brush.Freeze();
    return brush;
}

// ICO with 32-bit DIB frames below 256 px and a PNG frame for 256 px.
static void WriteIco(Stream stream, IReadOnlyList<(int Size, BitmapSource Bitmap)> frames)
{
    var images = frames.Select(f => f.Size >= 256 ? EncodePng(f.Bitmap) : EncodeDib(f.Bitmap)).ToList();
    using var writer = new BinaryWriter(stream);
    writer.Write((ushort)0);
    writer.Write((ushort)1);
    writer.Write((ushort)frames.Count);

    var offset = 6 + 16 * frames.Count;
    for (var i = 0; i < frames.Count; i++)
    {
        var size = frames[i].Size;
        writer.Write((byte)(size >= 256 ? 0 : size));
        writer.Write((byte)(size >= 256 ? 0 : size));
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write(images[i].Length);
        writer.Write(offset);
        offset += images[i].Length;
    }

    foreach (var image in images)
    {
        writer.Write(image);
    }
}

static byte[] EncodePng(BitmapSource bitmap)
{
    var encoder = new PngBitmapEncoder();
    encoder.Frames.Add(BitmapFrame.Create(bitmap));
    using var memory = new MemoryStream();
    encoder.Save(memory);
    return memory.ToArray();
}

static byte[] EncodeDib(BitmapSource bitmap)
{
    int width = bitmap.PixelWidth, height = bitmap.PixelHeight, stride = width * 4;
    var pixels = new byte[stride * height];
    bitmap.CopyPixels(pixels, stride, 0);
    var maskStride = (width + 31) / 32 * 4;

    using var memory = new MemoryStream();
    using var writer = new BinaryWriter(memory);
    writer.Write(40);
    writer.Write(width);
    writer.Write(height * 2);
    writer.Write((ushort)1);
    writer.Write((ushort)32);
    writer.Write(0);
    writer.Write(stride * height + maskStride * height);
    writer.Write(0);
    writer.Write(0);
    writer.Write(0);
    writer.Write(0);

    // Bottom-up rows; transparency comes from the alpha channel, so the AND mask stays empty.
    for (var y = height - 1; y >= 0; y--)
    {
        writer.Write(pixels, y * stride, stride);
    }

    writer.Write(new byte[maskStride * height]);
    return memory.ToArray();
}
