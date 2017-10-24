using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using AcTools.DataFile;
using AcTools.Kn5File;
using AcTools.Render.Base.Utils;
using AcTools.Utils;
using AcTools.Utils.Helpers;
using CommandLine;
using CommandLine.Text;
using DdsCompress;
using JetBrains.Annotations;
using SlimDX.Direct2D;
using Bitmap = System.Drawing.Bitmap;
using PixelFormat = System.Drawing.Imaging.PixelFormat;

namespace AcTools.Extra.BannersTool {
    internal static class Program {
        private static bool _preserveGradients = true;
        private static double _gradientsThreshold = 0.4;
        private static bool _dxt1Mode = true;
        private static bool _mipMaps = true;
        private static bool _productionQuality;

        private static byte[] CopySafe(Bitmap bitmap) {
            int w = bitmap.Width, h = bitmap.Height;
            var r = new byte[w * h * 4];
            for (int i = 0, y = 0; y < h; y++) {
                for (var x = 0; x < w; x++) {
                    var c = bitmap.GetPixel(x, y);
                    r[i++] = c.B;
                    r[i++] = c.G;
                    r[i++] = c.R;
                    r[i++] = c.A;
                }
            }
            return r;
        }

        private static TextureReader _textureReader;

        [CanBeNull]
        private static byte[] RemoveTransparencyDds(byte[] ddsImageBytes, CompressFormat? namePreferredFormat) {
            var format = namePreferredFormat ?? (_dxt1Mode ? CompressFormat.DXT1 : CompressFormat.RGB);
            var dxtMode = format == CompressFormat.DXT1 || format == CompressFormat.DXT5;

            if (_textureReader == null) {
                _textureReader = new TextureReader();
            }

            byte[] pngImageBytes;
            if (_preserveGradients) {
                pngImageBytes = RemoveTransparencyBitmap(_textureReader.ToPng(ddsImageBytes), out var containsGradients);
                if (containsGradients) {
                    switch (format) {
                        case CompressFormat.RGB:
                            format = CompressFormat.RGBA;
                            break;
                        case CompressFormat.Luminance:
                            format = CompressFormat.LuminanceAlpha;
                            break;
                        case CompressFormat.RGB565:
                            format = CompressFormat.A4R4G4B4;
                            break;
                        case CompressFormat.DXT1:
                            format = CompressFormat.DXT5;
                            break;
                        default:
                            throw new ArgumentOutOfRangeException();
                    }
                }
            } else {
                pngImageBytes = _textureReader.ToPng(ddsImageBytes, true);
            }

            if (pngImageBytes == null) {
                return null;
            }

            using (var pngImageStream = new MemoryStream(pngImageBytes))
            using (var pngImage = (Bitmap)Image.FromStream(pngImageStream))
            using (var output = new MemoryStream()) {
                new Compressor {
                    MipmapMaxLevel = _mipMaps ? -1 : 0,
                    RoundMode = dxtMode ? CompressRoundMode.ToNearestMultipleOfFour : CompressRoundMode.None,
                    Quality = _productionQuality ? CompressQuality.Production : CompressQuality.Normal,
                    ResizeFilter = CompressResizeFilter.Mitchell,
                    Format = format
                }.Process(pngImage.Width, pngImage.Height, CopySafe(pngImage), output, null, null);
                return output.ToArray();
            }
        }

        [CanBeNull]
        private static byte[] RemoveTransparencyBitmap(byte[] image, out bool containsGradients) {
            var fixThreshold = _preserveGradients ? _gradientsThreshold * 255 : 0;
            var anythingChanged = false;

            using (var stream = new MemoryStream(image))
            using (var bitmap = (Bitmap)Image.FromStream(stream)) {
                int w = bitmap.Width, h = bitmap.Height;
                using (var modifiedCopy = new Bitmap(w, h, PixelFormat.Format24bppRgb)) {
                    for (var y = 0; y < h; y++) {
                        for (var x = 0; x < w; x++) {
                            var c = bitmap.GetPixel(x, y);
                            modifiedCopy.SetPixel(x, y, Color.FromArgb(255, c.R, c.G, c.B));

                            if (c.A < fixThreshold) {
                                goto ConsiderGradients;
                            }

                            if (c.A != 255) {
                                anythingChanged = true;
                            }
                        }
                    }

                    if (!anythingChanged) {
                        containsGradients = false;
                        return null;
                    }

                    using (var output = new MemoryStream()) {
                        modifiedCopy.Save(output, ImageFormat.Png);
                        containsGradients = false;
                        return output.ToArray();
                    }
                }

                ConsiderGradients:
                using (var modifiedCopy = new Bitmap(w, h, PixelFormat.Format32bppArgb)) {
                    for (var y = 0; y < h; y++) {
                        for (var x = 0; x < w; x++) {
                            var c = bitmap.GetPixel(x, y);
                            modifiedCopy.SetPixel(x, y, Color.FromArgb((c.A / _gradientsThreshold).ClampToByte(), c.R, c.G, c.B));
                            if (c.A != 255 && c.A > fixThreshold) {
                                anythingChanged = true;
                            }
                        }
                    }

                    if (!anythingChanged) {
                        containsGradients = false;
                        return null;
                    }

                    using (var output = new MemoryStream()) {
                        modifiedCopy.Save(output, ImageFormat.Png);
                        containsGradients = true;
                        return output.ToArray();
                    }
                }
            }
        }

        [CanBeNull]
        private static byte[] RemoveTransparency(byte[] image, CompressFormat? namePreferredFormat) {
            return image.Length > 3 && image[0] == 'D' && image[1] == 'D' && image[2] == 'S' ?
                    RemoveTransparencyDds(image, namePreferredFormat) :
                    RemoveTransparencyBitmap(image, out _);
        }

        private static void ProcessKn5(string carId, string carsDirectory, List<TextureRef> rules, ZipArchive output, string pathPrefix) {
            var carDirectory = Path.Combine(carsDirectory, carId);
            var kn5Filename = FileUtils.GetMainCarFilename(carDirectory);
            if (kn5Filename == null || !File.Exists(kn5Filename)) {
                Console.Error.WriteLine($"    {carId}: main KN5-file not found");
                kn5Filename = null;
            }

            var skins = Path.Combine(carDirectory, "skins");
            if (!Directory.Exists(skins)) {
                Console.Error.WriteLine($"    {carId}: skins directory not found");
                return;
            }

            Kn5 kn5 = null;
            var cache = new Dictionary<string, byte[]>();

            byte[] GetTexture(TextureRef name) {
                if (cache.TryGetValue(name.TextureName, out var result) || kn5Filename == null) return result;
                if (kn5 == null) kn5 = Kn5.FromFile(kn5Filename);
                return cache[name.TextureName] = RemoveTransparency(kn5.TexturesData[name.TextureName], name.PreferredFormat);
            }

            foreach (var skinDirectory in Directory.GetDirectories(skins)) {
                Console.WriteLine($"Skin: {Path.GetFileName(skinDirectory)}");

                foreach (var texture in rules) {
                    Console.WriteLine($"  Texture: {texture.TextureName}");

                    var overridden = Path.Combine(skinDirectory, texture.TextureName);
                    var bytes = File.Exists(overridden) ? RemoveTransparency(File.ReadAllBytes(overridden), texture.PreferredFormat) : GetTexture(texture);
                    if (bytes != null) {
                        using (var write = output.CreateEntry(pathPrefix + Path.Combine(carId, "skins", Path.GetFileName(skinDirectory), texture.TextureName),
                                CompressionLevel.Optimal).Open()) {
                            write.Write(bytes, 0, bytes.Length);
                        }

                        Console.WriteLine("    Fixed and saved within patch file");
                    } else {
                        Console.WriteLine("    Either not a semi-transparent or fully transparent texture, skip");
                    }
                }
            }
        }

        private static string GetCarName(string carId, string carsDirectory) {
            return DataWrapper.FromCarDirectory(Path.Combine(carsDirectory, carId))
                              .GetIniFile("car.ini")["INFO"].GetNonEmpty("SCREEN_NAME") ?? carId;
        }

        private static string JoinToReadableString(IEnumerable<string> items) {
            if (items == null) return null;
            var list = items as IReadOnlyList<string> ?? items.ToList();
            switch (list.Count) {
                case 0:
                    return string.Empty;
                case 1:
                    return list[0];
                default:
                    return $@"{string.Join(@", ", list.Take(list.Count - 1))} and {list.Last()}";
            }
        }

        private class Options {
            [Value(0, HelpText = "List of car IDs to process; if omitted, cars from rules will be searched for and used.", MetaName = "car IDs")]
            public IEnumerable<string> Cars { get; set; }

            [CanBeNull, Option('d', "directory", Required = false,
                    HelpText = "Path to content/cars directory; if omitted, app will try to find it from Steam.")]
            public string Directory { get; set; }

            [Option('r', "rules", Required = false, Default = new []{ "Rules.txt" }, HelpText = "Rules files.")]
            public IEnumerable<string> RulesFiles { get; set; }

            [CanBeNull, Option('o', "output", Required = false, HelpText = "Output file; if omitted, will be put next to executable.")]
            public string OutputFile { get; set; }

            [Option('m', "mod", Required = false, Default = true, HelpText = "Pack as JSGME modification.")]
            public bool PackAsMod { get; set; }

            [Option("gradients", Required = false, Default = true, HelpText = "Keep gradients by stretching alpha.")]
            public bool PreserveGradients { get; set; }

            [Option("gradient-threshold", Required = false, Default = 0.4, HelpText = "Threshold for gradients.")]
            public double GradientsThreshold { get; set; }

            [Option("dxt1", Required = false, HelpText = "Use DXT1 compression for DDS textures by default.")]
            public bool Dxt1Mode { get; set; } = false;

            [Option("dxt1-production-quality", Required = false, Default = false, HelpText = "Use production-quality DXT1 compression; might be worse.")]
            public bool ProductionQuality { get; set; }

            [Option("mipmaps", Required = false, Default = true, HelpText = "Generate mipmaps DDS compression.")]
            public bool MipMaps { get; set; }
        }

        private class TextureRef {
            public string TextureName;
            public CompressFormat? PreferredFormat;
        }

        private static CompressFormat? ParseFormat(string format) {
            switch (format?.Trim().ToLowerInvariant()) {
                case "dxt1":
                    return CompressFormat.DXT1;

                case "dxt":
                case "dxt5":
                    return CompressFormat.DXT5;

                case "l":
                case "lum":
                case "luminance":
                    return CompressFormat.Luminance;

                case "la":
                case "lumalpha":
                case "luminancealpha":
                    return CompressFormat.LuminanceAlpha;

                case "rgb565":
                case "rgb5650":
                case "565":
                case "5650":
                    return CompressFormat.RGB565;

                case "rgba4444":
                case "4444":
                    return CompressFormat.A4R4G4B4;

                case "rgba":
                case "rgba8888":
                case "8888":
                    return CompressFormat.RGBA;

                case "rgb":
                case "rgb888":
                case "rgba8880":
                case "888":
                case "8880":
                    return CompressFormat.RGB;

                default:
                    return null;
            }
        }

        private static void Run(Options options) {
            var rulesFiles = options.RulesFiles.ToList();
            if (rulesFiles.Count == 0) {
                rulesFiles.Add("Rules.txt");
            }

            Console.WriteLine($"Rules:\n  {string.Join("\n  ", rulesFiles)}");
            var rules = rulesFiles.SelectMany(File.ReadAllLines).Select(x => x.Split('#')[0].Split(':'))
                                  .Where(x => x.Length == 2 || x.Length == 3).GroupBy(x => x[0].Trim()).ToDictionary(
                                          x => x.Key,
                                          x => x.Select(y => new TextureRef {
                                              TextureName = y[1].Trim(),
                                              PreferredFormat = ParseFormat(y.ElementAtOrDefault(2))
                                          }).ToList());

            // AC root
            var carsDirectory = options.Directory;
            if (carsDirectory == null) {
                var acRoot = AcRootFinder.TryToFind();
                if (acRoot == null) {
                    throw new Exception("Fail to find AC root directory");
                }

                carsDirectory = FileUtils.GetCarsDirectory(acRoot);
            }

            // List of cars to process
            var carIds = options.Cars.ToList();
            if (carIds.Count == 0) {
                carIds = rules.Keys.ToList();
            }

            carIds = carIds.Where(x => Directory.Exists(Path.Combine(carsDirectory, x))).ToList();

            // Car names for description
            var carNames = carIds.Select(x => GetCarName(x, carsDirectory)).ToList();
            var description =
                    $"Replaces skin textures to make windscreen banners non-transparent based on set of rules. Affects: {JoinToReadableString(carNames)}.";

            // How to pack
            string modPrefix, pathPrefix;
            if (options.PackAsMod) {
                modPrefix = Path.Combine("MODS", carIds.Count == 1 ? $"Transparency Banner Patch For {FileUtils.EnsureFileNameIsValid(carNames[0])}" :
                        "Transparency Banner Patch") + Path.DirectorySeparatorChar;
                pathPrefix = Path.Combine(modPrefix, "content", "cars") + Path.DirectorySeparatorChar;
            } else {
                modPrefix = "";
                pathPrefix = "";
            }

            // Copy global options to static variables
            _preserveGradients = options.PreserveGradients;
            _gradientsThreshold = options.GradientsThreshold;
            _dxt1Mode = options.Dxt1Mode;
            _mipMaps = options.MipMaps;
            _productionQuality = options.ProductionQuality;

            // Result filename
            var outputFilename = FileUtils.EnsureUnique(options.OutputFile ?? Path.Combine(Environment.CurrentDirectory,
                    carIds.Count == 1 ? $"transparencyPatch_{carIds[0]}.zip" : $"transparencyPatch_{carIds.Count}.zip"));

            // Packing
            using (var zip = ZipFile.Open(outputFilename, ZipArchiveMode.Create)) {
                if (options.PackAsMod) {
                    using (var writer = new StreamWriter(zip.CreateEntry(modPrefix + "Description.jsgme", CompressionLevel.Optimal).Open(),
                            Encoding.UTF8, 2048, false)) {
                        writer.Write(description.WordWrap(120));
                    }
                }

                foreach (var carId in carIds) {
                    if (rules.TryGetValue(carId, out var carRules)) {
                        ProcessKn5(carId, carsDirectory, carRules, zip, pathPrefix);
                    }
                }
            }

            // Adding archive description
            using (var stream = new FileStream(outputFilename, FileMode.Open, FileAccess.ReadWrite)) {
                stream.Seek(-2, SeekOrigin.End);
                var bytes = Encoding.GetEncoding(1252).GetBytes(description.WordWrap(80));
                var length = Math.Min((ushort)bytes.Length, ushort.MaxValue);
                stream.Write(BitConverter.GetBytes(length), 0, 2);
                stream.Write(bytes, 0, length);
            }

            // Debug
#if DEBUG
            Process.Start(outputFilename);
#endif
        }

        private class FixedWriter : TextWriter {
            public override Encoding Encoding => Encoding.UTF8;

            private char _previous;

            public override void Write(char value) {
                if (value == '\r') return;
                if (value != '\n' || _previous != '\n') {
                    Console.Out.Write(value);
                    _previous = value;
                }
            }
        }

        private static int Main(string[] args) {
            Console.OutputEncoding = Encoding.UTF8;

            try {
                var parsed = new Parser(with => {
                    with.EnableDashDash = true;
                    with.HelpWriter = new FixedWriter();
                }).ParseArguments<Options>(args);
                return parsed.MapResult(o => {
                    Run(o);
                    return 0;
                }, _ => 1);
            } catch (IOException e) {
                Console.Error.WriteLine(e.Message);
                return 2;
            } catch (Exception e) {
                Console.Error.WriteLine(e.ToString());
                return 2;
            } finally {
                DisposeHelper.Dispose(ref _textureReader);
                /*Console.WriteLine("<Press any key>");
                Console.ReadKey();*/
            }
        }
    }
}