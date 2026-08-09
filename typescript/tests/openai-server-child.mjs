import { appendFileSync } from "node:fs";
import { createServer } from "node:http";

const logPath = process.argv[2];
const server = createServer(async (request, response) => {
  const chunks = [];
  for await (const chunk of request) {
    chunks.push(chunk);
  }
  const body = chunks.length ? JSON.parse(Buffer.concat(chunks).toString()) : null;
  appendFileSync(logPath, `${JSON.stringify({ url: request.url, body })}\n`);
  response.setHeader("content-type", "application/json");
  if (request.url === "/v1/models") {
    response.end(
      JSON.stringify({
        object: "list",
        data: [{ id: "gpt-5.6-sol", object: "model", created: 0, owned_by: "local" }],
      }),
    );
    return;
  }
  if (request.url === "/v1/responses") {
    const text = JSON.stringify({ answer: 42 });
    response.end(
      JSON.stringify({
        id: "resp_test",
        object: "response",
        created_at: 1,
        status: "completed",
        error: null,
        incomplete_details: null,
        instructions: null,
        max_output_tokens: null,
        model: "gpt-5.6-sol",
        output: [
          {
            id: "msg_test",
            type: "message",
            status: "completed",
            role: "assistant",
            content: [{ type: "output_text", text, annotations: [] }],
          },
        ],
        parallel_tool_calls: true,
        previous_response_id: null,
        reasoning: { effort: "low", summary: null },
        store: false,
        temperature: 1,
        text: { format: { type: "text" } },
        tool_choice: "auto",
        tools: [],
        top_p: 1,
        truncation: "disabled",
        usage: {
          input_tokens: 1,
          input_tokens_details: { cached_tokens: 0 },
          output_tokens: 1,
          output_tokens_details: { reasoning_tokens: 0 },
          total_tokens: 2,
        },
        user: null,
        metadata: {},
      }),
    );
    return;
  }
  if (request.url === "/v1/chat/completions") {
    response.end(
      JSON.stringify({
        id: "chat_test",
        object: "chat.completion",
        created: 1,
        model: "deepseek/deepseek-v4-flash-0731",
        choices: [
          {
            index: 0,
            finish_reason: "stop",
            message: { role: "assistant", content: "fixture response" },
          },
        ],
        usage: { prompt_tokens: 1, completion_tokens: 1, total_tokens: 2 },
      }),
    );
    return;
  }
  response.statusCode = 404;
  response.end("{}");
});

server.listen(0, "127.0.0.1", () => console.log(server.address().port));
