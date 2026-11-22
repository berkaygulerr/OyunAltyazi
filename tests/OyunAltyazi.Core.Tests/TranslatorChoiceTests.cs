using OyunAltyazi.Core;
namespace OyunAltyazi.Core.Tests;

public class TranslatorChoiceTests
{
    [Theory]
    [InlineData("auto", true, true, TranslatorKind.Ct2)]      // Auto never silently moves to ONNX
    [InlineData("auto", false, true, TranslatorKind.Google)]
    [InlineData("auto", true, false, TranslatorKind.Ct2)]
    [InlineData("auto", false, false, TranslatorKind.Google)]
    [InlineData("ct2", true, true, TranslatorKind.Ct2)]
    [InlineData("ct2", false, true, TranslatorKind.Google)]
    [InlineData("onnx", true, true, TranslatorKind.Onnx)]     // explicit opt-in wins over an installed CT2
    [InlineData("onnx", false, true, TranslatorKind.Onnx)]
    [InlineData("onnx", true, false, TranslatorKind.Google)]  // not installed: fall back, never half-load
    [InlineData("google_only", true, true, TranslatorKind.Google)]
    [InlineData("google_forced", true, true, TranslatorKind.Google)]
    [InlineData(null, true, true, TranslatorKind.Ct2)]
    [InlineData("whatever", true, true, TranslatorKind.Ct2)]
    public void Resolves(string? choice, bool ct2, bool onnx, TranslatorKind expected) =>
        Assert.Equal(expected, TranslatorChoice.Resolve(choice, ct2, onnx));
}
