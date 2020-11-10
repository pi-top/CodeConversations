using System.IO;

namespace CodeConversations.Models
{
    public class CardJsonFiles
    {
        public static string SelectLanguage { get; } = Path.Combine(".", "Cards", $"{nameof(SelectLanguage)}.json");
        public static string IntroduceRover { get; } = Path.Combine(".", "Cards", $"{nameof(IntroduceRover)}.json");
        public static string ResetRover { get; } = Path.Combine(".", "Cards", $"{nameof(ResetRover)}.json");
    }
}
