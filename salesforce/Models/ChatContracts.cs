namespace ChatApp.Models;

public sealed record ChatMessageDto
{
    public string Role { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
}

public sealed record AgentResponseDto
{
    public string AgentName { get; init; } = string.Empty;
    public string Soql { get; init; } = string.Empty;
    public string Explanation { get; init; } = string.Empty;
}

public sealed record AgentChatResponseDto
{
    public List<AgentResponseDto> AgentResponses { get; init; } = [];
    public List<ChatMessageDto> UpdatedHistory { get; init; } = [];
}
