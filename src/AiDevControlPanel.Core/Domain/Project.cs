namespace AiDevControlPanel.Core.Domain;

public sealed class Project
{
    public int Id { get; set; }

    public string Name { get; set; } = "";

    public string LocalPath { get; set; } = "";

    /// <summary>GitHub repository as "owner/name".</summary>
    public string GitHubRepo { get; set; } = "";

    public string DefaultBranch { get; set; } = "main";

    public bool IsPublic { get; set; }

    /// <summary>Checked projects show their TSAs and issues in the side panels.</summary>
    public bool IsChecked { get; set; } = true;

    public OnboardingState OnboardingState { get; set; }

    public string? LastError { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
