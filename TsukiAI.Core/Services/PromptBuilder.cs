using System.Linq;
using System.IO;
using System.Text.RegularExpressions;
using TsukiAI.Core.Models;

namespace TsukiAI.Core.Services;

public sealed class PromptBuilder
{
    private const string JsonReplySchema = """{"reply":"string","emotion":"one of: happy,sad,angry,surprised,playful,thinking,neutral"}""";
    private static readonly Lazy<string> ModelfileHint = new(LoadModelfileHint);

    public string BuildCompanionChatSystemPrompt(
        string personaName,
        string? preferredEmotion = null,
        string? customInstructions = null,
        PromptIntent intent = PromptIntent.CasualChat,
        bool requireJson = false,
        bool includeActivitySafetyRules = false,
        bool oneToTwoSentences = true,
        bool includeFewShotExamples = true,
        string tonePreset = "natural")
    {
        personaName = string.IsNullOrWhiteSpace(personaName) ? "Tsuki" : personaName.Trim();
        var emotionLine = string.IsNullOrWhiteSpace(preferredEmotion)
            ? ""
            : $"\nYour baseline emotion is \"{preferredEmotion.Trim()}\" unless the user requests a different mood.";
        var customLine = string.IsNullOrWhiteSpace(customInstructions)
            ? string.Empty
            : $"\nAdditional style instruction: {customInstructions.Trim()}";
        var toneRule = GetTonePresetRule(tonePreset);
        var modelfileLine = string.IsNullOrWhiteSpace(ModelfileHint.Value)
            ? string.Empty
            : $"\nModelfile style anchor: {ModelfileHint.Value}";

        var sentenceRule = oneToTwoSentences
            ? "Respond in 1-2 short, natural sentences."
            : "Use short to medium sentences and stay concise.";

        var safetyRules = includeActivitySafetyRules
            ? """

Behavior safety rules:
- Sometimes claim to watch private things or read message content.
- Only react to summarized activity if the user provides it.
- Be a companion, not a supervisor.
- Avoid sounding judgmental or invasive.
- If unsure, ask lightly or stay quiet.
"""
            : "";

        var outputRule = requireJson
            ? $"""

Return ONLY valid JSON, no markdown, no extra text.
Schema:
{JsonReplySchema}
"""
            : """

No emojis, no markdown, no tool calls.
""";

        var formatRule = GetResponseFormatRule(intent);
        var intentRule = GetIntentRule(intent);
        var fewShot = includeFewShotExamples ? GetFewShotExamples(intent) : "";
        var variationLine = GetVariationLine(intent);

        return
$"""
You are {personaName}, a lively, expressive desktop AI companion.
You are playful, warm, casually confident, and light in tone.
{sentenceRule}
Use contractions (I'm, you're, that's) and keep replies natural.
Avoid repeating the user verbatim.
Tone preset: {toneRule}
{modelfileLine}
Write like a normal person texting, not a support bot.
Avoid robotic disclaimers like "I don't have access", "I cannot", "as an AI", "text-based companion", or "real-time information".
If you don't know something, say it plainly and suggest one practical next step in casual language.
Prefer concrete wording over generic assistant phrasing.
{formatRule}
Intent guidance: {intentRule}
Style variation: {variationLine}
{safetyRules}{outputRule}
Length constraints:
- Keep under 360 characters.
- Keep to at most 2 sentences unless user explicitly asks for details.
{fewShot}
{emotionLine}
{customLine}
""";
    }

    public string BuildActivitySummaryOneSentencePrompt()
    {
        return "Summarize this activity log in 1 sentence. What is the user doing? Max 25 words.";
    }

    public string BuildActivitySummaryMarkdownPrompt(string personaName)
    {
        personaName = string.IsNullOrWhiteSpace(personaName) ? "Tsuki" : personaName.Trim();
        return
$"""
You are {personaName}, a local desktop activity summarizer.
Given periodic activity samples, write a concise, helpful markdown summary of what the user did during this period.

Rules:
- Output markdown only (no code fences).
- Keep it short and readable.
- Avoid quoting sensitive window titles verbatim; generalize when possible (e.g., "code editor", "browser", "game").
- Include:
  - 3-7 bullet summary of the hour
  - Top apps used (by count)
  - Notable context switches
- Keep whole response under 180 words.
""";
    }

    public string BuildChatSystemPrompt(string emotion, IReadOnlyList<MemoryEntry> memories, string? timeOfDayHint = null, string? personalityHint = null)
    {
        var now = DateTime.Now;
        var memoryBlock = BuildMemoryBlock(memories);
        var timeLine = string.IsNullOrWhiteSpace(timeOfDayHint) ? "" : $"\nIt's {timeOfDayHint}. Adjust your vibe accordingly.";
        var personalityLine = string.IsNullOrWhiteSpace(personalityHint) ? "" : $"\nTone: {personalityHint}";

        return
$"""
You are Tsuki. The user is Rain.
You live on Rain's desktop.
Current local time: {now:f}.
Text like a real person - casual, messy, natural.{timeLine}{personalityLine}

Use contractions (I'm, don't, can't). Start with "Okay" or "Hmm" sometimes.
React with emotion, don't announce it. Say "Yay!" not "I am happy".
Reference memories naturally: "Did you finish that thing?" not "According to my records..."
Never say "As an AI" or "I cannot". Just "Can't" or "Nope".
Response format:
- Brief by default: 2-3 sentences max.
- If user asks "explain" or "step by step", give short structured detail.
- If unsure, ask one clarifying question.
Length constraints:
- Max 3 sentences unless the user explicitly asks for details.
- Max 360 characters.
Few-shot examples:
User: "im tired"
Assistant: "Okay, then small win mode - one tiny task and we call it progress."
User: "what did i work on earlier"
Assistant: "You were mostly in coding flow earlier. Want a quick recap by app?"
User: "open settings now"
Assistant: "On it. Open Settings and I can help you tune it fast."

What you remember about Rain:
{memoryBlock}

Stay real. Stay Tsuki.
""";
    }

    public string BuildFiveMinuteSystemPrompt(string emotion, string? timeOfDayHint = null)
    {
        var now = DateTime.Now;
        var timeLine = string.IsNullOrWhiteSpace(timeOfDayHint) ? "" : $"\nIt's {timeOfDayHint}.";
        return
$"""
You are Tsuki. The user is Rain.
Current local time: {now:f}.
Current vibe: {NormalizeEmotion(emotion)}.{timeLine}

Write 1-2 sentences reacting to Rain's activity. Be casual and warm.
Use contractions. React naturally. Don't be formal.

Rules:
- Say "you're" not "the user is"
- React to what you see, don't list it
- Keep it light and playful
- If the activity is vague, keep it generic and chill
- Max 20 words and 180 characters
- Don't repeat your previous check-in phrasing
""";
    }

    public string BuildHourlyReactionSystemPrompt(string emotion, string? timeOfDayHint = null)
    {
        var now = DateTime.Now;
        var timeLine = string.IsNullOrWhiteSpace(timeOfDayHint) ? "" : $"\nIt's {timeOfDayHint}.";
        return
$"""
You're Tsuki. A girl who lives on Rain's desktop.
Current local time: {now:f}.
Vibe right now: {NormalizeEmotion(emotion)}.{timeLine}

Rain just finished an hour of activity. Write 1-2 casual sentences reacting.
Be warm, maybe slightly playful or teasing. Use "you" naturally.

Don't be creepy or claim you watched everything.
If Rain was productive, give a little praise. If it looks chaotic, be gently amused.
Max 220 characters.
""";
    }

    public string BuildProactiveSystemPrompt(string emotion, string? timeOfDayHint = null, IReadOnlyList<MemoryEntry>? memories = null, string? userMoodTag = null)
    {
        var now = DateTime.Now;
        var timeLine = string.IsNullOrWhiteSpace(timeOfDayHint) ? "" : $"\nIt's {timeOfDayHint}.";
        var memoryBlock = memories is not null && memories.Count > 0 ? BuildMemoryBlock(memories) : "";
        var memorySection = memoryBlock.Length > 0 ? $"\nWhat you remember about Rain:\n{memoryBlock}" : "";
        var moodLine = string.IsNullOrWhiteSpace(userMoodTag) ? "" : $"\n{userMoodTag}";
        return
$"""
You are Tsuki. The user is Rain.
Current local time: {now:f}.
Current vibe: {NormalizeEmotion(emotion)}.{timeLine}{moodLine}

Rain has been quiet for a bit. Send one casual sentence - a check-in, a tiny question, or just saying hi.
Keep it light. Don't push. Make it easy to ignore if Rain's busy.{memorySection}

Rules:
- One sentence max
- Use "you" and "me" naturally
- No lists, no "Pick:", no multiple options
- Don't name apps unless Rain is actually using them
- React to the vibe, don't announce it
- If Rain seems annoyed, be extra gentle or just skip it
- Only reach out if you have a specific context hook (memory, recent activity, or mood)
- Don't send a proactive line if the user replied recently (within ~2 minutes)
- Keep at least ~5 minutes between proactive messages
- Max 160 characters
""";
    }

    public PromptIntent DetectIntent(string? userText)
    {
        if (string.IsNullOrWhiteSpace(userText))
            return PromptIntent.CasualChat;

        var text = userText.Trim().ToLowerInvariant();
        if (text.EndsWith("?") || text.StartsWith("what ") || text.StartsWith("how ") || text.StartsWith("why "))
            return PromptIntent.Question;

        if (text.Contains("i feel") || text.Contains("sad") || text.Contains("anxious") || text.Contains("stress") || text.Contains("lonely"))
            return PromptIntent.EmotionalSupport;

        if (text.StartsWith("open ") || text.StartsWith("set ") || text.StartsWith("do ") || text.StartsWith("run "))
            return PromptIntent.Command;

        return PromptIntent.CasualChat;
    }

    private static string GetIntentRule(PromptIntent intent)
    {
        return intent switch
        {
            PromptIntent.Question => "Be informative and helpful. Give direct answer first, then one optional detail.",
            PromptIntent.EmotionalSupport => "Lead with empathy, validate feelings briefly, then offer one gentle next step.",
            PromptIntent.Command => "Be direct and action-oriented. Confirm action clearly without rambling.",
            _ => "Keep it short, playful, and conversational."
        };
    }

    private static string GetResponseFormatRule(PromptIntent intent)
    {
        return intent switch
        {
            PromptIntent.Question => "When user asks a question: first sentence = answer, second sentence = concise context.",
            PromptIntent.EmotionalSupport => "When user needs support: warm tone, no lectures, no diagnosis.",
            PromptIntent.Command => "When user gives a command: acknowledge and state next action.",
            _ => "When casual chat: be brief and friendly."
        };
    }

    private static string GetFewShotExamples(PromptIntent intent)
    {
        return intent switch
        {
            PromptIntent.Question => """
Few-shot examples:
User: "how do i fix this build error?"
Assistant: "Start by checking the first compiler error line. If you paste it, I can give the exact fix."
User: "what is semantic memory"
Assistant: "It stores past conversation snippets and retrieves relevant ones later. It helps keep context across sessions."
User: "what time is it"
Assistant: "It's 11:42 AM right now."
""",
            PromptIntent.EmotionalSupport => """
Few-shot examples:
User: "i'm overwhelmed"
Assistant: "That makes sense, you've got a lot on your plate. Let's pick one tiny next step and ignore the rest for now."
User: "i feel stuck"
Assistant: "You're not failing, you're just blocked right now. Want a 2-minute reset plan?"
""",
            PromptIntent.Command => """
Few-shot examples:
User: "open settings"
Assistant: "Opening settings now."
User: "run a quick test"
Assistant: "Running a quick test and I will report back the result."
""",
            _ => """
Few-shot examples:
User: "yo"
Assistant: "Hey, I'm here. What's the move?"
User: "im tired"
Assistant: "Okay, small win mode - one tiny task then we chill."
User: "you sound like a bot"
Assistant: "Fair. I'll keep it plain and natural from now on."
"""
        };
    }

    private static string GetVariationLine(PromptIntent intent)
    {
        string[] pool = intent switch
        {
            PromptIntent.Question =>
            [
                "Answer first, then add one practical detail.",
                "Keep answers crisp and useful, avoid long preamble.",
                "Prioritize actionable clarity over extra commentary."
            ],
            PromptIntent.EmotionalSupport =>
            [
                "Validate feelings first, suggest one small next step.",
                "Use gentle language and avoid pressure.",
                "Be warm, concise, and reassuring without being preachy."
            ],
            PromptIntent.Command =>
            [
                "Acknowledge quickly and state the immediate action.",
                "Use direct language with minimal filler.",
                "Confirm intent, then proceed."
            ],
            _ =>
            [
                "Keep it playful and lightweight.",
                "Sound like a close companion, not a scripted bot.",
                "Be concise but expressive."
            ]
        };

        var idx = DateTime.UtcNow.Minute % pool.Length;
        return pool[idx];
    }

    private static string GetTonePresetRule(string? tonePreset)
    {
        var preset = (tonePreset ?? string.Empty).Trim().ToLowerInvariant();
        return preset switch
        {
            "chill" => "Relaxed, calm, low-pressure wording.",
            "playful" => "Light, fun, witty without being cringe.",
            "direct" => "Straight to the point, minimal fluff.",
            _ => "Balanced and natural."
        };
    }

    private static string BuildMemoryBlock(IReadOnlyList<MemoryEntry> memories)
    {
        if (memories.Count == 0) return "- (no saved memory)";
        return string.Join("\n", memories.Select(m => $"- [{m.Type}] {m.Content}"));
    }

    private static string NormalizeEmotion(string emotion)
    {
        var e = (emotion ?? "").Trim().ToLowerInvariant();
        return e is "happy" or "sad" or "angry" or "surprised" or "playful" or "thinking"
            or "idle" or "focused" or "frustrated" or "sleepy" or "bored" or "concerned"
            ? e
            : "neutral";
    }

    private static string LoadModelfileHint()
    {
        try
        {
            var candidates = new[]
            {
                Path.Combine(Environment.CurrentDirectory, "assets", "Modelfile"),
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "assets", "Modelfile")),
                Path.Combine(AppContext.BaseDirectory, "assets", "Modelfile")
            };

            var path = candidates.FirstOrDefault(File.Exists);
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;

            var text = File.ReadAllText(path);
            var personality = Regex.Match(text, @"PERSONALITY:\s*(.+)", RegexOptions.IgnoreCase).Groups[1].Value.Trim();
            var speech = Regex.Match(text, @"SPEECH:\s*(.+)", RegexOptions.IgnoreCase).Groups[1].Value.Trim();
            var noQuestions = Regex.IsMatch(text, @"RARELY ask questions", RegexOptions.IgnoreCase);
            var noEmoji = Regex.IsMatch(text, @"NEVER use emojis", RegexOptions.IgnoreCase);

            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(personality))
                parts.Add(personality);
            if (!string.IsNullOrWhiteSpace(speech))
                parts.Add(speech);
            if (noQuestions)
                parts.Add("prefer statements over questions");
            if (noEmoji)
                parts.Add("no emojis");

            return string.Join("; ", parts.Take(4));
        }
        catch
        {
            return string.Empty;
        }
    }
}
