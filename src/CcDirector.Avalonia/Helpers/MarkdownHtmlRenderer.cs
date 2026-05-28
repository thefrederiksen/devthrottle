using Markdig;

namespace CcDirector.Avalonia.Helpers;

/// <summary>
/// Renders Markdown to a fully-styled standalone HTML document
/// suitable for display in a WebView.
/// </summary>
public static class MarkdownHtmlRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseEmojiAndSmiley()
        .UseSoftlineBreakAsHardlineBreak()
        .Build();

    public static string Render(string markdown)
    {
        // Fully qualified: the Markdown.Avalonia package introduces a top-level
        // "Markdown" namespace that otherwise shadows the Markdig.Markdown class.
        var body = Markdig.Markdown.ToHtml(markdown ?? string.Empty, Pipeline);
        return Wrap(body);
    }

    private const string HtmlTemplate = """
        <!DOCTYPE html>
        <html lang="en">
        <head>
          <meta charset="utf-8" />
          <meta name="color-scheme" content="dark" />
          <style>
            :root { color-scheme: dark; }
            html, body { margin: 0; padding: 0; background: #1e1e1e; color: #d4d4d4; }
            body {
              font-family: 'Segoe UI', system-ui, -apple-system, sans-serif;
              font-size: 14px;
              line-height: 1.55;
              padding: 24px 32px;
              max-width: 920px;
              margin: 0 auto;
            }
            h1, h2, h3, h4, h5, h6 { color: #e0e0e0; margin-top: 1.6em; margin-bottom: 0.5em; }
            h1 { border-bottom: 1px solid #3c3c3c; padding-bottom: 0.3em; }
            h2 { border-bottom: 1px solid #2d2d2d; padding-bottom: 0.2em; }
            a { color: #4ea1ff; text-decoration: none; }
            a:hover { text-decoration: underline; }
            p, ul, ol, blockquote { margin: 0.6em 0; }
            code {
              background: #2d2d2d; color: #d4d4d4;
              padding: 1px 5px; border-radius: 3px;
              font-family: 'Cascadia Mono', Consolas, 'Courier New', monospace;
              font-size: 0.92em;
            }
            pre {
              background: #1b1b1b; border: 1px solid #2d2d2d;
              padding: 12px 14px; border-radius: 4px;
              overflow-x: auto; line-height: 1.45;
            }
            pre code { background: transparent; padding: 0; }
            blockquote {
              border-left: 3px solid #3c3c3c; color: #aaaaaa;
              margin: 0.8em 0; padding: 0.2em 0 0.2em 12px;
            }
            table { border-collapse: collapse; margin: 0.8em 0; }
            th, td { border: 1px solid #3c3c3c; padding: 6px 10px; }
            th { background: #252526; color: #569cd6; text-align: left; }
            tr:nth-child(even) td { background: #252526; }
            hr { border: 0; border-top: 1px solid #3c3c3c; margin: 1.5em 0; }
            img { max-width: 100%; }
            input[type=checkbox] { accent-color: #4ea1ff; }
            ::selection { background: #264f78; }
          </style>
        </head>
        <body>
        {{BODY}}
        </body>
        </html>
        """;

    private static string Wrap(string body)
    {
        return HtmlTemplate.Replace("{{BODY}}", body);
    }
}
