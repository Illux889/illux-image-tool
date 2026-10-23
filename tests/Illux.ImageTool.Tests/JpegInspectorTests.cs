using ImageMagick;
using Illux.ImageTool.Jpeg;

namespace Illux.ImageTool.Tests;

public sealed class JpegInspectorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "illux-jpeginspector-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public void Inspect_Baseline_Rgb()
    {
        var path = TestImages.Write(_dir, "a.jpg", 64, 48, ColorSpace.sRGB, 8, 72);
        var info = JpegInspector.Inspect(path);
        Assert.Equal(JpegCoding.Baseline, info.Coding);
        Assert.Equal(8, info.Precision);
        Assert.Equal(64, info.Width);
        Assert.Equal(48, info.Height);
        Assert.Equal(3, info.Components);
    }

    [Fact]
    public void Inspect_Progressive()
    {
        var path = TestImages.Write(_dir, "p.jpg", 64, 48, ColorSpace.sRGB, 8, 72, progressive: true);
        Assert.Equal(JpegCoding.Progressive, JpegInspector.Inspect(path).Coding);
    }

    [Fact]
    public void Inspect_Gray_HasOneComponent()
    {
        var path = TestImages.Write(_dir, "g.jpg", 32, 32, ColorSpace.Gray, 8, 72);
        Assert.Equal(1, JpegInspector.Inspect(path).Components);
    }

    [Fact]
    public void Inspect_CmykFromMagick_HasAdobeMarker()
    {
        // Verified by dumping the bytes: ImageMagick writes an Adobe APP14 marker ("Adobe", version 100,
        // flags 0/0) for CMYK JPEGs, with transform byte 2 (YCCK), not 0 (plain CMYK).
        var path = TestImages.Write(_dir, "c.jpg", 64, 48, ColorSpace.CMYK, 8, 72);
        var info = JpegInspector.Inspect(path);
        Assert.Equal(4, info.Components);
        Assert.True(info.HasAdobeMarker);
        Assert.Equal(2, info.AdobeTransform);
    }

    [Fact]
    public void Inspect_Rgb_HasNoAdobeMarker()
    {
        var path = TestImages.Write(_dir, "a.jpg", 32, 32, ColorSpace.sRGB, 8, 72);
        var info = JpegInspector.Inspect(path);
        Assert.False(info.HasAdobeMarker);
        Assert.Equal(-1, info.AdobeTransform);
    }

    [Fact]
    public void Inspect_Density()
    {
        var path = TestImages.Write(_dir, "d.jpg", 64, 48, ColorSpace.sRGB, 8, 150);
        var info = JpegInspector.Inspect(path);
        Assert.NotNull(info.JfifDensity);
        Assert.Equal(150, info.JfifDensity!.Value.X);
        Assert.Equal(150, info.JfifDensity.Value.Y);
        Assert.Equal(1, info.JfifDensity.Value.Units);
    }

    [Fact]
    public void Inspect_NoPpi_HasNoUsableDensity()
    {
        var path = TestImages.Write(_dir, "n.jpg", 64, 48, ColorSpace.sRGB, 8, null);
        // Verified by dumping the bytes: Density(0,0) makes ImageMagick write JFIF units 0 (aspect ratio only), 1:1.
        var density = JpegInspector.Inspect(path).JfifDensity;
        Assert.Equal((1d, 1d, 0), density);
    }

    [Fact]
    public void Inspect_IccRoundTrip()
    {
        var path = TestImages.Write(_dir, "i.jpg", 64, 48, ColorSpace.sRGB, 8, 72, icc: TestImages.SrgbIcc);
        Assert.Equal(TestImages.SrgbIcc, JpegInspector.Inspect(path).IccProfile);
    }

    [Fact]
    public void Inspect_LargeIccIsReassembledFromMultipleChunks()
    {
        // The CMYK profile is far larger than the 65519-byte limit of one APP2 chunk.
        Assert.True(TestImages.CmykIcc.Length > 65519);
        var path = TestImages.Write(_dir, "ci.jpg", 64, 48, ColorSpace.CMYK, 8, 72, icc: TestImages.CmykIcc);
        Assert.Equal(TestImages.CmykIcc, JpegInspector.Inspect(path).IccProfile);
    }

    [Fact]
    public void Inspect_NoIcc_IsNull()
    {
        var path = TestImages.Write(_dir, "x.jpg", 32, 32, ColorSpace.sRGB, 8, 72);
        Assert.Null(JpegInspector.Inspect(path).IccProfile);
    }

    [Fact]
    public void Inspect_NotJpeg_Throws()
    {
        var path = TestImages.Write(_dir, "a.png", 16, 16, ColorSpace.sRGB, 8, 72);
        Assert.Throws<InvalidDataException>(() => JpegInspector.Inspect(path));
    }

    // ---- hand-built streams ----

    private static readonly byte[] Soi = { 0xFF, 0xD8 };
    private static readonly byte[] Sos = { 0xFF, 0xDA };

    private static byte[] Segment(byte marker, params byte[] data) =>
        new byte[] { 0xFF, marker, (byte)((data.Length + 2) >> 8), (byte)((data.Length + 2) & 0xFF) }.Concat(data).ToArray();

    private static byte[] Sof(byte marker) =>
        Segment(marker, 8, 0, 4, 0, 8, 3, 1, 0x11, 0, 2, 0x11, 1, 3, 0x11, 1);

    private static byte[] IccChunk(int seq, int count, params byte[] payload) =>
        Segment(0xE2, System.Text.Encoding.ASCII.GetBytes("ICC_PROFILE\0").Concat(new[] { (byte)seq, (byte)count }).Concat(payload).ToArray());

    private static byte[] Build(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    private static JpegInfo InspectBytes(byte[] bytes) => JpegInspector.Inspect(new MemoryStream(bytes));

    [Fact]
    public void Inspect_HandBuiltBaseline_ReadsFrameHeader()
    {
        var info = InspectBytes(Build(Soi, Sof(0xC0), Sos));
        Assert.Equal(JpegCoding.Baseline, info.Coding);
        Assert.Equal((8, 8, 4, 3), (info.Precision, info.Width, info.Height, info.Components));
    }

    [Theory]
    [InlineData(new byte[] { 0xFF })] // too short for SOI
    [InlineData(new byte[] { 0xFF, 0xD8 })] // nothing after SOI
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF })] // marker prefix without code
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xC0, 0x00 })] // inside the length field
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xC0, 0x00, 0x11, 0x08, 0x00 })] // inside the segment body
    public void Inspect_Truncated_Throws(byte[] bytes)
    {
        Assert.Throws<InvalidDataException>(() => InspectBytes(bytes));
    }

    [Fact]
    public void Inspect_NoFrameHeaderBeforeSos_Throws()
    {
        Assert.Throws<InvalidDataException>(() => InspectBytes(Build(Soi, Sos)));
    }

    [Fact]
    public void Inspect_FillBytesBeforeMarker_Parse()
    {
        var bytes = Build(Soi, new byte[] { 0xFF, 0xFF, 0xFF }, Sof(0xC0).Skip(1).ToArray(), new byte[] { 0xFF, 0xFF }, Sos.Skip(1).ToArray());
        Assert.Equal(JpegCoding.Baseline, InspectBytes(bytes).Coding);
    }

    [Fact]
    public void Inspect_RstAndTemMarkersBeforeSos_AreSkipped()
    {
        var bytes = Build(Soi, new byte[] { 0xFF, 0x01 }, new byte[] { 0xFF, 0xD0 }, new byte[] { 0xFF, 0xD7 }, Sof(0xC2), Sos);
        Assert.Equal(JpegCoding.Progressive, InspectBytes(bytes).Coding);
    }

    [Theory]
    [InlineData(0xC1, JpegCoding.ExtendedSequential)]
    [InlineData(0xC3, JpegCoding.Lossless)]
    [InlineData(0xC7, JpegCoding.Lossless)]
    [InlineData(0xCB, JpegCoding.Lossless)]
    [InlineData(0xCF, JpegCoding.Lossless)]
    [InlineData(0xC9, JpegCoding.Arithmetic)]
    [InlineData(0xCA, JpegCoding.Arithmetic)]
    [InlineData(0xCD, JpegCoding.Arithmetic)]
    [InlineData(0xCE, JpegCoding.Arithmetic)]
    public void Inspect_SofVariants_MapToCoding(int marker, JpegCoding expected)
    {
        Assert.Equal(expected, InspectBytes(Build(Soi, Sof((byte)marker), Sos)).Coding);
    }

    [Theory]
    [InlineData(0xC5)]
    [InlineData(0xC6)]
    public void Inspect_HierarchicalSof_Throws(int marker)
    {
        Assert.Throws<InvalidDataException>(() => InspectBytes(Build(Soi, Sof((byte)marker), Sos)));
    }

    [Fact]
    public void Inspect_IccChunksOutOfOrder_AreReassembledInSequence()
    {
        var bytes = Build(Soi, IccChunk(3, 3, 7, 8), IccChunk(1, 3, 1, 2, 3), IccChunk(2, 3, 4, 5, 6), Sof(0xC0), Sos);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, InspectBytes(bytes).IccProfile);
    }

    public static TheoryData<string, byte[][]> BrokenIccChunks => new()
    {
        { "missing chunk 2", new[] { IccChunk(1, 3, 1), IccChunk(3, 3, 3) } },
        { "duplicate sequence", new[] { IccChunk(1, 2, 1), IccChunk(1, 2, 2) } },
        { "sequence 0", new[] { IccChunk(0, 1, 1) } },
        { "sequence above count", new[] { IccChunk(1, 1, 1), IccChunk(2, 1, 2) } },
        { "count mismatch between chunks", new[] { IccChunk(1, 2, 1), IccChunk(2, 3, 2) } },
        { "fewer chunks than count", new[] { IccChunk(1, 2, 1) } },
        { "count 0", new[] { IccChunk(1, 0, 1) } },
    };

    [Theory]
    [MemberData(nameof(BrokenIccChunks))]
    public void Inspect_BrokenIccChunks_YieldNullProfile(string description, byte[][] chunks)
    {
        var bytes = Build(Soi, Build(chunks), Sof(0xC0), Sos);
        Assert.Null(InspectBytes(bytes).IccProfile);
        Assert.NotEmpty(description);
    }

    [Fact]
    public void Write_UnsupportedColorSpace_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TestImages.Write(_dir, "x.jpg", 8, 8, ColorSpace.Lab, 8, 72));
    }
}
