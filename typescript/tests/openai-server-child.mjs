import { appendFileSync } from "node:fs";
import { createServer } from "node:http";

const logPath = process.argv[2];
const mode = process.argv[3];
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
    const completed = {
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
    };
    const item = completed.output[0];
    const part = item.content[0];
    response.setHeader("content-type", "text/event-stream");
    const event = (type, data) =>
      response.write(`event: ${type}\ndata: ${JSON.stringify(data)}\n\n`);
    event("response.created", {
      type: "response.created",
      response: { ...completed, status: "in_progress", output: [] },
    });
    event("response.output_item.added", {
      type: "response.output_item.added",
      output_index: 0,
      item: { ...item, status: "in_progress", content: [] },
    });
    event("response.content_part.added", {
      type: "response.content_part.added",
      item_id: item.id,
      output_index: 0,
      content_index: 0,
      part: { ...part, text: "" },
    });
    event("response.output_text.delta", {
      type: "response.output_text.delta",
      item_id: item.id,
      output_index: 0,
      content_index: 0,
      delta: part.text,
    });
    event("response.output_text.done", {
      type: "response.output_text.done",
      item_id: item.id,
      output_index: 0,
      content_index: 0,
      text: part.text,
    });
    event("response.content_part.done", {
      type: "response.content_part.done",
      item_id: item.id,
      output_index: 0,
      content_index: 0,
      part,
    });
    event("response.output_item.done", {
      type: "response.output_item.done",
      output_index: 0,
      item,
    });
    event("response.completed", { type: "response.completed", response: completed });
    response.end();
    return;
  }
  if (request.url === "/v1/chat/completions") {
    response.setHeader("content-type", "text/event-stream");
    if (mode === "workspace" || mode === "workspace-parameterized") {
      const hasToolResult = body.messages.some((message) => message.role === "tool");
      const chunk = {
        id: "chat_workspace",
        object: "chat.completion.chunk",
        created: 1,
        model: "fixture",
        choices: [
          {
            index: 0,
            finish_reason: null,
            delta: hasToolResult
              ? { content: "Complete." }
              : {
                  tool_calls: [
                    {
                      index: 0,
                      id: "call_workspace",
                      type: "function",
                      function: {
                        name: "run_tests",
                        arguments:
                          mode === "workspace-parameterized"
                            ? JSON.stringify({
                                value:
                                  "spaces ' \" $() `touch marker` ; New-Item marker ; && || | > <\n* $HOME",
                              })
                            : "{}",
                      },
                    },
                  ],
                },
          },
        ],
      };
      response.write(`data: ${JSON.stringify(chunk)}\n\n`);
      response.write(
        `data: ${JSON.stringify({
          ...chunk,
          choices: [
            {
              index: 0,
              finish_reason: hasToolResult ? "stop" : "tool_calls",
              delta: {},
            },
          ],
        })}\n\n`,
      );
      response.write(
        `data: ${JSON.stringify({
          ...chunk,
          choices: [],
          usage: { prompt_tokens: 1, completion_tokens: 1, total_tokens: 2 },
        })}\n\n`,
      );
      response.end("data: [DONE]\n\n");
      return;
    }
    const chunk = {
      id: "chat_test",
      object: "chat.completion.chunk",
      created: 1,
      model: "fixture",
      choices: [
        {
          index: 0,
          finish_reason: null,
          delta: {
            tool_calls: [
              {
                index: 0,
                id: "call_accept",
                type: "function",
                function: { name: "accept", arguments: JSON.stringify({ accepted: true }) },
              },
            ],
          },
        },
      ],
    };
    response.write(`data: ${JSON.stringify(chunk)}\n\n`);
    response.write(
      `data: ${JSON.stringify({ ...chunk, choices: [{ index: 0, finish_reason: "tool_calls", delta: {} }] })}\n\n`,
    );
    response.write(
      `data: ${JSON.stringify({ ...chunk, choices: [], usage: { prompt_tokens: 1, completion_tokens: 1, total_tokens: 2 } })}\n\n`,
    );
    response.end("data: [DONE]\n\n");
    return;
  }
  response.statusCode = 404;
  response.end("{}");
});

server.listen(0, "127.0.0.1", () => console.log(server.address().port));
