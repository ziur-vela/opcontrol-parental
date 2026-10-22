// Copyright (c) 2026 Ziur / OpControl IT Solutions
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

namespace OpControl.Parental.Helper;

/// <summary>Speaks text with SAPI in the device language voice when one is installed.</summary>
internal static class Speech
{
    public static void Speak(string text, string? language)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        try
        {
            var type = Type.GetTypeFromProgID("SAPI.SpVoice");
            if (type is null) return;
            dynamic? voice = Activator.CreateInstance(type);
            if (voice is null) return;

            var spanish = (language ?? "en").StartsWith("es", StringComparison.OrdinalIgnoreCase);
            try
            {
                dynamic tokens = voice.GetVoices();
                var count = (int)tokens.Count;
                for (var i = 0; i < count; i++)
                {
                    dynamic token = tokens.Item(i);
                    var description = (string?)token.GetDescription() ?? "";
                    var lcid = (int)token.Language;
                    var primary = lcid & 0x3ff;
                    var match = spanish
                        ? primary == 10 || description.Contains("Spanish", StringComparison.OrdinalIgnoreCase)
                            || description.Contains("Español", StringComparison.OrdinalIgnoreCase)
                        : primary == 9 || description.Contains("English", StringComparison.OrdinalIgnoreCase);
                    if (!match) continue;
                    voice.Voice = token;
                    break;
                }
            }
            catch
            {
                // Keep the default voice when the installed voice list cannot be read.
            }

            voice.Speak(text, 0);
        }
        catch
        {
            // Speech is optional; never let it break the helper.
        }
    }
}
