import { appendFileSync } from "node:fs";
import { createServer } from "node:http";

const logPath = process.argv[2];
let visit = 0;

const server = createServer(async (request, response) => {
  const chunks = [];
  for await (const chunk of request) {
    chunks.push(chunk);
  }
  const body = chunks.length ? JSON.parse(Buffer.concat(chunks).toString()) : null;
  appendFileSync(logPath, `${JSON.stringify({ url: request.url, body })}\n`);

  if (request.url === "/v1/models") {
    response.setHeader("content-type", "application/json");
    response.end(
      JSON.stringify({
        object: "list",
        data: [{ id: "fixture", object: "model", created: 0, owned_by: "local" }],
      }),
    );
    return;
  }

  visit++;
  response.setHeader("content-type", "text/event-stream");
  const tools = new Map(body.tools.map((tool) => [tool.function.name, tool.function]));
  if (visit <= 2) {
    const name = visit === 1 ? "load_skill" : "read_skill_resource";
    const tool = tools.get(name);
    const properties = Object.keys(tool.parameters.properties);
    const args = Object.fromEntries(
      properties.map((property, index) => [
        property,
        index === 0 ? "test-skill" : "references/rules.md",
      ]),
    );
    const chunk = {
      id: `chat_skill_${visit}`,
      object: "chat.completion.chunk",
      created: visit,
      model: "fixture",
      choices: [
        {
          index: 0,
          finish_reason: null,
          delta: {
            tool_calls: [
              {
                index: 0,
                id: `call_${visit}`,
                type: "function",
                function: { name, arguments: JSON.stringify(args) },
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
  } else {
    const chunk = {
      id: `chat_skill_${visit}`,
      object: "chat.completion.chunk",
      created: visit,
      model: "fixture",
      choices: [{ index: 0, finish_reason: null, delta: { content: "Reviewed with the skill." } }],
    };
    response.write(`data: ${JSON.stringify(chunk)}\n\n`);
    response.write(
      `data: ${JSON.stringify({ ...chunk, choices: [{ index: 0, finish_reason: "stop", delta: {} }] })}\n\n`,
    );
  }
  response.end("data: [DONE]\n\n");
});

server.listen(0, "127.0.0.1", () => console.log(server.address().port));
