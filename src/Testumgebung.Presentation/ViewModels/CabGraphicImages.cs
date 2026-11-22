using System.Buffers.Binary;
using Testumgebung.Domain.Labels.CabJScript;

namespace Testumgebung.Presentation.ViewModels;

public static class CabGraphicImages
{
    public static CabGraphicImage? Find(CabSourceEditor editor, CabBoundCommand command)
        => Find(editor, command, CabGraphicValues.Text(command, "name"));

    public static CabGraphicImage? Find(CabSourceEditor editor, CabBoundCommand command, string name)
    {
        var resource = CabResourceEditor.Resources(editor).LastOrDefault(r => r.Command.Syntax.Range.Offset < command.Syntax.Range.Offset && r.Name == name);
        if (resource is null) return null;
        try
        {
            var data = CabResourceEditor.Read(editor, resource);
            var dimensions = Dimensions(data);
            return new(name, resource.Type, data, dimensions.Width, dimensions.Height);
        }
        catch (Exception error) when (error is InvalidDataException or FormatException or ArgumentException)
        { return null; }
    }

    public static (int Width, int Height) Dimensions(byte[] data)
    {
        var span = data.AsSpan();
        if (data.Length >= 24 && span[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            return Safe(BinaryPrimitives.ReadInt32BigEndian(span[16..]), BinaryPrimitives.ReadInt32BigEndian(span[20..]));
        if (data.Length >= 26 && data[0] == 'B' && data[1] == 'M')
            return Safe(BinaryPrimitives.ReadInt32LittleEndian(span[18..]), Math.Abs((long)BinaryPrimitives.ReadInt32LittleEndian(span[22..])));
        if (data.Length >= 10 && data[0] == 'G' && data[1] == 'I' && data[2] == 'F')
            return Safe(BinaryPrimitives.ReadUInt16LittleEndian(span[6..]), BinaryPrimitives.ReadUInt16LittleEndian(span[8..]));
        return OtherDimensions(data);
    }

    private static (int, int) OtherDimensions(byte[] data)
    {
        var span = data.AsSpan();
        if (data.Length >= 8 && (data[0] == 'I' && data[1] == 'I' || data[0] == 'M' && data[1] == 'M')) return Tiff(data);
        if (data.Length >= 128 && data[0] == 10)
            return Safe(BinaryPrimitives.ReadUInt16LittleEndian(span[8..]) - BinaryPrimitives.ReadUInt16LittleEndian(span[4..]) + 1,
                BinaryPrimitives.ReadUInt16LittleEndian(span[10..]) - BinaryPrimitives.ReadUInt16LittleEndian(span[6..]) + 1);
        return (0, 0);
    }

    private static (int, int) Safe(long width, long height)
        => width is > 0 and <= 1_000_000 && height is > 0 and <= 1_000_000 ? ((int)width, (int)height) : (0, 0);

    private static (int, int) Tiff(byte[] data)
    {
        var little = data[0] == 'I';
        uint Read(int offset, bool word) => word
            ? little ? BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset)) : BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset))
            : little ? BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset)) : BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset));
        if (Read(2, true) != 42) return (0, 0);
        var directory = (long)Read(4, false);
        if (directory > data.Length - 2) return (0, 0);
        var count = Read((int)directory, true); long width = 0, height = 0;
        for (var index = 0; index < count; index++)
        {
            var at = directory + 2 + index * 12;
            if (at > data.Length - 12) return (0, 0);
            var tag = Read((int)at, true); var type = Read((int)at + 2, true);
            if (Read((int)at + 4, false) != 1 || type is not (3 or 4)) continue;
            var value = Read((int)at + 8, type == 3);
            if (tag == 256) width = value;
            if (tag == 257) height = value;
        }
        return Safe(width, height);
    }
}
