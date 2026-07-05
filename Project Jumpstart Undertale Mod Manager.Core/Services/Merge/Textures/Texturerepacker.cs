using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ImageMagick;
using UndertaleModLib;
using UndertaleModLib.Models;
using UndertaleModLib.Util;

namespace Project_Jumpstart_Undertale_Mod_Manager.Services.Merge.Textures;

// ---------------------------------------------------------------------------
// TIER 3: texture repacker.
//
// This is a faithful port of UndertaleModTool's "ImportGraphics (Full Repack)"
// script (Texture packer by Samuel Roy), with the script harness removed
// (no ScriptMessage / SetProgressBar / EnsureDataLoaded) so it can run inside
// the Core merge pipeline. The packer classes below are copied almost verbatim
// from that script.
//
// STRATEGY: always full repack.
//   1. Export every existing sprite frame / background / font PNG to a temp dir,
//      recording each one's target/bounding coords so they survive the repack.
//   2. Overlay the mod's override PNGs on top (same filename => mod.win).
//   3. Clear ALL texture page items + embedded textures.
//   4. Pack the whole pile into fresh atlases.
//   5. Rebuild every texture page item and re-wire sprites/backgrounds/fonts.
//
// The same-size fast-path (item.ReplaceTexture for identical dimensions,
// skipping the repack) is intentionally NOT here yet — deferred until this
// always-repack path is proven correct on a real Undertale + Deltarune merge.
//
// NAMING CONVENTION the packer consumes (flat, from the original script):
//   sprite frame : <spriteName>_<frameIndex>.png   e.g. spr_susie_0.png
//   background   : <backgroundName>.png             e.g. bg_ruinsbg.png
//   font         : <fontName>.png                   e.g. fnt_main.png
// The merge pipeline is responsible for translating your folder-based mod
// layout (sprites/spr_susie/0.png) into this flat naming before calling Repack.
//
// VERIFY-AGAINST-SOURCE NOTES (things most likely to need a nudge in Rider):
//   - TextureWorker.ExportAsPNG / ReadBGRAImageFromFile / GetImageSizeFromFile /
//     ResizeImage / SaveImageToFile  — confirmed from SpriteEditor + the script.
//   - GMImage.FromMagickImage(...).ConvertToPng()  — confirmed from the script.
//   - UndertaleTexturePageItem source/target/bounding fields  — confirmed.
//   - newSprite.NewMaskEntry() has a FIXME in the source for 2024.6+ collision
//     masks. Undertale (GMS1) and current Deltarune are fine; if you ever load a
//     2024.6+ target, revisit the mask block (see comment below).
// ---------------------------------------------------------------------------

/// <summary>
/// Result of a repack pass. Non-fatal per-asset problems (bad names, oversized
/// images) are collected here instead of thrown, so one bad mod file doesn't
/// abort the whole merge.
/// </summary>
public sealed record RepackResult(int AtlasCount, int PageItemCount, IReadOnlyList<string> Warnings);

/// <summary>
/// Rebuilds a data file's texture pages from scratch, folding in a directory of
/// override PNGs. Plain helper class (not a *Service), so it sits outside the
/// interface/DI arch rules; the merge service news it up or you can wrap it in
/// an ITextureRepacker later if you want it injected.
/// </summary>
public sealed class TextureRepacker
{
    private readonly int _atlasSize;
    private readonly int _padding;

    /// <param name="atlasSize">
    /// Max atlas dimension. 2048 matches the script default and is safe for
    /// GMS1/GMS2 targets. Larger = fewer pages but more VRAM per page.
    /// </param>
    /// <param name="padding">Pixel gap between packed items. 2 is the script default.</param>
    public TextureRepacker(int atlasSize = 2048, int padding = 2)
    {
        _atlasSize = atlasSize;
        _padding = padding;
    }

    /// <summary>
    /// Full repack. Mutates <paramref name="data"/> in place. PNGs in
    /// <paramref name="overridesDir"/> (flat naming, see file header) replace
    /// any base asset with the same name and add brand-new sprites.
    /// </summary>
    public async Task<RepackResult> RepackAsync(UndertaleData data, string overridesDir)
    {
        if (data is null) throw new ArgumentNullException(nameof(data));
        if (!Directory.Exists(overridesDir))
            throw new DirectoryNotFoundException($"Overrides directory not found: {overridesDir}");

        var warnings = new List<string>();

        // Temp working area: <temp>/pjumpstart_repack_<guid>/Textures
        string workRoot = Path.Combine(Path.GetTempPath(), "pjumpstart_repack_" + Guid.NewGuid().ToString("N"));
        string texturesDir = Path.Combine(workRoot, "Textures");
        Directory.CreateDirectory(texturesDir);

        try
        {
            // 1. Export every existing texture-backed asset, recording coords.
            var assetCoordinateDict = new Dictionary<string, int[]>();
            var assetTypeDict = new Dictionary<string, string>();
            await ExportExistingAsync(data, texturesDir, assetCoordinateDict, assetTypeDict);

            // 2. Overlay the mod's overrides (same filename => overwrite => mod wins).
            foreach (string src in Directory.GetFiles(overridesDir, "*.png", SearchOption.AllDirectories))
            {
                string dest = Path.Combine(texturesDir, Path.GetFileName(src));
                File.Copy(src, dest, overwrite: true);
            }

            // 3. Wipe existing pages/items — we rebuild all of them.
            data.TexturePageItems.Clear();
            data.EmbeddedTextures.Clear();

            // 4. Pack the whole pile.
            string atlasDescPath = Path.Combine(workRoot, "atlas.txt");
            var packer = new Packer { FitHeuristic = BestFitHeuristic.Area };
            await Task.Run(() =>
            {
                packer.Process(texturesDir, "*.png", _atlasSize, _padding, debugMode: false);
                packer.SaveAtlasses(atlasDescPath);
            });

            // 5. Rebuild the graph from the packed atlases.
            int pageItems = RebuildGraph(data, packer, atlasDescPath, assetCoordinateDict, assetTypeDict, warnings);

            return new RepackResult(packer.Atlasses.Count, pageItems, warnings);
        }
        finally
        {
            TryDeleteDirectory(workRoot);
        }
    }

    // -- step 1: export existing --------------------------------------------

    private static async Task ExportExistingAsync(
        UndertaleData data,
        string texturesDir,
        Dictionary<string, int[]> coords,
        Dictionary<string, string> types)
    {
        // Just in case I want to make it parallel, but a race condition appeared, so I left it serialized for now.
        var gate = new object();

        using var worker = new TextureWorker();

        void RecordSpriteFrame(UndertaleSprite sprite)
        {
            if (sprite is null) return;
            for (int i = 0; i < sprite.Textures.Count; i++)
            {
                UndertaleTexturePageItem tex = sprite.Textures[i]?.Texture;
                if (tex is null) continue;

                string key = $"{sprite.Name.Content}_{i}";
                worker.ExportAsPNG(tex, Path.Combine(texturesDir, key + ".png"));
                lock (gate)
                {
                    coords[key] = [tex.TargetX, tex.TargetY, tex.TargetWidth, tex.TargetHeight, tex.BoundingWidth, tex.BoundingHeight];
                    types[key] = "spr";
                }
            }
        }

        void RecordBackground(UndertaleBackground bg)
        {
            if (bg?.Texture is null) return;
            UndertaleTexturePageItem tex = bg.Texture;
            string key = bg.Name.Content;
            worker.ExportAsPNG(tex, Path.Combine(texturesDir, key + ".png"));
            lock (gate)
            {
                coords[key] = [tex.TargetX, tex.TargetY, tex.TargetWidth, tex.TargetHeight, tex.BoundingWidth, tex.BoundingHeight];
                types[key] = "bg";
            }
        }

        void RecordFont(UndertaleFont font)
        {
            if (font?.Texture is null) return;
            UndertaleTexturePageItem tex = font.Texture;
            string key = font.Name.Content;
            worker.ExportAsPNG(tex, Path.Combine(texturesDir, key + ".png"));
            lock (gate)
            {
                coords[key] = [tex.TargetX, tex.TargetY, tex.TargetWidth, tex.TargetHeight, tex.BoundingWidth, tex.BoundingHeight];
                types[key] = "fnt";
            }
        }

        await Task.Run(() =>
        {
            foreach (UndertaleSprite s in data.Sprites) RecordSpriteFrame(s);
            foreach (UndertaleBackground b in data.Backgrounds) RecordBackground(b);
            foreach (UndertaleFont f in data.Fonts) RecordFont(f);
        });
    }

    // -- step 5: rebuild graph ----------------------------------------------

    private static int RebuildGraph(
        UndertaleData data,
        Packer packer,
        string atlasDescPath,
        Dictionary<string, int[]> coords,
        Dictionary<string, string> types,
        List<string> warnings)
    {
        int lastTextPage = data.EmbeddedTextures.Count - 1;
        int lastTextPageItem = data.TexturePageItems.Count - 1;

        string prefix = Path.Combine(
            Path.GetDirectoryName(atlasDescPath)!,
            Path.GetFileNameWithoutExtension(atlasDescPath));

        int atlasCount = 0;
        foreach (Atlas atlas in packer.Atlasses)
        {
            string atlasName = $"{prefix}{atlasCount:000}.png";
            using MagickImage atlasImage = TextureWorker.ReadBGRAImageFromFile(atlasName);
            IPixelCollection<byte> atlasPixels = atlasImage.GetPixels();

            var texture = new UndertaleEmbeddedTexture
            {
                Name = new UndertaleString($"Texture {++lastTextPage}")
            };
            texture.TextureData.Image = GMImage.FromMagickImage(atlasImage).ConvertToPng();
            data.EmbeddedTextures.Add(texture);

            foreach (Node n in atlas.Nodes)
            {
                if (n.Texture is null) continue;

                var pageItem = new UndertaleTexturePageItem
                {
                    Name = new UndertaleString($"PageItem {++lastTextPageItem}"),
                    SourceX = (ushort)n.Bounds.X,
                    SourceY = (ushort)n.Bounds.Y,
                    SourceWidth = (ushort)n.Bounds.Width,
                    SourceHeight = (ushort)n.Bounds.Height,
                    BoundingWidth = (ushort)n.Bounds.Width,
                    BoundingHeight = (ushort)n.Bounds.Height,
                    TexturePage = texture
                };
                data.TexturePageItems.Add(pageItem);

                string stripped = Path.GetFileNameWithoutExtension(n.Texture.Source);
                string assetType = ResolveAssetType(stripped, types, warnings);
                if (assetType is null)
                    continue; // unknown name — warning already recorded

                RestoreTargetBounds(pageItem, stripped, n, coords);

                switch (assetType)
                {
                    case "bg":
                        WireBackground(data, stripped, pageItem, warnings);
                        break;
                    case "fnt":
                        WireFont(data, stripped, pageItem, warnings);
                        break;
                    default: // "spr"
                        WireSprite(data, stripped, pageItem, n, atlasPixels, warnings);
                        break;
                }
            }

            atlasCount++;
        }

        return lastTextPageItem + 1;
    }

    private static string ResolveAssetType(string stripped, Dictionary<string, string> types, List<string> warnings)
    {
        if (types.TryGetValue(stripped, out string known))
            return known;

        // Brand-new mod asset (not in the base export). Fall back to prefix.
        // Backgrounds/fonts must be prefixed bg_/fnt_; everything else is a sprite.
        int firstUnderscore = stripped.IndexOf('_');
        if (firstUnderscore <= 0)
        {
            // No prefix and not a known asset: only the special Undertale
            // "background0"/"background1" names are allowed to be bare.
            if (stripped is "background0" or "background1")
                return "bg";
            warnings.Add($"Image '{stripped}' has no type prefix and isn't a known asset. Skipped.");
            return null;
        }

        string prefix = stripped[..firstUnderscore];
        return prefix switch
        {
            "bg" => "bg",
            "fnt" => "fnt",
            _ => "spr"
        };
    }

    private static void RestoreTargetBounds(
        UndertaleTexturePageItem tex, string name, Node n, Dictionary<string, int[]> coords)
    {
        if (coords.TryGetValue(name, out int[] c))
        {
            tex.TargetX = (ushort)c[0];
            tex.TargetY = (ushort)c[1];
            tex.TargetWidth = (ushort)c[2];
            tex.TargetHeight = (ushort)c[3];
            tex.BoundingWidth = (ushort)c[4];
            tex.BoundingHeight = (ushort)c[5];
        }
        else
        {
            // New asset: no saved trim, so target == full source bounds.
            tex.TargetX = 0;
            tex.TargetY = 0;
            tex.TargetWidth = (ushort)n.Bounds.Width;
            tex.TargetHeight = (ushort)n.Bounds.Height;
        }
    }

    private static void WireBackground(UndertaleData data, string name, UndertaleTexturePageItem pageItem, List<string> warnings)
    {
        UndertaleBackground bg = data.Backgrounds.ByName(name);
        if (bg is null)
        {
            warnings.Add($"Background '{name}' not found in data; skipped. (Creating new backgrounds isn't supported yet.)");
            return;
        }
        bg.Texture = pageItem;
    }

    private static void WireFont(UndertaleData data, string name, UndertaleTexturePageItem pageItem, List<string> warnings)
    {
        UndertaleFont font = data.Fonts.ByName(name);
        if (font is null)
        {
            warnings.Add($"Font '{name}' not found in data; skipped. (Creating new fonts isn't supported yet.)");
            return;
        }
        font.Texture = pageItem;
    }

    private static void WireSprite(
        UndertaleData data, string stripped, UndertaleTexturePageItem pageItem,
        Node n, IPixelCollection<byte> atlasPixels, List<string> warnings)
    {
        int lastUnderscore = stripped.LastIndexOf('_');
        string spriteName;
        int frame;
        try
        {
            spriteName = stripped[..lastUnderscore];
            frame = int.Parse(stripped[(lastUnderscore + 1)..]);
        }
        catch
        {
            warnings.Add($"Sprite image '{stripped}' has an invalid name (expected <name>_<frame>). Skipped.");
            return;
        }

        var texEntry = new UndertaleSprite.TextureEntry { Texture = pageItem };
        UndertaleSprite sprite = data.Sprites.ByName(spriteName);

        if (sprite is null)
        {
            CreateNewSprite(data, spriteName, frame, n, atlasPixels, texEntry);
            return;
        }

        // Existing sprite: place this frame, growing the list if needed.
        if (frame > sprite.Textures.Count - 1)
        {
            while (frame > sprite.Textures.Count - 1)
                sprite.Textures.Add(texEntry);
            return;
        }
        sprite.Textures[frame] = texEntry;
    }

    private static void CreateNewSprite(
        UndertaleData data, string spriteName, int frame, Node n,
        IPixelCollection<byte> atlasPixels, UndertaleSprite.TextureEntry texEntry)
    {
        var newSprite = new UndertaleSprite
        {
            Name = data.Strings.MakeString(spriteName),
            Width = (uint)n.Bounds.Width,
            Height = (uint)n.Bounds.Height,
            MarginLeft = 0,
            MarginRight = n.Bounds.Width - 1,
            MarginTop = 0,
            MarginBottom = n.Bounds.Height - 1,
            OriginX = 0,
            OriginY = 0
        };

        // Pad out earlier frames if this file jumped straight to frame N.
        for (int i = 0; i < frame; i++)
            newSprite.Textures.Add(null);

        // Collision mask from alpha. NOTE (from the source script's FIXME):
        // 2024.6+ collision masks use bounding-box dimensions and would need
        // CalculateMaskDimensions(data) + NewMaskEntry(data). GMS1 Undertale and
        // current Deltarune use the per-pixel path below.
        newSprite.CollisionMasks.Add(newSprite.NewMaskEntry());

        int width = ((n.Bounds.Width + 7) / 8) * 8;
        var maskBits = new BitArray(width * n.Bounds.Height);
        for (int y = 0; y < n.Bounds.Height; y++)
        {
            for (int x = 0; x < n.Bounds.Width; x++)
            {
                IMagickColor<byte> px = atlasPixels.GetPixel(x + n.Bounds.X, y + n.Bounds.Y).ToColor();
                maskBits[y * width + x] = px is not null && px.A > 0;
            }
        }

        // Reverse bit order within each byte (GM mask bit packing).
        var packed = new BitArray(width * n.Bounds.Height);
        for (int i = 0; i < maskBits.Length; i += 8)
            for (int j = 0; j < 8; j++)
                packed[j + i] = maskBits[-(j - 7) + i];

        int numBytes = maskBits.Length / 8;
        var bytes = new byte[numBytes];
        packed.CopyTo(bytes, 0);
        for (int i = 0; i < bytes.Length; i++)
            newSprite.CollisionMasks[0].Data[i] = bytes[i];

        newSprite.Textures.Add(texEntry);
        data.Sprites.Add(newSprite);
    }

    private static void TryDeleteDirectory(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // Temp cleanup is best-effort; leaving a temp folder is not fatal.
        }
    }

    // =======================================================================
    // Packer classes — ported verbatim from "Texture packer by Samuel Roy"
    // (ImportGraphics Full Repack). Kept internal to this file.
    // =======================================================================

    private sealed class TextureInfo
    {
        public string Source;
        public int Width;
        public int Height;
    }

    private enum SplitType { Horizontal, Vertical }

    private enum BestFitHeuristic { Area, MaxOneAxis }

    private struct Rect
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
    }

    private sealed class Node
    {
        public Rect Bounds;
        public TextureInfo Texture;
        public SplitType SplitType;
    }

    private sealed class Atlas
    {
        public int Width;
        public int Height;
        public List<Node> Nodes;
    }

    private sealed class Packer
    {
        public List<TextureInfo> SourceTextures;
        public StringWriter Log;
        public StringWriter Error;
        public int Padding;
        public int AtlasSize;
        public bool DebugMode;
        public BestFitHeuristic FitHeuristic;
        public List<Atlas> Atlasses;

        public Packer()
        {
            SourceTextures = new List<TextureInfo>();
            Log = new StringWriter();
            Error = new StringWriter();
        }

        public void Process(string sourceDir, string pattern, int atlasSize, int padding, bool debugMode)
        {
            Padding = padding;
            AtlasSize = atlasSize;
            DebugMode = debugMode;

            ScanForTextures(sourceDir, pattern);
            List<TextureInfo> textures = SourceTextures.ToList();

            Atlasses = new List<Atlas>();
            while (textures.Count > 0)
            {
                var atlas = new Atlas { Width = atlasSize, Height = atlasSize };
                List<TextureInfo> leftovers = LayoutAtlas(textures, atlas);

                if (leftovers.Count == 0)
                {
                    // Shrink until it stops fitting, then step back up once.
                    while (leftovers.Count == 0)
                    {
                        atlas.Width /= 2;
                        atlas.Height /= 2;
                        leftovers = LayoutAtlas(textures, atlas);
                    }
                    atlas.Width *= 2;
                    atlas.Height *= 2;
                    leftovers = LayoutAtlas(textures, atlas);
                }

                Atlasses.Add(atlas);
                textures = leftovers;
            }
        }

        public void SaveAtlasses(string destination)
        {
            int atlasCount = 0;
            string prefix = Path.Combine(
                Path.GetDirectoryName(destination)!,
                Path.GetFileNameWithoutExtension(destination));

            using var tw = new StreamWriter(destination);
            tw.WriteLine("source_tex, atlas_tex, x, y, width, height");
            foreach (Atlas atlas in Atlasses)
            {
                string atlasName = $"{prefix}{atlasCount:000}.png";

                using (MagickImage img = CreateAtlasImage(atlas))
                    TextureWorker.SaveImageToFile(img, atlasName);

                foreach (Node n in atlas.Nodes)
                {
                    if (n.Texture is null) continue;
                    tw.Write(n.Texture.Source + ", ");
                    tw.Write(atlasName + ", ");
                    tw.Write(n.Bounds.X + ", ");
                    tw.Write(n.Bounds.Y + ", ");
                    tw.Write(n.Bounds.Width + ", ");
                    tw.WriteLine(n.Bounds.Height.ToString());
                }
                atlasCount++;
            }
        }

        private void ScanForTextures(string path, string wildcard)
        {
            var di = new DirectoryInfo(path);
            foreach (FileInfo fi in di.GetFiles(wildcard, SearchOption.AllDirectories))
            {
                (int width, int height) = TextureWorker.GetImageSizeFromFile(fi.FullName);
                if (width == -1 || height == -1)
                    continue;

                if (width <= AtlasSize && height <= AtlasSize)
                {
                    SourceTextures.Add(new TextureInfo { Source = fi.FullName, Width = width, Height = height });
                    Log.WriteLine($"Added {fi.FullName}");
                }
                else
                {
                    Error.WriteLine($"{fi.FullName} is too large to fit in the atlas. Skipping!");
                }
            }
        }

        private void HorizontalSplit(Node toSplit, int width, int height, List<Node> list)
        {
            var n1 = new Node();
            n1.Bounds.X = toSplit.Bounds.X + width + Padding;
            n1.Bounds.Y = toSplit.Bounds.Y;
            n1.Bounds.Width = toSplit.Bounds.Width - width - Padding;
            n1.Bounds.Height = height;
            n1.SplitType = SplitType.Vertical;

            var n2 = new Node();
            n2.Bounds.X = toSplit.Bounds.X;
            n2.Bounds.Y = toSplit.Bounds.Y + height + Padding;
            n2.Bounds.Width = toSplit.Bounds.Width;
            n2.Bounds.Height = toSplit.Bounds.Height - height - Padding;
            n2.SplitType = SplitType.Horizontal;

            if (n1.Bounds.Width > 0 && n1.Bounds.Height > 0) list.Add(n1);
            if (n2.Bounds.Width > 0 && n2.Bounds.Height > 0) list.Add(n2);
        }

        private void VerticalSplit(Node toSplit, int width, int height, List<Node> list)
        {
            var n1 = new Node();
            n1.Bounds.X = toSplit.Bounds.X + width + Padding;
            n1.Bounds.Y = toSplit.Bounds.Y;
            n1.Bounds.Width = toSplit.Bounds.Width - width - Padding;
            n1.Bounds.Height = toSplit.Bounds.Height;
            n1.SplitType = SplitType.Vertical;

            var n2 = new Node();
            n2.Bounds.X = toSplit.Bounds.X;
            n2.Bounds.Y = toSplit.Bounds.Y + height + Padding;
            n2.Bounds.Width = width;
            n2.Bounds.Height = toSplit.Bounds.Height - height - Padding;
            n2.SplitType = SplitType.Horizontal;

            if (n1.Bounds.Width > 0 && n1.Bounds.Height > 0) list.Add(n1);
            if (n2.Bounds.Width > 0 && n2.Bounds.Height > 0) list.Add(n2);
        }

        private TextureInfo FindBestFitForNode(Node node, List<TextureInfo> textures)
        {
            TextureInfo bestFit = null;
            float nodeArea = node.Bounds.Width * node.Bounds.Height;
            float maxCriteria = 0.0f;

            foreach (TextureInfo ti in textures)
            {
                switch (FitHeuristic)
                {
                    case BestFitHeuristic.MaxOneAxis:
                        if (ti.Width <= node.Bounds.Width && ti.Height <= node.Bounds.Height)
                        {
                            float wRatio = (float)ti.Width / node.Bounds.Width;
                            float hRatio = (float)ti.Height / node.Bounds.Height;
                            float ratio = wRatio > hRatio ? wRatio : hRatio;
                            if (ratio > maxCriteria) { maxCriteria = ratio; bestFit = ti; }
                        }
                        break;
                    case BestFitHeuristic.Area:
                        if (ti.Width <= node.Bounds.Width && ti.Height <= node.Bounds.Height)
                        {
                            float coverage = (ti.Width * ti.Height) / nodeArea;
                            if (coverage > maxCriteria) { maxCriteria = coverage; bestFit = ti; }
                        }
                        break;
                }
            }
            return bestFit;
        }

        private List<TextureInfo> LayoutAtlas(List<TextureInfo> input, Atlas atlas)
        {
            var freeList = new List<Node>();
            List<TextureInfo> textures = input.ToList();
            atlas.Nodes = new List<Node>();

            var root = new Node();
            root.Bounds.Width = atlas.Width;
            root.Bounds.Height = atlas.Height;
            root.SplitType = SplitType.Horizontal;
            freeList.Add(root);

            while (freeList.Count > 0 && textures.Count > 0)
            {
                Node node = freeList[0];
                freeList.RemoveAt(0);

                TextureInfo bestFit = FindBestFitForNode(node, textures);
                if (bestFit != null)
                {
                    if (node.SplitType == SplitType.Horizontal)
                        HorizontalSplit(node, bestFit.Width, bestFit.Height, freeList);
                    else
                        VerticalSplit(node, bestFit.Width, bestFit.Height, freeList);

                    node.Texture = bestFit;
                    node.Bounds.Width = bestFit.Width;
                    node.Bounds.Height = bestFit.Height;
                    textures.Remove(bestFit);
                }
                atlas.Nodes.Add(node);
            }
            return textures;
        }

        private MagickImage CreateAtlasImage(Atlas atlas)
        {
            var img = new MagickImage(MagickColors.Transparent, (uint)atlas.Width, (uint)atlas.Height);
            foreach (Node n in atlas.Nodes)
            {
                if (n.Texture is null) continue;
                using MagickImage sourceImg = TextureWorker.ReadBGRAImageFromFile(n.Texture.Source);
                using IMagickImage<byte> resized = TextureWorker.ResizeImage(sourceImg, n.Bounds.Width, n.Bounds.Height);
                img.Composite(resized, n.Bounds.X, n.Bounds.Y, CompositeOperator.Copy);
            }
            return img;
        }
    }
}