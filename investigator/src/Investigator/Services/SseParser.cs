using System.Runtime.CompilerServices;
using System.Text.Json;
using Investigator.Models;

namespace Investigator.Services;

internal static class SseParser
{
    public static async IAsyncEnumerable<ContentBlock> ParseSseStream(
        Stream stream,
        ILogger logger,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(stream);
        var processor = new StreamEventProcessor(logger);

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!line.StartsWith("data: ")) continue;
            var data = line["data: ".Length..];
            if (data == "[DONE]") break;

            StreamEvent? evt;
            try
            {
                evt = JsonSerializer.Deserialize<StreamEvent>(data);
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Failed to deserialize SSE event: {Data}", data);
                continue;
            }

            if (evt is null)
            {
                logger.LogWarning("Deserialized SSE event was null: {Data}", data);
                continue;
            }

            foreach (var block in processor.ProcessEvent(evt))
                yield return block;
        }
    }
}
