namespace M0LTE.S5066.Host.Station;

/// <summary>
/// Morse code audio for the optional identification hook (plan 9.1, decision D4;
/// core-architecture.md 10 item 13): a keyed tone with standard PARIS timing and raised-cosine
/// keying, so the ID is clean on air and readable by ear and by a CW decoder.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Timing is the PARIS standard: one dit is 1200 / wpm ms; a dah is three dits; one dit
/// between the elements of a character, three between characters, seven between words. "PARIS"
/// and its word gap are 50 dits, so 20 wpm sends it 20 times a minute.</item>
/// <item>Each element rises and falls on a raised-cosine edge of <see cref="RiseMs"/> (less for a
/// dit too short to hold two), inside the element's own time, so the timing is unchanged and the
/// keying makes no clicks.</item>
/// <item>The tone's phase runs on through the gaps, so every element starts and ends at the same
/// smooth envelope whatever the tone.</item>
/// </list>
/// </remarks>
public static class Morse
{
    /// <summary>The rise and fall time of each element, in ms: 5 ms, usual for clean CW.</summary>
    public const double RiseMs = 5;

    private static readonly Dictionary<char, string> Codes = new()
    {
        ['A'] = ".-", ['B'] = "-...", ['C'] = "-.-.", ['D'] = "-..", ['E'] = ".", ['F'] = "..-.",
        ['G'] = "--.", ['H'] = "....", ['I'] = "..", ['J'] = ".---", ['K'] = "-.-", ['L'] = ".-..",
        ['M'] = "--", ['N'] = "-.", ['O'] = "---", ['P'] = ".--.", ['Q'] = "--.-", ['R'] = ".-.",
        ['S'] = "...", ['T'] = "-", ['U'] = "..-", ['V'] = "...-", ['W'] = ".--", ['X'] = "-..-",
        ['Y'] = "-.--", ['Z'] = "--..",
        ['0'] = "-----", ['1'] = ".----", ['2'] = "..---", ['3'] = "...--", ['4'] = "....-",
        ['5'] = ".....", ['6'] = "-....", ['7'] = "--...", ['8'] = "---..", ['9'] = "----.",
        ['/'] = "-..-.", ['?'] = "..--..", ['.'] = ".-.-.-", [','] = "--..--", ['='] = "-...-",
        ['-'] = "-....-", ['+'] = ".-.-.",
    };

    /// <summary>The characters that can be sent: A to Z, 0 to 9, <c>/ ? . , = - +</c> and the space.</summary>
    public static string Alphabet => "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789/?.,=-+ ";

    /// <summary>The dots and dashes of one character, or null when it has none.</summary>
    public static string? CodeOf(char c) => Codes.TryGetValue(char.ToUpperInvariant(c), out string? code) ? code : null;

    /// <summary>The character a run of dots and dashes stands for, or null.</summary>
    public static char? CharOf(string code)
    {
        foreach (KeyValuePair<char, string> kv in Codes)
        {
            if (kv.Value == code)
            {
                return kv.Key;
            }
        }

        return null;
    }

    /// <summary>
    /// The text as it will be sent: upper case, runs of spaces as one, trimmed. Throws when a
    /// character has no Morse code.
    /// </summary>
    /// <exception cref="ArgumentException">A character cannot be sent.</exception>
    public static string Normalise(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string[] words = text.ToUpperInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (char c in string.Concat(words))
        {
            if (!Codes.ContainsKey(c))
            {
                throw new ArgumentException($"'{c}' has no Morse code; use A to Z, 0 to 9, / ? . , = - + and spaces", nameof(text));
            }
        }

        return string.Join(' ', words);
    }

    /// <summary>
    /// The keying of the text in dits: true for key down. No leading or trailing gap: the first
    /// unit is the first element and the last is the end of the last element.
    /// </summary>
    public static List<bool> Units(string text)
    {
        string sent = Normalise(text);
        var units = new List<bool>();
        for (int i = 0; i < sent.Length; i++)
        {
            char c = sent[i];
            if (c == ' ')
            {
                // A word gap is seven dits; the three of the character gap are already there.
                units.AddRange([false, false, false, false]);
                continue;
            }

            if (i > 0)
            {
                // The character gap, or the rest of the word gap after a space.
                units.AddRange([false, false, false]);
            }

            string code = Codes[c];
            for (int e = 0; e < code.Length; e++)
            {
                if (e > 0)
                {
                    units.Add(false);
                }

                units.AddRange(code[e] == '.' ? [true] : [true, true, true]);
            }
        }

        return units;
    }

    /// <summary>The length of one dit at <paramref name="wpm"/> words a minute, in ms (PARIS).</summary>
    public static double DitMs(int wpm) => 1200.0 / wpm;

    /// <summary>How long the text takes to send at <paramref name="wpm"/>, in ms, rounded up.</summary>
    public static int DurationMs(string text, int wpm) => (int)Math.Ceiling(Units(text).Count * DitMs(wpm));

    /// <summary>
    /// The text as audio: a tone of <paramref name="toneHz"/> at peak amplitude <paramref name="level"/>
    /// (a fraction of full scale), keyed at <paramref name="wpm"/> with raised-cosine edges.
    /// </summary>
    /// <param name="text">The text; see <see cref="Alphabet"/>.</param>
    /// <param name="wpm">Words a minute, PARIS timing.</param>
    /// <param name="toneHz">The tone.</param>
    /// <param name="level">Peak amplitude, above 0 and at most 1.</param>
    /// <param name="sampleRate">Samples a second.</param>
    public static float[] Generate(string text, int wpm, double toneHz, double level, int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(wpm, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1);
        if (!(level > 0 && level <= 1))
        {
            throw new ArgumentOutOfRangeException(nameof(level), "must be above 0 and at most 1");
        }

        if (!(toneHz > 0 && toneHz < sampleRate / 2.0))
        {
            throw new ArgumentOutOfRangeException(nameof(toneHz), "must be above 0 and below half the sample rate");
        }

        List<bool> units = Units(text);
        double ditSamples = DitMs(wpm) * sampleRate / 1000;
        var audio = new float[(int)Math.Ceiling(units.Count * ditSamples)];
        double rise = Math.Min(RiseMs * sampleRate / 1000, ditSamples / 2);
        double w = 2 * Math.PI * toneHz / sampleRate;

        // Each key-down run, as a sample range: unit boundaries rounded to the nearest sample.
        int u = 0;
        while (u < units.Count)
        {
            if (!units[u])
            {
                u++;
                continue;
            }

            int end = u;
            while (end < units.Count && units[end])
            {
                end++;
            }

            int from = (int)Math.Round(u * ditSamples);
            int to = Math.Min(audio.Length, (int)Math.Round(end * ditSamples));
            for (int i = from; i < to; i++)
            {
                double fromStart = i - from + 0.5;
                double fromEnd = to - i - 0.5;
                double edge = Math.Min(fromStart, fromEnd);
                double envelope = edge >= rise ? 1 : 0.5 - (0.5 * Math.Cos(Math.PI * edge / rise));
                audio[i] = (float)(level * envelope * Math.Sin(w * i));
            }

            u = end;
        }

        return audio;
    }
}
