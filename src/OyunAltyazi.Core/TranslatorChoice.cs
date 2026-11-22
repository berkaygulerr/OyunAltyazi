namespace OyunAltyazi.Core;

public enum TranslatorKind { Google, Ct2, Onnx }

/// <summary>
/// Maps the saved setting to a backend. Explicit choices ("ct2", "onnx", "google_only", "google_forced") are never overridden.
/// "auto" (and anything unknown) keeps the existing behaviour: CTranslate2 when installed, otherwise Google. Native ONNX CPU is
/// opt-in only: measured on the reference machine it is ~10-20x slower per line than CT2 on CPU (see docs/verification/onnx-vs-ct2),
/// so Auto must not silently switch to it. "gemini" is a separate explicit mode (see <see cref="UseGemini"/>): its local base is resolved exactly like Auto.
/// </summary>
public static class TranslatorChoice
{
    public static TranslatorKind Resolve(string? choice, bool ct2Installed, bool onnxInstalled) => choice switch
    {
        "google_only" or "google_forced" => TranslatorKind.Google,
        "onnx" => onnxInstalled ? TranslatorKind.Onnx : TranslatorKind.Google,
        _ => ct2Installed ? TranslatorKind.Ct2 : TranslatorKind.Google,   // "auto", "ct2", legacy/unknown
    };

    /// <summary>The optional online Gemini translator runs only when the user selected it AND consented AND stored a key. Otherwise no request is ever made.</summary>
    public static bool UseGemini(string? choice, bool consent, bool hasKey) => choice == "gemini" && consent && hasKey;
}
