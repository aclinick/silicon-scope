namespace SiliconScope.Core;

public sealed record NpuDetectionResult(bool IsPresent, string? LuidToken, string DisplayName);
