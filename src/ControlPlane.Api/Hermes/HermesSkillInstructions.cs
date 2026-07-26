using ControlPlane.Api.Chat;
using Microsoft.Extensions.Options;

namespace ControlPlane.Api.Hermes;

public sealed class HermesSkillInstructions(IOptions<HermesOptions> options)
{
    private static readonly string[] SkillNames =
    [
        "endpoint-support-router",
        "endpoint-safety",
        "ibsng-support",
        "office-support"
    ];

    private readonly Lazy<string> _instructions = new(() =>
    {
        var root = Path.GetFullPath(options.Value.SkillsRoot);
        var sections = new List<string>(SkillNames.Length);
        foreach (var name in SkillNames)
        {
            var path = Path.Combine(root, name, "SKILL.md");
            if (!File.Exists(path))
                throw new FileNotFoundException(
                    $"Required reviewed Hermes skill '{name}' was not found.", path);
            sections.Add($"<reviewed-skill name=\"{name}\">\n"
                + File.ReadAllText(path)
                + "\n</reviewed-skill>");
        }

        return string.Join("\n\n", sections);
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    public string GetInstructions() => _instructions.Value;
}
