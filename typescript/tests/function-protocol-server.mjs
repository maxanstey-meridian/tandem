import { appendFileSync } from "node:fs";
import { createServer } from "node:http";

const logPath = process.argv[2];
let implementerVisit = 0;
let reviewerVisit = 0;
const sources = [
  `(input) => input.trim().toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-+|-+$/g, "")`,
  `(input) => input.trim().toLowerCase().normalize("NFD").replace(/[\\u0300-\\u036f]/g, "").replace(/[^a-z0-9]+/g, "-").replace(/^-+|-+$/g, "")`,
  `function slugify(input) { return input.trim().toLowerCase().normalize("NFD").replace(/[\\u0300-\\u036f]/g, "").replace(/[^a-z0-9]+/g, "-").replace(/^-+|-+$/g, ""); }`,
];

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
  if (request.url === "/v1/chat/completions") {
    const source = sources[implementerVisit++];
    const implementation = {
      implementation: source,
      rationale: `Implementation revision ${implementerVisit}`,
    };
    response.setHeader("content-type", "text/event-stream");
    const chunk = {
      id: `chat_implementer_${implementerVisit}`,
      object: "chat.completion.chunk",
      created: implementerVisit,
      model: "fixture-ds4",
      choices: [
        {
          index: 0,
          finish_reason: null,
          delta: {
            tool_calls: [
              {
                index: 0,
                id: `call_${implementerVisit}`,
                type: "function",
                function: {
                  name: "submit_implementation",
                  arguments: JSON.stringify(implementation),
                },
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
  if (request.url === "/v1/responses") {
    reviewerVisit++;
    const review =
      reviewerVisit === 1
        ? {
            decision: "RequestChanges",
            summary: "Correct behavior, but improve maintainability.",
            findings: [
              "Use a named function expression so the implementation is self-identifying.",
            ],
          }
        : { decision: "Accept", summary: "The slugify implementation is accepted.", findings: [] };
    const completed = {
      id: `resp_reviewer_${reviewerVisit}`,
      object: "response",
      created_at: reviewerVisit,
      status: "completed",
      error: null,
      incomplete_details: null,
      instructions: null,
      max_output_tokens: null,
      model: "gpt-5.6-sol",
      output: [
        {
          id: `msg_reviewer_${reviewerVisit}`,
          type: "message",
          status: "completed",
          role: "assistant",
          content: [{ type: "output_text", text: JSON.stringify(review), annotations: [] }],
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
  response.statusCode = 404;
  response.end("{}");
});

server.listen(0, "127.0.0.1", () => console.log(server.address().port));
