namespace TsukiAI.Core.Models;

public enum MessageSource
{
    Text,
    Voice,
    Activity,
    System,
}

public sealed record ConversationMessage(
    string Role,
    string Content,
    DateTime Timestamp,
    MessageSource Source = MessageSource.Text
);
