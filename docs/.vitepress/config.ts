import { defineConfig } from "vitepress";

export default defineConfig({
  title: "Tandem",
  description: "Typed agentic pipelines for C# and TypeScript",
  base: "/tandem/",

  themeConfig: {
    nav: [
      { text: "Get Started", link: "/getting-started" },
      { text: "Guides", link: "/guides/mental-model" },
      { text: "Packages", link: "/reference/packages" },
      { text: "NuGet", link: "https://www.nuget.org/packages/Meridian.Tandem" },
      { text: "npm", link: "https://www.npmjs.com/package/@maxanstey-meridian/tandem" },
    ],

    sidebar: [
      {
        text: "Introduction",
        items: [
          { text: "What is Tandem?", link: "/" },
          { text: "Getting Started", link: "/getting-started" },
        ],
      },
      {
        text: "Guides",
        items: [
          { text: "The Mental Model", link: "/guides/mental-model" },
          { text: "State", link: "/guides/state" },
          { text: "Agents", link: "/guides/agents" },
          { text: "Capabilities", link: "/guides/capabilities" },
          { text: "Stages", link: "/guides/stages" },
          { text: "Routes & Outputs", link: "/guides/routes" },
          { text: "Interactions", link: "/guides/interactions" },
          { text: "Parallel Groups", link: "/guides/parallel" },
          { text: "Collections", link: "/guides/collections" },
          { text: "Running a Pipeline", link: "/guides/running" },
          { text: "Persistence", link: "/guides/persistence" },
          { text: "Workspace Tools", link: "/guides/workspace-tools" },
          { text: "Packet Files", link: "/guides/packets" },
          { text: "Tandem Studio", link: "/guides/studio" },
          { text: "Examples", link: "/guides/examples" },
        ],
      },
      {
        text: "Reference",
        items: [
          { text: "Packages", link: "/reference/packages" },
          { text: "Chat Clients", link: "/reference/chat-clients" },
          { text: "C# and TypeScript", link: "/reference/csharp-and-typescript" },
        ],
      },
      {
        text: "Misc",
        items: [
          { text: "How It Works", link: "/misc/how-it-works" },
          { text: "Limitations", link: "/misc/limitations" },
        ],
      },
    ],

    socialLinks: [{ icon: "github", link: "https://github.com/maxanstey-meridian/tandem" }],

    search: {
      provider: "local",
    },
  },
});
