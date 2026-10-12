using System.Reflection;
using Testumgebung.Persistence.Results;

namespace Testumgebung.Engine.Tests;

public sealed class StoredScalarReadBoundaryTests
{
    public static IEnumerable<object?[]> Cases()
    {
        yield return [null, null]; yield return [42L, null]; yield return [Array.Empty<object>(), null]; yield return [new object?[] { 7L, "text" }, null];
        yield return [new object?[] { "b", true }, true]; yield return [new object?[] { "b", false }, false]; yield return [new object?[] { "b", "true" }, false];
        yield return [new object?[] { "i", 42L }, 42L]; yield return [new object?[] { "i", "42" }, null];
        yield return [new object?[] { "d", 1.25 }, 1.25]; yield return [new object?[] { "d", 42L }, 42d]; yield return [new object?[] { "d", "42" }, null];
        yield return [new object?[] { "s", "text" }, "text"]; yield return [new object?[] { "s", 42L }, null];
        yield return [new object?[] { "t", DateTime.UnixEpoch.Ticks }, DateTime.UnixEpoch]; yield return [new object?[] { "t", "ticks" }, null]; yield return [new object?[] { "unknown", true }, null];
    }
    [Theory, MemberData(nameof(Cases))]
    public void Binary_scalar_decoder_accepts_only_documented_payload_types(object? stored, object? expected)
        => Assert.Equal(expected, typeof(ResultFile).GetMethod("ReadValue", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [stored]));

    [Theory]
    [InlineData("image.PNG", "image/png")]
    [InlineData("image.jpg", "image/jpeg")]
    [InlineData("image.jpeg", "image/jpeg")]
    [InlineData("image.gif", "image/gif")]
    [InlineData("image.bmp", "image/bmp")]
    [InlineData("data.bin", "application/octet-stream")]
    public void Attachment_media_types_are_case_insensitive_and_unknown_files_are_binary(string name, string expected)
    {
        var builder = typeof(ResultRecorder).GetNestedType("Builder", BindingFlags.NonPublic)!;
        Assert.Equal(expected, builder.GetMethod("MediaTypeOf", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [name]));
    }
}
