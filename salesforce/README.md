# ChatApp — ASP.NET Core Web API with Microsoft.Extensions.AI

A minimal ASP.NET Core 8 Web API that accepts a list of `ChatMessage` objects as
conversation **history** and forwards them to Claude via the `IChatClient`
abstraction from **Microsoft.Extensions.AI**.

---

## Project Structure

```
ChatApp/
├── Controllers/
│   └── ChatController.cs       # POST /api/chat  — uses IChatClient
├── Models/
│   ├── ChatMessage.cs          # { Role, Content }
│   └── ChatModels.cs           # ChatRequest / ChatResponse
├── appsettings.json
├── Program.cs                  # Registers IChatClient (Anthropic provider)
└── ChatApp.csproj
```

---

## NuGet Packages

```
Microsoft.Extensions.AI          9.3.0-preview.*
Microsoft.Extensions.AI.Anthropic 9.3.0-preview.*
Swashbuckle.AspNetCore          6.5.0
```

---

## Quick Start

### 1. Add your API key

```json
// appsettings.json
{
  "Anthropic": {
    "ApiKey": "sk-ant-...",
    "Model":  "claude-opus-4-5"
  }
}
```

Or via environment variable (recommended for production):

```bash
export Anthropic__ApiKey="sk-ant-..."
```

### 2. Run

```bash
dotnet run
```

Swagger UI → **http://localhost:5000**

---

## How IChatClient is registered (Program.cs)

```csharp
builder.Services.AddSingleton<IChatClient>(
    new AnthropicClientBuilder(apiKey)
        .UseAnthropicChatClient(model)
        .Build()
);
```

The controller receives `IChatClient` via DI — no direct Anthropic dependency in
business logic.

---

## API

### `POST /api/chat`

**Request:**

```json
{
  "messages": [
    { "role": "user",      "content": "Hello!" },
    { "role": "assistant", "content": "Hi! How can I help?" },
    { "role": "user",      "content": "What is the capital of France?" }
  ]
}
```

**Response (200):**

```json
{
  "reply": "The capital of France is Paris.",
  "history": [
    { "role": "user",      "content": "Hello!" },
    { "role": "assistant", "content": "Hi! How can I help?" },
    { "role": "user",      "content": "What is the capital of France?" },
    { "role": "assistant", "content": "The capital of France is Paris." }
  ]
}
```

Pass the returned `history` as `messages` in your next request to maintain
multi-turn context.

---

## Swapping Providers

Because the controller only depends on `IChatClient`, you can switch to OpenAI,
Azure OpenAI, or any other MEAI-compatible provider by changing only `Program.cs`:

```csharp
// OpenAI example
builder.Services.AddSingleton<IChatClient>(
    new OpenAIClient(apiKey).AsChatClient("gpt-4o")
);
```
