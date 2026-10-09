using System.CommandLine;
using System.Text;

namespace RedmineCli.Commands;

/// <summary>redmine guide: the guide for AI agents (guide.md, embedded in the exe).</summary>
internal static class GuideCommand
{
    public static Command Create()
    {
        var command = new Command("guide", "AI エージェント向けの手順書 (Markdown) を表示する。安全モデル、典型的な作業の流れ、JSON の形、終了コード").WithNotes("""
            例:
              redmine guide            # 全文を表示
              redmine guide | more
            """);
        command.SetAction(_ =>
        {
            Output.Out(Text().TrimEnd());
            return Exit.Ok;
        });
        return command;
    }

    public static string Text()
    {
        using var stream = typeof(GuideCommand).Assembly.GetManifestResourceStream("RedmineCli.guide.md")
            ?? throw new InvalidOperationException("guide.md is not embedded");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
