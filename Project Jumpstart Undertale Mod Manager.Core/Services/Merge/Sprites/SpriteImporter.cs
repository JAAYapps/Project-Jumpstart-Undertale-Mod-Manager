using System.Text.Json;
using ImageMagick;
using UndertaleModLib;
using UndertaleModLib.Models;
using UndertaleModLib.Util;
using PjumModLib;

namespace Project_Jumpstart_Undertale_Mod_Manager.Services.Merge.Sprites;

public static class SpriteImporter
{
    public static SpriteJson ReadJson(string file)
    {
        SpriteJson json;
        try
        {
            json = JsonSerializer.Deserialize<SpriteJson>(File.ReadAllText(file), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                   ?? throw new InvalidOperationException($"Sprite JSON '{file}' parsed to null.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Sprite JSON '{file}' is invalid: {ex.Message}", ex);
        }
        if (json.CollisionKind is string kind && !Enum.TryParse<UndertaleSprite.SepMaskType>(kind, true, out _))
            throw new InvalidOperationException($"Sprite JSON '{file}' has collisionKind '{kind}'; use AxisAlignedRect, Precise or RotatedRect.");
        if ((json.Width is null) != (json.Height is null))
            throw new InvalidOperationException($"Sprite JSON '{file}' sets only one of width and height; set both.");
        return json;
    }

    public static void Apply(UndertaleData data, UndertaleSprite sprite, SpriteJson? json, IReadOnlyList<(int Index, string File)> masks, List<string> warnings, string modName)
    {
        (int Width, int Height) maskSizeBefore = sprite.CalculateMaskDimensions(data);

        if (json is not null)
        {
            if (json.Width is uint width && json.Height is uint height)
            {
                sprite.Width = width;
                sprite.Height = height;
            }
            if (json.OriginX is int originX) sprite.OriginX = originX;
            if (json.OriginY is int originY) sprite.OriginY = originY;
            if (json.Margins is SpriteMarginsJson margins)
            {
                sprite.MarginLeft = margins.Left;
                sprite.MarginTop = margins.Top;
                sprite.MarginRight = margins.Right;
                sprite.MarginBottom = margins.Bottom;
            }
            if (json.BBoxMode is uint bboxMode) sprite.BBoxMode = bboxMode;
            if (json.CollisionKind is string kind) sprite.SepMasks = Enum.Parse<UndertaleSprite.SepMaskType>(kind, true);
            if (json.Transparent is bool transparent) sprite.Transparent = transparent;
            if (json.Smooth is bool smooth) sprite.Smooth = smooth;
            if (json.Preload is bool preload) sprite.Preload = preload;
            if (json.Frames is int frames && frames >= 0)
                while (sprite.Textures.Count > frames)
                    sprite.Textures.RemoveAt(sprite.Textures.Count - 1);
        }

        (int Width, int Height) maskSize = sprite.CalculateMaskDimensions(data);
        if (masks.Count == 0 && json?.Masks is 0)
        {
            sprite.CollisionMasks.Clear();
        }
        else if (masks.Count == 0 && json?.Masks is int wanted && wanted > 0 && wanted != sprite.CollisionMasks.Count)
        {
            sprite.CollisionMasks.Clear();
            using var worker = new TextureWorker();
            for (int i = 0; i < wanted; i++)
                sprite.CollisionMasks.Add(maskSize.Width > 0 && maskSize.Height > 0
                    ? MaskFromFrame(data, worker, sprite, i, maskSize)
                    : new UndertaleSprite.MaskEntry([], Math.Max(0, maskSize.Width), Math.Max(0, maskSize.Height)));
            warnings.Add($"[{modName}] sprite '{sprite.Name.Content}' asks for {wanted} collision mask(s) but has no mask files; they were built from its frames.");
        }
        else if (masks.Count > 0)
        {
            var read = masks.OrderBy(m => m.Index)
                            .Select(m => new UndertaleSprite.MaskEntry(TextureWorker.ReadMaskData(m.File, maskSize.Width, maskSize.Height), maskSize.Width, maskSize.Height))
                            .ToList();
            sprite.CollisionMasks.Clear();
            foreach (UndertaleSprite.MaskEntry mask in read)
                sprite.CollisionMasks.Add(mask);
        }
        else if (maskSize != maskSizeBefore && sprite.CollisionMasks.Count > 0 && (maskSize.Width <= 0 || maskSize.Height <= 0))
        {
            int count = sprite.CollisionMasks.Count;
            sprite.CollisionMasks.Clear();
            for (int i = 0; i < count; i++)
                sprite.CollisionMasks.Add(new UndertaleSprite.MaskEntry([], Math.Max(0, maskSize.Width), Math.Max(0, maskSize.Height)));
        }
        else if (maskSize != maskSizeBefore && sprite.CollisionMasks.Count > 0)
        {
            int count = Math.Min(sprite.CollisionMasks.Count, Math.Max(1, sprite.Textures.Count));
            sprite.CollisionMasks.Clear();
            using var worker = new TextureWorker();
            for (int i = 0; i < count; i++)
                sprite.CollisionMasks.Add(MaskFromFrame(data, worker, sprite, i, maskSize));
            warnings.Add($"[{modName}] sprite '{sprite.Name.Content}' changed size but has no mask files; its {count} collision mask(s) were rebuilt from its frames.");
        }

        if (sprite.CollisionMasks.Count > 1 && sprite.CollisionMasks.Count > sprite.Textures.Count)
            while (sprite.CollisionMasks.Count > Math.Max(1, sprite.Textures.Count))
                sprite.CollisionMasks.RemoveAt(sprite.CollisionMasks.Count - 1);
    }

    private static UndertaleSprite.MaskEntry MaskFromFrame(UndertaleData data, TextureWorker worker, UndertaleSprite sprite, int frame, (int Width, int Height) size)
    {
        int stride = (size.Width + 7) / 8;
        var bits = new byte[stride * size.Height];
        UndertaleTexturePageItem? item = frame < sprite.Textures.Count ? sprite.Textures[frame]?.Texture : null;
        if (item is not null)
        {
            (int x, int y) offset = data.IsVersionAtLeast(2024, 6) ? (sprite.MarginLeft, sprite.MarginTop) : (0, 0);
            using IMagickImage<byte> image = worker.GetTextureFor(item, sprite.Name.Content, includePadding: true);
            IPixelCollection<byte> pixels = image.GetPixels();
            for (int y = 0; y < size.Height; y++)
                for (int x = 0; x < size.Width; x++)
                {
                    int px = x + offset.x, py = y + offset.y;
                    if (px < image.Width && py < image.Height && pixels.GetPixel(px, py).ToColor() is { A: > 0 })
                        bits[y * stride + x / 8] |= (byte)(1 << (7 - x % 8));
                }
        }
        return new UndertaleSprite.MaskEntry(bits, size.Width, size.Height);
    }
}
