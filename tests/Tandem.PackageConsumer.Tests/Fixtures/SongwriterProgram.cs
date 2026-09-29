using Examples.Songwriter;
using Tandem;

var participants = SongwriterDefinitions.Create(
    new SongwriterClients(
        new ScriptedChatClient(
            ScriptedChatClient.Text("{\"lyrics\":\"First draft\"}"),
            ScriptedChatClient.Text("{\"lyrics\":\"Linted\\ndraft\"}"),
            ScriptedChatClient.Text("{\"lyrics\":\"Final\\ndraft\"}")
        ),
        new ScriptedChatClient(
            ScriptedChatClient.Text("{\"accepted\":false,\"feedback\":\"Sharpen it.\"}"),
            ScriptedChatClient.Text("{\"accepted\":true,\"feedback\":\"Accepted.\"}")
        )
    )
);
var result = await new PipelineRunner().RunAsync(
    new SongwriterComposition(participants).Build(),
    new SongwriterState("Rebuild after a storm."),
    cancellationToken: CancellationToken.None
);
if (!result.Succeeded || result.State.Lyrics != "Final\ndraft")
    throw new Exception("Songwriter package proof failed.");
