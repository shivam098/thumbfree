namespace ThumbFree.App.UI;

/// <summary>A Help section: its lines, and examples of what you say and what ThumbFree types.</summary>
internal sealed record HelpSection(string Title, string[] Lines, (string Said, string Typed)[]? Examples = null);

/// <summary>Everything Help explains, in one place. The examples are rows of tests/vectors/layout.tsv, so they're what ThumbFree really types.</summary>
internal static class HelpText
{
    public const string NoticesUrl = "https://github.com/shivam098/thumbfree/blob/main/windows/THIRD-PARTY-NOTICES.md";

    public static IReadOnlyList<HelpSection> Sections(AppSettings settings) =>
    [
        new("Dictate into any app", [
            "Click where you want to type, then tap Ctrl+Win (press and let go): ThumbFree starts listening. There's nothing to hold.",
            "Speak naturally. The pill at the bottom of the screen shows your voice level and your words.",
            "Tap Ctrl+Win again when you're done: your text is typed where the cursor is.",
            "Esc cancels. Ctrl+Win+Space works the same as Ctrl+Win.",
            $"{settings.PasteLastHotkey} pastes the last dictation again.",
            "A dictation finishes by itself after 10 minutes, so the microphone is never left on.",
            "Password fields, and apps running as administrator, are never typed into.",
        ]),
        new("Spoken punctuation", [
            "“comma” → ,   “full stop” → .   “question mark” → ?   “exclamation mark” → !",
            "“colon” → :   “semicolon” → ;   and “period” at the very end → .",
            "“new line” starts a new line; “new paragraph” leaves an empty line first.",
            "“scratch that” drops the phrase before it: “send it to John, scratch that, send it to Sarah” → “Send it to Sarah.”",
            "Fillers are left out: “um”, “uh”, “you know”, “basically”, “thik hai na”, and words said twice.",
        ]),
        new(
            "Smart formatting",
            [
                "Emails: in a mail app (or when you say a greeting and sign your name at the end), the greeting and the sign-off get lines of their own.",
                "Lists: say “bullet point … bullet point …”, “first … second … third …”, or a heading like “Things to do today” followed by the items.",
                "Corrections: after “actually”, “sorry”, “I mean”, “no wait” or “or rather”, only the corrected time, day or number is kept.",
                "Lists and emails need a box that takes several lines. Turn smart formatting off in Settings if you'd rather it didn't.",
            ],
            [
                (PracticeTexts.EmailToSay, PracticeTexts.EmailLaidOut),
                (PracticeTexts.ListToSay, PracticeTexts.ListLaidOut),
                ("My plan: first, book the flights, second, find a hotel, and third, pack the bags.", "My plan:\n1. Book the flights.\n2. Find a hotel.\n3. Pack the bags."),
                ("Call me at 3 PM, actually 4.", "Call me at 4 PM."),
            ]),
        new("Hindi + English", [
            $"{settings.DictationLanguageHotkey} switches between English and Hindi + English; the pill shows HI + EN while it's on.",
            "Hindi is typed in Roman letters and English words stay English: “kal ki meeting cancel ho gayi hai”.",
            "Standard is quick. Accurate makes fewer mistakes but types a few seconds after you finish, and needs 8 GB of memory or more.",
        ]),
        new("Your words", [
            "Words I use often (Settings): names and jargon, written your way. “git hub co-pilot” → “GitHub Copilot”.",
            "Personal dictionary: for a word it mishears completely, “what it hears = what to type”, like “journals = genres”.",
            "“What do you do?” adds the names and acronyms of your field. It stays on this PC.",
        ]),
        new("History and lost text", [
            "Every dictation is kept for 30 days on this PC (500 at most). Click one in History, on Home or in the tray menu to copy it.",
            "Nothing from password fields is kept. Turn History off or clear it in Settings.",
            "If a text box closes before your words are ready, they're never typed anywhere else: a notification keeps them; click it to copy.",
            "If the microphone goes silent mid-dictation (a call or another app took it), ThumbFree tells you straight away.",
        ]),
        new("Privacy", [
            "Speech is turned into text on this PC. Nothing you say is uploaded, and there's no account.",
            "The internet is used only to download a speech model, once.",
            "The microphone is on only from one tap of Ctrl+Win to the next.",
            "Recordings are kept only if you turn on “Help improve ThumbFree”, and never from password fields or web browsers. They stay on this PC, and leave it only in a zip you save and share yourself.",
            "Logs hold timings and error names, never your words.",
        ]),
        new("Licences and thanks", [
            "NVIDIA Parakeet TDT (English, Standard and Accurate): CC BY 4.0",
            "Kroko ASR by Banafo (English, Live): CC BY-SA 4.0",
            "Edge-Punct-Casing (punctuation for Live): Apache-2.0",
            "OpenAI Whisper: MIT",
            "Oriserve Whisper-Hindi2Hinglish, Swift and Apex (Hindi + English): Apache-2.0",
            "sherpa-onnx: Apache-2.0",
            "ONNX Runtime: MIT",
            "whisper.cpp and Whisper.net: MIT",
            "NAudio: MIT",
            "Silero VAD: MIT",
            "Each model keeps its own licence; the notices list where they come from and how they were converted.",
        ]),
    ];
}
